using ScoreSaber.Core.Gameplay;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Leaderboards.Services {
    internal static class LeaderboardScorePreparation {
        internal static Task<Result> Queue(LeaderboardSnapshot ownedSnapshot, string replayPath, string songHash, string songName, string difficulty, string characteristic) =>
            Queue(ownedSnapshot, replayPath, songHash, songName, difficulty, characteristic, false);

        internal static Task<Result> Queue(LeaderboardSnapshot ownedSnapshot, string replayPath, string songHash, string songName, string difficulty, string characteristic, bool preparePlayerNames) {
            var request = new Request(ownedSnapshot, replayPath, songHash, songName, difficulty, characteristic,
                CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()), CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()), preparePlayerNames);
            return ReplayStorageService.QueueOwnedPreparation(request.Run);
        }

        private sealed class Request {
            private readonly bool _preparePlayerNames;
            private readonly LeaderboardSnapshot _snapshot;
            private readonly string _replayPath, _songHash, _songName, _difficulty, _characteristic;
            private readonly CultureInfo _culture, _uiCulture;

            internal Request(LeaderboardSnapshot snapshot, string replayPath, string songHash, string songName, string difficulty, string characteristic, CultureInfo culture, CultureInfo uiCulture, bool preparePlayerNames) {
                _snapshot = snapshot;
                _replayPath = replayPath;
                _songHash = songHash;
                _songName = songName;
                _difficulty = difficulty;
                _characteristic = characteristic;
                _culture = culture;
                _uiCulture = uiCulture;
                _preparePlayerNames = preparePlayerNames;
            }

            internal Result Run() {
                CultureInfo previousCulture = CultureInfo.CurrentCulture;
                CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
                try {
                    CultureInfo.CurrentCulture = _culture;
                    CultureInfo.CurrentUICulture = _uiCulture;
                    if (_snapshot.PlayerScore != null) {
                        _snapshot.PlayerScore.Mods = new List<string>(_snapshot.PlayerScore.Mods);
                    }

                    var prepared = new PreparedLeaderboardScore[_snapshot.Scores.Items.Count];
                    for (int i = 0; i < prepared.Length; i++) {
                        LeaderboardScore score = _snapshot.Scores.Items[i];
                        score.Mods = new List<string>(score.Mods);
                        string modifierText = string.Join(",", score.Mods);
                        PreparedGameplayModifiers modifiers = score.Mods.Count == 0 ? null : ScoreSaberGameplayModifiers.Prepare(score.Mods.ToArray(), false);
                        double maxScore = _snapshot.Leaderboard.MaxScore * (modifiers?.TotalMultiplier ?? 1);
                        bool hasLocalReplay = HasLocalReplay(score.Player.Id);
                        score.Weight = Math.Round(score.Weight * 100, 2);
                        score.PP = Math.Round(score.PP, 2);
                        double accuracy = Math.Round((score.ModifiedScore / maxScore) * 100, 2);
                        if (hasLocalReplay) {
                            score.HasReplay = true;
                        }
                        PreparedLeaderboardPlayerName playerName = _preparePlayerNames ? PreparedLeaderboardPlayerName.TryCreate(score, modifierText, accuracy) : null;
                        prepared[i] = new PreparedLeaderboardScore(score, modifierText, modifiers, accuracy, hasLocalReplay, playerName);
                    }
                    return new Result(prepared);
                } catch (Exception error) {
                    return new Result(error: error);
                } finally {
                    CultureInfo.CurrentCulture = previousCulture;
                    CultureInfo.CurrentUICulture = previousUICulture;
                }
            }

            private bool HasLocalReplay(string playerId) {
                if (File.Exists($@"{_replayPath}\{playerId}-{_songHash}-{_difficulty}-{_characteristic}.dat")) {
                    return true;
                }
                string songName = ReplayExtensions.Truncate(ReplayExtensions.ReplaceInvalidChars(_songName), 155);
                if (File.Exists($@"{_replayPath}\{playerId}-{songName}-{_difficulty}-{_characteristic}-{_songHash}.dat")) {
                    return true;
                }
                return File.Exists($@"{_replayPath}\{playerId}-{songName}-{_songHash}.dat");
            }
        }

        internal sealed class Result {
            internal PreparedLeaderboardScore[] Scores { get; }
            private readonly Exception _error;

            internal Result(PreparedLeaderboardScore[] scores = null, Exception error = null) {
                Scores = scores;
                _error = error;
            }

            internal void ThrowIfFailed() {
                if (_error != null) {
                    ExceptionDispatchInfo.Capture(_error).Throw();
                }
            }
        }
    }
}
