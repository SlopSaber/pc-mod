using Newtonsoft.Json;
using SongCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ScoreSaber.Core.BeatSaver {
    internal class BeatSaverService {
        private const int DownloadAttemptCount = 3;
        private const int DownloadRetryDelayMs = 750;
        private const int DownloadTimeoutSeconds = 45;
        private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(30);

        private readonly Http _http;

        internal BeatSaverService(Http http) {
            _http = http;
        }

        internal async Task<BeatSaverMap> GetMapByHash(string hash, CancellationToken cancellationToken) {
            string normalizedHash = NormalizeHash(hash);
            if (string.IsNullOrEmpty(normalizedHash)) {
                throw new ArgumentException("BeatSaver hash is required", nameof(hash));
            }

            byte[] response = await _http.GetRawBytesAsync($"https://api.beatsaver.com/maps/hash/{normalizedHash.ToLowerInvariant()}");
            cancellationToken.ThrowIfCancellationRequested();
            return await DecodeMap(response, cancellationToken);
        }

        internal async Task<BeatSaverMap> GetMapById(string id, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(id)) {
                throw new ArgumentException("BeatSaver id is required", nameof(id));
            }

            byte[] response = await _http.GetRawBytesAsync($"https://api.beatsaver.com/maps/id/{id.Trim()}");
            cancellationToken.ThrowIfCancellationRequested();
            return await DecodeMap(response, cancellationToken);
        }

        internal async Task DownloadMapByHash(string hash, BeatSaverVersion version, CancellationToken cancellationToken) {
            string normalizedHash = NormalizeHash(hash);
            if (string.IsNullOrEmpty(normalizedHash)) {
                throw new ArgumentException("BeatSaver hash is required", nameof(hash));
            }

            if (!IsBeatSaverHash(normalizedHash)) {
                throw new ArgumentException("BeatSaver hash must be a 40-character SHA1", nameof(hash));
            }

            string lowerHash = normalizedHash.ToLowerInvariant();
            string songUrl = string.IsNullOrEmpty(version?.DownloadUrl)
                ? $"https://cdn.beatsaver.com/{lowerHash}.zip"
                : version.DownloadUrl;
            string customSongsPath = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
            string customSongPath = Path.Combine(customSongsPath, lowerHash);
            string tempRootPath = Path.Combine(customSongsPath, $".ss-{lowerHash.Substring(0, 8)}-{Guid.NewGuid():N}");
            string tempSongPath = Path.Combine(tempRootPath, "song");
            string zipPath = Path.Combine(tempRootPath, $"{lowerHash}.zip");

            using (await BeatSaverPreparationWorker.EnterDestination(customSongPath)) {
                try {
                    await RunFile(new FileRequest(FileAction.Setup, customSongsPath, tempRootPath, tempSongPath, zipPath, customSongPath));
                    await DownloadAndExtractMap(BuildDownloadUrls(songUrl, lowerHash), zipPath, tempSongPath, cancellationToken);
                    await RunFile(new FileRequest(FileAction.Commit, customSongsPath, tempRootPath, tempSongPath, zipPath, customSongPath));
                } finally {
                    await RunFile(new FileRequest(FileAction.Cleanup, customSongsPath, tempRootPath, tempSongPath, zipPath, customSongPath));
                }
            }
        }

        internal BeatSaverVersion SelectVersion(BeatSaverMap map, string hash) {
            string normalizedHash = NormalizeHash(hash);
            BeatSaverVersion[] versions = map?.Versions ?? Array.Empty<BeatSaverVersion>();
            return versions.FirstOrDefault(version => string.Equals(version.Hash, normalizedHash, StringComparison.OrdinalIgnoreCase)) ??
                versions.FirstOrDefault();
        }

        internal BeatSaverDifficulty SelectDifficulty(BeatSaverVersion version, string difficulty) {
            BeatSaverDifficulty[] diffs = version?.Diffs ?? Array.Empty<BeatSaverDifficulty>();
            string normalizedDifficulty = NormalizeDifficulty(difficulty);
            return diffs.FirstOrDefault(diff => string.Equals(NormalizeDifficulty(diff.Difficulty), normalizedDifficulty, StringComparison.OrdinalIgnoreCase)) ??
                diffs.FirstOrDefault();
        }

        internal static string NormalizeHash(string hash) {
            return hash?.Trim() ?? string.Empty;
        }

        internal static string NormalizeDifficulty(string difficulty) {
            if (string.IsNullOrEmpty(difficulty)) {
                return string.Empty;
            }

            return difficulty.Replace("+", "Plus");
        }

        private async Task DownloadAndExtractMap(IReadOnlyList<string> urls, string zipPath, string tempSongPath, CancellationToken cancellationToken) {
            Exception lastError = null;

            foreach (string url in urls) {
                for (int attempt = 1; attempt <= DownloadAttemptCount; attempt++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunFile(new FileRequest(FileAction.PrepareAttempt, null, null, tempSongPath, zipPath, null));

                    try {
                        await DownloadZipToFile(url, zipPath, cancellationToken);
                        await RunFile(new FileRequest(FileAction.Extract, null, null, tempSongPath, zipPath, null, cancellationToken));
                        return;
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        lastError = ex;
                        Plugin.Log.Warn($"BeatSaver map download failed from {url} (attempt {attempt}/{DownloadAttemptCount}): {ex.Message}");
                        await RunFile(new FileRequest(FileAction.PrepareAttempt, null, null, tempSongPath, zipPath, null));

                        if (attempt >= DownloadAttemptCount || !ShouldRetry(ex)) {
                            break;
                        }

                        await Task.Delay(DownloadRetryDelayMs * attempt, cancellationToken);
                    }
                }
            }

            throw new IOException("Failed to download BeatSaver map zip", lastError);
        }

        private async Task DownloadZipToFile(string url, string zipPath, CancellationToken cancellationToken) {
            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET)) {
                foreach (var header in _http.PersistentRequestHeaders) {
                    request.SetRequestHeader(header.Key, header.Value);
                }

                request.timeout = DownloadTimeoutSeconds;
                request.downloadHandler = new DownloadHandlerFile(zipPath) {
                    removeFileOnAbort = true
                };

                AsyncOperation asyncOperation = request.SendWebRequest();
                ulong downloadedBytes = 0;
                DateTime lastProgressAt = DateTime.UtcNow;

                try {
                    while (!asyncOperation.isDone) {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (request.downloadedBytes > downloadedBytes) {
                            downloadedBytes = request.downloadedBytes;
                            lastProgressAt = DateTime.UtcNow;
                        } else if (DateTime.UtcNow - lastProgressAt > DownloadStallTimeout) {
                            request.Abort();
                            throw new BeatSaverDownloadException("download stalled", true);
                        }

                        await Task.Delay(100, cancellationToken);
                    }
                } catch (OperationCanceledException) {
                    request.Abort();
                    throw;
                }

                if (request.IsConnectionError() || request.IsProtocolError()) {
                    throw CreateDownloadException(request);
                }
            }
        }

        private static IReadOnlyList<string> BuildDownloadUrls(string songUrl, string lowerHash) {
            var urls = new List<string>();
            AddDownloadUrl(urls, songUrl);
            AddDownloadUrl(urls, $"https://cdn.beatsaver.com/{lowerHash}.zip");
            return urls;
        }

        private static void AddDownloadUrl(List<string> urls, string url) {
            if (string.IsNullOrWhiteSpace(url)) {
                return;
            }

            string trimmed = url.Trim();
            if (urls.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))) {
                return;
            }

            urls.Add(trimmed);
        }

        private static void EnsureDownloadedZip(string zipPath) {
            var zipFile = new FileInfo(zipPath);
            if (!zipFile.Exists || zipFile.Length == 0) {
                throw new BeatSaverDownloadException("downloaded zip was empty", true);
            }
        }

        private static bool IsBeatSaverHash(string hash) {
            return hash.Length == 40 && hash.All(Uri.IsHexDigit);
        }

        private static void ExtractZip(string zipPath, string tempSongPath, CancellationToken cancellationToken) {
            int extractedFiles = 0;
            string rootPath = Path.GetFullPath(tempSongPath);
            if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString())) {
                rootPath += Path.DirectorySeparatorChar;
            }

            using (ZipArchive archive = ZipFile.OpenRead(zipPath)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    cancellationToken.ThrowIfCancellationRequested();

                    string destinationPath = Path.GetFullPath(Path.Combine(tempSongPath, entry.FullName));
                    if (!destinationPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) {
                        throw new BeatSaverDownloadException("downloaded zip contained an unsafe path", false);
                    }

                    if (string.IsNullOrEmpty(entry.Name)) {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    entry.ExtractToFile(destinationPath);
                    extractedFiles++;
                }
            }

            if (extractedFiles == 0) {
                throw new BeatSaverDownloadException("downloaded zip did not contain map files", true);
            }
        }

        private static void ReplaceDirectory(string sourcePath, string destinationPath, List<string> warnings) {
            string backupPath = null;
            bool replaced = false;

            if (Directory.Exists(destinationPath)) {
                backupPath = $"{destinationPath}.{Guid.NewGuid():N}.backup";
                Directory.Move(destinationPath, backupPath);
            }

            try {
                Directory.Move(sourcePath, destinationPath);
                replaced = true;
            } catch {
                TryRestoreDirectory(backupPath, destinationPath, warnings);
                throw;
            } finally {
                if (replaced && backupPath != null) {
                    TryDeleteDirectory(backupPath, warnings);
                }
            }
        }

        private static void TryRestoreDirectory(string backupPath, string destinationPath, List<string> warnings) {
            if (backupPath == null || Directory.Exists(destinationPath) || !Directory.Exists(backupPath)) {
                return;
            }

            try {
                Directory.Move(backupPath, destinationPath);
            } catch (IOException ex) {
                warnings.Add($"Unable to restore previous BeatSaver map folder: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                warnings.Add($"Unable to restore previous BeatSaver map folder: {ex.Message}");
            }
        }

        private static bool ShouldRetry(Exception ex) {
            BeatSaverDownloadException downloadException = ex as BeatSaverDownloadException;
            if (downloadException != null) {
                return downloadException.Retryable;
            }

            return ex is IOException || ex is InvalidDataException;
        }

        private static BeatSaverDownloadException CreateDownloadException(UnityWebRequest request) {
            int statusCode = (int)request.responseCode;
            string message = !string.IsNullOrEmpty(request.error)
                ? request.error
                : statusCode > 0
                    ? $"HTTP {statusCode}"
                    : "download request failed";
            bool retryable = request.IsConnectionError() || statusCode == 0 || statusCode == 408 || statusCode == 429 || statusCode >= 500;

            return new BeatSaverDownloadException(message, retryable);
        }

        private static void TryDelete(string path, List<string> warnings) {
            try {
                File.Delete(path);
            } catch (IOException ex) {
                warnings.Add($"Unable to delete BeatSaver map zip: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                warnings.Add($"Unable to delete BeatSaver map zip: {ex.Message}");
            }
        }

        private static void TrySetHidden(string path, bool hidden, List<string> warnings) {
            try {
                FileAttributes attributes = File.GetAttributes(path);
                File.SetAttributes(path, hidden ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden);
            } catch (IOException ex) {
                warnings.Add($"Unable to update BeatSaver map temp folder attributes: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                warnings.Add($"Unable to update BeatSaver map temp folder attributes: {ex.Message}");
            }
        }

        private static void TryDeleteDirectory(string path, List<string> warnings) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, true);
                }
            } catch (IOException ex) {
                warnings.Add($"Unable to delete BeatSaver map folder: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                warnings.Add($"Unable to delete BeatSaver map folder: {ex.Message}");
            }
        }

        private static async Task<BeatSaverMap> DecodeMap(byte[] response, CancellationToken cancellationToken) {
            if (JsonConvert.DefaultSettings != null) {
                return JsonConvert.DeserializeObject<BeatSaverMap>(Encoding.UTF8.GetString(response));
            }

            BeatSaverPreparationWorker.Result<BeatSaverMap> result = await BeatSaverPreparationWorker.Decode(response, cancellationToken);
            if (result.Error != null) {
                ExceptionDispatchInfo.Capture(result.Error).Throw();
            }

            return result.Value;
        }

        private static async Task RunFile(FileRequest request) {
            BeatSaverPreparationWorker.Result<bool> result = await BeatSaverPreparationWorker.File(request);
            foreach (string warning in result.Warnings) {
                Plugin.Log.Warn(warning);
            }

            if (result.Error != null) {
                ExceptionDispatchInfo.Capture(result.Error).Throw();
            }
        }

        internal enum FileAction {
            Setup,
            PrepareAttempt,
            Extract,
            Commit,
            Cleanup
        }

        internal sealed class FileRequest {
            private readonly FileAction _action;
            private readonly string _customSongsPath;
            private readonly string _tempRootPath;
            private readonly string _tempSongPath;
            private readonly string _zipPath;
            private readonly string _destinationPath;
            private readonly CancellationToken _cancellationToken;

            internal FileRequest(FileAction action, string customSongsPath, string tempRootPath, string tempSongPath, string zipPath, string destinationPath, CancellationToken cancellationToken = default) {
                _action = action;
                _customSongsPath = customSongsPath;
                _tempRootPath = tempRootPath;
                _tempSongPath = tempSongPath;
                _zipPath = zipPath;
                _destinationPath = destinationPath;
                _cancellationToken = cancellationToken;
            }

            internal BeatSaverPreparationWorker.Result<bool> Run() {
                var warnings = new List<string>();
                Exception error = null;
                try {
                    switch (_action) {
                        case FileAction.Setup:
                            Directory.CreateDirectory(_customSongsPath);
                            Directory.CreateDirectory(_tempRootPath);
                            TrySetHidden(_tempRootPath, true, warnings);
                            break;
                        case FileAction.PrepareAttempt:
                            TryDelete(_zipPath, warnings);
                            TryDeleteDirectory(_tempSongPath, warnings);
                            break;
                        case FileAction.Extract:
                            EnsureDownloadedZip(_zipPath);
                            Directory.CreateDirectory(_tempSongPath);
                            ExtractZip(_zipPath, _tempSongPath, _cancellationToken);
                            TryDelete(_zipPath, warnings);
                            break;
                        case FileAction.Commit:
                            TrySetHidden(_tempRootPath, false, warnings);
                            ReplaceDirectory(_tempSongPath, _destinationPath, warnings);
                            break;
                        case FileAction.Cleanup:
                            TryDelete(_zipPath, warnings);
                            TryDeleteDirectory(_tempRootPath, warnings);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                } catch (Exception ex) {
                    error = ex;
                }

                return new BeatSaverPreparationWorker.Result<bool>(error == null, error, warnings.ToArray());
            }
        }

        private sealed class BeatSaverDownloadException : Exception {
            internal bool Retryable { get; }

            internal BeatSaverDownloadException(string message, bool retryable) : base(message) {
                Retryable = retryable;
            }
        }
    }
}
