using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
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

public sealed class AzurPromiliaApiClient : IDisposable
{

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
    }

    public List<string> BaseDomains { get; set; }
    public AzurPromiliaChannelConfig Channel => _channel;
    public void SetSubChannelCode(string subChannelCode)
    {
        if (!string.IsNullOrEmpty(subChannelCode))
        {
            _subChannelCode = subChannelCode;
        }
    }

    public void SetTraceId(string traceId) => _traceId = traceId ?? string.Empty;

    #region Request plumbing
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

    private static string ComposeSigningPayload(long timestamp, string middle, string? uToken)
        => timestamp.ToString(CultureInfo.InvariantCulture) + middle + (uToken ?? string.Empty);
    internal static string BuildGetSigningPayload(long timestamp, IReadOnlyDictionary<string, string?> data,
        string? uToken)
        => ComposeSigningPayload(timestamp, BuildCanonicalQuery(data), uToken);
    private Dictionary<string, string> BuildSignedHeaders(string signedPayload)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string signature = AzurPromiliaCrypto.ComputeSignature(
            ComposeSigningPayload(timestamp, signedPayload, null),
            _channel.AppId, _channel.ChannelCode, _channel.SecretKey);

        Dictionary<string, string> headers = AzurPromiliaDeviceInfo.BuildBaseHeaders(_channel, _subChannelCode,
            _traceId, null);

        headers["timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture);
        headers["sign"] = signature;
        return headers;
    }

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

    private void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string?> data)
        => ApplyHeaders(request, BuildCanonicalQuery(data));
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
public sealed class AzurPromiliaApiException : Exception
{
    public AzurPromiliaApiException(int code, string message, string path)
        : base($"[{code}] {message} (path: {path})")
    {
        Code = code;
        Path = path;
    }

    public int Code { get; }
    public string Path { get; }
}
