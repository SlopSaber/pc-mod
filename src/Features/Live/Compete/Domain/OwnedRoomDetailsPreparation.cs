using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Domain {
    internal static class OwnedRoomDetailsPreparation {
        internal static bool CanCapture(CompeteRoom room) {
            return room != null && room.GetType() == typeof(CompeteRoom)
                && room.Players is CompetePlayer[] players && players.GetType() == typeof(CompetePlayer[])
                && players.All(player => player == null || player.GetType() == typeof(CompetePlayer));
        }

        internal static Task<PreparedPlayers> Prepare(CompetePlayer[] details, CompetePlayer[] current, bool teamMode, string teamOneId) {
            return ReplayStorageService.QueueOwnedPreparation(() => Merge(details, current, teamMode, teamOneId));
        }

        private static PreparedPlayers Merge(CompetePlayer[] details, CompetePlayer[] current, bool teamMode, string teamOneId) {
            Dictionary<string, CompetePlayer> livePlayers = current
                .Where(player => !string.IsNullOrEmpty(player.PlayerId))
                .ToDictionary(player => player.PlayerId, player => player);
            CompetePlayer[] players = details.Select(player => {
                if (!string.IsNullOrEmpty(player.PlayerId) && livePlayers.TryGetValue(player.PlayerId, out CompetePlayer livePlayer)) {
                    return new CompetePlayer(player.Name, livePlayer.Status, player.TeamId, player.Rank,
                        player.IsLocalPlayer, player.PlayerId, livePlayer.IsBot, player.AvatarUrl, player.IsActive || livePlayer.IsActive);
                }
                return player;
            }).ToArray();
            CompetePlayer[] active = players.Where(player => player.IsActive).ToArray();
            return new PreparedPlayers(players, active,
                teamMode ? Array.Empty<CompetePlayer>() : active,
                teamMode ? active.Where(player => player.TeamId == teamOneId).ToArray() : Array.Empty<CompetePlayer>(),
                teamMode ? active.Where(player => player.TeamId != teamOneId).ToArray() : Array.Empty<CompetePlayer>(), teamMode, teamOneId);
        }

        internal static void TryAttachFreshPlayers(CompeteRoom room) {
            if (!(room.Players is CompetePlayer[] players) || players.Length < 4096 || Thread.CurrentThread.IsThreadPoolThread) {
                return;
            }
            TryAttachOwnedFreshPlayers(room, players);
        }

        private static void TryAttachOwnedFreshPlayers(CompeteRoom room, CompetePlayer[] players) {
            Task<PreparedPlayers> task = null;
            try {
                if (!CanCapture(room)) {
                    return;
                }
                CompeteTeam firstTeam = room.Teams.Count > 0 ? room.Teams[0] : null;
                if (room.Teams.Count > 0 && (firstTeam == null || firstTeam.GetType() != typeof(CompeteTeam))) {
                    return;
                }
                bool teamMode = room.PlayerListMode == CompetePlayerListMode.Teams;
                string teamOneId = firstTeam == null ? "team1" : firstTeam.Id;
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => Merge(players, Array.Empty<CompetePlayer>(), teamMode, teamOneId), out task)) {
                    return;
                }
            } catch {
                if (task == null) {
                    return;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                room.AttachPreparedPlayers(task.GetAwaiter().GetResult());
            } catch {
            }
        }

        internal static bool TryPrepareCurrentPlayers(CompeteRoom room, bool teamMode, CompeteTeam teamOne, out PreparedPlayers prepared) {
            prepared = null;
            if (room.GetType() != typeof(CompeteRoom) || teamOne == null || teamOne.GetType() != typeof(CompeteTeam)
                || !(room.Players is CompetePlayer[] players) || players.GetType() != typeof(CompetePlayer[])
                || Thread.CurrentThread.IsThreadPoolThread || !HasMaterialPartitions(players, teamMode, teamOne.Id)) {
                return false;
            }
            return TryPrepareOwnedCurrentPlayers(players, teamMode, teamOne.Id, out prepared);
        }

        private static bool HasMaterialPartitions(CompetePlayer[] players, bool teamMode, string teamOneId) {
            if (players.Length >= 4096) {
                return true;
            }
            if (!teamMode || string.IsNullOrEmpty(teamOneId)) {
                return false;
            }
            long bytes = 0;
            foreach (CompetePlayer player in players) {
                if (player == null || player.GetType() != typeof(CompetePlayer)) {
                    return false;
                }
                string teamId = player.TeamId;
                if (player.IsActive && teamId != null && !ReferenceEquals(teamId, teamOneId) && teamId.Length == teamOneId.Length) {
                    bytes += (long)teamId.Length * sizeof(char);
                    if (bytes >= 1024 * 1024) {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryPrepareOwnedCurrentPlayers(CompetePlayer[] players, bool teamMode, string teamOneId, out PreparedPlayers prepared) {
            prepared = null;
            Task<PreparedPlayers> task = null;
            try {
                var snapshot = (CompetePlayer[])players.Clone();
                foreach (CompetePlayer player in snapshot) {
                    if (player == null || player.GetType() != typeof(CompetePlayer)) {
                        return false;
                    }
                }
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => Merge(snapshot, Array.Empty<CompetePlayer>(), teamMode, teamOneId), out task)) {
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
                PreparedPlayers result = task.GetAwaiter().GetResult();
                if (!result.Matches(players, teamMode, teamOneId)) {
                    return false;
                }
                prepared = result;
                return true;
            } catch {
                return false;
            }
        }

        internal sealed class PreparedPlayers {
            private readonly CompetePlayer[] _playerSnapshot;

            internal PreparedPlayers(CompetePlayer[] players, CompetePlayer[] active, CompetePlayer[] regular,
                CompetePlayer[] teamOne, CompetePlayer[] teamTwo, bool teamMode, string teamOneId) {
                Players = players;
                _playerSnapshot = players.ToArray();
                Active = active;
                Regular = regular;
                TeamOne = teamOne;
                TeamTwo = teamTwo;
                TeamMode = teamMode;
                TeamOneId = teamOneId;
            }

            internal CompetePlayer[] Players { get; }
            internal CompetePlayer[] Active { get; }
            internal CompetePlayer[] Regular { get; }
            internal CompetePlayer[] TeamOne { get; }
            internal CompetePlayer[] TeamTwo { get; }
            internal bool TeamMode { get; }
            internal string TeamOneId { get; }

            internal bool Matches(IReadOnlyList<CompetePlayer> players, bool teamMode, string teamOneId) {
                if (TeamMode != teamMode || TeamOneId != teamOneId || players.Count != _playerSnapshot.Length) {
                    return false;
                }
                for (int i = 0; i < _playerSnapshot.Length; i++) {
                    if (!ReferenceEquals(players[i], _playerSnapshot[i])) {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
