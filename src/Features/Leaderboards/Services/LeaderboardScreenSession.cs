using ScoreSaber.Core;
using ScoreSaber.Features.Leaderboards.Domain;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Leaderboards.Services {
    internal class LeaderboardScreenSession : IDisposable {
        internal event Action<LeaderboardScreenState> StateChanged;

        private readonly LeaderboardScreenLoader _leaderboardLoader;

        private BeatmapKey? _beatmapKey;
        private LeaderboardScreenScope _scope = LeaderboardScreenScope.Global;
        private int _page = 1;
        private CancellationTokenSource _refreshCancellation;
        private long _refreshRevision;
        private bool _disposed;

        internal LeaderboardScreenState CurrentState { get; private set; } = LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, string.Empty, false, null, string.Empty, false, 1);

        public LeaderboardScreenSession(
            LeaderboardScreenLoader leaderboardLoader) {
            _leaderboardLoader = leaderboardLoader;
        }

        internal void SetBeatmap(BeatmapKey beatmapKey) {
            if (!ScoreSaberBeatmapKey.IsSupported(beatmapKey)) {
                ClearBeatmap();
                return;
            }

            _beatmapKey = beatmapKey;
            _page = 1;
            Refresh();
        }

        internal void ClearBeatmap() {
            _beatmapKey = null;
            _page = 1;
            CancelRefresh();
        }

        internal void SelectScope(LeaderboardScreenScope scope) {
            if (_scope == scope && _page == 1) {
                Refresh();
                return;
            }

            _scope = scope;
            _page = 1;
            Refresh();
        }

        internal void PageUp() {
            if (_page <= 1) {
                return;
            }

            _page--;
            Refresh();
        }

        internal void PageDown() {
            _page++;
            Refresh();
        }

        internal void RefreshFromFirstPage() {
            _page = 1;
            Refresh();
        }

        internal ScoreMap GetScore(int index) {
            if (CurrentState?.Leaderboard == null || index < 0 || index >= CurrentState.Leaderboard.Scores.Length) {
                return null;
            }

            return CurrentState.Leaderboard.Scores[index];
        }

        private void Refresh() => LoadCurrent().RunTask();

        private async Task LoadCurrent() {
            if (_disposed || !_beatmapKey.HasValue) {
                return;
            }

            long revision = CancelRefresh();
            if (_disposed || revision != _refreshRevision) {
                return;
            }
            var refresh = new CancellationTokenSource();
            _refreshCancellation = refresh;
            CancellationToken cancellationToken = refresh.Token;
            BeatmapKey beatmapKey = _beatmapKey.Value;
            LeaderboardScreenScope scope = _scope;
            int page = _page;
            Func<bool> publicationGuard = _leaderboardLoader.CapturePublicationGuard(cancellationToken);

            try {
                Publish(LeaderboardScreenState.Loading(page), refresh, cancellationToken, publicationGuard);
                if (!IsCurrent(refresh, cancellationToken) || !publicationGuard()) {
                    return;
                }
                LeaderboardScreenState state = await _leaderboardLoader.Load(beatmapKey, scope, page, cancellationToken);
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                if (!IsCurrent(refresh, cancellationToken) || !publicationGuard()) {
                    return;
                }

                Publish(state, refresh, cancellationToken, publicationGuard);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                if (!IsCurrent(refresh, cancellationToken) || !publicationGuard()) {
                    return;
                }
                Plugin.Log.Error($"Failed to load LeaderboardCore ScoreSaber leaderboard: {ex}");
                Publish(LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, "Failed to load leaderboard, score won't upload", true, null, string.Empty, false, page), refresh, cancellationToken, publicationGuard);
            }
        }

        private long CancelRefresh() {
            long revision = ++_refreshRevision;
            CancellationTokenSource refresh = _refreshCancellation;
            _refreshCancellation = null;
            refresh?.Cancel();
            refresh?.Dispose();
            return revision;
        }

        internal void CancelPendingRefresh() => CancelRefresh();

        private bool IsCurrent(CancellationTokenSource refresh, CancellationToken cancellationToken) =>
            !_disposed && ReferenceEquals(_refreshCancellation, refresh) && !cancellationToken.IsCancellationRequested;

        private void Publish(LeaderboardScreenState state, CancellationTokenSource refresh, CancellationToken cancellationToken, Func<bool> publicationGuard) {
            if (!IsCurrent(refresh, cancellationToken) || !publicationGuard()) {
                return;
            }
            state.PublicationGuard = () => IsCurrent(refresh, cancellationToken) && ReferenceEquals(CurrentState, state) && publicationGuard();
            CurrentState = state;
            Delegate[] subscribers = StateChanged?.GetInvocationList();
            if (subscribers == null) {
                return;
            }
            foreach (Delegate subscriber in subscribers) {
                if (!state.CanPublish) {
                    return;
                }
                ((Action<LeaderboardScreenState>)subscriber)(state);
            }
        }

        public void Dispose() {
            _disposed = true;
            CancelRefresh();
        }
    }
}
