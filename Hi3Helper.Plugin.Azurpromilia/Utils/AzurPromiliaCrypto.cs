using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

/// <summary>
/// Pure managed re-implementation of the Azur Promilia launcher's native <c>cutils.node</c> addon
/// (<c>extApi1</c> / <c>extApi3</c> / <c>extApi4</c>) plus the resource-index cipher.
/// <para>
/// The algorithms below were recovered byte-exactly from <c>napi-cutils@0.4.10</c>'s
/// <c>build/Release/cutils.node</c> and verified against the real addon (15/15 cases).
/// See <c>docs/12-原生模块逆向与CSharp复刻.md</c> for the disassembly evidence.
/// </para>
/// <para>
/// Consequence: this plugin does <b>not</b> need <c>cutils.node</c>, Node.js or OpenSSL at runtime.
/// </para>
/// </summary>
public static class AzurPromiliaCrypto
{
    /// <summary>
    /// Hard-coded AES-256 key used by <c>extApi1</c> to decrypt <c>res/app_config.dat</c>.
    /// 32 ASCII characters -> 32 raw bytes (the literal is used as-is, it is NOT Base64-decoded).
    /// </summary>
    private const string AppConfigKeyLiteral = "5f109ac9d64946e191c354d524bfe590";

    /// <summary>
    /// AES-192 key for the resource index. 24 ASCII characters used as raw bytes (NOT Base64-decoded),
    /// even though the literal happens to look like Base64.
    /// </summary>
    private const string ResourceIndexKeyLiteral = "mJ9dH2bR7fT1zW4eQ6cY8a==";

    /// <summary>
    /// AES IV for the resource index. 16 ASCII characters used as raw bytes.
    /// </summary>
    private const string ResourceIndexIvLiteral = "aP3sD8fG0hJ6kL9z";

    private static readonly byte[] AppConfigKeyBytes = Encoding.ASCII.GetBytes(AppConfigKeyLiteral);
    private static readonly byte[] ResourceIndexKeyBytes = Encoding.UTF8.GetBytes(ResourceIndexKeyLiteral);
    private static readonly byte[] ResourceIndexIvBytes = Encoding.UTF8.GetBytes(ResourceIndexIvLiteral);

    #region extApi1 - app_config.dat

    /// <summary>
    /// Port of <c>cutils.extApi1(path)</c>.
    /// <para>
    /// Layout of the input file: <c>[16-byte IV][ciphertext...]</c>.
    /// Cipher: AES-256-CBC, key = <see cref="AppConfigKeyLiteral"/>, padding = PKCS#7.
    /// Output: the decrypted JSON as a UTF-8 string.
    /// </para>
    /// </summary>
    /// <param name="configPath">Path to <c>res/app_config.dat</c>.</param>
    /// <returns>The decrypted JSON text, or an empty string when the input is too short.</returns>
    public static string DecryptAppConfig(string configPath)
    {
        byte[] raw = File.ReadAllBytes(configPath);
        return DecryptAppConfig(raw);
    }

    /// <summary>
    /// Port of <c>cutils.extApi1</c> operating on an in-memory buffer.
    /// </summary>
    public static string DecryptAppConfig(ReadOnlySpan<byte> raw)
    {
        // The native implementation returns an empty string for anything <= 16 bytes.
        if (raw.Length <= 16)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> iv = raw[..16];
        ReadOnlySpan<byte> cipherText = raw[16..];

        byte[] plain = AesCbcDecrypt(AppConfigKeyBytes, iv, cipherText);
        return Encoding.UTF8.GetString(plain);
    }

    #endregion

    #region extApi2 - signing context initialisation

    /// <summary>
    /// Port of <c>cutils.extApi2(secretKey, appId, channelCode)</c>.
    /// <para>
    /// The native method performs no cryptography at all: it simply stores the three values
    /// into process-wide statics that <c>extApi3</c> later consumes. Because the C# port passes the
    /// values explicitly to <see cref="ComputeSignature"/>, this method only validates them and
    /// mirrors the native <c>true</c> return value.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when all three values are non-empty, matching the native behaviour.</returns>
    public static bool InitializeSigningContext(string secretKey, string appId, string channelCode)
        => !string.IsNullOrEmpty(secretKey)
           && !string.IsNullOrEmpty(appId)
           && !string.IsNullOrEmpty(channelCode);

    #endregion

    #region extApi3 - request signing

    /// <summary>
    /// Port of <c>cutils.extApi3(payload)</c>.
    /// <para>
    /// <c>sign = lowercase_hex(MD5(payload + appId + channelCode + secretKey))</c>
    /// </para>
    /// <para>
    /// The concatenation order is significant and was confirmed empirically: the payload comes
    /// <b>first</b>, followed by appId, channelCode and secretKey.
    /// </para>
    /// </summary>
    public static string ComputeSignature(string payload, string appId, string channelCode, string secretKey)
    {
        string concatenated = payload + appId + channelCode + secretKey;
        return Md5Hex(concatenated);
    }

    #endregion

    #region extApi4 - password encryption

    /// <summary>
    /// Port of <c>cutils.extApi4(password, pwdKey[, iv])</c>.
    /// <para>
    /// <c>Base64( ASCII(Base64(AES-256-CBC(md5hex(UTF8(password)), key, iv))) + rawIV )</c>
    /// </para>
    /// <list type="bullet">
    /// <item>plaintext is the 32-character lowercase MD5 hex of the UTF-8 password;</item>
    /// <item>key is <paramref name="pwdKey"/> truncated to, or zero-padded up to, 32 bytes;</item>
    /// <item>IV is 16 bytes: caller-supplied, or 16 random <c>[A-Za-z0-9]</c> characters;</item>
    /// <item>the raw IV is appended to the inner Base64 text, then the whole thing is Base64-encoded again.</item>
    /// </list>
    /// <para>The result is always 108 characters long, because the plaintext is always 32 bytes.</para>
    /// </summary>
    /// <param name="password">Plaintext password, used as UTF-8 (non-ASCII passwords are supported).</param>
    /// <param name="pwdKey">
    /// Server-provided <c>encrypt.password.pwd</c> key, taken from <c>launcherInitConfig.common</c>.
    /// </param>
    /// <param name="iv">Optional 16-byte IV. A random alphanumeric one is generated when omitted.</param>
    public static string EncryptPassword(string password, string pwdKey, byte[]? iv = null)
    {
        byte[] plain = Encoding.ASCII.GetBytes(Md5Hex(password));

        byte[] key = new byte[32];
        byte[] keySource = Encoding.UTF8.GetBytes(pwdKey);
        Buffer.BlockCopy(keySource, 0, key, 0, Math.Min(keySource.Length, 32));

        iv ??= RandomAlphanumericIv(16);

        byte[] cipherText = AesCbcEncrypt(key, iv, plain);
        byte[] innerBase64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(cipherText));

        byte[] envelope = new byte[innerBase64.Length + iv.Length];
        Buffer.BlockCopy(innerBase64, 0, envelope, 0, innerBase64.Length);
        Buffer.BlockCopy(iv, 0, envelope, innerBase64.Length, iv.Length);

        return Convert.ToBase64String(envelope);
    }

    private static byte[] RandomAlphanumericIv(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        byte[] result = new byte[length];
        Span<byte> random = stackalloc byte[length];
        RandomNumberGenerator.Fill(random);

        for (int i = 0; i < length; i++)
        {
            result[i] = (byte)alphabet[random[i] % alphabet.Length];
        }

        return result;
    }

    #endregion

    #region Resource index cipher

    /// <summary>
    /// Decrypts a resource index file: <c>AES-192-CBC(base64_decode(fileText))</c> with the
    /// hard-coded key/IV pair, returning the CSV payload as UTF-8 text.
    /// </summary>
    /// <param name="indexText">The raw text content of the index file (Base64).</param>
    /// <exception cref="CryptographicException">Thrown when the payload cannot be decrypted.</exception>
    public static string DecryptResourceIndex(string indexText)
    {
        // The launcher feeds the file text straight into Buffer.from(raw, 'base64').
        // Whitespace/newlines inside the Base64 are tolerated by both implementations.
        byte[] binary = Convert.FromBase64String(indexText.Trim());
        byte[] plain = AesCbcDecrypt(ResourceIndexKeyBytes, ResourceIndexIvBytes, binary);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// Reads and decrypts a resource index file from disk.
    /// </summary>
    public static string DecryptResourceIndexFromFile(string indexPath)
        => DecryptResourceIndex(File.ReadAllText(indexPath));

    #endregion

    #region Hashing helpers

    /// <summary>
    /// Returns the lowercase hexadecimal MD5 of the UTF-8 encoding of <paramref name="value"/>.
    /// </summary>
    public static string Md5Hex(string value)
        => Md5Hex(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Returns the lowercase hexadecimal MD5 of <paramref name="data"/>.
    /// </summary>
    public static string Md5Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes the lowercase hexadecimal MD5 of a file using streaming reads.
    /// </summary>
    /// <param name="filePath">File to hash.</param>
    /// <param name="onProgress">Optional callback receiving the number of bytes hashed so far.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<string> Md5FileAsync(string filePath, Action<long>? onProgress = null,
        CancellationToken token = default)
    {
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long total = 0;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                       .ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
                onProgress?.Invoke(total);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="Md5FileAsync"/>, used from verification hot loops.
    /// </summary>
    public static string Md5File(string filePath, Action<long>? onProgress = null,
        CancellationToken token = default)
    {
        using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long total = 0;

        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                total += read;
                onProgress?.Invoke(total);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Raw AES-CBC primitives

    /// <summary>
    /// AES-CBC encryption with PKCS#7 padding, returning the raw ciphertext (no IV prefix).
    /// </summary>
    internal static byte[] AesCbcEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> plainText)
    {
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plainText.ToArray(), 0, plainText.Length);
    }

    /// <summary>
    /// AES-CBC decryption with PKCS#7 padding.
    /// </summary>
    internal static byte[] AesCbcDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv,
        ReadOnlySpan<byte> cipherText)
    {
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText.ToArray(), 0, cipherText.Length);
    }

    #endregion
}
