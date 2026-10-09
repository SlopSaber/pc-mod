using ScoreSaber.Core.Configuration;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Leaderboards.Services;
using ScoreSaber.Features.ScoreSubmission.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Replays {

    internal class ReplayStorageService {
        private static readonly object PreparationLock = new object();
        private static Task _preparationTail = Task.CompletedTask;
        private readonly SettingsService _settings;

        public ReplayStorageService(SettingsService settings) {
            _settings = settings;
        }

        internal bool LocalReplayExists(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, ScoreMap scoreMap) => GetExistingReplayPath(beatmapLevel, beatmapKey, scoreMap) != null;

        internal byte[] ReadLocalReplay(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, ScoreMap scoreMap) {
            string replayPath = GetExistingReplayPath(beatmapLevel, beatmapKey, scoreMap);
            return replayPath == null ? null : WaitForPreparation(new PreparationRequest(Operation.Read, replayPath)).Bytes;
        }

        internal async Task<byte[]> ReadLocalReplayAsync(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, ScoreMap scoreMap) {
            foreach (string replayPath in GetReplayPaths(beatmapLevel, beatmapKey, scoreMap)) {
                PreparationResult exists = await QueuePreparation(new PreparationRequest(Operation.Exists, replayPath));
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                exists.ThrowIfFailed();
                if (exists.Exists) {
                    PreparationResult replay = await QueuePreparation(new PreparationRequest(Operation.Read, replayPath));
                    await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                    replay.ThrowIfFailed();
                    return replay.Bytes;
                }
            }

            return null;
        }

        internal void SaveLocalReplay(ScoreSaberUploadData uploadData, BeatmapKey beatmapKey, byte[] replay) {
            if (!_settings.Current.saveLocalReplays || replay == null) {
                return;
            }

            try {
                WaitForPreparation(new PreparationRequest(Operation.CreateDirectory, _settings.ReplayPath));
                string replayPath = GetReplayPath(uploadData.PlayerId, uploadData.LeaderboardId, beatmapKey);
                WaitForPreparation(new PreparationRequest(Operation.Write, replayPath, (byte[])replay.Clone()));
            } catch (Exception ex) {
                Plugin.Log.Error($"Failed to write local replay; {ex}");
            }
        }

        internal async Task SaveLocalReplayAsync(ScoreSaberUploadData uploadData, BeatmapKey beatmapKey, byte[] replay) {
            if (!_settings.Current.saveLocalReplays || replay == null) {
                return;
            }

            try {
                PreparationResult directory = await QueuePreparation(new PreparationRequest(Operation.CreateDirectory, _settings.ReplayPath));
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                directory.ThrowIfFailed();
                string replayPath = GetReplayPath(uploadData.PlayerId, uploadData.LeaderboardId, beatmapKey);
                PreparationResult saved = await QueuePreparation(new PreparationRequest(Operation.Write, replayPath, (byte[])replay.Clone()));
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                saved.ThrowIfFailed();
            } catch (Exception ex) {
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                Plugin.Log.Error($"Failed to write local replay; {ex}");
            }
        }

        internal string GetReplayPath(string playerId, string songHash, BeatmapKey beatmapKey) => _settings.ReplayPathFor(playerId, songHash, beatmapKey);

        internal string GetExistingReplayPath(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, ScoreMap scoreMap) {
            foreach (string replayPath in GetReplayPaths(beatmapLevel, beatmapKey, scoreMap)) {
                if (WaitForPreparation(new PreparationRequest(Operation.Exists, replayPath)).Exists) {
                    return replayPath;
                }
            }

            return null;
        }

        private IEnumerable<string> GetReplayPaths(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, ScoreMap scoreMap) {
            string playerId = scoreMap.Score.Player.Id;
            string songHash = scoreMap.Parent.SongHash;

            yield return GetReplayPath(playerId, songHash, beatmapKey);

            string songName = WaitForPreparation(new PreparationRequest(Operation.PrepareSongName, beatmapLevel.songName)).Text;
            string difficulty = beatmapKey.difficulty.SerializedName();
            string characteristic = beatmapKey.CharacteristicSerializedName();

            yield return _settings.LegacyReplayPathFor(playerId, songName, difficulty, characteristic, songHash);
            yield return _settings.LegacyReplayPathFor(playerId, songName, songHash);
        }

        private static PreparationResult WaitForPreparation(PreparationRequest request) {
            Task<PreparationResult> task = QueuePreparation(request);
            if (!task.IsCompleted) {
                ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
            }

            PreparationResult result = task.GetAwaiter().GetResult();
            result.ThrowIfFailed();
            return result;
        }

        private static Task<PreparationResult> QueuePreparation(PreparationRequest request) {
            return QueueOwnedPreparation(request.Run);
        }

        internal static Task<T> QueueOwnedPreparation<T>(Func<T> prepare) {
            if (ExecutionContext.IsFlowSuppressed()) {
                return QueuePreparationWithoutContext(prepare);
            }

            using (ExecutionContext.SuppressFlow()) {
                return QueuePreparationWithoutContext(prepare);
            }
        }

        internal static bool TryQueueOwnedPreparationWhenIdle<T>(Func<T> prepare, out Task<T> task) {
            task = null;
            try {
                lock (PreparationLock) {
                    if (!_preparationTail.IsCompleted) {
                        return false;
                    }

                    if (ExecutionContext.IsFlowSuppressed()) {
                        task = Task.Factory.StartNew(prepare, CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                        _preparationTail = task;
                    } else {
                        using (ExecutionContext.SuppressFlow()) {
                            task = Task.Factory.StartNew(prepare, CancellationToken.None,
                                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                            _preparationTail = task;
                        }
                    }
                    return true;
                }
            } catch {
                return task != null;
            }
        }

        private static Task<T> QueuePreparationWithoutContext<T>(Func<T> prepare) {
            lock (PreparationLock) {
                Task<T> task = _preparationTail.ContinueWith(
                    (_, state) => ((Func<T>)state)(), prepare,
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _preparationTail = task;
                _ = task.ContinueWith(completed => {
                    lock (PreparationLock) {
                        if (ReferenceEquals(_preparationTail, completed)) {
                            _preparationTail = Task.CompletedTask;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return task;
            }
        }

        private enum Operation {
            Exists,
            Read,
            CreateDirectory,
            Write,
            PrepareSongName
        }

        private sealed class PreparationRequest {
            private readonly Operation _operation;
            private readonly string _value;
            private readonly byte[] _bytes;

            internal PreparationRequest(Operation operation, string value, byte[] bytes = null) {
                _operation = operation;
                _value = value;
                _bytes = bytes;
            }

            internal PreparationResult Run() {
                try {
                    switch (_operation) {
                        case Operation.Exists:
                            return new PreparationResult(exists: File.Exists(_value));
                        case Operation.Read:
                            return new PreparationResult(bytes: File.ReadAllBytes(_value));
                        case Operation.CreateDirectory:
                            Directory.CreateDirectory(_value);
                            break;
                        case Operation.Write:
                            File.WriteAllBytes(_value, _bytes);
                            break;
                        case Operation.PrepareSongName:
                            return new PreparationResult(text: _value.ReplaceInvalidChars().Truncate(155));
                    }

                    return new PreparationResult();
                } catch (Exception error) {
                    return new PreparationResult(error: error);
                }
            }
        }

        private sealed class PreparationResult {
            internal bool Exists { get; }
            internal byte[] Bytes { get; }
            internal string Text { get; }
            private Exception Error { get; }

            internal PreparationResult(bool exists = false, byte[] bytes = null, string text = null, Exception error = null) {
                Exists = exists;
                Bytes = bytes;
                Text = text;
                Error = error;
            }

            internal void ThrowIfFailed() {
                if (Error != null) {
                    ExceptionDispatchInfo.Capture(Error).Throw();
                }
            }
        }
    }
}
