using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.AzurPromilia.Management.Api;
using Hi3Helper.Plugin.AzurPromilia.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;
using SevenZipExtractor;

namespace Hi3Helper.Plugin.AzurPromilia.Management;

internal partial class AzurPromiliaGameInstaller
{
    private sealed class DownloadItem
    {
        public required IReadOnlyList<string> Urls { get; init; }
        public required string RelativePath { get; init; }
        public required string LocalPath { get; init; }

        public long Size { get; init; }

        public string? Md5 { get; init; }
    }

    private sealed class Install
    {
        private readonly AzurPromiliaGameInstaller _owner;
        private readonly HttpClient _client;
        private long _lastProgressTicks;

        public Install(AzurPromiliaGameInstaller owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _client = owner.DownloadHttpClient;
        }

        private AzurPromiliaGameManager Manager => _owner.Manager;

        public async Task<(long Total, long Downloaded)> GetPreloadSizesAsync(GameReleaseInfo release,
            CancellationToken token)
        {
            Manager.GetGamePath(out string? installPath);
            AzurPromiliaGameIndex? index = await _owner.FetchRealityIndexAsync(release, token)
                .ConfigureAwait(false);
            if (index == null || string.IsNullOrEmpty(installPath))
            {
                return default;
            }

            string stage = AzurPromiliaGameManager.GetPreloadPath(installPath, release.VersionCode!);
            (List<DownloadItem> items, _) = ComputeFileDiff(index, Manager.ReadLocalManifest(installPath),
                installPath, BuildExclusionSet(release), token);
            long total = 0;
            long downloaded = 0;
            foreach (DownloadItem item in items)
            {
                total += item.Size;
                string path = GetContentPath(stage, item.RelativePath);
                if (File.Exists(path) && new FileInfo(path).Length == item.Size)
                {
                    downloaded += item.Size;
                }
                else if (File.Exists(path + TempFileSuffix))
                {
                    downloaded += Math.Min(item.Size, new FileInfo(path + TempFileSuffix).Length);
                }
            }

            return (total, downloaded);
        }

        public async Task RunAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        {
            if (await _owner.InitAsync(token).ConfigureAwait(false) != 0)
            {
                throw new InvalidOperationException("Could not refresh the game's published packages.");
            }

            GameReleaseInfo? release = kind == GameInstallerKind.Preload
                ? Manager.Preload
                : Manager.Released;

            if (release == null || string.IsNullOrEmpty(release.VersionCode))
            {
                throw new InvalidOperationException(
                    "No published game version is available. Check the launcher API response and try again later.");
            }

            string installPath = _owner.EnsureAndGetGamePath();
            Directory.CreateDirectory(installPath);

            Manager.IsGameInstalled(out bool installed);
            bool isFreshInstall = kind == GameInstallerKind.Install && !installed;
            if (kind == GameInstallerKind.Install && installed)
            {
                kind = GameInstallerKind.Update;
            }

            progressStateDelegate?.Invoke(InstallProgressState.Preparing);

            // A fresh install honours the server's download mode; updates are always file-level.
            if (isFreshInstall && Manager.DownloadMode == AzurPromiliaGameManager.DownloadModeSlice &&
                release.GetPackage(AzurPromiliaGameManager.PackageTypeSlice) != null &&
                !Directory.Exists(AzurPromiliaGameManager.GetPreloadPath(installPath, release.VersionCode)))
            {
                await RunSliceInstallAsync(release, installPath, progressDelegate, progressStateDelegate, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await RunFileInstallAsync(release, installPath, isFreshInstall, kind, progressDelegate,
                    progressStateDelegate, token).ConfigureAwait(false);
            }

            progressStateDelegate?.Invoke(InstallProgressState.Completed);
            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] {Kind} finished for version {Version}.", kind, release.VersionCode);
        }

        private async Task RunSliceInstallAsync(GameReleaseInfo release, string installPath,
            InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate,
            CancellationToken token)
        {
            GamePackageInfo? slicePackage = release.GetPackage(AzurPromiliaGameManager.PackageTypeSlice);
            if (slicePackage?.DownloadUrl == null)
            {
                throw new InvalidOperationException(
                    "The server requested a fragmented install but published no SLICE package.");
            }

            string tempPath = AzurPromiliaGameManager.GetSliceTempPath(installPath);
            Directory.CreateDirectory(tempPath);

            string indexTarget = GetContentPath(tempPath, release.VersionCode!);
            AzurPromiliaGameIndex index = await Manager
                .LoadIndexAsync(slicePackage, indexTarget, true, token)
                .ConfigureAwait(false);

            if (index.Slices.Count == 0 || string.IsNullOrEmpty(index.GameZipMd5))
            {
                throw new InvalidDataException("The SLICE index contained no fragments or no archive descriptor.");
            }

            string gameZipPath = Path.Combine(tempPath, "game.zip");
            bool archiveReady = false;

            if (File.Exists(gameZipPath) && new FileInfo(gameZipPath).Length == index.GameZipSize)
            {
                progressStateDelegate?.Invoke(InstallProgressState.Verify);
                string existingMd5 = await AzurPromiliaCrypto.Md5FileAsync(gameZipPath, null, token)
                    .ConfigureAwait(false);
                archiveReady = string.Equals(existingMd5, index.GameZipMd5, StringComparison.OrdinalIgnoreCase);

                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromiliaInstaller] Existing game.zip reuse check: {Result}", archiveReady);
            }

            if (!archiveReady)
            {
                if (File.Exists(gameZipPath))
                {
                    File.Delete(gameZipPath);
                }

                List<DownloadItem> fragments = new(index.Slices.Count);
                foreach (AzurPromiliaIndexEntry slice in index.Slices)
                {
                    fragments.Add(new DownloadItem
                    {
                        Urls = GetDownloadUrls(slice),
                        RelativePath = slice.Name,
                        LocalPath = GetContentPath(tempPath, slice.Name),
                        Size = slice.Size,
                        Md5 = slice.Md5
                    });
                }

                await DownloadFilesAsync(fragments, progressDelegate, progressStateDelegate, token)
                    .ConfigureAwait(false);

                progressStateDelegate?.Invoke(InstallProgressState.Install);
                await MergeFragmentsAsync(index, tempPath, gameZipPath, progressDelegate, token)
                    .ConfigureAwait(false);

                DeleteFragments(index, tempPath);
            }

            progressStateDelegate?.Invoke(InstallProgressState.Install);
            await ExtractArchiveAsync(gameZipPath, installPath, progressDelegate, token).ConfigureAwait(false);

            await CacheRealityIndexAsync(release, installPath, token).ConfigureAwait(false);

            TryDeleteDirectory(tempPath);

            progressDelegate?.Invoke(default);
        }

        private static void DeleteFragments(AzurPromiliaGameIndex index, string tempPath)
        {
            foreach (AzurPromiliaIndexEntry slice in index.Slices)
            {
                string fragmentPath = GetContentPath(tempPath, slice.Name);
                try
                {
                    if (File.Exists(fragmentPath))
                    {
                        File.Delete(fragmentPath);
                    }
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[AzurPromiliaInstaller] Could not delete fragment '{Path}': {Error}", fragmentPath,
                        ex.Message);
                }
            }
        }
        private static async Task MergeFragmentsAsync(AzurPromiliaGameIndex index, string tempPath,
            string gameZipPath, InstallProgressDelegate? progressDelegate, CancellationToken token)
        {
            const int highWaterMark = 4 * 1024 * 1024;

            string tempZipPath = gameZipPath + TempFileSuffix;
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            InstallProgress mergeProgress = default;
            mergeProgress.TotalBytesToDownload = index.GameZipSize;
            mergeProgress.TotalStateToComplete = 1;
            mergeProgress.TotalCountToDownload = 1;
            progressDelegate?.Invoke(in mergeProgress);

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] Merging {Count} fragment(s) into '{Path}'",
                index.Slices.Count, gameZipPath);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(highWaterMark);
            long written = 0;

            try
            {
                await using (FileStream output = new(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                 highWaterMark, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    foreach (AzurPromiliaIndexEntry slice in index.Slices)
                    {
                        token.ThrowIfCancellationRequested();

                        string fragmentPath = GetContentPath(tempPath, slice.Name);
                        if (!File.Exists(fragmentPath))
                        {
                            throw new FileNotFoundException(
                                $"Fragment '{slice.Name}' is missing after download.", fragmentPath);
                        }

                        await using FileStream input = new(fragmentPath, FileMode.Open, FileAccess.Read,
                            FileShare.Read, highWaterMark, FileOptions.Asynchronous | FileOptions.SequentialScan);

                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            written += read;

                            mergeProgress.DownloadedBytes = written;
                            progressDelegate?.Invoke(in mergeProgress);
                        }
                    }

                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(true); // force the data out of the OS cache before hashing
                }

                string mergedMd5 = await AzurPromiliaCrypto.Md5FileAsync(tempZipPath, null, token)
                    .ConfigureAwait(false);

                if (!string.Equals(mergedMd5, index.GameZipMd5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempZipPath);
                    throw new InvalidDataException(
                        $"Merged archive MD5 mismatch. Expected {index.GameZipMd5}, got {mergedMd5}. " +
                        "The fragments were discarded; retry the install.");
                }

                if (File.Exists(gameZipPath))
                {
                    File.Delete(gameZipPath);
                }

                File.Move(tempZipPath, gameZipPath);

                mergeProgress.DownloadedBytes = index.GameZipSize;
                mergeProgress.StateCount = 1;
                progressDelegate?.Invoke(in mergeProgress);

                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromiliaInstaller] Merge verified. game.zip is {Size} bytes.", written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        private static async Task ExtractArchiveAsync(string archivePath, string targetPath,
            InstallProgressDelegate? progressDelegate, CancellationToken token)
        {
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException("The merged game archive is missing.", archivePath);
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] Extracting '{Archive}' into '{Target}'", archivePath, targetPath);

            Directory.CreateDirectory(targetPath);

            using ArchiveFile archive = new(archivePath);

            InstallProgress extractProgress = default;
            extractProgress.TotalStateToComplete = 1;
            extractProgress.TotalCountToDownload = archive.Count;
            foreach (Entry entry in archive.Entries)
            {
                string name = entry.FileName ?? throw new InvalidDataException("Archive entry has no name.");
                _ = GetContentPath(targetPath, name.TrimEnd('/', '\\'));
                extractProgress.TotalBytesToDownload += checked((long)entry.Size);
            }
            progressDelegate?.Invoke(in extractProgress);

            archive.ExtractProgress += (_, args) =>
            {
                extractProgress.DownloadedCount = args.Count;
                extractProgress.DownloadedBytes = (long)args.TotalRead;
                progressDelegate?.Invoke(in extractProgress);
            };

            await archive.ExtractAsync(targetPath, true, token).ConfigureAwait(false);

            extractProgress.DownloadedCount = archive.Count;
            extractProgress.StateCount = 1;
            progressDelegate?.Invoke(in extractProgress);
        }
        private async Task CacheRealityIndexAsync(GameReleaseInfo release, string installPath,
            CancellationToken token)
        {
            GamePackageInfo? package = release.GetPackage(AzurPromiliaGameManager.PackageTypeReality);
            if (package?.DownloadUrl == null)
            {
                throw new InvalidDataException("The server published no per-file index for this version.");
            }

            string targetPath = AzurPromiliaGameManager.GetIndexPath(installPath, release.VersionCode!);
            AzurPromiliaGameIndex index = await Manager.LoadIndexAsync(package, targetPath, false, token)
                .ConfigureAwait(false);
            Manager.WriteLocalManifest(installPath, release, index, token);
        }


        private async Task RunFileInstallAsync(GameReleaseInfo release, string installPath, bool isFreshInstall,
            GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        {
            AzurPromiliaGameIndex? index =
                await _owner.FetchRealityIndexAsync(release, token).ConfigureAwait(false);

            if (index == null || index.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    "The server published no per-file index for this version, so a file-level install is not possible.");
            }

            AzurPromiliaLocalManifest? previousManifest =
                isFreshInstall ? null : Manager.ReadLocalManifest(installPath);

            HashSet<string> excluded = BuildExclusionSet(release);

            progressStateDelegate?.Invoke(InstallProgressState.Verify);

            (List<DownloadItem> toDownload, List<string> obsolete) =
                ComputeFileDiff(index, previousManifest, installPath, excluded, token);

            string stage = AzurPromiliaGameManager.GetPreloadPath(installPath, release.VersionCode!);
            if (kind == GameInstallerKind.Preload)
            {
                List<DownloadItem> staged = new(toDownload.Count);
                foreach (DownloadItem item in toDownload)
                {
                    staged.Add(new DownloadItem
                    {
                        Urls = item.Urls,
                        RelativePath = item.RelativePath,
                        LocalPath = GetContentPath(stage, item.RelativePath),
                        Size = item.Size,
                        Md5 = item.Md5
                    });
                }

                await DownloadFilesAsync(staged, progressDelegate, progressStateDelegate, token)
                    .ConfigureAwait(false);
                return;
            }

            if (Directory.Exists(stage))
            {
                foreach (DownloadItem item in toDownload)
                {
                    string source = GetContentPath(stage, item.RelativePath);
                    if (!string.IsNullOrEmpty(item.Md5) && File.Exists(source) &&
                        new FileInfo(source).Length == item.Size &&
                        await IsHashValidAsync(source, item.Md5, token).ConfigureAwait(false))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                        File.Move(source, item.LocalPath, true);
                    }
                }
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] File diff: {Download} to download, {Obsolete} obsolete, mode={Mode}",
                toDownload.Count, obsolete.Count, Manager.DownloadMode);

            if (toDownload.Count > 0)
            {
                await DownloadFilesAsync(toDownload, progressDelegate, progressStateDelegate, token)
                    .ConfigureAwait(false);
            }
            else
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromiliaInstaller] Every file already matches the index; nothing to download.");
            }

            HashSet<string> verified = new(toDownload.Select(item => item.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            await VerifyAndRepairAsync(index, installPath, verified, progressDelegate, progressStateDelegate, token)
                .ConfigureAwait(false);

            if (obsolete.Count > 0 && kind == GameInstallerKind.Update)
            {
                progressStateDelegate?.Invoke(InstallProgressState.Removing);
                DeleteObsoleteFiles(obsolete, installPath);
            }

            Manager.WriteLocalManifest(installPath, release, index, token);

            TryDeleteDirectory(stage);
        }
        private static HashSet<string> BuildExclusionSet(GameReleaseInfo release)
        {
            HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);

            if (release.GameExcludedFileList == null)
            {
                return excluded;
            }

            foreach (GameExcludedFile entry in release.GameExcludedFileList)
            {
                if (!string.IsNullOrEmpty(entry.FilePath))
                {
                    string path = entry.FilePath.Replace('\\', '/').Trim('/');
                    excluded.Add(entry.FileType == 1 ? path + "/" : path);
                }
            }

            return excluded;
        }
        private static (List<DownloadItem> ToDownload, List<string> Obsolete) ComputeFileDiff(
            AzurPromiliaGameIndex index,
            AzurPromiliaLocalManifest? previousManifest,
            string installPath,
            HashSet<string> excluded,
            CancellationToken token)
        {
            Dictionary<string, AzurPromiliaLocalManifestFile> previousByPath =
                new(StringComparer.OrdinalIgnoreCase);

            if (previousManifest?.Files != null)
            {
                foreach (AzurPromiliaLocalManifestFile file in previousManifest.Files)
                {
                    previousByPath[NormalizeIndexPath(file.Path)] = file;
                }
            }

            List<DownloadItem> toDownload = new();
            HashSet<string> currentPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (AzurPromiliaIndexEntry entry in index.Files)
            {
                token.ThrowIfCancellationRequested();
                string relative = NormalizeIndexPath(entry.Name);
                if (!currentPaths.Add(relative))
                {
                    throw new InvalidDataException($"Duplicate file '{relative}' in the index.");
                }

                string localPath = GetContentPath(installPath, relative);
                bool needsDownload;

                if (!File.Exists(localPath))
                {
                    needsDownload = true;
                }
                else
                {
                    long localSize = new FileInfo(localPath).Length;
                    needsDownload = localSize != entry.Size;

                    // Size matches: trust the previous manifest hash when it agrees with the index,
                    // otherwise fall back to hashing the file on disk.
                    if (!needsDownload && !string.IsNullOrEmpty(entry.Md5))
                    {
                        if (previousByPath.TryGetValue(relative, out AzurPromiliaLocalManifestFile? previous) &&
                            string.Equals(previous.Md5, entry.Md5, StringComparison.OrdinalIgnoreCase))
                        {
                            needsDownload = false;
                        }
                        else
                        {
                            string localMd5 = AzurPromiliaCrypto.Md5File(localPath, null, token);
                            needsDownload = !string.Equals(localMd5, entry.Md5, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (!needsDownload)
                {
                    continue;
                }

                toDownload.Add(new DownloadItem
                {
                    Urls = GetDownloadUrls(entry),
                    RelativePath = relative,
                    LocalPath = localPath,
                    Size = entry.Size,
                    Md5 = entry.Md5
                });
            }

            List<string> obsolete = new();
            foreach (KeyValuePair<string, AzurPromiliaLocalManifestFile> previous in previousByPath)
            {
                if (currentPaths.Contains(previous.Key) || excluded.Contains(previous.Key) ||
                    excluded.Any(path => path.EndsWith('/') &&
                                         previous.Key.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                obsolete.Add(previous.Key);
            }

            return (toDownload, obsolete);
        }

        private static void DeleteObsoleteFiles(List<string> obsolete, string installPath)
        {
            foreach (string relative in obsolete)
            {
                string localPath = GetContentPath(installPath, relative);

                try
                {
                    if (File.Exists(localPath))
                    {
                        File.Delete(localPath);
                        SharedStatic.InstanceLogger.LogInformation(
                            "[AzurPromiliaInstaller] Removed obsolete file: {Path}", relative);
                    }
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[AzurPromiliaInstaller] Could not remove '{Path}': {Error}", relative, ex.Message);
                }
            }
        }
        private async Task VerifyAndRepairAsync(AzurPromiliaGameIndex index, string installPath,
            HashSet<string> verified,
            InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate,
            CancellationToken token)
        {
            progressStateDelegate?.Invoke(InstallProgressState.Verify);

            SharedStatic.InstanceLogger.LogInformation(
                "[AzurPromiliaInstaller] Verifying {Count} file(s)...", index.Files.Count);

            InstallProgress verifyProgress = default;
            verifyProgress.TotalCountToDownload = index.Files.Count;
            verifyProgress.TotalStateToComplete = 1;

            long totalBytes = 0;
            foreach (AzurPromiliaIndexEntry entry in index.Files)
            {
                totalBytes += entry.Size;
            }

            verifyProgress.TotalBytesToDownload = totalBytes;

            ConcurrentBag<DownloadItem> broken = new();

            await Parallel.ForEachAsync(index.Files,
                new ParallelOptions { MaxDegreeOfParallelism = DefaultConcurrency, CancellationToken = token },
                async (entry, innerToken) =>
                {
                    string relative = NormalizeIndexPath(entry.Name);
                    string localPath = GetContentPath(installPath, relative);
                    bool ok = false;

                    if (verified.Contains(relative))
                    {
                        ok = true;
                    }
                    else if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size)
                    {
                        ok = await IsHashValidAsync(localPath, entry.Md5, innerToken).ConfigureAwait(false);
                    }

                    if (!ok)
                    {
                        broken.Add(new DownloadItem
                        {
                            Urls = GetDownloadUrls(entry),
                            RelativePath = relative,
                            LocalPath = localPath,
                            Size = entry.Size,
                            Md5 = entry.Md5
                        });
                    }

                    Interlocked.Add(ref verifyProgress.DownloadedBytes, entry.Size);
                    Interlocked.Increment(ref verifyProgress.DownloadedCount);
                    ReportProgress(progressDelegate, in verifyProgress);
                }).ConfigureAwait(false);

            List<DownloadItem> brokenList = broken.ToList();
            if (brokenList.Count == 0)
            {
                SharedStatic.InstanceLogger.LogInformation(
                    "[AzurPromiliaInstaller] Integrity check passed for all files.");
                verifyProgress.StateCount = 1;
                progressDelegate?.Invoke(in verifyProgress);
                return;
            }

            SharedStatic.InstanceLogger.LogWarning(
                "[AzurPromiliaInstaller] {Count} file(s) failed the integrity check; repairing.",
                brokenList.Count);

            await DownloadFilesAsync(brokenList, progressDelegate, progressStateDelegate, token)
                .ConfigureAwait(false);

            verifyProgress.StateCount = 1;
            progressDelegate?.Invoke(in verifyProgress);
        }


        private async Task DownloadFilesAsync(List<DownloadItem> items,
            InstallProgressDelegate? progressDelegate, InstallProgressStateDelegate? progressStateDelegate,
            CancellationToken token)
        {
            if (items.Count == 0)
            {
                return;
            }

            InstallProgress progress = default;
            progress.TotalCountToDownload = items.Count;
            progress.TotalStateToComplete = 1;

            long total = 0;
            foreach (DownloadItem item in items)
            {
                total += item.Size;
            }

            progress.TotalBytesToDownload = total;
            progressDelegate?.Invoke(in progress);
            progressStateDelegate?.Invoke(InstallProgressState.Download);

            await Parallel.ForEachAsync(items,
                new ParallelOptions { MaxDegreeOfParallelism = DefaultConcurrency, CancellationToken = token },
                async (item, innerToken) =>
                {
                    bool ok = await DownloadItemAsync(item, bytesDelta =>
                    {
                        Interlocked.Add(ref progress.DownloadedBytes, bytesDelta);
                        ReportProgress(progressDelegate, in progress);
                    }, innerToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        throw new IOException(
                            $"Failed to download '{item.RelativePath}' after {MaxFileRetries} attempts.");
                    }

                    Interlocked.Increment(ref progress.DownloadedCount);
                    ReportProgress(progressDelegate, in progress);
                }).ConfigureAwait(false);

            progress.StateCount = 1;
            progressDelegate?.Invoke(in progress);
        }
        private async Task<bool> DownloadItemAsync(DownloadItem item, Action<long> reportDelta,
            CancellationToken token)
        {
            string? directory = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = item.LocalPath + TempFileSuffix;

            if (File.Exists(item.LocalPath) && new FileInfo(item.LocalPath).Length == item.Size &&
                await IsHashValidAsync(item.LocalPath, item.Md5, token).ConfigureAwait(false))
            {
                reportDelta(item.Size);
                return true;
            }

            if (File.Exists(tempPath))
            {
                reportDelta(new FileInfo(tempPath).Length);
            }

            for (int attempt = 0; attempt < MaxFileRetries; attempt++)
            {
                token.ThrowIfCancellationRequested();

                long resumeOffset = 0;
                if (File.Exists(tempPath))
                {
                    resumeOffset = new FileInfo(tempPath).Length;

                    if (resumeOffset > item.Size)
                    {
                        File.Delete(tempPath);
                        reportDelta(-resumeOffset);
                        resumeOffset = 0;
                    }
                    else if (resumeOffset == item.Size)
                    {
                        if (await IsHashValidAsync(tempPath, item.Md5, token).ConfigureAwait(false))
                        {
                            PromoteTempFile(tempPath, item.LocalPath);
                            return true;
                        }

                        File.Delete(tempPath);
                        reportDelta(-resumeOffset);
                        resumeOffset = 0;
                    }
                }

                try
                {
                    string url = item.Urls[attempt % item.Urls.Count];
                    using HttpRequestMessage request = new(HttpMethod.Get, url);
                    if (resumeOffset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                    }

                    using HttpResponseMessage response = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent &&
                        response.Content.Headers.ContentRange?.From != resumeOffset)
                    {
                        throw new IOException($"Unexpected download range for '{item.RelativePath}'.");
                    }

                    if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.OK)
                    {
                        File.Delete(tempPath);
                        reportDelta(-resumeOffset);
                        resumeOffset = 0;
                    }

                    await using Stream source = await response.Content.ReadAsStreamAsync(token)
                        .ConfigureAwait(false);

                    await using FileStream destination = new(tempPath,
                        resumeOffset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                        1 << 17, FileOptions.Asynchronous);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 17);
                    try
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            reportDelta(read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    await destination.FlushAsync(token).ConfigureAwait(false);
                    await destination.DisposeAsync().ConfigureAwait(false);

                    long received = new FileInfo(tempPath).Length;
                    if (received < item.Size)
                    {
                        continue;
                    }

                    if (received != item.Size ||
                        !await IsHashValidAsync(tempPath, item.Md5, token).ConfigureAwait(false))
                    {
                        SharedStatic.InstanceLogger.LogWarning(
                            "[AzurPromiliaInstaller] Hash mismatch for '{Path}' (attempt {Attempt}); retrying.",
                            item.RelativePath, attempt + 1);

                        File.Delete(tempPath);
                        reportDelta(-received);
                        continue;
                    }

                    PromoteTempFile(tempPath, item.LocalPath);
                    return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[AzurPromiliaInstaller] Download of '{Path}' failed (attempt {Attempt}): {Error}",
                        item.RelativePath, attempt + 1, ex.Message);

                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), token).ConfigureAwait(false);
                }
            }

            return false;
        }

        private static async Task<bool> IsHashValidAsync(string path, string? expectedMd5, CancellationToken token)
        {
            if (string.IsNullOrEmpty(expectedMd5))
            {
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            string actual = await AzurPromiliaCrypto.Md5FileAsync(path, null, token).ConfigureAwait(false);
            return string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> GetDownloadUrls(AzurPromiliaIndexEntry entry)
            => entry.Urls.Count > 0
                ? entry.Urls
                : throw new InvalidDataException($"Index entry '{entry.Name}' carries no download URL.");

        private static void PromoteTempFile(string tempPath, string finalPath)
            => File.Move(tempPath, finalPath, true);
        private void ReportProgress(InstallProgressDelegate? progressDelegate, in InstallProgress progress)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastProgressTicks);

            if (now - last <= Stopwatch.Frequency / 10 ||
                Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last)
            {
                return;
            }

            progressDelegate?.Invoke(in progress);
        }


        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[AzurPromiliaInstaller] Could not delete '{Path}': {Error}", path, ex.Message);
            }
        }
    }
}
