using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.ViewControllers;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Parser;
using ScoreSaber.Core;
using ScoreSaber.Core.Configuration;
using ScoreSaber.Features.Live.Ludus.Domain;
using ScoreSaber.Features.Live.Ludus.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace ScoreSaber.Features.Live.Ludus.UI {
    [HotReload(RelativePathToLayout = @"./LiveChatFloatingViewController.bsml")]
    internal class LiveChatFloatingViewController : BSMLAutomaticViewController {
        internal const float ChatWidth = 120f;
        internal const float ChatHeight = 140f;

        private const int VisibleMessageCount = 10;
        private const float FooterReserve = 10f;
        private const float FooterWithStatusReserve = 18f;
        private const float TopPadding = 4f;
        private const float MessageRowMinHeight = 8.4f;
        private const float MessageTextWidth = ChatWidth - 11f;
        private const float MessageTextFontSize = 3.4f;
        private const float MessageTextVerticalPadding = 2f;
        private const float TextLineSpacing = 1.5f;
        private const float KeyboardDistance = 0.75f;
        private const float KeyboardVerticalOffset = -0.28f;
        private const float KeyboardWidth = 50f;
        private const float KeyboardHeight = 28f;
        private const float KeyboardContentScale = 0.46f;
        private const float KeyboardDismissWidth = 150f;
        private const float KeyboardDismissHeight = 90f;
        private const float StatusAutoClearSeconds = 3f;
        private const string DefaultStatus = "Spectator chat";

        private static readonly Color ChatBackground = new Color(0f, 0f, 0f, 0.12f);
        private static readonly Color ChatHighlight = new Color(0.980f, 0.800f, 0.082f, 0.08f);
        private static readonly Color ChatAccent = new Color(0.980f, 0.800f, 0.082f, 1f);
        private static readonly Color LinkAccent = new Color(0.18f, 0.75f, 1f, 1f);
        private const string ChatNameColor = "#CDEEFF";
        private const string LogNameColor = "#BBBBBB";
        private const string TimeColor = "#BBBBBB";
        private static readonly Color MessageColor = Color.white;

        private SettingsService _settings;
        private LudusSessionService _ludusSession;
        private LiveChatLinkService _linkService;
        private string _chatDraft = string.Empty;
        private string _status = DefaultStatus;
        private string _viewerStatus = string.Empty;
        private float _appliedTextScale = -1f;
        private int _statusVersion;
        private bool _statusAutoClear;
        private IReadOnlyList<LiveChatEntry> _currentMessages = Array.Empty<LiveChatEntry>();
        private LiveChatFloatingRow[] _visibleRows = Array.Empty<LiveChatFloatingRow>();
        private readonly List<GameObject> _messageObjects = new List<GameObject>();
        private GameObject _chatButtonObject;
        private GameObject _keyboardDismissZonesObject;
        private Coroutine _statusClearCoroutine;
        private TMP_FontAsset _chatFont;
        private TextMeshProUGUI _messageMeasurementText;
        private bool _useOwnedMessages;
        private bool _presentationRetired;
        private long _messageVersion;
        private long _preparationVersion = -1;
        private long _preparationCacheVersion;
        private CultureInfo _preparationCulture;
        private TimeZoneInfo _preparationTimeZone;
        private Task<LiveChatLinkService.OwnedPresentationResult> _preparation;
        private RenderContext _renderContext;

        [UIParams]
        private readonly BSMLParserParams _parserParams = null;

        [UIComponent("chat-keyboard")]
        private readonly ModalKeyboard _chatKeyboard = null;

        [UIValue("chat-draft")]
        private string chatDraft {
            get => _chatDraft;
            set => SetValue(ref _chatDraft, value, nameof(chatDraft));
        }

        [UIValue("status")]
        private string status {
            get => _status;
            set {
                SetValue(ref _status, value, nameof(status));
                RenderMessageRows();
            }
        }

        [UIValue("status-font-size")]
        private float statusFontSize => 2.65f * CurrentTextScale;

        [UIValue("empty-font-size")]
        private float emptyFontSize => 3.2f * CurrentTextScale;

        [UIValue("control-font-size")]
        private float controlFontSize => 3.35f * CurrentTextScale;

        [UIValue("small-control-font-size")]
        private float smallControlFontSize => 2.4f * CurrentTextScale;

        [Inject]
        internal void Construct(SettingsService settings, LudusSessionService ludusSession, LiveChatLinkService linkService) {
            _settings = settings;
            _ludusSession = ludusSession;
            _linkService = linkService;
            _linkService.StatusChanged += SetStatus;
            _linkService.ResolvedTextChanged += RebuildMessages;
        }

        protected override void OnDestroy() {
            RetireOwnedPresentation();
            if (_linkService != null) {
                _linkService.StatusChanged -= SetStatus;
                _linkService.ResolvedTextChanged -= RebuildMessages;
            }
            base.OnDestroy();
        }

        internal void SetMessages(IReadOnlyList<LiveChatEntry> messages) {
            _useOwnedMessages = false;
            _messageVersion++;
            _currentMessages = messages?.ToArray() ?? Array.Empty<LiveChatEntry>();
            RebuildMessages();
        }

        internal void SetViewerCount(int viewerCount) {
            string nextStatus = viewerCount < 0 ? string.Empty : FormatViewerStatus(viewerCount);
            if (_viewerStatus == nextStatus) {
                return;
            }

            _viewerStatus = nextStatus;
            RenderMessageRows();
        }

        internal void RefreshLayoutSettings() {
            float nextTextScale = CurrentTextScale;
            if (Math.Abs(_appliedTextScale - nextTextScale) < 0.001f) {
                return;
            }

            _appliedTextScale = nextTextScale;
            NotifyTextScaleChanged();
            RebuildMessages();
        }

        private void RebuildMessages() {
            if (_useOwnedMessages) {
                _messageVersion++;
                StartOwnedPreparation();
                return;
            }

            RebuildImmediateMessages();
        }

        private void RebuildImmediateMessages() {
            _visibleRows = _currentMessages
                .Skip(System.Math.Max(0, _currentMessages.Count - VisibleMessageCount))
                .Select(entry => new LiveChatFloatingRow(entry, _linkService.FirstLink(entry.Text), _linkService.DisplaySenderName(entry), _linkService.DisplayText(entry)))
                .ToArray();

            RenderMessageRows();
        }

        internal void SetOwnedMessages(IReadOnlyList<LiveChatEntry> messages) {
            if (_presentationRetired) {
                return;
            }

            _currentMessages = messages?.ToArray() ?? Array.Empty<LiveChatEntry>();
            _useOwnedMessages = true;
            RebuildMessages();
        }

        internal void RetireOwnedPresentation() {
            _presentationRetired = true;
            _messageVersion++;
        }

        private void StartOwnedPreparation() {
            if (!_useOwnedMessages || _presentationRetired || _preparation != null || _preparationVersion == _messageVersion) {
                return;
            }

            _preparationVersion = _messageVersion;
            CultureInfo culture = CultureInfo.CurrentCulture;
            LiveChatEntry[] entries = _currentMessages.Skip(Math.Max(0, _currentMessages.Count - VisibleMessageCount)).ToArray();
            if (culture.GetType() != typeof(CultureInfo) || !culture.IsReadOnly
                || entries.Any(entry => entry == null || entry.CreatedAtUnixMs > 253402300799999L)) {
                RebuildImmediateMessages();
                return;
            }

            _preparationCulture = culture;
            _preparationTimeZone = TimeZoneInfo.Local;
            _preparationCacheVersion = _linkService.TextCacheVersion;
            _preparation = _linkService.PrepareOwnedPresentation(entries, culture, _preparationTimeZone);
            _ = _preparation.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private bool PreparationIsCurrent(long version, long cacheVersion, CultureInfo culture, TimeZoneInfo timeZone) => _useOwnedMessages && !_presentationRetired
            && version == _messageVersion && cacheVersion == _linkService.TextCacheVersion
            && ReferenceEquals(culture, CultureInfo.CurrentCulture)
            && ReferenceEquals(timeZone, TimeZoneInfo.Local);

        internal void FlushOwnedPresentation() {
            if (_preparation == null || !_preparation.IsCompleted) {
                StartOwnedPreparation();
                return;
            }

            Task<LiveChatLinkService.OwnedPresentationResult> completed = _preparation;
            long version = _preparationVersion;
            long cacheVersion = _preparationCacheVersion;
            CultureInfo culture = _preparationCulture;
            TimeZoneInfo timeZone = _preparationTimeZone;
            _preparation = null;
            LiveChatLinkService.OwnedPresentationResult result;
            try {
                result = completed.GetAwaiter().GetResult();
            } catch (Exception ex) {
                if (PreparationIsCurrent(version, cacheVersion, culture, timeZone)) {
                    Plugin.Log.Warn($"Failed to prepare live chat: {ex.Message}");
                }
                StartOwnedPreparation();
                return;
            }

            if (!PreparationIsCurrent(version, cacheVersion, culture, timeZone)) {
                if (version == _messageVersion) {
                    _messageVersion++;
                }
                StartOwnedPreparation();
                return;
            }

            foreach (LiveChatLinkService.ResolutionRequest request in result.Resolutions) {
                if (!PreparationIsCurrent(version, cacheVersion, culture, timeZone)) {
                    StartOwnedPreparation();
                    return;
                }
                _linkService.AdmitResolution(request);
                if (!PreparationIsCurrent(version, cacheVersion, culture, timeZone)) {
                    StartOwnedPreparation();
                    return;
                }
            }

            if (result.Error != null) {
                Plugin.Log.Warn($"Failed to prepare live chat: {result.Error.Message}");
                return;
            }

            _visibleRows = result.Rows;
            RenderMessageRows();
        }

        internal void SetStatus(string value) {
            SetStatus(value, ShouldAutoClearStatus(value));
        }

        internal void ResumeStatusAutoClear() {
            StartStatusAutoClearIfReady();
        }

        private void SetStatus(string value, bool autoClear) {
            _statusVersion++;
            status = string.IsNullOrEmpty(value) ? DefaultStatus : value;
            _statusAutoClear = autoClear && ShouldAutoClearStatus(status);

            if (_statusClearCoroutine != null) {
                StopCoroutine(_statusClearCoroutine);
                _statusClearCoroutine = null;
            }

            StartStatusAutoClearIfReady();
        }

        private void StartStatusAutoClearIfReady() {
            if (!_statusAutoClear || _statusClearCoroutine != null || !gameObject.activeInHierarchy) {
                return;
            }

            _statusClearCoroutine = StartCoroutine(ClearStatusAfterDelay(_statusVersion));
        }

        private IEnumerator ClearStatusAfterDelay(int statusVersion) {
            yield return new WaitForSeconds(StatusAutoClearSeconds);
            if (_statusVersion == statusVersion) {
                _statusVersion++;
                _statusAutoClear = false;
                status = DefaultStatus;
            }

            _statusClearCoroutine = null;
        }

        [UIAction("#post-parse")]
        private void Parsed() {
            RectTransform rectTransform = transform as RectTransform;
            if (rectTransform != null) {
                rectTransform.pivot = new Vector2(0.5f, 0f);
                rectTransform.sizeDelta = new Vector2(ChatWidth, ChatHeight);
            }

            PositionKeyboard();
            EnsureKeyboardDismissZones();
            EnsureChatButton();
            RenderMessageRows();
        }

        [UIAction("noop")]
        private void Noop() {
        }

        [UIAction("chat-entered")]
        private void ChatEntered(string value) {
            SendChatValue(value);
        }

        [UIAction("send-chat")]
        private void SendChat() {
            SendChatValue(chatDraft);
        }

        private void SendChatValue(string value) {
            string message = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(message)) {
                chatDraft = string.Empty;
                SetStatus("Enter a message first.");
                return;
            }

            if (_ludusSession.SendChatMessage(message)) {
                chatDraft = string.Empty;
                SetStatus(DefaultStatus, false);
            } else {
                SetStatus("Live chat is not connected.");
            }
        }

        [UIAction("decrease-text-size")]
        private void DecreaseTextSize() {
            AdjustTextScale(-0.1f);
        }

        [UIAction("increase-text-size")]
        private void IncreaseTextSize() {
            AdjustTextScale(0.1f);
        }

        [UIAction("decrease-window-size")]
        private void DecreaseWindowSize() {
            AdjustWindowScale(-0.1f);
        }

        [UIAction("increase-window-size")]
        private void IncreaseWindowSize() {
            AdjustWindowScale(0.1f);
        }

        private void OpenLink(LiveChatLinkTarget target) {
            _linkService.Open(target, CancellationToken.None).RunTask();
        }

        private float CurrentTextScale => Clamp(_settings?.Current.liveChatOverlayTextScale ?? 1.25f, 0.9f, 1.8f);

        private void AdjustTextScale(float delta) {
            if (_settings == null) {
                return;
            }

            _settings.Current.liveChatOverlayTextScale = Clamp(_settings.Current.liveChatOverlayTextScale + delta, 0.9f, 1.8f);
            _settings.Save();
            SetStatus("Text " + _settings.Current.liveChatOverlayTextScale.ToString("0.00") + "x");
            _appliedTextScale = -1f;
            RefreshLayoutSettings();
        }

        private void AdjustWindowScale(float delta) {
            if (_settings == null) {
                return;
            }

            _settings.Current.liveChatOverlayScale = Clamp(_settings.Current.liveChatOverlayScale + delta, 0.85f, 1.75f);
            _settings.Save();
            SetStatus("Window " + _settings.Current.liveChatOverlayScale.ToString("0.00") + "x");
        }

        private void NotifyTextScaleChanged() {
            NotifyPropertyChanged(nameof(statusFontSize));
            NotifyPropertyChanged(nameof(emptyFontSize));
            NotifyPropertyChanged(nameof(controlFontSize));
            NotifyPropertyChanged(nameof(smallControlFontSize));
        }

        private void SetValue<T>(ref T field, T value, string propertyName) {
            field = value;
            NotifyPropertyChanged(propertyName);
        }

        private static float Clamp(float value, float min, float max) {
            if (value < min) {
                return min;
            }

            if (value > max) {
                return max;
            }

            return value;
        }

        private static bool ShouldAutoClearStatus(string value) {
            if (string.IsNullOrWhiteSpace(value) || value == DefaultStatus) {
                return false;
            }

            return !StartsWithAny(
                value,
                "Resolving linked map",
                "Checking linked map",
                "Downloading linked map");
        }

        private static bool StartsWithAny(string value, params string[] prefixes) {
            for (int i = 0; i < prefixes.Length; i++) {
                if (value.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

        private static string FormatViewerStatus(int viewerCount) {
            int safeCount = System.Math.Max(0, viewerCount);
            return safeCount == 1 ? "1 viewer" : $"{safeCount} viewers";
        }

        private bool HasStatusLine => !string.IsNullOrWhiteSpace(status) && status != DefaultStatus;

        private sealed class RenderContext {
            internal RenderContext(long messageVersion, long cacheVersion) {
                MessageVersion = messageVersion;
                CacheVersion = cacheVersion;
            }
            internal long MessageVersion { get; }
            internal long CacheVersion { get; }
            internal List<GameObject> UncommittedObjects { get; } = new List<GameObject>();
        }

        private sealed class StalePresentationException : Exception { }

        private bool RenderIsCurrent(RenderContext context) => context == null
            || (!_presentationRetired && ReferenceEquals(_renderContext, context)
                && context.MessageVersion == _messageVersion
                && context.CacheVersion == (_linkService?.TextCacheVersion ?? 0));

        private void CheckRender(RenderContext context) {
            if (!RenderIsCurrent(context)) {
                throw new StalePresentationException();
            }
        }

        private GameObject CreatePresentationObject(string name, RenderContext context, bool track = false) {
            CheckRender(context);
            GameObject created = new GameObject(name, typeof(RectTransform));
            if (!RenderIsCurrent(context)) {
                Destroy(created);
                throw new StalePresentationException();
            }
            if (track) {
                _messageObjects.Add(created);
            } else {
                context?.UncommittedObjects.Add(created);
            }
            return created;
        }

        private void RenderMessageRows() {
            if (_presentationRetired) {
                return;
            }
            var context = new RenderContext(_messageVersion, _linkService?.TextCacheVersion ?? 0);
            _renderContext = context;
            CheckRender(context);
            LiveChatFloatingRow[] rows = _visibleRows;
            CheckRender(context);
            try {
                if (_presentationRetired || transform == null) {
                    return;
                }
                EnsureChatButton();
                CheckRender(context);
                ClearMessageObjects();
                CheckRender(context);
                AddViewerStatusLine();
                CheckRender(context);
                AddStatusLine();
                CheckRender(context);
                if (rows.Length == 0) {
                    return;
                }
                float y = HasStatusLine ? FooterWithStatusReserve : FooterReserve;
                CheckRender(context);
                for (int i = rows.Length - 1; i >= 0; i--) {
                    float rowHeight = RowHeightFor(rows[i]);
                    CheckRender(context);
                    if (y + rowHeight > ChatHeight - TopPadding) {
                        break;
                    }
                    AddMessageRow(rows[i], y, rowHeight);
                    CheckRender(context);
                    y += rowHeight;
                    CheckRender(context);
                }

            } catch (StalePresentationException) {
            } finally {
                if (ReferenceEquals(_renderContext, context)) {
                    _renderContext = null;
                }
                foreach (GameObject unfinished in context.UncommittedObjects) {
                    Destroy(unfinished);
                }
            }
        }

        private float RowHeightFor(LiveChatFloatingRow row) {
            RenderContext context = _renderContext;
            CheckRender(context);
            float minimumHeight = MessageRowMinHeight * CurrentTextScale;
            CheckRender(context);
            float measuredHeight = MeasureMessageTextHeight(row.DisplayText) + MessageTextVerticalPadding;
            CheckRender(context);
            if (measuredHeight < minimumHeight) {
                return minimumHeight;
            }

            return measuredHeight;
        }

        private void EnsureChatButton() {
            RenderContext context = _renderContext;
            CheckRender(context);
            if (_chatButtonObject != null || transform == null) {
                return;
            }

            GameObject root = CreatePresentationObject("Live Chat Open Button", context);
            CheckRender(context);
            root.transform.SetParent(transform, false);
            CheckRender(context);
            root.transform.SetAsLastSibling();
            CheckRender(context);

            RectTransform rectTransform = root.transform as RectTransform;
            CheckRender(context);
            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.pivot = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.sizeDelta = new Vector2(45f, 7.5f);
            CheckRender(context);
            rectTransform.anchoredPosition = new Vector2((ChatWidth * 0.5f) - 24.5f, 1.5f);
            CheckRender(context);
            rectTransform.localScale = Vector3.one;
            CheckRender(context);

            Image background = root.AddComponent<Image>();
            CheckRender(context);
            background.color = new Color(0f, 0f, 0f, 0.22f);
            CheckRender(context);
            background.raycastTarget = true;
            CheckRender(context);
            ApplyNoGlow(background);
            CheckRender(context);

            Button button = root.AddComponent<Button>();
            CheckRender(context);
            button.transition = Selectable.Transition.None;
            CheckRender(context);
            button.targetGraphic = background;
            CheckRender(context);
            button.onClick.AddListener(OpenKeyboard);
            CheckRender(context);

            AddText(root.transform, "Send Message", new Color(0.8f, 0.92f, 1f, 0.92f), 2.45f * CurrentTextScale, new Vector2(0f, -0.65f), new Vector2(45f, 7.5f), TextAlignmentOptions.Center);
            CheckRender(context);
            context?.UncommittedObjects.Remove(root);
            _chatButtonObject = root;
        }

        private void OpenKeyboard() {
            PositionKeyboard();
            EnsureKeyboardDismissZones();
            _parserParams?.EmitEvent("open-chat-keyboard");
            StartCoroutine(PositionKeyboardNextFrame());
        }

        private IEnumerator PositionKeyboardNextFrame() {
            yield return null;
            PositionKeyboard();
            EnsureKeyboardDismissZones();
        }

        private void PositionKeyboard() {
            if (_chatKeyboard == null) {
                return;
            }

            RectTransform rectTransform = _chatKeyboard.transform as RectTransform;
            if (rectTransform == null) {
                return;
            }

            ApplyKeyboardSizing(rectTransform);

            Camera mainCamera = Camera.main;
            if (mainCamera != null) {
                Transform cameraTransform = mainCamera.transform;
                Vector3 position = cameraTransform.position
                    + (cameraTransform.forward * KeyboardDistance)
                    + (cameraTransform.up * KeyboardVerticalOffset);
                rectTransform.position = position;
                rectTransform.rotation = Quaternion.LookRotation(position - cameraTransform.position, cameraTransform.up);
                rectTransform.localScale = Vector3.one;
                return;
            }

            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0f, -58f);
            rectTransform.localScale = Vector3.one;
        }

        private void ApplyKeyboardSizing(RectTransform rectTransform) {
            rectTransform.sizeDelta = new Vector2(KeyboardWidth, KeyboardHeight);
            ApplyKeyboardBackgroundSizing(rectTransform);

            Transform keyboardParent = rectTransform.Find("KeyboardParent");
            if (keyboardParent == null) {
                return;
            }

            RectTransform keyboardParentRect = keyboardParent as RectTransform;
            if (keyboardParentRect != null) {
                keyboardParentRect.anchoredPosition = Vector2.zero;
                keyboardParentRect.sizeDelta = new Vector2(KeyboardWidth, KeyboardHeight);
            }

            keyboardParent.localScale = Vector3.one * KeyboardContentScale;
        }

        private void ApplyKeyboardBackgroundSizing(RectTransform rectTransform) {
            RectTransform background = rectTransform.Find("BG") as RectTransform;
            if (background == null) {
                return;
            }

            background.anchorMin = new Vector2(0.5f, 0.5f);
            background.anchorMax = new Vector2(0.5f, 0.5f);
            background.pivot = new Vector2(0.5f, 0.5f);
            background.anchoredPosition = Vector2.zero;
            background.sizeDelta = new Vector2(KeyboardWidth, KeyboardHeight);
            background.localScale = Vector3.one;
        }

        private void HideKeyboard() {
            _parserParams?.EmitEvent("hide-chat-keyboard");
        }

        private void EnsureKeyboardDismissZones() {
            if (_keyboardDismissZonesObject != null || _chatKeyboard == null) {
                return;
            }

            _keyboardDismissZonesObject = new GameObject("Live Chat Keyboard Outside Dismiss Zones", typeof(RectTransform));
            _keyboardDismissZonesObject.transform.SetParent(_chatKeyboard.transform, false);
            _keyboardDismissZonesObject.transform.SetAsFirstSibling();

            RectTransform root = _keyboardDismissZonesObject.transform as RectTransform;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = Vector2.zero;
            root.localScale = Vector3.one;

            float sideWidth = (KeyboardDismissWidth - KeyboardWidth) * 0.5f;
            float verticalHeight = (KeyboardDismissHeight - KeyboardHeight) * 0.5f;
            AddKeyboardDismissZone(root, new Vector2(-(KeyboardWidth * 0.5f) - (sideWidth * 0.5f), 0f), new Vector2(sideWidth, KeyboardDismissHeight));
            AddKeyboardDismissZone(root, new Vector2((KeyboardWidth * 0.5f) + (sideWidth * 0.5f), 0f), new Vector2(sideWidth, KeyboardDismissHeight));
            AddKeyboardDismissZone(root, new Vector2(0f, (KeyboardHeight * 0.5f) + (verticalHeight * 0.5f)), new Vector2(KeyboardWidth, verticalHeight));
            AddKeyboardDismissZone(root, new Vector2(0f, -(KeyboardHeight * 0.5f) - (verticalHeight * 0.5f)), new Vector2(KeyboardWidth, verticalHeight));
        }

        private void AddKeyboardDismissZone(Transform parent, Vector2 anchoredPosition, Vector2 sizeDelta) {
            GameObject zoneObject = new GameObject("Dismiss Zone", typeof(RectTransform));
            zoneObject.transform.SetParent(parent, false);

            RectTransform rectTransform = zoneObject.transform as RectTransform;
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = sizeDelta;
            rectTransform.localScale = Vector3.one;

            Image image = zoneObject.AddComponent<Image>();
            image.color = Color.clear;
            image.raycastTarget = true;
            ApplyNoGlow(image);

            Button button = zoneObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            button.onClick.AddListener(HideKeyboard);
        }

        private void ClearMessageObjects() {
            RenderContext context = _renderContext;
            CheckRender(context);
            GameObject[] retired = _messageObjects.ToArray();
            _messageObjects.Clear();
            for (int i = retired.Length - 1; i >= 0; i--) {
                if (retired[i] != null) {
                    Destroy(retired[i]);
                }
            }
            CheckRender(context);
        }

        private void AddViewerStatusLine() {
            RenderContext context = _renderContext;
            CheckRender(context);
            if (string.IsNullOrWhiteSpace(_viewerStatus)) {
                return;
            }

            GameObject root = CreatePresentationObject("Live Chat Viewer Count", context, true);
            CheckRender(context);
            root.transform.SetParent(transform, false);
            CheckRender(context);

            RectTransform rectTransform = root.transform as RectTransform;
            CheckRender(context);
            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.pivot = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.sizeDelta = new Vector2(48f, 7.5f);
            CheckRender(context);
            rectTransform.anchoredPosition = new Vector2(-35f, 1.5f);
            CheckRender(context);
            rectTransform.localScale = Vector3.one;
            CheckRender(context);

            Image background = root.AddComponent<Image>();
            CheckRender(context);
            background.color = new Color(0f, 0f, 0f, 0.12f);
            CheckRender(context);
            background.raycastTarget = false;
            CheckRender(context);
            ApplyNoGlow(background);
            CheckRender(context);

            AddAccent(root.transform, ChatAccent, 7.5f);
            CheckRender(context);
            AddText(root.transform, _viewerStatus, new Color(0.92f, 0.94f, 0.98f, 0.82f), 2.15f * CurrentTextScale, new Vector2(3f, -0.6f), new Vector2(43f, 7.2f), TextAlignmentOptions.Left);
            CheckRender(context);
        }

        private void AddStatusLine() {
            RenderContext context = _renderContext;
            CheckRender(context);
            if (!HasStatusLine) {
                return;
            }

            GameObject root = CreatePresentationObject("Live Chat Status", context, true);
            CheckRender(context);
            root.transform.SetParent(transform, false);
            CheckRender(context);

            RectTransform rectTransform = root.transform as RectTransform;
            CheckRender(context);
            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.pivot = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.sizeDelta = new Vector2(ChatWidth - 9f, 7.5f);
            CheckRender(context);
            rectTransform.anchoredPosition = new Vector2(0f, 9.5f);
            CheckRender(context);
            rectTransform.localScale = Vector3.one;
            CheckRender(context);

            Image background = root.AddComponent<Image>();
            CheckRender(context);
            background.color = new Color(0f, 0f, 0f, 0.22f);
            CheckRender(context);
            background.raycastTarget = false;
            CheckRender(context);
            ApplyNoGlow(background);
            CheckRender(context);

            AddAccent(root.transform, ChatAccent, 7.5f);
            CheckRender(context);
            AddText(root.transform, status, new Color(0.92f, 0.94f, 0.98f, 0.95f), 2.3f * CurrentTextScale, new Vector2(3f, -0.6f), new Vector2(ChatWidth - 15f, 7.2f), TextAlignmentOptions.Left);
            CheckRender(context);
        }

        private void AddMessageRow(LiveChatFloatingRow row, float y, float height) {
            RenderContext context = _renderContext;
            CheckRender(context);
            GameObject root = CreatePresentationObject("Live Chat Message", context, true);
            CheckRender(context);
            root.transform.SetParent(transform, false);
            CheckRender(context);

            RectTransform rectTransform = root.transform as RectTransform;
            CheckRender(context);
            rectTransform.anchorMin = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.anchorMax = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.pivot = new Vector2(0.5f, 0f);
            CheckRender(context);
            rectTransform.sizeDelta = new Vector2(ChatWidth, height);
            CheckRender(context);
            rectTransform.anchoredPosition = new Vector2(0f, y);
            CheckRender(context);
            rectTransform.localScale = Vector3.one;
            CheckRender(context);

            Image background = root.AddComponent<Image>();
            CheckRender(context);
            background.color = row.HasAccent ? ChatHighlight : ChatBackground;
            CheckRender(context);
            background.raycastTarget = row.LinkTarget != null;
            CheckRender(context);
            ApplyNoGlow(background);
            CheckRender(context);

            if (row.LinkTarget != null) {
                Button button = root.AddComponent<Button>();
                CheckRender(context);
                button.transition = Selectable.Transition.None;
                CheckRender(context);
                button.targetGraphic = background;
                CheckRender(context);
                LiveChatLinkTarget target = row.LinkTarget;
                CheckRender(context);
                button.onClick.AddListener(() => OpenLink(target));
                CheckRender(context);
            }

            if (row.HasAccent) {
                AddAccent(root.transform, row.LinkTarget == null ? ChatAccent : LinkAccent, height);
                CheckRender(context);
            }

            Vector2 textPosition = new Vector2(6f, -1f);
            CheckRender(context);
            Vector2 textSize = new Vector2(MessageTextWidth, height - MessageTextVerticalPadding);
            CheckRender(context);
            AddText(root.transform, row.DisplayText, MessageColor, MessageTextFontSize * CurrentTextScale, textPosition, textSize, TextAlignmentOptions.TopLeft, true);
            CheckRender(context);
        }

        private void AddAccent(Transform parent, Color color, float height) {
            RenderContext context = _renderContext;
            CheckRender(context);
            GameObject accent = CreatePresentationObject("Accent", context);
            CheckRender(context);
            accent.transform.SetParent(parent, false);
            CheckRender(context);
            Image image = accent.AddComponent<Image>();
            CheckRender(context);
            image.color = color;
            CheckRender(context);
            image.raycastTarget = false;
            CheckRender(context);
            ApplyNoGlow(image);
            CheckRender(context);

            RectTransform rect = accent.transform as RectTransform;
            CheckRender(context);
            rect.anchorMin = new Vector2(0f, 0f);
            CheckRender(context);
            rect.anchorMax = new Vector2(0f, 0f);
            CheckRender(context);
            rect.pivot = new Vector2(0f, 0f);
            CheckRender(context);
            rect.sizeDelta = new Vector2(1f, height);
            CheckRender(context);
            rect.anchoredPosition = Vector2.zero;
            CheckRender(context);
            rect.localScale = Vector3.one;
            CheckRender(context);

            context?.UncommittedObjects.Remove(accent);
        }

        private float MeasureMessageTextHeight(string value) {
            RenderContext context = _renderContext;
            CheckRender(context);
            TextMeshProUGUI text = MessageMeasurementText;
            CheckRender(context);
            if (text == null) {
                return MessageRowMinHeight * CurrentTextScale;
            }

            ConfigureText(text, Color.clear, MessageTextFontSize * CurrentTextScale, TextAlignmentOptions.TopLeft, true);
            CheckRender(context);
            text.text = value ?? string.Empty;
            CheckRender(context);
            text.rectTransform.sizeDelta = new Vector2(MessageTextWidth, ChatHeight);
            CheckRender(context);
            Vector2 preferredValues = text.GetPreferredValues(text.text, MessageTextWidth, float.PositiveInfinity);
            CheckRender(context);
            text.text = string.Empty;
            CheckRender(context);

            if (float.IsNaN(preferredValues.y) || float.IsInfinity(preferredValues.y) || preferredValues.y <= 0f) {
                return MessageRowMinHeight * CurrentTextScale;
            }

            return preferredValues.y;
        }

        private TextMeshProUGUI MessageMeasurementText {
            get {
                RenderContext context = _renderContext;
                CheckRender(context);
                if (_messageMeasurementText != null) {
                    return _messageMeasurementText;
                }

                if (transform == null) {
                    return null;
                }

                GameObject textObject = CreatePresentationObject("Live Chat Message Measurement", context);
                CheckRender(context);
                textObject.transform.SetParent(transform, false);
                CheckRender(context);
                TextMeshProUGUI measurement = textObject.AddComponent<TextMeshProUGUI>();
                CheckRender(context);

                RectTransform rect = measurement.rectTransform;
                CheckRender(context);
                rect.anchorMin = new Vector2(0f, 1f);
                CheckRender(context);
                rect.anchorMax = new Vector2(0f, 1f);
                CheckRender(context);
                rect.pivot = new Vector2(0f, 1f);
                CheckRender(context);
                rect.anchoredPosition = Vector2.zero;
                CheckRender(context);
                rect.sizeDelta = new Vector2(MessageTextWidth, ChatHeight);
                CheckRender(context);
                rect.localScale = Vector3.one;
                CheckRender(context);

                context?.UncommittedObjects.Remove(textObject);
                _messageMeasurementText = measurement;
                return measurement;
            }
        }

        private TextMeshProUGUI AddText(Transform parent, string value, Color color, float fontSize, Vector2 anchoredPosition, Vector2 sizeDelta, TextAlignmentOptions alignment, bool wordWrapping = false) {
            RenderContext context = _renderContext;
            CheckRender(context);
            GameObject textObject = CreatePresentationObject("Text", context);
            CheckRender(context);
            textObject.transform.SetParent(parent, false);
            CheckRender(context);

            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            CheckRender(context);
            ConfigureText(text, color, fontSize, alignment, wordWrapping);
            CheckRender(context);
            text.text = value ?? string.Empty;
            CheckRender(context);

            RectTransform rect = text.rectTransform;
            CheckRender(context);
            rect.anchorMin = new Vector2(0f, 1f);
            CheckRender(context);
            rect.anchorMax = new Vector2(0f, 1f);
            CheckRender(context);
            rect.pivot = new Vector2(0f, 1f);
            CheckRender(context);
            rect.anchoredPosition = anchoredPosition;
            CheckRender(context);
            rect.sizeDelta = sizeDelta;
            CheckRender(context);
            context?.UncommittedObjects.Remove(textObject);
            return text;
        }

        private void ConfigureText(TextMeshProUGUI text, Color color, float fontSize, TextAlignmentOptions alignment, bool wordWrapping) {
            RenderContext context = _renderContext;
            CheckRender(context);
            text.font = ChatFont;
            CheckRender(context);
            text.richText = true;
            CheckRender(context);
            text.SetWordWrapping(wordWrapping);
            CheckRender(context);
            text.overflowMode = TextOverflowModes.Ellipsis;
            CheckRender(context);
            text.alignment = alignment;
            CheckRender(context);
            text.color = color;
            CheckRender(context);
            text.fontSize = fontSize;
            CheckRender(context);
            text.lineSpacing = TextLineSpacing;
            CheckRender(context);
            text.raycastTarget = false;
            CheckRender(context);
        }

        private void ApplyNoGlow(Image image) {
            RenderContext context = _renderContext;
            CheckRender(context);
            if (image == null) {
                return;
            }
            Material material = Utilities.ImageResources.NoGlowMat;
            CheckRender(context);
            if (material == null) {
                return;
            }

            image.material = material;
            CheckRender(context);
        }

        private TMP_FontAsset ChatFont {
            get {
                RenderContext context = _renderContext;
                CheckRender(context);
                if (_chatFont != null) {
                    return _chatFont;
                }

                TMP_FontAsset font = FindChatFont("Teko-Medium SDF No Glow", context)
                    ?? FindChatFont("Teko-Medium SDF", context);
                if (ReferenceEquals(font, null)) {
                    TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    CheckRender(context);
                    font = fonts.FirstOrDefault();
                }
                _chatFont = font;
                return font;
            }
        }

        private TMP_FontAsset FindChatFont(string name, RenderContext context) {
            CheckRender(context);
            TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            CheckRender(context);
            foreach (TMP_FontAsset font in fonts) {
                string fontName = font.name;
                CheckRender(context);
                if (fontName == name) {
                    return font;
                }
            }
            return null;
        }

        internal static LiveChatFloatingRow PrepareOwnedRow(LiveChatEntry entry, LiveChatLinkTarget target, string sender, string text, string time) {
            return new LiveChatFloatingRow(entry.IsChat ? FirstNonEmpty(sender, "Unknown") : "Log", Truncate(text, 180), time, entry.IsChat, target);
        }

        internal sealed class LiveChatFloatingRow {
            internal LiveChatFloatingRow(LiveChatEntry entry, LiveChatLinkTarget linkTarget, string senderName, string text) {
                string title = entry.IsChat ? FirstNonEmpty(senderName, "Unknown") : "Log";
                string detail = Truncate(text, 180);
                Title = title;
                Detail = detail;
                Status = entry.DisplayTime;
                IsChat = entry.IsChat;
                LinkTarget = linkTarget;
                PlainText = $"{Status} {Title}: {Detail}";
                DisplayText = BuildDisplayText(Status, Title, Detail, IsChat);
            }

            internal LiveChatFloatingRow(string title, string detail, string status, bool isChat, LiveChatLinkTarget linkTarget) {
                Title = FirstNonEmpty(title, isChat ? "Unknown" : "Log");
                Detail = detail ?? string.Empty;
                Status = status ?? string.Empty;
                IsChat = isChat;
                LinkTarget = linkTarget;
                PlainText = $"{Status} {Title}: {Detail}";
                DisplayText = BuildDisplayText(Status, Title, Detail, IsChat);
            }

            internal string Title { get; }
            internal string Detail { get; }
            internal string Status { get; }
            internal string DisplayText { get; }
            internal string PlainText { get; }
            internal bool IsChat { get; }
            internal LiveChatLinkTarget LinkTarget { get; }
            internal bool HasAccent => !IsChat || LinkTarget != null;
        }

        private static string BuildDisplayText(string status, string title, string detail, bool isChat) {
            string safeStatus = EscapeRichText(status);
            string safeTitle = EscapeRichText(FirstNonEmpty(title, isChat ? "Unknown" : "Log"));
            string safeDetail = EscapeRichText(detail);
            string nameColor = isChat ? ChatNameColor : LogNameColor;
            string timePrefix = string.IsNullOrEmpty(safeStatus) ? string.Empty : $"<color={TimeColor}>{safeStatus}</color> ";
            return $"{timePrefix}<color={nameColor}><b>{safeTitle}</b></color>: {safeDetail}";
        }

        private static string EscapeRichText(string value) => (value ?? string.Empty).Replace("<", "<\u2060");

        private static string FirstNonEmpty(params string[] values) {
            foreach (string value in values) {
                if (!string.IsNullOrWhiteSpace(value)) {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string Truncate(string value, int maxLength) {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength - 3) + "...";
        }
    }
}
