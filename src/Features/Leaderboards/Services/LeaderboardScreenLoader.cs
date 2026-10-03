using ScoreSaber.Core.Configuration;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Players.Services;
using ScoreSaber.Features.Players.Domain;
using System;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeneratedApiException = ScoreSaber.Core.Api.Generated.ApiException;

namespace ScoreSaber.Features.Leaderboards.Services {
    internal class LeaderboardScreenLoader {
        private readonly BeatmapLevelsModel _beatmapLevelsModel;
        private readonly LeaderboardQueryService _leaderboardQueryService;
        private readonly GameSessionService _gameSessionService;
        private readonly SettingsService _settings;

        public LeaderboardScreenLoader(
            BeatmapLevelsModel beatmapLevelsModel,
            LeaderboardQueryService leaderboardQueryService,
            GameSessionService gameSessionService,
            SettingsService settings) {
            _beatmapLevelsModel = beatmapLevelsModel;
            _leaderboardQueryService = leaderboardQueryService;
            _gameSessionService = gameSessionService;
            _settings = settings;
        }

        internal async Task<LeaderboardScreenState> Load(BeatmapKey beatmapKey, LeaderboardScreenScope scope, int page, CancellationToken cancellationToken) {
            if (ScoreSaberBeatmapKey.IsWip(beatmapKey)) {
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, "ScoreSaber doesn't support WIP levels", false, null, string.Empty, false, page);
            }

            if (!ScoreSaberBeatmapKey.IsSupported(beatmapKey)) {
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, string.Empty, false, null, string.Empty, false, page);
            }

            BeatmapLevel beatmapLevel = _beatmapLevelsModel.GetBeatmapLevel(beatmapKey.levelId);
            if (beatmapLevel == null) {
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, "Failed to load beatmap", false, null, string.Empty, false, page);
            }

            if (!_gameSessionService.HasAuthenticatedSession) {
                _gameSessionService.EnsureAuthenticated();
                if (_gameSessionService.Status != GameSessionService.LoginStatus.Error) {
                    return LeaderboardScreenState.Loading(page);
                }

                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, "Authentication failed. Restart Beat Saber and try again.", false, null, string.Empty, false, page);
            }

            bool filterAroundCountry = ShouldFilterAroundCountry(scope);
            string playerId = _gameSessionService.LocalPlayerInfo.playerId;
            LeaderboardMap leaderboard;
            try {
                leaderboard = await _leaderboardQueryService.GetLeaderboardData(beatmapLevel, beatmapKey, scope, page, filterAroundCountry, cancellationToken);
            } catch (GeneratedApiException ex) when (IsLeaderboardNotFoundResponse(ex)) {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaderboardScreenState.Failed(
                    LeaderboardScreenStatus.NoLeaderboard,
                    "Play this level to create a ScoreSaber leaderboard",
                    true,
                    null,
                    "Unranked",
                    false,
                    page);
            } catch (GeneratedApiException ex) when (IsNoPlayerScoreResponse(ex)) {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.NoPlayerScore, GetApiMessage(ex), true, null, string.Empty, false, page);
            } catch (GeneratedApiException ex) {
                cancellationToken.ThrowIfCancellationRequested();
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Error, GetApiMessage(ex), true, null, string.Empty, false, page);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CreateLoadedState(leaderboard, scope, filterAroundCountry, page, playerId);
        }

        internal Func<bool> CapturePublicationGuard(CancellationToken cancellationToken) {
            GameSession session = _gameSessionService.GameSession;
            LocalPlayerInfo localPlayer = _gameSessionService.LocalPlayerInfo;
            string playerId = session?.PlayerId;
            string sessionId = session?.SessionId;
            string sessionKey = session?.SessionKey;
            string localPlayerId = localPlayer?.playerId;
            return () => !cancellationToken.IsCancellationRequested
                && ReferenceEquals(_gameSessionService.GameSession, session) && ReferenceEquals(_gameSessionService.LocalPlayerInfo, localPlayer)
                && session?.PlayerId == playerId && session?.SessionId == sessionId && session?.SessionKey == sessionKey && localPlayer?.playerId == localPlayerId;
        }

        private LeaderboardScreenState CreateLoadedState(LeaderboardMap leaderboard, LeaderboardScreenScope scope, bool filterAroundCountry, int page, string playerId) {
            int playerScoreIndex = Array.FindIndex(leaderboard.Scores, score => score.Score.Player.Id == playerId);
            bool canPage = CanPageScope(scope, filterAroundCountry);
            string rankedStatus = GetRankedStatus(leaderboard.LeaderboardInfo.Leaderboard);
            if (scope == LeaderboardScreenScope.AroundPlayer && playerScoreIndex == -1 && !filterAroundCountry) {
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.NoPlayerScore, "You haven't set a score on this leaderboard", true, leaderboard, rankedStatus, canPage, page);
            }

            if (leaderboard.Scores.Length == 0) {
                string emptyText = page > 1 ? "No scores on this page" : "No scores on this leaderboard, be the first!";
                return LeaderboardScreenState.Failed(LeaderboardScreenStatus.Empty, emptyText, true, leaderboard, rankedStatus, canPage, page);
            }

            return LeaderboardScreenState.Loaded(leaderboard, playerScoreIndex, rankedStatus, canPage, page);
        }

        private bool ShouldFilterAroundCountry(LeaderboardScreenScope scope) => scope == LeaderboardScreenScope.Country && _settings.Current.enableCountryLeaderboards;

        private static bool CanPageScope(LeaderboardScreenScope scope, bool filterAroundCountry) => scope != LeaderboardScreenScope.AroundPlayer || filterAroundCountry;

        private static bool IsNoPlayerScoreResponse(GeneratedApiException ex) => ex.StatusCode == 404 && GetApiMessage(ex).IndexOf("hasn't set a score", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsLeaderboardNotFoundResponse(GeneratedApiException ex) =>
            ex.StatusCode == 404 && GetApiMessage(ex).IndexOf("Leaderboard not found", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string GetApiMessage(GeneratedApiException ex) {
            if (!string.IsNullOrEmpty(ex.Response)) {
                try {
                    JObject body = JObject.Parse(ex.Response);
                    string message = body.Value<string>("message") ?? body.Value<string>("errorMessage") ?? body.Value<string>("error");
                    if (!string.IsNullOrEmpty(message)) {
                        return message;
                    }
                } catch (Exception) {
                }
            }

            return string.IsNullOrEmpty(ex.Message) ? "Failed to load leaderboard" : ex.Message;
        }

        private static string GetRankedStatus(LeaderboardDetails leaderboardInfo) => leaderboardInfo.Status switch {
            LeaderboardStatus.Ranked => leaderboardInfo.PositiveModifiers ? "Ranked (DA = +0.02, GN +0.04)" : "Ranked (modifiers disabled)",
            LeaderboardStatus.Qualified => "Qualified",
            LeaderboardStatus.Loved => "Loved",
            _ => "Unranked"
        };
    }
}
