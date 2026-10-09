using ScoreSaber.Features.Replays;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Protocol {
    internal static class OwnedLocalRoomSelection {
        internal static bool TryFind(IList<LiveMatchRoomState> rooms, string localPlayerId, out LiveMatchRoomState selected) {
            selected = null;
            if (!(rooms is List<LiveMatchRoomState> list) || list.GetType() != typeof(List<LiveMatchRoomState>)
                || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }
            try {
                if (!HasMaterialSelection(list, localPlayerId)) {
                    return false;
                }
            } catch {
                return false;
            }
            return TryFindOwned(list, localPlayerId, out selected);
        }

        private static bool HasMaterialSelection(List<LiveMatchRoomState> rooms, string localPlayerId) {
            long entries = rooms.Count;
            long bytes = 0;
            foreach (LiveMatchRoomState room in rooms) {
                if (room == null || room.GetType() != typeof(LiveMatchRoomState)
                    || room.PlayerIds == null || room.PlayerIds.GetType() != typeof(List<string>)) {
                    return false;
                }
                entries += room.PlayerIds.Count;
                if (entries >= 4096) {
                    return true;
                }
                string matchId = room.MatchId;
                if (matchId != null && (long)matchId.Length == (long)localPlayerId.Length + 7) {
                    bytes += (long)matchId.Length * sizeof(char);
                }
                foreach (string id in room.PlayerIds) {
                    if (ReferenceEquals(id, localPlayerId)) {
                        return false;
                    }
                    if (id != null && !ReferenceEquals(id, localPlayerId) && id.Length == localPlayerId.Length) {
                        bytes += (long)id.Length * sizeof(char);
                    }
                }
                if (entries >= 4096 || bytes >= 1024 * 1024) {
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindOwned(List<LiveMatchRoomState> rooms, string localPlayerId, out LiveMatchRoomState selected) {
            selected = null;
            Task<int> task = null;
            LiveMatchRoomState[] snapshot = null;
            List<string>[] idLists = null;
            string[] matchIds = null;
            string[][] playerIds = null;
            try {
                snapshot = rooms.ToArray();
                idLists = new List<string>[snapshot.Length];
                matchIds = new string[snapshot.Length];
                playerIds = new string[snapshot.Length][];
                foreach (LiveMatchRoomState room in snapshot) {
                    if (room == null || room.GetType() != typeof(LiveMatchRoomState)
                        || room.PlayerIds == null || room.PlayerIds.GetType() != typeof(List<string>)) {
                        return false;
                    }
                }
                for (int i = 0; i < snapshot.Length; i++) {
                    matchIds[i] = snapshot[i].MatchId;
                    idLists[i] = snapshot[i].PlayerIds;
                    playerIds[i] = idLists[i].ToArray();
                }
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => Find(matchIds, playerIds, localPlayerId), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                int index = task.GetAwaiter().GetResult();
                if (rooms.Count != snapshot.Length) {
                    return false;
                }
                int checkedCount = index < 0 ? snapshot.Length : index + 1;
                for (int i = 0; i < checkedCount; i++) {
                    if (!ReferenceEquals(rooms[i], snapshot[i]) || !ReferenceEquals(snapshot[i].MatchId, matchIds[i])
                        || !ReferenceEquals(snapshot[i].PlayerIds, idLists[i]) || idLists[i].Count != playerIds[i].Length) {
                        return false;
                    }
                    for (int j = 0; j < playerIds[i].Length; j++) {
                        if (!ReferenceEquals(idLists[i][j], playerIds[i][j])) {
                            return false;
                        }
                    }
                }
                selected = index < 0 ? null : snapshot[index];
                return true;
            } catch {
                return false;
            }
        }

        private static int Find(string[] matchIds, string[][] playerIds, string localPlayerId) {
            string playerMatchId = "player:" + localPlayerId;
            for (int i = 0; i < matchIds.Length; i++) {
                if (matchIds[i] == playerMatchId) {
                    return i;
                }
                foreach (string id in playerIds[i]) {
                    if (id == localPlayerId) {
                        return i;
                    }
                }
            }
            return -1;
        }
    }
}
