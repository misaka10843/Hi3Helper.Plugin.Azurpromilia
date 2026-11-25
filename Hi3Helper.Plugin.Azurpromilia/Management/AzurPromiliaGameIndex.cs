using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management;

public sealed class AzurPromiliaIndexEntry
{
    public required string Name { get; init; }

    public long Size { get; init; }

    public string? Md5 { get; init; }

    public IReadOnlyList<string> Urls { get; init; } = [];
}
public sealed class AzurPromiliaGameIndex
{
    public string? GameZipMd5 { get; private set; }
    public long GameZipSize { get; private set; }
    public List<AzurPromiliaIndexEntry> Slices { get; } = new();
    public List<AzurPromiliaIndexEntry> Files { get; } = new();
    public List<string> Directories { get; } = new();
    public static AzurPromiliaGameIndex ParseInstallIndex(string csv, GamePackageInfo? package = null)
    {
        AzurPromiliaGameIndex index = new();

        foreach (string[] fields in EnumerateRows(csv))
        {
            switch (fields.Length)
            {
                case 3:
                    // [type, size, md5] -> the merged game.zip descriptor.
                    if (long.TryParse(fields[1], out long zipSize))
                    {
                        index.GameZipSize = zipSize;
                    }

                    index.GameZipMd5 = NormalizeHash(fields[2]);
                    break;

                case 5:
                    // [type, _, size, md5, url] -> a fragment named after its own MD5.
                    index.Slices.Add(new AzurPromiliaIndexEntry
                    {
                        Name = fields[3],
                        Size = ParseSize(fields[2]),
                        Md5 = NormalizeHash(fields[3]),
                        Urls = ResolveUrls(fields[4], package)
                    });
                    break;
            }
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Install index parsed: {Slices} fragment(s), game.zip {Size} bytes, md5={Md5}",
            index.Slices.Count, index.GameZipSize, index.GameZipMd5 ?? "(none)");

        return index;
    }

    public static AzurPromiliaGameIndex ParseGameIndex(string csv, GamePackageInfo? package = null)
    {
        AzurPromiliaGameIndex index = new();

        foreach (string[] fields in EnumerateRows(csv))
        {
            switch (fields.Length)
            {
                case 4:
                    index.Directories.Add(fields[3]);
                    break;

                case 5:
                    index.Files.Add(new AzurPromiliaIndexEntry
                    {
                        Name = fields[3],
                        Size = ParseSize(fields[1]),
                        Md5 = NormalizeHash(fields[2]),
                        Urls = ResolveUrls(fields[4], package)
                    });
                    break;
            }
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Game index parsed: {Files} file(s), {Directories} director(ies)",
            index.Files.Count, index.Directories.Count);

        return index;
    }

    private static IEnumerable<string[]> EnumerateRows(string csv)
    {
        int position = 0;
        int length = csv.Length;

        while (position < length)
        {
            int newline = csv.IndexOf('\n', position);
            int end = newline < 0 ? length : newline;

            while (end > position && csv[end - 1] == '\r')
            {
                end--;
            }

            // The launcher removes a single trailing comma before splitting.
            if (end > position && csv[end - 1] == ',')
            {
                end--;
            }

            if (end > position)
            {
                string[] fields = csv.Substring(position, end - position).Split(',');
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = fields[i].Trim();
                }

                yield return fields;
            }

            if (newline < 0)
            {
                yield break;
            }

            position = newline + 1;
        }
    }

    private static long ParseSize(string value)
        => long.TryParse(value, out long size) && size >= 0
            ? size
            : throw new InvalidDataException($"Invalid index size '{value}'.");

    private static IReadOnlyList<string> ResolveUrls(string path, GamePackageInfo? package)
        => package == null ? [path] : package.ResolveUrls(path).ToArray();

    private static string? NormalizeHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }
}
