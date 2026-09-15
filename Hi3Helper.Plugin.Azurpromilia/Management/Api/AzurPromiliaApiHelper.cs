using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

/// <summary>
/// Signed HTTP client for the Azur Promilia launcher API.
/// <para>
/// Every call carries a <c>timestamp</c> + <c>sign</c> header pair, where
/// <c>sign = md5(timestamp + signedPayload + utoken + appId + channelCode + secretKey)</c> and
/// <c>signedPayload</c> depends on the verb:
/// </para>
/// <list type="bullet">
/// <item><c>GET</c> — the canonical query string (keys sorted ascending, joined as <c>k=v&amp;</c>).</item>
/// <item><c>POST</c> — the exact JSON request body string.</item>
/// </list>
/// <para>
/// Requests are retried once on the same host and then rotated to the next failover domain,
/// mirroring the launcher's dual-line behaviour.
/// </para>
/// </summary>
public sealed class AzurPromiliaApiClient : IDisposable
{
    private const string UserApiPrefix = "/passportv2/client";

    private readonly AzurPromiliaChannelConfig _channel;
    private readonly HttpClient _httpClient;

    private string _subChannelCode;
    private string _traceId;
    private bool _isDisposed;

    public AzurPromiliaApiClient(AzurPromiliaChannelConfig channel, HttpClient httpClient,
        string subChannelCode, string traceId)
    {
        _channel = channel;
        _httpClient = httpClient;
        _subChannelCode = subChannelCode;
        _traceId = traceId;

        BaseDomains = new List<string> { "https://api-grp.manjuu.com", "https://api-grp-l2.manjuu.com" };
        UserDomains = new List<string>(BaseDomains);
        ComplianceDomains = new List<string>(BaseDomains);
    }

    /// <summary>Business API / download domains.</summary>
    public List<string> BaseDomains { get; set; }

    /// <summary>Passport (login) domains.</summary>
    public List<string> UserDomains { get; set; }

    /// <summary>Policy/compliance domains.</summary>
    public List<string> ComplianceDomains { get; set; }

    /// <summary>
    /// Password-encryption key published as <c>encrypt.password.pwd</c>, consumed by
    /// <see cref="AzurPromiliaCrypto.EncryptPassword"/>.
    /// </summary>
    public string? PasswordKey { get; set; }

    /// <summary>Current login token, injected into the <c>utoken</c> header and the signature.</summary>
    public string? UToken { get; private set; }

    /// <summary>Current account id, required by <c>getGameVersionConfig</c> / <c>getGameExeInfo</c>.</summary>
    public string? Uid { get; set; }

    /// <summary>Channel configuration in use.</summary>
    public AzurPromiliaChannelConfig Channel => _channel;

    /// <summary>Updates the login token used for signing and the <c>utoken</c> header.</summary>
    public void SetUToken(string? token) => UToken = token;

    /// <summary>Updates the attribution sub-channel code.</summary>
    public void SetSubChannelCode(string subChannelCode)
    {
        if (!string.IsNullOrEmpty(subChannelCode))
        {
            _subChannelCode = subChannelCode;
        }
    }

    /// <summary>Updates the trace id used for the <c>grp-trace-id</c> header.</summary>
    public void SetTraceId(string traceId) => _traceId = traceId ?? string.Empty;

    #region Request plumbing

    /// <summary>
    /// Builds the canonical query string used for signing: keys sorted ascending,
    /// rendered as <c>k=v</c> and joined with <c>&amp;</c>.
    /// </summary>
    internal static string BuildCanonicalQuery(IReadOnlyDictionary<string, string?> data)
    {
        if (data.Count == 0)
        {
            return string.Empty;
        }

        List<string> keys = new(data.Count);
        foreach (KeyValuePair<string, string?> pair in data)
        {
            keys.Add(pair.Key);
        }

        keys.Sort(StringComparer.Ordinal);

        StringBuilder builder = new();
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(keys[i]).Append('=').Append(data[keys[i]] ?? string.Empty);
        }

        return builder.ToString();
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> data)
    {
        if (data.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        bool first = true;
        foreach (KeyValuePair<string, string?> pair in data)
        {
            if (string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(Uri.EscapeDataString(pair.Key))
                   .Append('=')
                   .Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The single place the signed-string formula lives:
    /// <c>timestamp</c> + verb-specific middle component + <c>utoken</c>.
    /// </summary>
    private static string ComposeSigningPayload(long timestamp, string middle, string? uToken)
        => timestamp.ToString(CultureInfo.InvariantCulture) + middle + (uToken ?? string.Empty);

    /// <summary>
    /// Builds the string a <c>GET</c> request signs:
    /// <c>timestamp</c> + canonical (key-sorted) query + <c>utoken</c>.
    /// </summary>
    internal static string BuildGetSigningPayload(long timestamp, IReadOnlyDictionary<string, string?> data,
        string? uToken)
        => ComposeSigningPayload(timestamp, BuildCanonicalQuery(data), uToken);

    /// <summary>
    /// Builds the string a <c>POST</c> request signs: <c>timestamp</c> + the exact JSON body + <c>utoken</c>.
    /// </summary>
    /// <remarks>
    /// This asymmetry with <see cref="BuildGetSigningPayload"/> is easy to get wrong and is not forgiving:
    /// signing the (always empty) query string of a POST instead of its body makes the server answer
    /// <c>code=2, "MD5校验错误"</c> even when the credentials are perfectly valid.
    /// </remarks>
    internal static string BuildPostSigningPayload(long timestamp, string body, string? uToken)
        => ComposeSigningPayload(timestamp, body, uToken);

    /// <summary>
    /// Computes the <c>timestamp</c> / <c>sign</c> headers for an explicit signed payload.
    /// </summary>
    /// <param name="signedPayload">
    /// The verb-dependent middle component of the signed string: the canonical query for a <c>GET</c>,
    /// or the exact JSON body for a <c>POST</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// The server rebuilds the signed string itself and compares digests, so the payload passed here has
    /// to match, byte for byte, what the server reconstructs. For a <c>POST</c> that means the body must
    /// be serialised <i>once</i>, signed, and then transmitted unchanged — re-serialising it on the way
    /// out (or signing a different object) breaks the signature.
    /// </para>
    /// <para>
    /// The <c>utoken</c> component is taken from the same value that goes into the <c>utoken</c> request
    /// header. The server folds in whatever that header carries, so the two must agree: sending an empty
    /// <c>utoken</c> header while signing the four characters <c>null</c> (or vice versa) is rejected.
    /// While signed out both sides are the empty string, which is what the official launcher does and
    /// what the server accepts.
    /// </para>
    /// </remarks>
    private Dictionary<string, string> BuildSignedHeaders(string signedPayload)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string signature = AzurPromiliaCrypto.ComputeSignature(
            ComposeSigningPayload(timestamp, signedPayload, UToken),
            _channel.AppId, _channel.ChannelCode, _channel.SecretKey);

        Dictionary<string, string> headers = AzurPromiliaDeviceInfo.BuildBaseHeaders(_channel, _subChannelCode,
            _traceId, UToken);

        headers["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture);
        headers["sign"] = signature;
        return headers;
    }

    /// <summary>
    /// Computes the <c>timestamp</c> / <c>sign</c> headers for a <c>GET</c>, whose signed payload is the
    /// canonical (key-sorted) query string.
    /// </summary>
    private Dictionary<string, string> BuildSignedHeaders(IReadOnlyDictionary<string, string?> data)
        => BuildSignedHeaders(BuildCanonicalQuery(data));

    private void ApplyHeaders(HttpRequestMessage request, string signedPayload)
    {
        foreach (KeyValuePair<string, string> header in BuildSignedHeaders(signedPayload))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", AzurPromiliaDeviceInfo.UserAgent);
    }

    /// <summary>
    /// Applies the signed headers for a <c>GET</c> request.
    /// </summary>
    private void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string?> data)
        => ApplyHeaders(request, BuildCanonicalQuery(data));

    /// <summary>
    /// Issues a signed GET with per-domain retry and failover.
    /// </summary>
    /// <param name="domains">Ordered list of failover domains.</param>
    /// <param name="path">Absolute path, e.g. <c>/pc-launcher/client/launcher/config</c>.</param>
    /// <param name="data">Query parameters (also used to derive the signature).</param>
    /// <param name="jsonTypeInfo">Source-generated type info for the response envelope.</param>
    /// <param name="token">Cancellation token.</param>
    public async Task<T?> GetAsync<T>(IReadOnlyList<string> domains, string path,
        Dictionary<string, string?> data,
        JsonTypeInfo<AzurPromiliaResponse<T>> jsonTypeInfo,
        CancellationToken token)
    {
        Exception? lastError = null;

        foreach (string domain in NormalizeDomains(domains))
        {
            string url = BuildUrl(domain, path, data);

            // Same-host retry once, then rotate to the next domain.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using HttpRequestMessage request = new(HttpMethod.Get, url);
                    ApplyHeaders(request, data);

                    using HttpResponseMessage response =
                        await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                                         .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) for {path} on {domain}");
                        continue;
                    }

                    AzurPromiliaResponse<T>? envelope =
                        await response.Content.ReadFromJsonAsync(jsonTypeInfo, token).ConfigureAwait(false);

                    if (envelope == null)
                    {
                        lastError = new InvalidDataException($"Empty response body for {path} on {domain}");
                        continue;
                    }

                    if (envelope.Code != 0)
                    {
                        // Business-level failure: surface it without retrying on another domain.
                        throw new AzurPromiliaApiException(envelope.Code, envelope.Msg ?? "unknown error", path);
                    }

                    return envelope.Data;
                }
                catch (AzurPromiliaApiException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    SharedStatic.InstanceLogger.LogWarning(
                        "[AzurPromilia] GET {Path} failed on {Domain} (attempt {Attempt}): {Error}",
                        path, domain, attempt + 1, ex.Message);
                }
            }
        }

        throw new HttpRequestException(
            $"All domains failed for {path}. Last error: {lastError?.Message}", lastError);
    }

    /// <summary>
    /// Issues a signed GET returning the raw response body, used for index files.
    /// </summary>
    /// <remarks>
    /// Index URLs are pre-signed CDN links, so the signature headers are ignored by the object store;
    /// they are still attached for consistency with the launcher's traffic shape.
    /// </remarks>
    public async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        ApplyHeaders(request, string.Empty);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a signed POST to the passport API, used for the login endpoints.
    /// </summary>
    /// <param name="path">Path relative to <c>/passportv2/client</c>, e.g. <c>/user/tokenlogin</c>.</param>
    /// <param name="body">
    /// The serialised JSON request body. It is signed verbatim and transmitted unchanged, so callers must
    /// serialise it exactly once and must not reformat it afterwards.
    /// </param>
    /// <param name="token">Cancellation token.</param>
    public async Task<LoginResult> PostLoginAsync(string path, string body, CancellationToken token)
    {
        string requestPath = UserApiPrefix + path;
        Exception? lastError = null;

        foreach (string domain in NormalizeDomains(UserDomains))
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, domain.TrimEnd('/') + requestPath);

                // The POST signature covers the JSON body, not the (always empty) query string.
                ApplyHeaders(request, body);

                // Byte-for-byte the string that was just signed. The official launcher advertises a bare
                // 'application/json', so no charset parameter is appended.
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response =
                    await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                                     .ConfigureAwait(false);

                string responseText = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // A 401 from tokenlogin means the saved token is stale rather than a transport problem.
                    lastError = new AzurPromiliaApiException((int)response.StatusCode,
                        string.IsNullOrWhiteSpace(responseText) ? response.ReasonPhrase ?? "request failed" : responseText,
                        requestPath);
                    continue;
                }

                LoginResult? result = JsonSerializer.Deserialize(responseText, AzurPromiliaApiContext.Default.LoginResult);
                if (result == null)
                {
                    lastError = new InvalidDataException($"Empty login response from {requestPath}");
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                SharedStatic.InstanceLogger.LogWarning(
                    "[AzurPromilia] POST {Path} failed on {Domain}: {Error}", requestPath, domain, ex.Message);
            }
        }

        throw new HttpRequestException(
            $"All login domains failed for {requestPath}. Last error: {lastError?.Message}", lastError);
    }

    private static string BuildUrl(string domain, string path, IReadOnlyDictionary<string, string?> data)
    {
        string query = BuildQueryString(data);
        string url = domain.TrimEnd('/') + path;
        return string.IsNullOrEmpty(query) ? url : url + "?" + query;
    }

    private static IEnumerable<string> NormalizeDomains(IReadOnlyList<string> domains)
    {
        foreach (string domain in domains)
        {
            if (!string.IsNullOrWhiteSpace(domain))
            {
                yield return domain;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _httpClient.Dispose();
    }

    #endregion
}

/// <summary>
/// Raised when the API answers with a non-zero business <c>code</c>.
/// </summary>
public sealed class AzurPromiliaApiException : Exception
{
    public AzurPromiliaApiException(int code, string message, string path)
        : base($"[{code}] {message} (path: {path})")
    {
        Code = code;
        Path = path;
    }

    /// <summary>Business error code returned by the API.</summary>
    public int Code { get; }

    /// <summary>Request path that produced the error.</summary>
    public string Path { get; }

    /// <summary>
    /// <c>true</c> when the code indicates an expired or missing login session.
    /// </summary>
    public bool IsAuthFailure => Code is 401 or 52004 or 58004;
}
