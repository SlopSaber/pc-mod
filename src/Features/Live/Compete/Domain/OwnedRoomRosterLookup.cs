using ScoreSaber.Live.V1;
using ScoreSaber.Features.Replays;
using ScoreSaber.Features.Live.Protocol;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Domain {
    internal sealed class OwnedRoomRosterLookup {
        private readonly CompetePlayer[] _players;
        private readonly LiveRoomPlayerState[] _states;
        private readonly bool[] _hasStates;
        private readonly bool[] _active;

        private OwnedRoomRosterLookup(CompetePlayer[] players, LiveRoomPlayerState[] states, bool[] hasStates, bool[] active,
            Dictionary<int, OwnedStringEquality> localMatches) {
            _players = players;
            _states = states;
            _hasStates = hasStates;
            _active = active;
            _localMatches = localMatches;
        }

        internal static OwnedRoomRosterLookup Prepare(IReadOnlyList<CompetePlayer> players,
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds, string localPlayerId) {
            if (!(players is CompetePlayer[] array) || array.GetType() != typeof(CompetePlayer[])
                || Thread.CurrentThread.IsThreadPoolThread || !HasMaterialLookup(array)) {
                return null;
            }
            return PrepareOwned(array, states, activeIds, localPlayerId);
        }

        private static OwnedRoomRosterLookup PrepareOwned(CompetePlayer[] players,
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds, string localPlayerId) {
            Task<OwnedRoomRosterLookup> task = null;
            try {
                var snapshot = (CompetePlayer[])players.Clone();
                foreach (CompetePlayer player in snapshot) {
                    if (player == null || player.GetType() != typeof(CompetePlayer)) {
                        return null;
                    }
                }
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => Build(snapshot, states, activeIds, localPlayerId), out task)) {
                    return null;
                }
            } catch {
                if (task == null) {
                    return null;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                return task.GetAwaiter().GetResult();
            } catch {
                return null;
            }
        }

        private static OwnedRoomRosterLookup Build(CompetePlayer[] players,
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds, string localPlayerId) {
            var foundStates = new LiveRoomPlayerState[players.Length];
            var hasStates = new bool[players.Length];
            var active = new bool[players.Length];
            for (int i = 0; i < players.Length; i++) {
                string playerId = players[i].PlayerId;
                hasStates[i] = states.TryGetValue(playerId, out foundStates[i]);
                active[i] = hasStates[i] || (!string.IsNullOrEmpty(playerId) && activeIds.Contains(playerId));
            }
            return new OwnedRoomRosterLookup(players, foundStates, hasStates, active,
                PrepareLocalMatches(players, hasStates, localPlayerId));
        }

        internal bool TryGet(int position, CompetePlayer player, out LiveRoomPlayerState state,
            out bool hasState, out bool isActive) {
            state = null;
            hasState = false;
            isActive = false;
            if (position < 0 || position >= _players.Length || !ReferenceEquals(player, _players[position])) {
                return false;
            }
            state = _states[position];
            hasState = _hasStates[position];
            isActive = _active[position];
            return true;
        }

        private static bool HasMaterialLookup(CompetePlayer[] players) {
            if (players.Length >= 4096) {
                return true;
            }
            long bytes = 0;
            foreach (CompetePlayer player in players) {
                if (player == null || player.GetType() != typeof(CompetePlayer) || player.PlayerId == null) {
                    return false;
                }
                bytes += (long)player.PlayerId.Length * sizeof(char);
                if (bytes >= 1024 * 1024) {
                    return true;
                }
            }
            return false;
        }

        private readonly Dictionary<int, OwnedStringEquality> _localMatches;
        internal bool HasLocalMatches => _localMatches != null;

        private static Dictionary<int, OwnedStringEquality> PrepareLocalMatches(CompetePlayer[] players, bool[] hasStates, string localPlayerId) {
            try {
                if (localPlayerId == null || localPlayerId.Length < 1024 * 1024) return null;
                Dictionary<int, OwnedStringEquality> matches = null;
                for (int i = 0; i < players.Length; i++) {
                    if (!hasStates[i]) continue;
                    OwnedStringEquality comparison = OwnedStringEquality.Prepare(players[i].PlayerId, localPlayerId, true);
                    if (!comparison.IsPrepared) continue;
                    if (matches == null) matches = new Dictionary<int, OwnedStringEquality>();
                    matches.Add(i, comparison);
                }
                return matches;
            } catch {
                return null;
            }
        }

        internal bool IsLocal(int position, CompetePlayer player, string playerId, string localPlayerId) {
            if (position >= 0 && position < _players.Length && ReferenceEquals(player, _players[position])
                && _localMatches != null && _localMatches.TryGetValue(position, out OwnedStringEquality comparison)
                && comparison.TryGet(playerId, localPlayerId, out bool equals)) return equals;
            return string.Equals(playerId, localPlayerId, StringComparison.Ordinal);
        }
    }
}
