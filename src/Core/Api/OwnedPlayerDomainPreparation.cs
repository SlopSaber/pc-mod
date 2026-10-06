using ScoreSaber.Core.Api.Generated;
using ScoreSaber.Core.Api.Paging;
using ScoreSaber.Features.Players.Domain;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {

    internal static class OwnedPlayerDomainPreparation {

        internal static Task<PagedResult<PlayerSummary>> Prepare(PlayerListResponse source) {
            if (source == null || !IsDefaultList(source.Data)) {
                return Task.FromResult(MapPlayers(source));
            }

            PlayerListResponse owned = new PlayerListResponse {
                Data = CopyList(source.Data, Copy),
                Metadata = source.Metadata == null ? null : new PlayerListResponseMetadata {
                    Page = source.Metadata.Page,
                    ItemsPerPage = source.Metadata.ItemsPerPage,
                    TotalItems = source.Metadata.TotalItems,
                    TotalPages = source.Metadata.TotalPages
                }
            };
            return ReplayStorageService.QueueOwnedPreparation(() => MapPlayers(owned));
        }

        internal static Task<PlayerProfile> Prepare(PlayerProfileResponse source) {
            if (source == null || !IsDefaultList(source.Badges)) {
                return Task.FromResult(GeneratedModelMapper.ToDomain(source));
            }

            PlayerProfileResponse owned = new PlayerProfileResponse {
                Id = source.Id,
                Name = source.Name,
                PlayerNameInGame = source.PlayerNameInGame,
                Country = source.Country,
                Role = source.Role,
                Avatar = source.Avatar,
                Permissions = source.Permissions,
                Banned = source.Banned,
                Inactive = source.Inactive,
                Stats = Copy(source.Stats),
                Bio = source.Bio,
                CreatedAt = source.CreatedAt,
                LastSeenAt = source.LastSeenAt,
                Followers = source.Followers,
                Following = source.Following,
                Badges = CopyList(source.Badges, Copy)
            };
            if (!HasExplicitDate(owned.CreatedAt) || !HasExplicitDate(owned.LastSeenAt)) {
                return Task.FromResult(GeneratedModelMapper.ToDomain(source));
            }
            return ReplayStorageService.QueueOwnedPreparation(() => GeneratedModelMapper.ToDomain(owned));
        }

        internal static Task<PlayerProfile> Prepare(PlayerBasicProfileResponse source) {
            if (source == null) {
                return Task.FromResult(GeneratedModelMapper.ToDomain(source));
            }

            PlayerBasicProfileResponse owned = new PlayerBasicProfileResponse {
                Id = source.Id,
                Name = source.Name,
                PlayerNameInGame = source.PlayerNameInGame,
                Country = source.Country,
                Role = source.Role,
                Avatar = source.Avatar,
                Permissions = source.Permissions,
                Banned = source.Banned,
                Inactive = source.Inactive,
                Stats = Copy(source.Stats)
            };
            return ReplayStorageService.QueueOwnedPreparation(() => GeneratedModelMapper.ToDomain(owned));
        }

        internal static Task<List<PlayerHistoryPoint>> Prepare(List<GlobalPlayerHistoryEntry> source) {
            if (!IsDefaultList(source)) {
                return Task.FromResult(MapHistory(source));
            }

            List<GlobalPlayerHistoryEntry> owned = CopyList(source, Copy);
            if (owned != null && owned.Any(point => point != null && !HasExplicitDate(point.CreatedAt))) {
                return Task.FromResult(MapHistory(source));
            }
            return ReplayStorageService.QueueOwnedPreparation(() => MapHistory(owned));
        }

        private static PagedResult<PlayerSummary> MapPlayers(PlayerListResponse source) {
            return new PagedResult<PlayerSummary> {
                Items = source.Data.Select(player => GeneratedModelMapper.ToDomain(player)).ToList(),
                Metadata = GeneratedModelMapper.ToDomain(source.Metadata)
            };
        }

        private static List<PlayerHistoryPoint> MapHistory(List<GlobalPlayerHistoryEntry> source) {
            return source.Select(point => GeneratedModelMapper.ToDomain(point)).ToList();
        }

        private static bool IsDefaultList<T>(List<T> source) {
            return source == null || source.GetType() == typeof(List<T>);
        }

        private static List<T> CopyList<T>(List<T> source, Func<T, T> copy) {
            if (source == null) {
                return null;
            }
            List<T> owned = new List<T>(source.Count);
            foreach (T item in source) {
                owned.Add(copy(item));
            }
            return owned;
        }

        internal static bool HasExplicitDate(string value) {
            if (string.IsNullOrEmpty(value)) {
                return true;
            }
            return value.Length >= 10 && value[4] == '-' && value[7] == '-' &&
                IsDigit(value[0]) && IsDigit(value[1]) && IsDigit(value[2]) && IsDigit(value[3]) &&
                IsDigit(value[5]) && IsDigit(value[6]) && IsDigit(value[8]) && IsDigit(value[9]);
        }

        private static bool IsDigit(char value) {
            return value >= '0' && value <= '9';
        }

        private static PlayerListResponseDataItem Copy(PlayerListResponseDataItem source) {
            return source == null ? null : new PlayerListResponseDataItem {
                Id = source.Id,
                Name = source.Name,
                PlayerNameInGame = source.PlayerNameInGame,
                Country = source.Country,
                Role = source.Role,
                Avatar = source.Avatar,
                Permissions = source.Permissions,
                Banned = source.Banned,
                Inactive = source.Inactive,
                Stats = Copy(source.Stats)
            };
        }

        private static PlayerProfileResponseBadgesItem Copy(PlayerProfileResponseBadgesItem source) {
            return source == null ? null : new PlayerProfileResponseBadgesItem {
                Image = source.Image,
                Description = source.Description
            };
        }

        private static GlobalPlayerHistoryEntry Copy(GlobalPlayerHistoryEntry source) {
            return source == null ? null : new GlobalPlayerHistoryEntry {
                Rank = source.Rank,
                TotalPP = source.TotalPP,
                TotalScore = source.TotalScore,
                TotalRankedScore = source.TotalRankedScore,
                Estimated = source.Estimated,
                CreatedAt = source.CreatedAt
            };
        }

        private static PlayerListResponseDataItemStats Copy(PlayerListResponseDataItemStats source) {
            return source == null ? null : new PlayerListResponseDataItemStats {
                RealmId = source.RealmId,
                RealmName = source.RealmName,
                Rank = source.Rank,
                CountryRank = source.CountryRank,
                TotalPP = source.TotalPP,
                TotalScore = source.TotalScore,
                TotalRankedScore = source.TotalRankedScore,
                AverageAccuracy = source.AverageAccuracy,
                WeightedAverageAccuracy = source.WeightedAverageAccuracy,
                CompletionAccuracy = source.CompletionAccuracy,
                TotalPlayedLeaderboards = source.TotalPlayedLeaderboards,
                TotalPlayedRankedLeaderboards = source.TotalPlayedRankedLeaderboards,
                TotalSubmittedPlays = source.TotalSubmittedPlays,
                TotalReplayViews = source.TotalReplayViews,
                Device = Copy(source.Device)
            };
        }

        private static PlayerProfileResponseStats Copy(PlayerProfileResponseStats source) {
            return source == null ? null : new PlayerProfileResponseStats {
                RealmId = source.RealmId,
                RealmName = source.RealmName,
                Rank = source.Rank,
                CountryRank = source.CountryRank,
                TotalPP = source.TotalPP,
                TotalScore = source.TotalScore,
                TotalRankedScore = source.TotalRankedScore,
                AverageAccuracy = source.AverageAccuracy,
                WeightedAverageAccuracy = source.WeightedAverageAccuracy,
                CompletionAccuracy = source.CompletionAccuracy,
                TotalPlayedLeaderboards = source.TotalPlayedLeaderboards,
                TotalPlayedRankedLeaderboards = source.TotalPlayedRankedLeaderboards,
                TotalSubmittedPlays = source.TotalSubmittedPlays,
                TotalReplayViews = source.TotalReplayViews,
                Device = Copy(source.Device)
            };
        }

        private static PlayerBasicProfileResponseStats Copy(PlayerBasicProfileResponseStats source) {
            return source == null ? null : new PlayerBasicProfileResponseStats {
                RealmId = source.RealmId,
                RealmName = source.RealmName,
                Rank = source.Rank,
                CountryRank = source.CountryRank,
                TotalPP = source.TotalPP,
                TotalScore = source.TotalScore,
                TotalRankedScore = source.TotalRankedScore,
                AverageAccuracy = source.AverageAccuracy,
                WeightedAverageAccuracy = source.WeightedAverageAccuracy,
                CompletionAccuracy = source.CompletionAccuracy,
                TotalPlayedLeaderboards = source.TotalPlayedLeaderboards,
                TotalPlayedRankedLeaderboards = source.TotalPlayedRankedLeaderboards,
                TotalSubmittedPlays = source.TotalSubmittedPlays,
                TotalReplayViews = source.TotalReplayViews,
                Device = Copy(source.Device)
            };
        }

        private static PlayerListResponseDataItemStatsDevice Copy(PlayerListResponseDataItemStatsDevice source) {
            return source == null ? null : new PlayerListResponseDataItemStatsDevice {
                HMD = source.HMD,
                ControllerLeft = source.ControllerLeft,
                ControllerRight = source.ControllerRight
            };
        }

        private static PlayerProfileResponseStatsDevice Copy(PlayerProfileResponseStatsDevice source) {
            return source == null ? null : new PlayerProfileResponseStatsDevice {
                HMD = source.HMD,
                ControllerLeft = source.ControllerLeft,
                ControllerRight = source.ControllerRight
            };
        }

        private static PlayerBasicProfileResponseStatsDevice Copy(PlayerBasicProfileResponseStatsDevice source) {
            return source == null ? null : new PlayerBasicProfileResponseStatsDevice {
                HMD = source.HMD,
                ControllerLeft = source.ControllerLeft,
                ControllerRight = source.ControllerRight
            };
        }
    }
}
