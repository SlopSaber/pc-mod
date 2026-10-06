using ScoreSaber.Core;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Live.Compete.Packets;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Packets.Handlers {
    internal sealed class CreateRoomCommandHandler : ILudusServerCommandHandler {
        public LudusCommandType Type => LudusCommandType.LudusCommandTypeCreateRoom;

        private long _requestVersion;

        public void Handle(ILudusServerCommandSession session, ServerCommand command) {
            if (session is CompeteLudusCommandSession commandSession && commandSession.CanPrepareOwnedRoomDetails) {
                RefreshOwnedRoomDetails(session, command.MatchId, ++_requestVersion).RunTask();
            } else {
                RefreshRoomDetails(session, command.MatchId).RunTask();
            }
        }

        private async Task RefreshOwnedRoomDetails(ILudusServerCommandSession session, string matchId, long requestVersion) {
            CompeteRoom initial = session.TournamentRoom;
            if (initial == null || (!string.IsNullOrEmpty(matchId) && !string.Equals(matchId, initial.Id, StringComparison.Ordinal))) {
                return;
            }

            System.Threading.CancellationToken token = default;
            try {
                token = session.ConnectionCancellationToken;
                CompeteRoom details = await session.DirectoryService.GetRoom(initial.TournamentId, initial.Id, token);
                if (details == null) {
                    return;
                }

                MergeCapture capture = await IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() => {
                    if (!RequestIsCurrent(session, initial.Id, token, requestVersion)) {
                        return null;
                    }
                    CompeteRoom current = session.TournamentRoom;
                    if (!OwnedRoomDetailsPreparation.CanCapture(details) || !OwnedRoomDetailsPreparation.CanCapture(current)
                        || (details.PlayerListMode == CompetePlayerListMode.Teams && details.Teams.Count > 0 && details.Teams[0] == null)) {
                        PublishCurrentMerge(session, details, current, null, initial.Id, token, requestVersion);
                        return null;
                    }
                    return new MergeCapture(details, current);
                });
                if (capture == null) {
                    return;
                }

                OwnedRoomDetailsPreparation.PreparedPlayers prepared = null;
                Exception error = null;
                try {
                    prepared = await OwnedRoomDetailsPreparation.Prepare(capture.DetailPlayers, capture.CurrentPlayers,
                        capture.TeamMode, capture.TeamOneId);
                } catch (Exception ex) {
                    error = ex;
                }

                await IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() => {
                    if (!RequestIsCurrent(session, initial.Id, token, requestVersion)) {
                        return;
                    }
                    CompeteRoom latest = session.TournamentRoom;
                    if (!ReferenceEquals(latest, capture.Current) || !capture.Matches()) {
                        PublishCurrentMerge(session, details, latest, null, initial.Id, token, requestVersion);
                        return;
                    }
                    if (error != null) {
                        Plugin.Log.Warn($"Failed to refresh live room details: {error.Message}");
                        return;
                    }
                    PublishCurrentMerge(session, details, latest, prepared, initial.Id, token, requestVersion);
                });
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                await IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() => {
                    if (RequestIsCurrent(session, initial.Id, token, requestVersion)) {
                        Plugin.Log.Warn($"Failed to refresh live room details: {ex.Message}");
                    }
                });
            }
        }

        private bool RequestIsCurrent(ILudusServerCommandSession session, string roomId, System.Threading.CancellationToken token, long requestVersion) {
            return requestVersion == _requestVersion && !token.IsCancellationRequested
                && token == session.ConnectionCancellationToken && session.TournamentRoom != null
                && string.Equals(roomId, session.TournamentRoom.Id, StringComparison.Ordinal);
        }

        private void PublishCurrentMerge(ILudusServerCommandSession session, CompeteRoom details, CompeteRoom current,
            OwnedRoomDetailsPreparation.PreparedPlayers prepared, string roomId, System.Threading.CancellationToken token, long requestVersion) {
            if (!RequestIsCurrent(session, roomId, token, requestVersion) || !ReferenceEquals(current, session.TournamentRoom)) {
                return;
            }
            CompeteRoom merged;
            if (prepared == null) {
                merged = MergeRoomDetails(details, current);
            } else {
                CompeteSongSelection song = ShouldKeepCurrentSong(current.Song, details.Song) ? current.Song : details.Song;
                merged = new CompeteRoom(details.Id, details.TournamentId, details.Name, details.Code, details.Round, details.State,
                    details.PlayerListMode, details.Teams, song, prepared.Players, current.LocalPlayerReady, details.PlayerCount,
                    ReferenceEquals(song, current.Song) ? current.SongStatus : details.SongStatus);
                merged.AttachPreparedPlayers(prepared);
            }
            if (!RequestIsCurrent(session, roomId, token, requestVersion) || !ReferenceEquals(current, session.TournamentRoom)) {
                return;
            }
            session.TournamentRoom = merged;
            if (RequestIsCurrent(session, merged.Id, token, requestVersion) && ReferenceEquals(merged, session.TournamentRoom)) {
                session.NotifyRoomUpdated(merged);
            }
        }

        private sealed class MergeCapture {
            internal MergeCapture(CompeteRoom details, CompeteRoom current) {
                Details = details;
                Current = current;
                DetailPlayers = details.Players.ToArray();
                CurrentPlayers = current.Players.ToArray();
                TeamMode = details.PlayerListMode == CompetePlayerListMode.Teams;
                TeamOneId = details.Teams.Count > 0 ? details.Teams[0]?.Id : "team1";
            }
            internal CompeteRoom Details { get; }
            internal CompeteRoom Current { get; }
            internal CompetePlayer[] DetailPlayers { get; }
            internal CompetePlayer[] CurrentPlayers { get; }
            internal bool TeamMode { get; }
            internal string TeamOneId { get; }

            internal bool Matches() {
                return SamePlayers(Details.Players, DetailPlayers) && SamePlayers(Current.Players, CurrentPlayers)
                    && TeamMode == (Details.PlayerListMode == CompetePlayerListMode.Teams)
                    && TeamOneId == (Details.Teams.Count > 0 ? Details.Teams[0]?.Id : "team1");
            }
            private static bool SamePlayers(IReadOnlyList<CompetePlayer> source, CompetePlayer[] snapshot) {
                if (source.Count != snapshot.Length) {
                    return false;
                }
                for (int i = 0; i < snapshot.Length; i++) {
                    if (!ReferenceEquals(source[i], snapshot[i])) {
                        return false;
                    }
                }
                return true;
            }
        }

        private static async Task RefreshRoomDetails(ILudusServerCommandSession session, string matchId) {
            CompeteRoom currentRoom = session.TournamentRoom;
            if (currentRoom == null) {
                return;
            }

            if (!string.IsNullOrEmpty(matchId) && !string.Equals(matchId, currentRoom.Id, StringComparison.Ordinal)) {
                return;
            }

            try {
                CompeteRoom details = await session.DirectoryService.GetRoom(currentRoom.TournamentId, currentRoom.Id, session.ConnectionCancellationToken);
                if (details == null || session.TournamentRoom == null) {
                    return;
                }

                session.TournamentRoom = MergeRoomDetails(details, session.TournamentRoom);
                session.NotifyRoomUpdated(session.TournamentRoom);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Plugin.Log.Warn($"Failed to refresh live room details: {ex.Message}");
            }
        }

        private static CompeteRoom MergeRoomDetails(CompeteRoom details, CompeteRoom current) {
            Dictionary<string, CompetePlayer> livePlayers = current.Players
                .Where(player => !string.IsNullOrEmpty(player.PlayerId))
                .ToDictionary(player => player.PlayerId, player => player);

            CompetePlayer[] players = details.Players.Select(player => {
                CompetePlayer livePlayer;
                if (!string.IsNullOrEmpty(player.PlayerId) && livePlayers.TryGetValue(player.PlayerId, out livePlayer)) {
                    return new CompetePlayer(
                        player.Name,
                        livePlayer.Status,
                        player.TeamId,
                        player.Rank,
                        player.IsLocalPlayer,
                        player.PlayerId,
                        livePlayer.IsBot,
                        player.AvatarUrl,
                        player.IsActive || livePlayer.IsActive);
                }

                return player;
            }).ToArray();

            CompeteSongSelection song = ShouldKeepCurrentSong(current.Song, details.Song) ? current.Song : details.Song;
            string songStatus = ReferenceEquals(song, current.Song) ? current.SongStatus : details.SongStatus;

            return new CompeteRoom(
                details.Id,
                details.TournamentId,
                details.Name,
                details.Code,
                details.Round,
                details.State,
                details.PlayerListMode,
                details.Teams,
                song,
                players,
                current.LocalPlayerReady,
                details.PlayerCount,
                songStatus);
        }

        private static bool ShouldKeepCurrentSong(CompeteSongSelection current, CompeteSongSelection details) {
            if (current == null) {
                return false;
            }

            if (details == null) {
                return true;
            }

            if (!string.IsNullOrEmpty(current.MapHash) && !string.IsNullOrEmpty(details.MapHash)) {
                return string.Equals(current.MapHash, details.MapHash, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(current.Name, details.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Difficulty, details.Difficulty, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Characteristic, details.Characteristic, StringComparison.OrdinalIgnoreCase);
        }
    }
}
