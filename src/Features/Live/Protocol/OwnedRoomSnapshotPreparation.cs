using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedRoomSnapshotPreparation {
        private readonly Entry[] _entries;

        private OwnedRoomSnapshotPreparation(Entry[] entries) {
            _entries = entries;
        }

        internal static OwnedRoomSnapshotPreparation Prepare(List<LiveMatchRoomState> rooms) {
            if (rooms == null || rooms.GetType() != typeof(List<LiveMatchRoomState>)) {
                return null;
            }

            var entries = new Entry[rooms.Count];
            for (int i = 0; i < rooms.Count; i++) {
                entries[i] = Entry.Capture(rooms[i]);
            }
            return new OwnedRoomSnapshotPreparation(entries);
        }

        internal bool TryGet(LiveMatchRoomState room, out Dictionary<string, LiveRoomPlayerState> states, out HashSet<string> activeIds) {
            states = null;
            activeIds = null;
            foreach (Entry entry in _entries) {
                if (entry == null || !entry.Matches(room)) {
                    continue;
                }

                entry.Error?.Throw();
                states = entry.States;
                activeIds = entry.ActiveIds;
                return true;
            }
            return false;
        }

        internal static Dictionary<string, LiveRoomPlayerState> GroupStates(IEnumerable<LiveRoomPlayerState> states) {
            return states.Where(state => !string.IsNullOrEmpty(state.PlayerId))
                .GroupBy(state => state.PlayerId)
                .ToDictionary(group => group.Key, group => group.First());
        }

        internal static HashSet<string> BuildActiveIds(LiveMatchRoomState room) {
            var playerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string playerId in room.PlayerIds) {
                if (!string.IsNullOrEmpty(playerId)) {
                    playerIds.Add(playerId);
                }
            }

            foreach (LiveRoomPlayerState state in room.PlayerStates) {
                if (!string.IsNullOrEmpty(state.PlayerId)) {
                    playerIds.Add(state.PlayerId);
                }
            }
            return playerIds;
        }

        private sealed class Entry {
            private readonly LiveMatchRoomState _room;
            private readonly List<LiveRoomPlayerState> _stateList;
            private readonly List<string> _idList;
            private readonly LiveRoomPlayerState[] _stateItems;
            private readonly string[] _stateIds;
            private readonly string[] _playerIds;

            private Entry(LiveMatchRoomState room) {
                _room = room;
                _stateList = room.PlayerStates;
                _idList = room.PlayerIds;
                _stateItems = _stateList?.ToArray();
                _stateIds = _stateItems?.Select(state => state?.PlayerId).ToArray();
                _playerIds = _idList?.ToArray();
                try {
                    States = GroupStates(_stateItems);
                    ActiveIds = BuildActiveIds(room);
                } catch (Exception ex) {
                    Error = ExceptionDispatchInfo.Capture(ex);
                }
            }

            internal Dictionary<string, LiveRoomPlayerState> States { get; }
            internal HashSet<string> ActiveIds { get; }
            internal ExceptionDispatchInfo Error { get; }

            internal static Entry Capture(LiveMatchRoomState room) {
                if (room == null || room.GetType() != typeof(LiveMatchRoomState)
                    || (room.PlayerStates != null && room.PlayerStates.GetType() != typeof(List<LiveRoomPlayerState>))
                    || (room.PlayerIds != null && room.PlayerIds.GetType() != typeof(List<string>))) {
                    return null;
                }
                if (room.PlayerStates != null && room.PlayerStates.Any(state => state != null && state.GetType() != typeof(LiveRoomPlayerState))) {
                    return null;
                }
                return new Entry(room);
            }

            internal bool Matches(LiveMatchRoomState room) {
                if (!ReferenceEquals(room, _room) || !ReferenceEquals(room.PlayerStates, _stateList)
                    || !ReferenceEquals(room.PlayerIds, _idList)) {
                    return false;
                }

                if (_stateList != null) {
                    if (_stateList.Count != _stateItems.Length) {
                        return false;
                    }
                    for (int i = 0; i < _stateItems.Length; i++) {
                        if (!ReferenceEquals(_stateList[i], _stateItems[i]) || _stateList[i]?.PlayerId != _stateIds[i]) {
                            return false;
                        }
                    }
                }

                if (_idList != null) {
                    if (_idList.Count != _playerIds.Length) {
                        return false;
                    }
                    for (int i = 0; i < _playerIds.Length; i++) {
                        if (_idList[i] != _playerIds[i]) {
                            return false;
                        }
                    }
                }
                return true;
            }
        }
    }
}
