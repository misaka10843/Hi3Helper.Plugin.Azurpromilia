using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.AzurPromilia.Utils;

public static class AzurPromiliaCrypto
{
    private const string AppConfigKeyLiteral = "5f109ac9d64946e191c354d524bfe590";
    private const string ResourceIndexKeyLiteral = "mJ9dH2bR7fT1zW4eQ6cY8a==";
    private const string ResourceIndexIvLiteral = "aP3sD8fG0hJ6kL9z";

    private static readonly byte[] AppConfigKeyBytes = Encoding.ASCII.GetBytes(AppConfigKeyLiteral);
    private static readonly byte[] ResourceIndexKeyBytes = Encoding.UTF8.GetBytes(ResourceIndexKeyLiteral);
    private static readonly byte[] ResourceIndexIvBytes = Encoding.UTF8.GetBytes(ResourceIndexIvLiteral);

    public static string DecryptAppConfig(string configPath)
    {
        byte[] raw = File.ReadAllBytes(configPath);
        return DecryptAppConfig(raw);
    }

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



    public static string ComputeSignature(string payload, string appId, string channelCode, string secretKey)
    {
        string concatenated = payload + appId + channelCode + secretKey;
        return Md5Hex(concatenated);
    }



    public static string DecryptResourceIndex(string indexText)
    {
        // The launcher feeds the file text straight into Buffer.from(raw, 'base64').
        // Whitespace/newlines inside the Base64 are tolerated by both implementations.
        byte[] binary = Convert.FromBase64String(indexText.Trim());
        byte[] plain = AesCbcDecrypt(ResourceIndexKeyBytes, ResourceIndexIvBytes, binary);
        return Encoding.UTF8.GetString(plain);
    }

    public static string DecryptResourceIndexFromFile(string indexPath)
        => DecryptResourceIndex(File.ReadAllText(indexPath));


    public static string Md5Hex(string value)
        => Md5Hex(Encoding.UTF8.GetBytes(value));
    public static string Md5Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

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

}
