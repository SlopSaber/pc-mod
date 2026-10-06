using BeatSaberMarkupLanguage.Attributes;
using HMUI;
using ScoreSaber.Core;
using ScoreSaber.Core.Presentation;
using System;

namespace ScoreSaber.Features.MainMenu.MainFlow.GlobalLeaderboard {
    internal class GlobalCell {

        #region BSML Components
        [UIComponent("profile-image")]
        private readonly ImageView _imageView = null;
        #endregion

        #region BSML Values
        [UIValue("pfp-url")]
        private readonly string _avatarUrl;

        [UIValue("username")]
        private readonly string _username;

        [UIValue("rank")]
        private readonly string _globalRank;

        [UIValue("pp")]
        private readonly string _ppText;

        [UIValue("flag-url")]
        private readonly string _flagUrl;

        [UIValue("country")]
        private readonly string _countryText;
        #endregion

        private readonly string _identifier;
        private readonly Action<string, string> _profileClicked;
        private readonly ScoreSaberUIMaterials _materials;

        public GlobalCell(ScoreSaberUIMaterials materials, string id, string avatarUrl, string username, string country, string rank, double pp, Action<string, string> onActivateProfile = null) {

            _materials = materials;
            _identifier = id;
            _avatarUrl = avatarUrl;
            _ppText = string.Format("<color=#6772E5>{0:n0}pp</color>", pp);
            _username = username;
            _globalRank = rank;
            _profileClicked = onActivateProfile;
            _countryText = $"{country}";
            _flagUrl = ScoreSaberEndpoints.Flag(country);
        }

        internal GlobalCell(ScoreSaberUIMaterials materials, PreparedGlobalRow row, Action<string, string> onActivateProfile) {
            _materials = materials;
            _identifier = row.Identifier;
            _avatarUrl = row.AvatarUrl;
            _ppText = row.PPText;
            _username = row.Username;
            _globalRank = row.RankText;
            _profileClicked = onActivateProfile;
            _countryText = row.CountryText;
            _flagUrl = row.FlagUrl;
        }

        [UIAction("profile-clicked")]
        private void ProfileClicked() {

            _profileClicked?.Invoke(_identifier, _username);
        }

        [UIAction("#post-parse")]
        private void Parsed() {

            _imageView.material = _materials.RoundedImageMaterial;
        }
    }

    internal readonly struct CapturedGlobalRow {
        internal readonly string Identifier;
        internal readonly string AvatarUrl;
        internal readonly string Username;
        internal readonly string Country;
        internal readonly int Rank;
        internal readonly double PP;

        internal CapturedGlobalRow(string identifier, string avatarUrl, string username, string country, int rank, double pp) {
            Identifier = identifier;
            AvatarUrl = avatarUrl;
            Username = username;
            Country = country;
            Rank = rank;
            PP = pp;
        }
    }

    internal sealed class PreparedGlobalRow {
        internal readonly string Identifier;
        internal readonly string AvatarUrl;
        internal readonly string Username;
        internal readonly string RankText;
        internal readonly string PPText;
        internal readonly string CountryText;
        internal readonly string FlagUrl;

        internal PreparedGlobalRow(CapturedGlobalRow row, string rankText, string ppText, string flagUrl) {
            Identifier = row.Identifier;
            AvatarUrl = row.AvatarUrl;
            Username = row.Username;
            RankText = rankText;
            PPText = ppText;
            CountryText = row.Country ?? string.Empty;
            FlagUrl = flagUrl;
        }
    }
}
