using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management.Api;

/// <summary>
/// Turns the user-supplied credentials from <see cref="AzurPromiliaAccount"/> into a live session.
/// <para>
/// Only the token-exchange endpoints are driven here (<c>tokenlogin</c>, <c>pwdlogin</c>,
/// <c>phonelogin</c>). The interactive parts of the official flow — Geetest / TCaptcha challenges and
/// SMS dispatch — are intentionally out of scope, which is exactly why credentials come from a file
/// the user controls.
/// </para>
/// </summary>
public static class AzurPromiliaLoginService
{
    /// <summary>
    /// Business code the passport API answers with when the request signature does not verify
    /// (<c>msg</c>: <c>MD5校验错误</c>). It always means the plugin built the request wrongly rather than
    /// that the credentials are bad, so it is reported as a distinct error.
    /// </summary>
    private const int SignatureRejectedCode = 2;

    /// <summary>
    /// Reports a login attempt that produced no session, calling out signature failures explicitly.
    /// </summary>
    private static void LogLoginFailure(string endpoint, LoginResult result)
    {
        if (result.Code == SignatureRejectedCode)
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromilia] {Endpoint} was rejected with code={Code} ({Msg}). The request signature did " +
                "not verify. A POST must be signed over its exact JSON body " +
                "(sign = md5(timestamp + body + utoken + appId + channelCode + secretKey)), and the value " +
                "signed as 'utoken' must match the 'utoken' header verbatim. This is a plugin defect, not a " +
                "credential problem.",
                endpoint, result.Code, result.Msg);
            return;
        }

        SharedStatic.InstanceLogger.LogWarning(
            "[AzurPromilia] {Endpoint} returned no usable session (code={Code}, msg={Msg}).",
            endpoint, result.Code, result.Msg);
    }

    /// <summary>
    /// Resolves a usable session and attaches it to <paramref name="api"/>.
    /// </summary>
    /// <param name="api">The API client to authenticate.</param>
    /// <param name="account">User-supplied credentials.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>
    /// The logged-in user on success; <c>null</c> when no credential strategy is configured or every
    /// strategy failed. Callers should treat <c>null</c> as "download URLs will be unavailable".
    /// </returns>
    public static async Task<LoginUserInfo?> ResolveAsync(AzurPromiliaApiClient api,
        AzurPromiliaAccount account,
        CancellationToken token)
    {
        // ---- Strategy 1: an explicit token supplied by the user ------------------------------
        string? tokenCandidate = null;
        int accountType = account.AccountType ?? AzurPromiliaAccountType.Password;

        if (!string.IsNullOrWhiteSpace(account.UToken))
        {
            tokenCandidate = account.UToken!.Trim();
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromilia] Using the 'utoken' supplied in the account file.");
        }
        else if (account.ImportFromOfficialLauncher)
        {
            // ---- Strategy 2: reuse whatever the official launcher already saved ---------------
            if (AzurPromiliaAccount.TryImportOfficialToken(api.Channel, out string? imported, out int importedType))
            {
                tokenCandidate = imported;
                accountType = importedType;
            }
        }

        if (!string.IsNullOrEmpty(tokenCandidate))
        {
            LoginUserInfo? viaToken = await TryTokenLoginAsync(api, tokenCandidate!, accountType, token)
                .ConfigureAwait(false);

            if (viaToken != null)
            {
                return Complete(api, account, viaToken);
            }

            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] The stored session token was rejected. Falling back to credential login if available.");
        }

        // ---- Strategy 3: password login -------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(account.Account) && !string.IsNullOrWhiteSpace(account.Password))
        {
            LoginUserInfo? viaPassword = await TryPasswordLoginAsync(api, account.Account!, account.Password!, token)
                .ConfigureAwait(false);

            if (viaPassword != null)
            {
                return Complete(api, account, viaPassword);
            }
        }

        // ---- Strategy 4: SMS-code login -------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(account.Phone) && !string.IsNullOrWhiteSpace(account.SmsCode))
        {
            LoginUserInfo? viaSms = await TryPhoneLoginAsync(api, account.Phone!, account.SmsCode!, token)
                .ConfigureAwait(false);

            if (viaSms != null)
            {
                return Complete(api, account, viaSms);
            }
        }

        SharedStatic.InstanceLogger.LogError(
            "[AzurPromilia] Not logged in. The game's download URLs are only issued to authenticated accounts, " +
            "so installation and updates will not be available until a session is provided. " +
            "Edit '{Path}' and fill in 'utoken' (or 'account' + 'password'), then restart the launcher.",
            account.SourcePath ?? AzurPromiliaAccount.FileName);

        return null;
    }

    private static LoginUserInfo Complete(AzurPromiliaApiClient api, AzurPromiliaAccount account,
        LoginUserInfo user)
    {
        api.SetUToken(user.UToken);
        api.Uid = user.Uid;
        account.SaveRefreshedIdentity(user.Uid, user.AccountType);

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Session established. uid={Uid}, accountType={AccountType}",
            user.Uid ?? "(unknown)", user.AccountType);

        if (user.UnsubState == 1)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromilia] This account is inside the account-deletion cool-down period.");
        }

        if (user.UserState == 2)
        {
            SharedStatic.InstanceLogger.LogWarning("[AzurPromilia] This account is banned.");
        }

        return user;
    }

    /// <summary>
    /// <c>POST /passportv2/client/user/tokenlogin</c> — exchanges a stored token for a live session.
    /// </summary>
    private static async Task<LoginUserInfo?> TryTokenLoginAsync(AzurPromiliaApiClient api, string uToken,
        int accountType, CancellationToken token)
    {
        // tokenlogin authenticates purely through the 'utoken' header.
        api.SetUToken(uToken);

        string body = JsonSerializer.Serialize(new TokenLoginRequest { AccountType = accountType },
            AzurPromiliaApiContext.Default.TokenLoginRequest);

        try
        {
            LoginResult result = await api.PostLoginAsync("/user/tokenlogin", body, token).ConfigureAwait(false);
            LoginUserInfo? user = result.GetUserInfo();

            if (user == null || string.IsNullOrEmpty(user.UToken))
            {
                LogLoginFailure("tokenlogin", result);
                api.SetUToken(null);
                return null;
            }

            SharedStatic.InstanceLogger.LogInformation("[AzurPromilia] tokenlogin succeeded.");
            return user;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[AzurPromilia] tokenlogin failed: {Error}", ex.Message);
            api.SetUToken(null);
            return null;
        }
    }

    /// <summary>
    /// <c>POST /passportv2/client/user/pwdlogin</c> — password login. The password is encrypted with
    /// the recovered <c>extApi4</c> algorithm using the server-published <c>pwdKey</c>.
    /// </summary>
    private static async Task<LoginUserInfo?> TryPasswordLoginAsync(AzurPromiliaApiClient api, string account,
        string password, CancellationToken token)
    {
        if (string.IsNullOrEmpty(api.PasswordKey))
        {
            SharedStatic.InstanceLogger.LogError(
                "[AzurPromilia] Password login needs the server's 'encrypt.password.pwd' key, which was not " +
                "returned by getLauncherConfig. Password login is unavailable; use 'utoken' instead.");
            return null;
        }

        string encryptedPassword = AzurPromiliaCrypto.EncryptPassword(password, api.PasswordKey);

        string body = JsonSerializer.Serialize(new PasswordLoginRequest
        {
            AccountType = AzurPromiliaAccountType.Password,
            Account = account,
            Password = encryptedPassword
        }, AzurPromiliaApiContext.Default.PasswordLoginRequest);

        try
        {
            LoginResult result = await api.PostLoginAsync("/user/pwdlogin", body, token).ConfigureAwait(false);
            LoginUserInfo? user = result.GetUserInfo();

            if (user == null || string.IsNullOrEmpty(user.UToken))
            {
                LogLoginFailure("pwdlogin", result);
                return null;
            }

            SharedStatic.InstanceLogger.LogInformation("[AzurPromilia] pwdlogin succeeded.");
            return user;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[AzurPromilia] pwdlogin failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// <c>POST /passportv2/client/user/phonelogin</c> — SMS-code login.
    /// </summary>
    private static async Task<LoginUserInfo?> TryPhoneLoginAsync(AzurPromiliaApiClient api, string phone,
        string code, CancellationToken token)
    {
        string body = JsonSerializer.Serialize(new PhoneLoginRequest
        {
            AccountType = AzurPromiliaAccountType.Phone,
            Phone = phone,
            Code = code
        }, AzurPromiliaApiContext.Default.PhoneLoginRequest);

        try
        {
            LoginResult result = await api.PostLoginAsync("/user/phonelogin", body, token).ConfigureAwait(false);
            LoginUserInfo? user = result.GetUserInfo();

            if (user == null || string.IsNullOrEmpty(user.UToken))
            {
                LogLoginFailure("phonelogin", result);
                return null;
            }

            SharedStatic.InstanceLogger.LogInformation("[AzurPromilia] phonelogin succeeded.");
            return user;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning("[AzurPromilia] phonelogin failed: {Error}", ex.Message);
            return null;
        }
    }
}
