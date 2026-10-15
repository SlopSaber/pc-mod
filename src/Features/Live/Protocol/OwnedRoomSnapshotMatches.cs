using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedRoomSnapshotMatches {
        private readonly Dictionary<int, Entry> _entries;

        private OwnedRoomSnapshotMatches(Dictionary<int, Entry> entries) {
            _entries = entries;
        }

        internal static OwnedRoomSnapshotMatches Prepare(List<LiveMatchRoomState> rooms, string matchId) {
            try {
                if (rooms == null || rooms.GetType() != typeof(List<LiveMatchRoomState>)
                    || matchId == null || matchId.Length < 1024 * 1024) return null;
                Dictionary<int, Entry> entries = null;
                for (int i = 0; i < rooms.Count; i++) {
                    LiveMatchRoomState room = rooms[i];
                    if (room == null || room.GetType() != typeof(LiveMatchRoomState)) continue;
                    OwnedStringEquality match = OwnedStringEquality.Prepare(room.MatchId, matchId, false);
                    OwnedStringEquality id = OwnedStringEquality.Prepare(room.RoomId, matchId, false);
                    if (!match.IsPrepared && !id.IsPrepared) continue;
                    if (entries == null) entries = new Dictionary<int, Entry>();
                    entries.Add(i, new Entry(room, match, id));
                }
                return entries == null ? null : new OwnedRoomSnapshotMatches(entries);
            } catch {
                return null;
            }
        }

        internal bool Matches(int position, LiveMatchRoomState room, string left, string right, bool roomId) {
            if (_entries.TryGetValue(position, out Entry entry) && ReferenceEquals(room, entry.Room)) {
                OwnedStringEquality comparison = roomId ? entry.RoomId : entry.MatchId;
                if (comparison.TryGet(left, right, out bool equals)) return equals;
            }
            return left == right;
        }

        private readonly struct Entry {
            internal readonly LiveMatchRoomState Room;
            internal readonly OwnedStringEquality MatchId;
            internal readonly OwnedStringEquality RoomId;

            internal Entry(LiveMatchRoomState room, OwnedStringEquality matchId, OwnedStringEquality roomId) {
                Room = room;
                MatchId = matchId;
                RoomId = roomId;
            }
        }
    }
}
