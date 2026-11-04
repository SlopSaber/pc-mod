using LeaderboardCore.Interfaces;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Leaderboards.Services;
using ScoreSaber.Features.Leaderboards.UI;
using ScoreSaber.Features.Leaderboards.UI.Avatars;
using ScoreSaber.Features.Players.Services;

namespace ScoreSaber.Features.Leaderboards {
    internal partial class LeaderboardBeatmapController : INotifyLeaderboardSet {
        private readonly ScoreSaberLeaderboardOverlayController _overlayController;
        private readonly LeaderboardAvatarHost _avatarHost;
        private readonly GameSessionService _gameSessionService;
        private readonly LeaderboardScreenSession _leaderboardSession;
        private readonly ICoroutineStarter _coroutineStarter;

        public LeaderboardBeatmapController(
            ScoreSaberLeaderboardOverlayController overlayController,
            LeaderboardAvatarHost avatarHost,
            GameSessionService gameSessionService,
            LeaderboardScreenSession leaderboardSession,
            ICoroutineStarter coroutineStarter) {
            _overlayController = overlayController;
            _avatarHost = avatarHost;
            _gameSessionService = gameSessionService;
            _leaderboardSession = leaderboardSession;
            _coroutineStarter = coroutineStarter;
        }

        private BeatmapKey _pendingBeatmapKey;
        private bool _firstParseScheduled;

        public void OnLeaderboardSet(BeatmapKey beatmapKey) {
            if (!ScoreSaberBeatmapKey.IsSupported(beatmapKey)) {
                _pendingBeatmapKey = beatmapKey;
                _leaderboardSession.ClearBeatmap();
                return;
            }

            if (!_overlayController.IsParsed) {
                // The first selection usually arrives in the same frame as the level selection menu's own
                // first activation, so the overlay is built a couple of frames later instead.
                _pendingBeatmapKey = beatmapKey;
                if (!_firstParseScheduled && _coroutineStarter != null) {
                    _firstParseScheduled = true;
                    _coroutineStarter.StartCoroutine(ParseAfterSelectionFrame());
                    return;
                }
                if (_firstParseScheduled) {
                    return;
                }
                _overlayController.EnsureParsed();
                _avatarHost.ClearAvatars();
            }

            ApplyBeatmap(beatmapKey);
        }

        private System.Collections.IEnumerator ParseAfterSelectionFrame() {
            yield return null;
            yield return null;
            _firstParseScheduled = false;
            _overlayController.EnsureParsed();
            _avatarHost.ClearAvatars();
            if (ScoreSaberBeatmapKey.IsSupported(_pendingBeatmapKey)) {
                ApplyBeatmap(_pendingBeatmapKey);
            }
        }

        private void ApplyBeatmap(BeatmapKey beatmapKey) {
            if (!ScoreSaberBeatmapKey.IsWip(beatmapKey)) {
                _gameSessionService.EnsureAuthenticated();
            }
            _leaderboardSession.SetBeatmap(beatmapKey);
        }
    }
}
