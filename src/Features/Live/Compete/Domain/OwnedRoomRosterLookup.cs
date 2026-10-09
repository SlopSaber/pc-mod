using ScoreSaber.Features.Live.Protocol;
using ScoreSaber.Features.Replays;
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

        private OwnedRoomRosterLookup(CompetePlayer[] players, LiveRoomPlayerState[] states, bool[] hasStates, bool[] active) {
            _players = players;
            _states = states;
            _hasStates = hasStates;
            _active = active;
        }

        internal static OwnedRoomRosterLookup Prepare(IReadOnlyList<CompetePlayer> players,
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds) {
            if (!(players is CompetePlayer[] array) || array.GetType() != typeof(CompetePlayer[])
                || array.Length < 4096 || Thread.CurrentThread.IsThreadPoolThread) {
                return null;
            }
            return PrepareOwned(array, states, activeIds);
        }

        private static OwnedRoomRosterLookup PrepareOwned(CompetePlayer[] players,
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds) {
            Task<OwnedRoomRosterLookup> task = null;
            try {
                var snapshot = (CompetePlayer[])players.Clone();
                foreach (CompetePlayer player in snapshot) {
                    if (player == null || player.GetType() != typeof(CompetePlayer)) {
                        return null;
                    }
                }
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => Build(snapshot, states, activeIds), out task)) {
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
            Dictionary<string, LiveRoomPlayerState> states, HashSet<string> activeIds) {
            var foundStates = new LiveRoomPlayerState[players.Length];
            var hasStates = new bool[players.Length];
            var active = new bool[players.Length];
            for (int i = 0; i < players.Length; i++) {
                string playerId = players[i].PlayerId;
                hasStates[i] = states.TryGetValue(playerId, out foundStates[i]);
                active[i] = hasStates[i] || (!string.IsNullOrEmpty(playerId) && activeIds.Contains(playerId));
            }
            return new OwnedRoomRosterLookup(players, foundStates, hasStates, active);
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
    }
}
