using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Live.Compete.Packets;
using ScoreSaber.Features.Live.Ludus.Services;
using ScoreSaber.Features.Live.Ludus.Packets;
using ScoreSaber.Features.Live.Protocol;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScoreSaber.Features.Live.Compete.Packets.Handlers {
    internal sealed class RoomSnapshotEnvelopeHandler : ILudusEnvelopeHandler<ILudusSessionPacketContext> {
        private readonly ILudusServerCommandSession _commandSession;

        internal RoomSnapshotEnvelopeHandler(ILudusServerCommandSession commandSession) {
            _commandSession = commandSession;
        }

        public LudusEnvelopeType Type => LudusEnvelopeType.RoomSnapshot;

        public void Handle(ILudusSessionPacketContext session, DecodedLudusEnvelope envelope) {
            ApplyRoomSnapshot(_commandSession, envelope.Rooms, envelope.PreparedRooms);
        }

        private static void ApplyRoomSnapshot(ILudusServerCommandSession session, IEnumerable<LiveMatchRoomState> rooms, OwnedRoomSnapshotPreparation prepared) {
            if (rooms == null) {
                session.NotifyViewersUpdated(null);
                return;
            }

            List<LiveMatchRoomState> roomList = rooms as List<LiveMatchRoomState> ?? rooms.ToList();
            LiveMatchRoomState room = FindSessionRoom(session, roomList);
            session.NotifyViewersUpdated(room?.Viewers);
            if (room == null || session.TournamentRoom == null) {
                return;
            }

            Dictionary<string, LiveRoomPlayerState> states;
            HashSet<string> activePlayerIds;
            if (prepared == null || !prepared.TryGet(room, out states, out activePlayerIds)) {
                states = OwnedRoomSnapshotPreparation.GroupStates(room.PlayerStates);
                activePlayerIds = ActivePlayerIds(room);
            }
            var players = new List<CompetePlayer>();
            bool localReady = false;

            IReadOnlyList<CompetePlayer> roster = session.TournamentRoom.Players;
            OwnedRoomRosterLookup lookup = OwnedRoomRosterLookup.Prepare(roster, states, activePlayerIds);
            int position = 0;
            foreach (CompetePlayer player in roster) {
                LiveRoomPlayerState state;
                bool hasState;
                bool isActive;
                if (lookup == null || !lookup.TryGet(position, player, out state, out hasState, out isActive)) {
                    hasState = states.TryGetValue(player.PlayerId, out state);
                    isActive = hasState || (!string.IsNullOrEmpty(player.PlayerId) && activePlayerIds.Contains(player.PlayerId));
                }
                position++;
                if (!hasState) {
                    if (player.IsLocalPlayer && isActive) {
                        localReady = session.TournamentRoom.LocalPlayerReady;
                    }

                    players.Add(new CompetePlayer(
                        player.Name,
                        isActive ? player.Status : "Offline",
                        player.TeamId,
                        player.Rank,
                        player.IsLocalPlayer,
                        player.PlayerId,
                        player.IsBot,
                        player.AvatarUrl,
                        isActive));
                    continue;
                }

                bool isLocal = string.Equals(player.PlayerId, session.LocalPlayerId, StringComparison.Ordinal);
                if (isLocal) {
                    localReady = state.ReadyState == LudusReadyState.LudusReadyStateReady;
                }

                players.Add(new CompetePlayer(
                    player.Name,
                    FormatPlayerStatus(state),
                    player.TeamId,
                    player.Rank,
                    isLocal,
                    player.PlayerId,
                    state.IsBot,
                    player.AvatarUrl,
                    true));
            }

            var nextRoom = new CompeteRoom(
                session.TournamentRoom.Id,
                session.TournamentRoom.TournamentId,
                session.TournamentRoom.Name,
                session.TournamentRoom.Code,
                session.TournamentRoom.Round,
                session.TournamentRoom.State,
                session.TournamentRoom.PlayerListMode,
                session.TournamentRoom.Teams,
                session.TournamentRoom.Song,
                players,
                localReady,
                Math.Max(session.TournamentRoom.PlayerCount, players.Count));
            OwnedRoomDetailsPreparation.TryAttachFreshPlayers(nextRoom);
            session.TournamentRoom = nextRoom;
            session.NotifyRoomUpdated(session.TournamentRoom);
        }

        private static HashSet<string> ActivePlayerIds(LiveMatchRoomState room) {
            return OwnedRoomSnapshotPreparation.BuildActiveIds(room);
        }

        private static LiveMatchRoomState FindSessionRoom(ILudusServerCommandSession session, IList<LiveMatchRoomState> rooms) {
            if (rooms == null || rooms.Count == 0) {
                return null;
            }

            if (session.TournamentRoom != null) {
                return rooms.FirstOrDefault(item => item.MatchId == session.TournamentRoom.Id || item.RoomId == session.TournamentRoom.Id);
            }

            string localPlayerId = session.LocalPlayerId;
            if (!string.IsNullOrEmpty(localPlayerId)) {
                LiveMatchRoomState playerRoom = rooms.FirstOrDefault(item =>
                    item.MatchId == $"player:{localPlayerId}" ||
                    item.PlayerIds.Contains(localPlayerId));
                if (playerRoom != null) {
                    return playerRoom;
                }
            }

            return rooms.Count == 1 ? rooms[0] : null;
        }

        private static string FormatPlayerStatus(LiveRoomPlayerState state) {
            if (state.ReadyState == LudusReadyState.LudusReadyStateReady) {
                return "Ready";
            }

            if (state.DownloadState == LudusDownloadState.LudusDownloadStateDownloading) {
                return "Downloading";
            }

            if (state.DownloadState == LudusDownloadState.LudusDownloadStateError) {
                return "Download Error";
            }

            if (state.PlayState == LudusPlayState.LudusPlayStateInGame) {
                return "In Game";
            }

            return "Waiting";
        }
    }
}
