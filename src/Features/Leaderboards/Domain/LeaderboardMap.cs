using ScoreSaber.Core.Api.Paging;
using ScoreSaber.Features.Replays;
using System;

namespace ScoreSaber.Features.Leaderboards.Domain {
    internal class LeaderboardMap {
        internal LeaderboardInfoMap LeaderboardInfo { get; set; }
        internal ScoreMap[] Scores { get; set; }
        internal LeaderboardScore PlayerScore { get; set; }

        internal LeaderboardMap(LeaderboardSnapshot leaderboard, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, int maxMultipliedScore, ReplayStorageService replayStorageService)
            : this(leaderboard, beatmapLevel, beatmapKey, maxMultipliedScore, replayStorageService, null) { }

        internal LeaderboardMap(LeaderboardSnapshot leaderboard, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, int maxMultipliedScore, ReplayStorageService replayStorageService, Action ensureCurrent) {
            ensureCurrent?.Invoke();
            LeaderboardInfo = new LeaderboardInfoMap(leaderboard.Leaderboard, beatmapLevel, beatmapKey);
            ensureCurrent?.Invoke();
            PlayerScore = leaderboard.PlayerScore;
            Scores = new ScoreMap[leaderboard.Scores.Items.Count];
            for (int i = 0; i < leaderboard.Scores.Items.Count; i++) {
                ensureCurrent?.Invoke();
                Scores[i] = new ScoreMap(leaderboard.Scores.Items[i], LeaderboardInfo, maxMultipliedScore, replayStorageService);
                ensureCurrent?.Invoke();
            }
        }

        internal LeaderboardMap(LeaderboardSnapshot leaderboard, PreparedLeaderboardScore[] scores, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, Action ensureCurrent) {
            ensureCurrent();
            LeaderboardInfo = new LeaderboardInfoMap(leaderboard.Leaderboard, beatmapLevel, beatmapKey);
            ensureCurrent();
            PlayerScore = leaderboard.PlayerScore;
            Scores = new ScoreMap[scores.Length];
            for (int i = 0; i < scores.Length; i++) {
                ensureCurrent();
                Scores[i] = new ScoreMap(scores[i], LeaderboardInfo);
                ensureCurrent();
            }
        }
    }

    internal class LeaderboardSnapshot {
        private bool _ownsScores;
        internal LeaderboardDetails Leaderboard { get; set; } = new LeaderboardDetails();
        internal PagedResult<LeaderboardScore> Scores { get; set; } = new PagedResult<LeaderboardScore>();
        internal LeaderboardScore PlayerScore { get; set; }

        internal static LeaderboardSnapshot Owned(LeaderboardDetails leaderboard, PagedResult<LeaderboardScore> scores, LeaderboardScore playerScore) {
            return new LeaderboardSnapshot { Leaderboard = leaderboard, Scores = scores, PlayerScore = playerScore, _ownsScores = true };
        }

        internal bool TakeOwnedScores() {
            bool owned = _ownsScores;
            _ownsScores = false;
            return owned;
        }
    }

    internal class LeaderboardInfoMap {
        internal LeaderboardDetails Leaderboard { get; set; }
        internal BeatmapLevel BeatmapLevel { get; set; }
        internal BeatmapKey BeatmapKey { get; set; }
        internal string SongHash { get; set; }

        internal LeaderboardInfoMap(LeaderboardDetails leaderboard, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey) {
            BeatmapLevel = beatmapLevel;
            BeatmapKey = beatmapKey;
            Leaderboard = leaderboard;
            SongHash = ScoreSaberBeatmapKey.GetSongHash(beatmapKey);
        }
    }
}
