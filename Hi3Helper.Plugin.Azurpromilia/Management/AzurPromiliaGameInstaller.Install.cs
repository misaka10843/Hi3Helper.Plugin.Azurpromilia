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
    /// <summary>
    /// A single download unit: either a final game file (REALITY) or a fragment (SLICE).
    /// </summary>
    private sealed class DownloadItem
    {
        public required string Url { get; init; }

        /// <summary>Path relative to the game root; for fragments this is the fragment file name.</summary>
        public required string RelativePath { get; init; }

        /// <summary>Absolute destination path on disk.</summary>
        public required string LocalPath { get; init; }

        public long Size { get; init; }

        public string? Md5 { get; init; }
    }

    /// <summary>
    /// Drives one install / update / preload operation.
    /// </summary>
    private sealed class Install
    {
        private readonly AzurPromiliaGameInstaller _owner;
        private readonly HttpClient _client;
        private long _lastProgressTicks;
        private int _networkErrorCount;

        public Install(AzurPromiliaGameInstaller owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _client = owner.DownloadHttpClient;
        }

        private AzurPromiliaGameManager Manager => _owner.Manager;

        public async Task RunAsync(GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        {
            await _owner.InitAsync(token).ConfigureAwait(false);

            GameReleaseInfo? release = kind == GameInstallerKind.Preload
                ? Manager.Preload ?? Manager.Released
                : Manager.Released;

            if (release == null || string.IsNullOrEmpty(release.VersionCode))
            {
                throw new InvalidOperationException(
                    "No published game version is available. This usually means the account is not logged in; " +
                    "provide credentials through the plugin's account.json file.");
            }

            string installPath = _owner.EnsureAndGetGamePath();
            Directory.CreateDirectory(installPath);

            bool isFreshInstall = kind == GameInstallerKind.Install &&
                                  !File.Exists(Path.Combine(installPath, "_index",
                                      AzurPromiliaGameManager.LocalManifestFileName));

            progressStateDelegate?.Invoke(InstallProgressState.Preparing);

            // A fresh install honours the server's download mode; updates are always file-level.
            if (kind == GameInstallerKind.Install && Manager.DownloadMode == AzurPromiliaGameManager.DownloadModeSlice)
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

        #region SLICE install: fragments -> game.zip -> extract

        /// <summary>
        /// SLICE install: download every fragment into <c>_temp</c>, concatenate them into
        /// <c>game.zip</c>, verify the merged archive, extract it into the game root, then clean up.
        /// </summary>
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

            string indexTarget = Path.Combine(tempPath, release.VersionCode!);
            AzurPromiliaGameIndex index = await Manager
                .LoadIndexAsync(slicePackage, indexTarget, true, token)
                .ConfigureAwait(false);

            if (index.Slices.Count == 0 || string.IsNullOrEmpty(index.GameZipMd5))
            {
                throw new InvalidDataException("The SLICE index contained no fragments or no archive descriptor.");
            }

            string gameZipPath = Path.Combine(tempPath, "game.zip");
            bool archiveReady = false;

            // Reuse an already-merged archive when its hash still matches.
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
                // Drop any stale archive so the merge starts from a clean slate.
                if (File.Exists(gameZipPath))
                {
                    File.Delete(gameZipPath);
                }

                List<DownloadItem> fragments = new(index.Slices.Count);
                foreach (AzurPromiliaIndexEntry slice in index.Slices)
                {
                    fragments.Add(new DownloadItem
                    {
                        Url = slice.Url ?? throw new InvalidDataException(
                            $"Fragment '{slice.Name}' carries no download URL."),
                        RelativePath = slice.Name,
                        LocalPath = Path.Combine(tempPath, slice.Name),
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

            // Cache the per-file index so future updates can diff against it.
            await CacheRealityIndexAsync(release, installPath, token).ConfigureAwait(false);

            TryDeleteDirectory(tempPath);

            progressDelegate?.Invoke(default);
        }

        private static void DeleteFragments(AzurPromiliaGameIndex index, string tempPath)
        {
            foreach (AzurPromiliaIndexEntry slice in index.Slices)
            {
                string fragmentPath = Path.Combine(tempPath, slice.Name);
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

        /// <summary>
        /// Concatenates all fragments into <c>game.zip.~tmp</c> in index order, flushes to disk, then
        /// verifies the merged archive's MD5 before promoting it to <c>game.zip</c>.
        /// </summary>
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

                        string fragmentPath = Path.Combine(tempPath, slice.Name);
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

                // Verify the merged archive before it replaces game.zip.
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

        /// <summary>
        /// Extracts <c>game.zip</c> into the game root using the bundled 7-Zip wrapper.
        /// </summary>
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
            extractProgress.TotalBytesToDownload = (long)archive.Entries.Sum(e => (long)e.Size);
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

        /// <summary>
        /// Downloads the REALITY index into <c>_index/&lt;version&gt;</c> so the next update has a
        /// per-file baseline to diff against. Failures here are non-fatal.
        /// </summary>
        private async Task CacheRealityIndexAsync(GameReleaseInfo release, string installPath,
            CancellationToken token)
        {
            GamePackageInfo? package = release.GetPackage(AzurPromiliaGameManager.PackageTypeReality);
            if (package?.DownloadUrl == null)
            {
                return;
            }

            try
            {
                string targetPath = AzurPromiliaGameManager.GetIndexPath(installPath, release.VersionCode!);
                AzurPromiliaGameIndex index = await Manager
                    .LoadIndexAsync(package, targetPath, false, token)
                    .ConfigureAwait(false);

                Manager.WriteLocalManifest(installPath, index, Manager.DownloadMode);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[AzurPromiliaInstaller] Could not cache the per-file index: {Error}", ex.Message);
            }
        }

        #endregion

        #region REALITY install / update: per-file download

        /// <summary>
        /// File-level install or update driven by the REALITY index.
        /// </summary>
        private async Task RunFileInstallAsync(GameReleaseInfo release, string installPath, bool isFreshInstall,
            GameInstallerKind kind, InstallProgressDelegate? progressDelegate,
            InstallProgressStateDelegate? progressStateDelegate, CancellationToken token)
        {
            (AzurPromiliaGameIndex? index, string? indexPath) =
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
                ComputeFileDiff(index, previousManifest, installPath, excluded);

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

            if (obsolete.Count > 0 && kind == GameInstallerKind.Update)
            {
                progressStateDelegate?.Invoke(InstallProgressState.Removing);
                DeleteObsoleteFiles(obsolete, installPath);
            }

            // Post-install integrity sweep: re-hash everything and repair what fails.
            await VerifyAndRepairAsync(index, installPath, progressDelegate, progressStateDelegate, token)
                .ConfigureAwait(false);

            Manager.WriteLocalManifest(installPath, index, Manager.DownloadMode);

            if (!string.IsNullOrEmpty(indexPath) && !File.Exists(indexPath))
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[AzurPromiliaInstaller] Index cache not present at '{Path}'.", indexPath);
            }
        }

        /// <summary>
        /// Builds the set of paths the server asked to preserve across updates.
        /// </summary>
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
                    excluded.Add(entry.FilePath!.Replace('\\', '/').TrimStart('/'));
                }
            }

            return excluded;
        }

        /// <summary>
        /// Determines which files need downloading and which previously installed files became obsolete.
        /// <para>
        /// A file is downloaded when it is missing locally, when its size differs, or when its MD5 does
        /// not match. When a previous manifest exists the hash comparison is trusted first, so a file
        /// whose hash is unchanged is only re-hashed if its size changed.
        /// </para>
        /// </summary>
        private static (List<DownloadItem> ToDownload, List<string> Obsolete) ComputeFileDiff(
            AzurPromiliaGameIndex index,
            AzurPromiliaLocalManifest? previousManifest,
            string installPath,
            HashSet<string> excluded)
        {
            Dictionary<string, AzurPromiliaLocalManifestFile> previousByPath =
                new(StringComparer.OrdinalIgnoreCase);

            if (previousManifest?.Files != null)
            {
                foreach (AzurPromiliaLocalManifestFile file in previousManifest.Files)
                {
                    previousByPath[file.Path] = file;
                }
            }

            List<DownloadItem> toDownload = new();
            HashSet<string> currentPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (AzurPromiliaIndexEntry entry in index.Files)
            {
                string relative = entry.Name.Replace('\\', '/').TrimStart('/');
                currentPaths.Add(relative);

                string localPath = Path.Combine(installPath, relative.Replace('/', Path.DirectorySeparatorChar));
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
                            string localMd5 = AzurPromiliaCrypto.Md5File(localPath);
                            needsDownload = !string.Equals(localMd5, entry.Md5, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                if (!needsDownload)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Url))
                {
                    throw new InvalidDataException($"Index entry '{entry.Name}' carries no download URL.");
                }

                toDownload.Add(new DownloadItem
                {
                    Url = entry.Url!,
                    RelativePath = relative,
                    LocalPath = localPath,
                    Size = entry.Size,
                    Md5 = entry.Md5
                });
            }

            List<string> obsolete = new();
            foreach (KeyValuePair<string, AzurPromiliaLocalManifestFile> previous in previousByPath)
            {
                if (currentPaths.Contains(previous.Key) || excluded.Contains(previous.Key))
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
                string localPath = Path.Combine(installPath, relative.Replace('/', Path.DirectorySeparatorChar));

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

        /// <summary>
        /// Re-hashes every indexed file and re-downloads the ones that fail, once.
        /// </summary>
        private async Task VerifyAndRepairAsync(AzurPromiliaGameIndex index, string installPath,
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
                    string relative = entry.Name.Replace('\\', '/').TrimStart('/');
                    string localPath = Path.Combine(installPath, relative.Replace('/', Path.DirectorySeparatorChar));
                    bool ok = false;

                    if (File.Exists(localPath) && new FileInfo(localPath).Length == entry.Size)
                    {
                        string localMd5 = await Task.Run(() => AzurPromiliaCrypto.Md5File(localPath, null, innerToken),
                            innerToken).ConfigureAwait(false);
                        ok = string.IsNullOrEmpty(entry.Md5) ||
                             string.Equals(localMd5, entry.Md5, StringComparison.OrdinalIgnoreCase);
                    }

                    if (!ok && !string.IsNullOrEmpty(entry.Url))
                    {
                        broken.Add(new DownloadItem
                        {
                            Url = entry.Url!,
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

        #endregion

        #region Download engine

        /// <summary>
        /// Downloads a batch of items with bounded concurrency, resumable transfers and per-file MD5
        /// verification. Concurrency degrades from 4 to 2 to 1 as network errors accumulate, matching
        /// the official launcher's behaviour.
        /// </summary>
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

            ConcurrentQueue<DownloadItem> queue = new(items);
            int workerCount = Math.Min(items.Count, DefaultConcurrency);
            Task[] workers = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workers[i] = Task.Run(() => WorkerAsync(), token);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            progress.StateCount = 1;
            progressDelegate?.Invoke(in progress);

            return;

            async Task WorkerAsync()
            {
                while (!token.IsCancellationRequested)
                {
                    if (!queue.TryDequeue(out DownloadItem? item))
                    {
                        return;
                    }

                    bool ok = await DownloadItemAsync(item, bytesDelta =>
                    {
                        Interlocked.Add(ref progress.DownloadedBytes, bytesDelta);
                        ReportProgress(progressDelegate, in progress);
                    }, token).ConfigureAwait(false);

                    if (ok)
                    {
                        Interlocked.Increment(ref progress.DownloadedCount);
                        ReportProgress(progressDelegate, in progress);
                        continue;
                    }

                    // Network-class failure: shed a worker and let the remaining ones retry the item.
                    int remaining = Interlocked.Increment(ref _networkErrorCount);
                    if (remaining < DefaultConcurrency && workerCount > MinimumConcurrency)
                    {
                        queue.Enqueue(item);
                        SharedStatic.InstanceLogger.LogWarning(
                            "[AzurPromiliaInstaller] Network error on '{Path}'; reducing download concurrency.",
                            item.RelativePath);
                        return;
                    }

                    throw new IOException(
                        $"Failed to download '{item.RelativePath}' after {MaxFileRetries} attempts.");
                }
            }
        }

        /// <summary>
        /// Downloads one item with resumable HTTP range requests and MD5 verification.
        /// </summary>
        /// <returns><c>true</c> on success; <c>false</c> for a retryable network failure.</returns>
        private async Task<bool> DownloadItemAsync(DownloadItem item, Action<long> reportDelta,
            CancellationToken token)
        {
            string? directory = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = item.LocalPath + TempFileSuffix;

            // Already correct? Skip the transfer entirely.
            if (File.Exists(item.LocalPath) && new FileInfo(item.LocalPath).Length == item.Size &&
                await IsHashValidAsync(item.LocalPath, item.Md5, token).ConfigureAwait(false))
            {
                return true;
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
                        // Oversized temp file: start over.
                        File.Delete(tempPath);
                        resumeOffset = 0;
                    }
                    else if (resumeOffset == item.Size)
                    {
                        // Fully downloaded already; just verify.
                        if (await IsHashValidAsync(tempPath, item.Md5, token).ConfigureAwait(false))
                        {
                            PromoteTempFile(tempPath, item.LocalPath);
                            return true;
                        }

                        File.Delete(tempPath);
                        resumeOffset = 0;
                    }
                }

                try
                {
                    using HttpRequestMessage request = new(HttpMethod.Get, item.Url);
                    if (resumeOffset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                    }

                    using HttpResponseMessage response = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);

                    // The server ignored the range: restart from zero.
                    if (resumeOffset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        resumeOffset = 0;
                        reportDelta(-new FileInfo(tempPath).Length);
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }

                    response.EnsureSuccessStatusCode();

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

                    if (!await IsHashValidAsync(tempPath, item.Md5, token).ConfigureAwait(false))
                    {
                        SharedStatic.InstanceLogger.LogWarning(
                            "[AzurPromiliaInstaller] Hash mismatch for '{Path}' (attempt {Attempt}); retrying.",
                            item.RelativePath, attempt + 1);

                        long discarded = new FileInfo(tempPath).Length;
                        File.Delete(tempPath);
                        reportDelta(-discarded);
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

                    // Brief backoff before resuming; the partial file is preserved.
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

        private static void PromoteTempFile(string tempPath, string finalPath)
        {
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
        }

        /// <summary>
        /// Throttles progress callbacks to at most ten per second so the launcher UI is not flooded.
        /// </summary>
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

        #endregion

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
