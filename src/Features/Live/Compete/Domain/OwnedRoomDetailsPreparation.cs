using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Linq;
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
