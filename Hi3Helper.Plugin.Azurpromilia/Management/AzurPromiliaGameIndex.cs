using System;
using System.Collections.Generic;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.AzurPromilia.Management;

/// <summary>One entry of a decrypted resource index.</summary>
public sealed class AzurPromiliaIndexEntry
{
    /// <summary>
    /// For REALITY indexes this is the path relative to the game root. For SLICE indexes it is the
    /// fragment's MD5, which is also the name the fragment is stored under in <c>_temp</c>.
    /// </summary>
    public required string Name { get; init; }

    public long Size { get; init; }

    public string? Md5 { get; init; }

    public string? Url { get; init; }
}

/// <summary>
/// A decrypted resource index.
/// <para>
/// The file is Base64 text that decrypts to a CSV payload. Two shapes exist, selected by the
/// installation mode the server reports:
/// </para>
/// <list type="bullet">
/// <item><description><b>SLICE</b> (<c>downloadMode = 1</c>): one 3-column row describing the merged
/// <c>game.zip</c>, followed by 5-column rows describing each fragment.</description></item>
/// <item><description><b>REALITY</b> (<c>downloadMode = 2</c>): 4-column rows for directories and
/// 5-column rows for files, laid out as <c>[type, size, md5, path, url]</c>.</description></item>
/// </list>
/// </summary>
public sealed class AzurPromiliaGameIndex
{
    /// <summary>MD5 of the merged <c>game.zip</c>. Only populated for SLICE indexes.</summary>
    public string? GameZipMd5 { get; private set; }

    /// <summary>Size of the merged <c>game.zip</c>. Only populated for SLICE indexes.</summary>
    public long GameZipSize { get; private set; }

    /// <summary>Fragment entries. Only populated for SLICE indexes.</summary>
    public List<AzurPromiliaIndexEntry> Slices { get; } = new();

    /// <summary>File entries. Only populated for REALITY indexes.</summary>
    public List<AzurPromiliaIndexEntry> Files { get; } = new();

    /// <summary>Directory paths present in the index, used to prune empty directories on update.</summary>
    public List<string> Directories { get; } = new();

    /// <summary><c>true</c> when this index describes fragments rather than final files.</summary>
    public bool IsSliceIndex => Slices.Count > 0;

    /// <summary>
    /// Parses an install (SLICE) index.
    /// <para>3 columns: <c>[type, size, md5]</c> describes the merged archive.</para>
    /// <para>5 columns: <c>[type, _, size, md5, url]</c> describes one fragment; the fragment is stored
    /// under its MD5, so <c>Name</c> is taken from the MD5 column.</para>
    /// </summary>
    public static AzurPromiliaGameIndex ParseInstallIndex(string csv)
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
                        Url = fields[4]
                    });
                    break;
            }
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Install index parsed: {Slices} fragment(s), game.zip {Size} bytes, md5={Md5}",
            index.Slices.Count, index.GameZipSize, index.GameZipMd5 ?? "(none)");

        return index;
    }

    /// <summary>
    /// Parses a game (REALITY) index.
    /// <para>4 columns: <c>[type, _, _, path]</c> is a directory.</para>
    /// <para>5 columns: <c>[type, size, md5, path, url]</c> is a file.</para>
    /// </summary>
    public static AzurPromiliaGameIndex ParseGameIndex(string csv)
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
                        Url = fields[4]
                    });
                    break;
            }
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[AzurPromilia] Game index parsed: {Files} file(s), {Directories} director(ies)",
            index.Files.Count, index.Directories.Count);

        return index;
    }

    /// <summary>
    /// Splits the CSV payload into rows, dropping empty lines and trailing commas, and trimming each
    /// field. Mirrors the launcher's <c>split(/\r?\n/)</c> + <c>split(',')</c> behaviour.
    /// </summary>
    private static IEnumerable<string[]> EnumerateRows(string csv)
    {
        // NOTE: deliberately index-based rather than span-based. An iterator method cannot keep a
        // ReadOnlySpan<char> alive across a 'yield return' (CS4007) because the compiler lowers the
        // body into a state machine that has to survive on the heap.
        int position = 0;
        int length = csv.Length;

        while (position < length)
        {
            int newline = csv.IndexOf('\n', position);
            int end = newline < 0 ? length : newline;

            // Drop the carriage return of a CRLF pair (and any stray trailing CRs).
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
        => long.TryParse(value, out long size) ? size : 0;

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
