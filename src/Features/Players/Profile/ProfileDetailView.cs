using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using HMUI;
using ScoreSaber.Core.Presentation;
using ScoreSaber.Core;
using ScoreSaber.Features.Leaderboards.UI;
using ScoreSaber.Features.Players.Services;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Players.Profile {

    internal class ProfileDetailView : MonoBehaviour, INotifyPropertyChanged {

        public event PropertyChangedEventHandler PropertyChanged;

        #region BSML Components
        [UIComponent("profile-modal-root")]
        public ModalView profileModalRoot = null;

        [UIComponent("profile-top")]
        protected ImageView _profileTop = null;

        [UIComponent("profile-line-border")]
        protected ImageView _profileLineBorder = null;

        [UIComponent("profile-picture")]
        public readonly ImageView profilePicture = null;

        [UIComponent("profile-prefix-picture")]
        protected readonly ImageView _profilePrefixPicture = null;
        public string profilePrefixPicture {
            set {
                bool hasImage = value != null;
                _profilePrefixPicture.gameObject.SetActive(hasImage);
                if (hasImage) {
                    _profilePrefixPicture.SetImageAsync(value).RunTask();
                }
            }
        }

        [UIComponent("player-name-text")]
        public readonly CurvedTextMeshPro playerNameText = null;

        [UIComponent("rank-text")]
        public readonly CurvedTextMeshPro rankText = null;

        [UIComponent("pp-text")]
        public readonly CurvedTextMeshPro ppText = null;

        [UIComponent("ranked-acc-text")]
        public readonly CurvedTextMeshPro rankedAccText = null;

        [UIComponent("total-score-text")]
        public readonly CurvedTextMeshPro totalScoreText = null;
        #endregion

        #region BSML Values
        private readonly ProfileBadgeHost _badgeHost = new ProfileBadgeHost();
        [UIValue("badge-host")]
        protected ProfileBadgeHost badgeHost => _badgeHost;

        private bool _profileSet = false;
        [UIValue("profile-set")]
        public bool profileSet {
            get => _profileSet;
            set {
                _profileSet = value;
                NotifyPropertyChanged();
            }
        }
        private bool _profileSetLoading = false;
        [UIValue("profile-set-loading")]
        public bool profileSetLoading {
            get => _profileSetLoading;
            set {
                _profileSetLoading = value;
                NotifyPropertyChanged();
            }
        }
        #endregion

        #region Custom Properties
        private ProfileDetailData _profileInfo { get; set; }
        private bool _isCyan { get; set; }

        private readonly HoverHint _profileHoverHint = null;
        private HoverHint profileHoverHint => _profileHoverHint ?? _profilePrefixPicture.gameObject.GetComponent<HoverHint>();
        #endregion

        private PlayerProfileService _playerProfileService = null;
        private ScoreSaberUIMaterials _materials = null;
        private IDisposable _hotReload;
        private Guid _profileRequest;
        private bool _destroyed;

        [Inject]
        private void Construct(PlayerProfileService playerProfileService, ScoreSaberUIMaterials materials) {
            _playerProfileService = playerProfileService;
            _materials = materials;
            ApplyRoundedMaterials();
        }

        internal void UseHotReload(IDisposable hotReload) {
            _hotReload?.Dispose();
            _hotReload = hotReload;
        }

        private void OnDestroy() {
            _destroyed = true;
            _profileRequest = Guid.Empty;
            _hotReload?.Dispose();
        }

        [UIAction("profile-url-click")]
        private void ProfileURLClicked() {
            if (_profileInfo == null) {
                return;
            }

            Application.OpenURL(ScoreSaberEndpoints.Player(_profileInfo.Player.Id));
        }

        [UIAction("#post-parse")]
        protected void Parsed() {
            _profileTop.material = Utilities.ImageResources.NoGlowMat;
            var background = profileModalRoot.gameObject.transform.GetChild(0);
            background.gameObject.SetActive(false);

            var modalPic = profilePicture;
            PanelView.ImageSkew(ref modalPic) = 0f;
            PanelView.ImageSkew(ref _profileLineBorder) = 0f;
            PanelView.ImageSkew(ref _profileTop) = 0f;

            ApplyRoundedMaterials();
        }

        internal Task ShowProfile(string playerId) => ShowProfile(playerId, out _);

        internal Task ShowProfile(string playerId, out Guid request) {
            request = Guid.NewGuid();
            _profileRequest = request;
            return ShowProfileCore(playerId, request);
        }

        internal bool IsCurrentProfileRequest(Guid request) =>
            request != Guid.Empty && !_destroyed && this != null && request == _profileRequest;

        private async Task ShowProfileCore(string playerId, Guid request) {
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            ApplyCrown(null, request);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            SetLoadingState(true, request);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }

            var player = await _playerProfileService.GetPlayerInfo(playerId, full: true);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            ProfileDetailData profile = await ProfileDetailData.PrepareOwned(player);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            profile.Player = player;
            _profileInfo = profile;

            await ApplyProfileFont(profile, request);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            playerNameText.text = profile.DisplayName;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
#pragma warning disable CS0612 // Type or member is obsolete
            profilePicture.SetImage(profile.Avatar);
#pragma warning restore CS0612 // Type or member is obsolete
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            rankText.text = profile.RankText;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            ppText.text = profile.PPText;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            rankedAccText.text = profile.RankedAccuracyText;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            totalScoreText.text = profile.TotalScoreText;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            _badgeHost.SetBadges(profile.Badges, () => IsCurrentProfileRequest(request));
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            ApplyCrown(profile.Crown, request);
            SetLoadingState(false, request);
        }

        public void SetProfileBadges(System.Collections.Generic.IReadOnlyList<ProfileBadgeData> badges) => _badgeHost.SetBadges(badges);

        public void SetLoadingState(bool loading) {
            profileSet = !loading;
            profileSetLoading = loading;
        }

        internal void SetLoadingState(bool loading, Guid request) {
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            profileSet = !loading;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            profileSetLoading = loading;
        }

        private async Task ApplyProfileFont(ProfileDetailData profile, Guid request) {
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            if (profile.UsesFurryFont) {
                var mat = await _materials.GetFurryFontMaterial();
                if (!IsCurrentProfileRequest(request)) {
                    return;
                }
                playerNameText.fontMaterial = mat;
                if (!IsCurrentProfileRequest(request)) {
                    return;
                }
                _isCyan = true;
                return;
            }
            if (_isCyan) {
                var mat = _materials.DefaultFontMaterial;
                if (!IsCurrentProfileRequest(request)) {
                    return;
                }
                playerNameText.fontMaterial = mat;
            }
        }

        private void ApplyRoundedMaterials() {
            if (_materials == null || profilePicture == null) {
                return;
            }

            var modalPic = profilePicture;
            modalPic.material = _materials.RoundedImageMaterial;
        }

        private void SetProfilePrefixPicture(string value, Guid request) {
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            bool hasImage = value != null;
            _profilePrefixPicture.gameObject.SetActive(hasImage);
            if (hasImage && IsCurrentProfileRequest(request)) {
                _profilePrefixPicture.SetImageAsync(value).RunTask();
            }
        }

        private void ApplyCrown(ProfileCrownData crown, Guid request) {
            SetProfilePrefixPicture(null, request);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            bool hasCrown = crown != null && crown.HasCrown;
            var hint = profileHoverHint;
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            hint.enabled = hasCrown;
            if (!hasCrown || !IsCurrentProfileRequest(request)) {
                return;
            }
            SetProfilePrefixPicture(crown.Image, request);
            if (!IsCurrentProfileRequest(request)) {
                return;
            }
            hint.text = crown.Description;
        }

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
