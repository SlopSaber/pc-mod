using Newtonsoft.Json;
using ScoreSaber.Core;
using ScoreSaber.Core.Gameplay;
using ScoreSaber.Features.Players.Domain;
using ScoreSaber.Features.ScoreSubmission.Domain;
using System;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.ScoreSubmission.Services {

    internal class ScoreUploadPayloadBuilder {
        private const string UploadSecret = "f0b4a81c9bd3ded1081b365f7628781f";
        private static readonly object PreparationLock = new object();
        private static Task _preparationTail = Task.CompletedTask;
        private readonly ScoreSaberRuntimeInfo _runtimeInfo;

        public ScoreUploadPayloadBuilder(ScoreSaberRuntimeInfo runtimeInfo) {
            _runtimeInfo = runtimeInfo;
        }

        internal async Task<ScoreUploadPayload> BuildAsync(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, LevelCompletionResults results, LocalPlayerInfo playerInfo, float playOutcomeTime, ScoreSaberPlayOutcome? playOutcomeOverride) {
            ScoreSaberUploadData scoreData = ScoreSaberUploadData.Create(beatmapLevel, beatmapKey, results, playerInfo, _runtimeInfo.UploadVersionHash, playOutcomeTime, playOutcomeOverride);
            string serializedScore = JsonConvert.SerializeObject(scoreData);
            var request = new PreparationRequest(serializedScore, playerInfo.playerKey, playerInfo.playerId);
            PreparationResult prepared = await QueuePreparation(request);
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            if (prepared.Error != null) {
                ExceptionDispatchInfo.Capture(prepared.Error).Throw();
            }

            return new ScoreUploadPayload {
                ScoreData = scoreData,
                EncryptedScoreData = prepared.Value
            };
        }

        private static Task<PreparationResult> QueuePreparation(PreparationRequest request) {
            if (ExecutionContext.IsFlowSuppressed()) {
                return QueuePreparationWithoutContext(request);
            }

            using (ExecutionContext.SuppressFlow()) {
                return QueuePreparationWithoutContext(request);
            }
        }

        private static Task<PreparationResult> QueuePreparationWithoutContext(PreparationRequest request) {
            lock (PreparationLock) {
                Task<PreparationResult> task = _preparationTail.ContinueWith(
                    (_, state) => ((PreparationRequest)state).Run(), request,
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

        private sealed class PreparationRequest {
            private readonly string _serializedScore;
            private readonly string _playerKey;
            private readonly string _playerId;

            internal PreparationRequest(string serializedScore, string playerKey, string playerId) {
                _serializedScore = serializedScore;
                _playerKey = playerKey;
                _playerId = playerId;
            }

            internal PreparationResult Run() {
                try {
                    string key = BuildUploadKey(_playerKey, _playerId);
                    byte[] encrypted = panda(Encoding.UTF8.GetBytes(_serializedScore), Encoding.UTF8.GetBytes(key));
                    return new PreparationResult(BitConverter.ToString(encrypted).Replace("-", string.Empty));
                } catch (Exception error) {
                    return new PreparationResult(null, error);
                }
            }
        }

        private sealed class PreparationResult {
            internal string Value { get; }
            internal Exception Error { get; }

            internal PreparationResult(string value, Exception error = null) {
                Value = value;
                Error = error;
            }
        }

        private static string BuildUploadKey(string playerKey, string playerId) {
            byte[] encodedPassword = Encoding.UTF8.GetBytes($"{UploadSecret}-{playerKey}-{playerId}-{UploadSecret}");
            using (var md5 = MD5.Create()) {
                return BitConverter.ToString(md5.ComputeHash(encodedPassword)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static byte[] panda(byte[] scoreData, byte[] key) {
            int n1 = 11;
            int n2 = 13;
            int ns = 257;

            for (int i = 0; i <= key.Length - 1; i++) {
                ns += ns % (key[i] + 1);
            }

            byte[] encrypted = new byte[scoreData.Length];
            for (int i = 0; i <= scoreData.Length - 1; i++) {
                ns = key[i % key.Length] + ns;
                n1 = (ns + 5) * (n1 & 255) + (n1 >> 8);
                n2 = (ns + 7) * (n2 & 255) + (n2 >> 8);
                ns = ((n1 << 8) + n2) & 255;
                encrypted[i] = (byte)(scoreData[i] ^ (byte)ns);
            }

            return encrypted;
        }
    }

    internal class ScoreUploadPayload {
        internal ScoreSaberUploadData ScoreData { get; set; }
        internal string EncryptedScoreData { get; set; } = string.Empty;
    }
}
