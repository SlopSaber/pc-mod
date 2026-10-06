using ScoreSaber.Core.Presentation;
using ScoreSaber.Features.Players.Domain;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Players.Profile {
    internal class ProfileDetailData {
        internal PlayerProfile Player { get; set; }
        internal string DisplayName { get; set; }
        internal string Avatar { get; set; }
        internal string RankText { get; set; }
        internal string PPText { get; set; }
        internal string RankedAccuracyText { get; set; }
        internal string TotalScoreText { get; set; }
        internal bool UsesFurryFont { get; set; }
        internal ProfileCrownData Crown { get; set; }
        internal List<ProfileBadgeData> Badges { get; set; } = new List<ProfileBadgeData>();

        internal static Task<ProfileDetailData> PrepareOwned(PlayerProfile player) {
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (culture.GetType() != typeof(CultureInfo)) {
                return Task.FromResult(Create(player));
            }

            string id = player.Id;
            string name = player.Name;
            string avatar = player.Avatar;
            int rank = player.Stats.Rank;
            double pp = player.Stats.TotalPP;
            double accuracy = player.Stats.AverageAccuracy;
            long totalScore = player.Stats.TotalScore;
            var badges = new List<CapturedBadge>();
            foreach (PlayerBadge badge in player.Badges) {
                badges.Add(new CapturedBadge(badge.Image, badge.Description));
            }

            var captured = new CapturedProfile(id, name, avatar, rank, pp, accuracy, totalScore, badges.ToArray());
            CultureInfo ownedCulture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
            return ReplayStorageService.QueueOwnedPreparation(() => CreateOwned(captured, ownedCulture));
        }

        private static ProfileDetailData CreateOwned(CapturedProfile player, CultureInfo culture) {
            Tuple<string, string> crownDetails = PlayerPresentation.GetCrownDetails(player.Id);
            var data = new ProfileDetailData {
                DisplayName = player.Name,
                Avatar = player.Avatar,
                RankText = "#" + string.Format(culture, "{0:n0}", player.Rank),
                PPText = "<color=#6772E5>" + string.Format(culture, "{0:n0}", player.PP) + "pp</color>",
                RankedAccuracyText = Math.Round(player.Accuracy, 2).ToString(culture) + "%",
                TotalScoreText = string.Format(culture, "{0:n0}", player.TotalScore),
                UsesFurryFont = PlayerPresentation.UsesFurryFont(player.Id),
                Crown = new ProfileCrownData {
                    Image = crownDetails.Item1,
                    Description = crownDetails.Item2
                }
            };

            foreach (CapturedBadge badge in player.Badges) {
                data.Badges.Add(new ProfileBadgeData {
                    Image = badge.Image,
                    Description = badge.Description
                });
            }
            return data;
        }

        private readonly struct CapturedBadge {
            internal readonly string Image;
            internal readonly string Description;

            internal CapturedBadge(string image, string description) {
                Image = image;
                Description = description;
            }
        }

        private sealed class CapturedProfile {
            internal readonly string Id;
            internal readonly string Name;
            internal readonly string Avatar;
            internal readonly int Rank;
            internal readonly double PP;
            internal readonly double Accuracy;
            internal readonly long TotalScore;
            internal readonly CapturedBadge[] Badges;

            internal CapturedProfile(string id, string name, string avatar, int rank, double pp,
                double accuracy, long totalScore, CapturedBadge[] badges) {
                Id = id;
                Name = name;
                Avatar = avatar;
                Rank = rank;
                PP = pp;
                Accuracy = accuracy;
                TotalScore = totalScore;
                Badges = badges;
            }
        }

        internal static ProfileDetailData Create(PlayerProfile player) {
            Tuple<string, string> crownDetails = PlayerPresentation.GetCrownDetails(player.Id);
            var data = new ProfileDetailData {
                Player = player,
                DisplayName = player.Name,
                Avatar = player.Avatar,
                RankText = $"#{string.Format("{0:n0}", player.Stats.Rank)}",
                PPText = $"<color=#6772E5>{string.Format("{0:n0}", player.Stats.TotalPP)}pp</color>",
                RankedAccuracyText = $"{Math.Round(player.Stats.AverageAccuracy, 2)}%",
                TotalScoreText = string.Format("{0:n0}", player.Stats.TotalScore),
                UsesFurryFont = PlayerPresentation.UsesFurryFont(player.Id),
                Crown = new ProfileCrownData {
                    Image = crownDetails.Item1,
                    Description = crownDetails.Item2
                }
            };

            foreach (PlayerBadge badge in player.Badges) {
                data.Badges.Add(new ProfileBadgeData {
                    Image = badge.Image,
                    Description = badge.Description
                });
            }

            return data;
        }
    }

    internal class ProfileCrownData {
        internal string Image { get; set; }
        internal string Description { get; set; }
        internal bool HasCrown => !string.IsNullOrEmpty(Image);
    }

    internal class ProfileBadgeData {
        internal string Image { get; set; }
        internal string Description { get; set; }
    }
}
