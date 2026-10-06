using ScoreSaber.Core.Api.Generated;
using ScoreSaber.Core.Api.Paging;
using ScoreSaber.Features.Leaderboards.Domain;
using ScoreSaber.Features.Replays;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {

    internal static class OwnedLeaderboardDomainPreparation {

        internal static Task<LeaderboardDetails> Prepare(LeaderboardResponse source) {
            if (source == null) {
                return Task.FromResult(GeneratedModelMapper.ToDomain(source));
            }

            LeaderboardResponse owned = new LeaderboardResponse {
                Id = source.Id,
                Map = Copy(source.Map),
                Difficulty = Copy(source.Difficulty),
                MaxScore = source.MaxScore,
                TotalScores = source.TotalScores,
                DailyScores = source.DailyScores,
                CreatedAt = source.CreatedAt,
                Realm = Copy(source.Realm)
            };
            if (!OwnedPlayerDomainPreparation.HasExplicitDate(owned.CreatedAt) ||
                (owned.Realm != null &&
                    (!OwnedPlayerDomainPreparation.HasExplicitDate(owned.Realm.RankedAt) ||
                     !OwnedPlayerDomainPreparation.HasExplicitDate(owned.Realm.QualifiedAt) ||
                     !OwnedPlayerDomainPreparation.HasExplicitDate(owned.Realm.LovedAt)))) {
                return Task.FromResult(GeneratedModelMapper.ToDomain(source));
            }
            return ReplayStorageService.QueueOwnedPreparation(() => GeneratedModelMapper.ToDomain(owned));
        }

        internal static async Task<PreparedScores> Prepare(LeaderboardScoresResponse source) {
            if (source == null || (source.Data != null && source.Data.GetType() != typeof(List<LeaderboardScoresResponseDataItem>))) {
                return MapScores(source);
            }

            List<LeaderboardScoresResponseDataItem> items = null;
            List<List<string>> originalMods = null;
            if (source.Data != null) {
                items = new List<LeaderboardScoresResponseDataItem>(source.Data.Count);
                originalMods = new List<List<string>>(source.Data.Count);
                foreach (LeaderboardScoresResponseDataItem item in source.Data) {
                    originalMods.Add(item?.Mods);
                    items.Add(Copy(item));
                }
            }
            List<string> originalPlayerMods = source.PlayerScore?.Mods;
            LeaderboardScoresResponse owned = new LeaderboardScoresResponse {
                Data = items,
                Metadata = source.Metadata == null ? null : new LeaderboardScoresResponseMetadata {
                    Page = source.Metadata.Page,
                    ItemsPerPage = source.Metadata.ItemsPerPage,
                    TotalItems = source.Metadata.TotalItems,
                    TotalPages = source.Metadata.TotalPages
                },
                PlayerScore = Copy(source.PlayerScore)
            };
            if ((owned.Data != null && owned.Data.Any(item => item != null && !OwnedPlayerDomainPreparation.HasExplicitDate(item.CreatedAt))) ||
                (owned.PlayerScore != null && !OwnedPlayerDomainPreparation.HasExplicitDate(owned.PlayerScore.CreatedAt))) {
                return MapScores(source);
            }

            // Borrowed Mods stay outside the worker closure and retain their exact identities.
            PreparedScores prepared = await ReplayStorageService.QueueOwnedPreparation(() => MapScores(owned));
            if (originalMods != null) {
                for (int i = 0; i < originalMods.Count; i++) {
                    if (originalMods[i] != null) {
                        prepared.Scores.Items[i].Mods = originalMods[i];
                    }
                }
            }
            if (originalPlayerMods != null && prepared.PlayerScore != null) {
                prepared.PlayerScore.Mods = originalPlayerMods;
            }
            return prepared;
        }

        private static PreparedScores MapScores(LeaderboardScoresResponse source) {
            return new PreparedScores(
                new PagedResult<LeaderboardScore> {
                    Items = source.Data.Select(score => GeneratedModelMapper.ToDomain(score)).ToList(),
                    Metadata = GeneratedModelMapper.ToDomain(source.Metadata)
                },
                GeneratedModelMapper.ToDomain(source.PlayerScore));
        }

        private static LeaderboardResponseMap Copy(LeaderboardResponseMap source) {
            return source == null ? null : new LeaderboardResponseMap {
                Hash = source.Hash,
                SongName = source.SongName,
                SongSubName = source.SongSubName,
                SongAuthorName = source.SongAuthorName,
                LevelAuthorName = source.LevelAuthorName,
                CoverUrl = source.CoverUrl
            };
        }

        private static LeaderboardResponseDifficulty Copy(LeaderboardResponseDifficulty source) {
            return source == null ? null : new LeaderboardResponseDifficulty {
                Difficulty = source.Difficulty,
                RawDifficulty = source.RawDifficulty,
                GameMode = source.GameMode
            };
        }

        private static LeaderboardResponseRealm Copy(LeaderboardResponseRealm source) {
            return source == null ? null : new LeaderboardResponseRealm {
                RealmId = source.RealmId,
                RealmName = source.RealmName,
                LeaderboardStatus = source.LeaderboardStatus,
                PositiveModifiers = source.PositiveModifiers,
                Stars = source.Stars,
                RankedAt = source.RankedAt,
                QualifiedAt = source.QualifiedAt,
                LovedAt = source.LovedAt
            };
        }

        private static LeaderboardScoresResponseDataItem Copy(LeaderboardScoresResponseDataItem source) {
            return source == null ? null : new LeaderboardScoresResponseDataItem {
                Id = source.Id,
                Rank = source.Rank,
                UnmodifiedScore = source.UnmodifiedScore,
                ModifiedScore = source.ModifiedScore,
                Accuracy = source.Accuracy,
                PP = source.PP,
                Weight = source.Weight,
                Mods = null,
                BadCuts = source.BadCuts,
                MissedNotes = source.MissedNotes,
                MaxCombo = source.MaxCombo,
                FullCombo = source.FullCombo,
                HasReplay = source.HasReplay,
                PersonalBest = source.PersonalBest,
                PlayOutcome = source.PlayOutcome,
                PlayOutcomeTime = source.PlayOutcomeTime,
                LegacyHMDId = source.LegacyHMDId,
                Version = source.Version,
                CreatedAt = source.CreatedAt,
                Player = Copy(source.Player),
                Device = Copy(source.Device)
            };
        }

        private static LeaderboardScoresResponsePlayerScore Copy(LeaderboardScoresResponsePlayerScore source) {
            return source == null ? null : new LeaderboardScoresResponsePlayerScore {
                Id = source.Id,
                Rank = source.Rank,
                UnmodifiedScore = source.UnmodifiedScore,
                ModifiedScore = source.ModifiedScore,
                Accuracy = source.Accuracy,
                PP = source.PP,
                Weight = source.Weight,
                Mods = null,
                BadCuts = source.BadCuts,
                MissedNotes = source.MissedNotes,
                MaxCombo = source.MaxCombo,
                FullCombo = source.FullCombo,
                HasReplay = source.HasReplay,
                PersonalBest = source.PersonalBest,
                PlayOutcome = source.PlayOutcome,
                PlayOutcomeTime = source.PlayOutcomeTime,
                LegacyHMDId = source.LegacyHMDId,
                Version = source.Version,
                CreatedAt = source.CreatedAt,
                Player = Copy(source.Player),
                Device = Copy(source.Device)
            };
        }

        private static LeaderboardScoresResponseDataItemPlayer Copy(LeaderboardScoresResponseDataItemPlayer source) {
            return source == null ? null : new LeaderboardScoresResponseDataItemPlayer {
                Id = source.Id,
                Name = source.Name,
                PlayerNameInGame = source.PlayerNameInGame,
                Country = source.Country,
                Role = source.Role,
                Avatar = source.Avatar,
                Permissions = source.Permissions
            };
        }

        private static LeaderboardScoresResponsePlayerScorePlayer Copy(LeaderboardScoresResponsePlayerScorePlayer source) {
            return source == null ? null : new LeaderboardScoresResponsePlayerScorePlayer {
                Id = source.Id,
                Name = source.Name,
                PlayerNameInGame = source.PlayerNameInGame,
                Country = source.Country,
                Role = source.Role,
                Avatar = source.Avatar,
                Permissions = source.Permissions
            };
        }

        private static LeaderboardScoresResponseDataItemDevice Copy(LeaderboardScoresResponseDataItemDevice source) {
            return source == null ? null : new LeaderboardScoresResponseDataItemDevice {
                HMD = source.HMD,
                ControllerLeft = source.ControllerLeft,
                ControllerRight = source.ControllerRight
            };
        }

        private static LeaderboardScoresResponsePlayerScoreDevice Copy(LeaderboardScoresResponsePlayerScoreDevice source) {
            return source == null ? null : new LeaderboardScoresResponsePlayerScoreDevice {
                HMD = source.HMD,
                ControllerLeft = source.ControllerLeft,
                ControllerRight = source.ControllerRight
            };
        }

        internal sealed class PreparedScores {
            internal readonly PagedResult<LeaderboardScore> Scores;
            internal readonly LeaderboardScore PlayerScore;

            internal PreparedScores(PagedResult<LeaderboardScore> scores, LeaderboardScore playerScore) {
                Scores = scores;
                PlayerScore = playerScore;
            }
        }
    }
}
