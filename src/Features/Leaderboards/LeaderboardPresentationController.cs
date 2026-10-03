using ScoreSaber.Features.Leaderboards.Adapters.LeaderboardCore;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Leaderboards.Services;
using ScoreSaber.Features.Leaderboards.UI;
using ScoreSaber.Features.Leaderboards.UI.Avatars;
using System;
using System.Threading;
using Zenject;

namespace ScoreSaber.Features.Leaderboards {
    internal class LeaderboardPresentationController : IInitializable, IDisposable {
        private readonly PanelView _panelView;
        private readonly LeaderboardScreenSession _leaderboardSession;
        private readonly ScoreSaberLeaderboardCoreViewController _leaderboardViewController;
        private readonly ScoreSaberLeaderboardOverlayController _overlayController;
        private readonly LeaderboardAvatarHost _avatarHost;

        private CancellationTokenSource _avatarCancellation;
        private bool _disposed;

        public LeaderboardPresentationController(
            PanelView panelView,
            LeaderboardScreenSession leaderboardSession,
            ScoreSaberLeaderboardCoreViewController leaderboardViewController,
            ScoreSaberLeaderboardOverlayController overlayController,
            LeaderboardAvatarHost avatarHost) {
            _panelView = panelView;
            _leaderboardSession = leaderboardSession;
            _leaderboardViewController = leaderboardViewController;
            _overlayController = overlayController;
            _avatarHost = avatarHost;
        }

        public void Initialize() {
            _leaderboardSession.StateChanged += LeaderboardStateChanged;
            _panelView.Disabled += _leaderboardSession.CancelPendingRefresh;
        }

        private void LeaderboardStateChanged(LeaderboardScreenState state) {
            if (!CanPublish(state)) return;
            Plugin.Log.Debug($"Leaderboard UI state: {state.Status}, loaded={state.IsLoaded}, scores={state.Leaderboard?.Scores?.Length ?? 0}");
            if (!CanPublish(state)) return;
            _leaderboardViewController.SetRankColumnOffset(LeaderboardRankLayout.OffsetFor(state.Status == LeaderboardScreenStatus.Loaded ? state.Leaderboard : null));
            if (!CanPublish(state)) return;
            _leaderboardViewController.ApplyState(state);
            if (!CanPublish(state) || !_overlayController.IsParsed) {
                return;
            }

            switch (state.Status) {
                case LeaderboardScreenStatus.Loading:
                    ResetAvatarCancellation(state);
                    if (!CanPublish(state)) return;
                    _avatarHost.ClearAvatars();
                    break;
                case LeaderboardScreenStatus.Loaded:
                    ShowLoadedLeaderboard(state);
                    break;
                default:
                    ShowLeaderboardError(state);
                    break;
            }
        }

        private void ShowLoadedLeaderboard(LeaderboardScreenState state) {
            if (!CanPublish(state)) return;
            _panelView.DismissLoadingPrompt();
            if (!CanPublish(state)) return;
            _panelView.SetRankedStatus(state.RankedStatus);
            if (!CanPublish(state) || state.Leaderboard == null) {
                return;
            }

            if (_avatarCancellation == null) {
                ResetAvatarCancellation(state);
            }
            if (!CanPublish(state)) return;
            _overlayController.ApplyAvatarLayout(state.Leaderboard);
            if (!CanPublish(state)) return;
            _avatarHost.LoadAvatars(state.Leaderboard, _avatarCancellation.Token);
        }

        private void ShowLeaderboardError(LeaderboardScreenState state) {
            if (!CanPublish(state)) return;
            _avatarHost.ClearAvatars();
            if (!CanPublish(state)) return;
            _panelView.DismissLoadingPrompt();
            if (!CanPublish(state)) return;
            _panelView.SetRankedStatus(GetRankedStatusText(state));
        }

        private static string GetRankedStatusText(LeaderboardScreenState state) {
            if (state.Leaderboard != null || state.Status == LeaderboardScreenStatus.NoLeaderboard) {
                return state.RankedStatus;
            }

            return "Unavailable";
        }

        private bool CanPublish(LeaderboardScreenState state) => !_disposed && state.CanPublish;

        private void ResetAvatarCancellation(LeaderboardScreenState state) {
            CancellationTokenSource previous = _avatarCancellation;
            _avatarCancellation = null;
            try {
                previous?.Cancel();
            } finally {
                previous?.Dispose();
            }
            if (CanPublish(state) && _avatarCancellation == null) {
                _avatarCancellation = new CancellationTokenSource();
            }
        }

        public void Dispose() {
            _disposed = true;
            CancellationTokenSource previous = _avatarCancellation;
            _avatarCancellation = null;
            _leaderboardSession.StateChanged -= LeaderboardStateChanged;
            _panelView.Disabled -= _leaderboardSession.CancelPendingRefresh;
            try {
                _leaderboardSession.CancelPendingRefresh();
            } finally {
                try {
                    previous?.Cancel();
                } finally {
                    previous?.Dispose();
                }
            }
        }
    }
}
