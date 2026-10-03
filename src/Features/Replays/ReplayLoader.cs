using IPA.Utilities.Async;
using ScoreSaber.Core.Configuration;
using IPA.Loader;
using ScoreSaber.Core.Gameplay;
using ScoreSaber.Features.Replays.Format;
using ScoreSaber.Features.ScoreSubmission.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Replays {
    internal class ReplayLoader : IDisposable {

        private readonly PlayerDataModel _playerDataModel;
        private readonly MenuTransitionsHelper _menuTransitionsHelper;
        private readonly GameScenesManager _gameScenesManager;
        private readonly ReplayFileCodec _replayFileCodec;
        private readonly EnvironmentsListModel _environmentsListModel;
        private readonly ReplayState _replayState;
        private readonly ScoreSubmissionService _scoreSubmissionService;
        private readonly SettingsService _settings;
        private readonly DiContainer _container;
        private LoadOperation _currentLoad;
        private bool _disposed;
        private bool _quitting;
        private bool _launchInvoking;

        private sealed class LoadOperation : IDisposable {
            internal readonly CancellationTokenSource Cancellation;
            internal readonly CancellationToken Token;
            internal readonly CancellationToken CallerToken;
            internal readonly int PluginVersion;
            internal bool LaunchPending;
            private bool _disposed;

            internal LoadOperation(CancellationToken callerToken) {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                Token = Cancellation.Token;
                CallerToken = callerToken;
                PluginVersion = Plugin.Instance.ReplayLoadVersion;
            }

            internal void Retire() {
                if (!_disposed) Cancellation.Cancel();
            }

            public void Dispose() {
                _disposed = true;
                Cancellation.Dispose();
            }
        }

        public ReplayLoader(PlayerDataModel playerDataModel, MenuTransitionsHelper menuTransitionsHelper, EnvironmentsListModel environmentsListModel, ReplayState replayState, ReplayFileCodec replayFileCodec, ScoreSubmissionService scoreSubmissionService, SettingsService settings, DiContainer container) {

            _playerDataModel = playerDataModel;
            _menuTransitionsHelper = menuTransitionsHelper;
            _gameScenesManager = menuTransitionsHelper._gameScenesManager;
            _replayFileCodec = replayFileCodec;
            _environmentsListModel = environmentsListModel;
            _replayState = replayState;
            _scoreSubmissionService = scoreSubmissionService;
            _settings = settings;
            _container = container;
            ReplayLaunchGuard.EnsureInstalled();
            Plugin.ReplayLoadsRetired += RetireLoads;
            Application.quitting += OnQuit;
            _gameScenesManager.transitionDidFinishEvent += OnNativeTransitionFinished;
        }

        public Task Load(byte[] replay, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, GameplayModifiers modifiers, string playerName) =>
            Load(replay, beatmapLevel, beatmapKey, modifiers, playerName, CancellationToken.None);

        internal async Task Load(byte[] replay, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, GameplayModifiers modifiers, string playerName, CancellationToken cancellationToken) {
            if (replay == null || replay.Length < 4) {
                throw new ArgumentException("Replay data is empty", nameof(replay));
            }

            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            if (_disposed || _quitting || !Plugin.Instance.IsEnabled || _launchInvoking) {
                throw new OperationCanceledException();
            }
            EnsureNativeAdmission();
            RetireLoads();
            var operation = new LoadOperation(cancellationToken);
            _currentLoad = operation;
            try {
                EnsureCurrent(operation);
                _replayState.BeginReplay(beatmapLevel, beatmapKey, modifiers, playerName);
                if (replay[0] == 93 && replay[1] == 0 && replay[2] == 0 && replay[3] == 128) {
                    await LoadLegacyReplay(replay, beatmapLevel, beatmapKey, modifiers, operation);
                } else {
                    ReplayFile replayFile = await LoadReplay(replay);
                    await StartReplay(replayFile, beatmapLevel, beatmapKey, operation);
                }
            } catch {
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                operation.LaunchPending = false;
                EnsureCurrent(operation);
                RetireLoads();
                throw;
            } finally {
                operation.Dispose();
            }
        }

        private bool IsCurrent(LoadOperation operation) =>
            !_disposed && !_quitting && ReferenceEquals(_currentLoad, operation) && Plugin.Instance.IsEnabled &&
            Plugin.Instance.ReplayLoadVersion == operation.PluginVersion && !operation.Token.IsCancellationRequested &&
            !operation.CallerToken.IsCancellationRequested;

        private void EnsureCurrent(LoadOperation operation) {
            if (!IsCurrent(operation)) throw new OperationCanceledException();
        }

        private void RetireLoads() {
            var operation = _currentLoad;
            _currentLoad = null;
            operation?.Retire();
        }

        private void OnQuit() {
            _quitting = true;
            RetireLoads();
        }

        private void OnNativeTransitionFinished(object setup, DiContainer container) => RetirePendingNativeLaunch();

        private void OnNativeTransitionFinished<TTransition>(TTransition transitionType,
            object setup, DiContainer container) => RetirePendingNativeLaunch();

        private void RetirePendingNativeLaunch() {
            if (_currentLoad?.LaunchPending == true) RetireLoads();
        }

        private void EnsureNativeAdmission() {
            if (_gameScenesManager.isInTransition) throw new InvalidOperationException("Cannot start a replay during a scene transition.");
        }

        private Action EnterNativeLaunch() {
            bool wasInvoking = _launchInvoking;
            _launchInvoking = true;
            return () => _launchInvoking = wasInvoking;
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            RetireLoads();
            Plugin.ReplayLoadsRetired -= RetireLoads;
            Application.quitting -= OnQuit;
            _gameScenesManager.transitionDidFinishEvent -= OnNativeTransitionFinished;
        }

        private async Task LoadLegacyReplay(byte[] replay, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, GameplayModifiers gameplayModifiers, LoadOperation operation) {
            List<Z.Keyframe> keyframes = await Task.Run(() => {
                byte[] decompressed = SevenZip.Compression.LZMA.SevenZipHelper.Decompress(replay);
                return AddFrames(DeserializeLegacyReplay(decompressed));
            });

            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            EnsureCurrent(operation);

            PlayerData playerData = _playerDataModel.playerData;
            PlayerSpecificSettings playerSettings = playerData.playerSpecificSettings;
            if (gameplayModifiers == null) {
                gameplayModifiers = new GameplayModifiers();
            }

            ColorScheme colorScheme = playerData.colorSchemesSettings.GetOverrideColorScheme();
            StartReplayLevel(beatmapLevel, beatmapKey, playerData.overrideEnvironmentSettings, colorScheme,
                colorScheme != null ? colorScheme.ShouldOverrideLightshowColors() : playerData.colorSchemesSettings.ShouldOverrideLightshowColors(),
                gameplayModifiers, playerSettings, operation, () => {
                    _replayState.LoadLegacyReplay(keyframes);
                    _scoreSubmissionService.SuspendForReplay();
                }, (setup, results) => {
                    if (ReferenceEquals(_replayState.LoadedLegacyKeyframes, keyframes) && _replayState.IsPlaybackEnabled) ReplayEnd(setup, results);
                });
        }

        private static Z.SavedData DeserializeLegacyReplay(byte[] decompressed) {
            BinaryFormatter formatter = new BinaryFormatter();
            try {
                using (var dataStream = new MemoryStream(decompressed)) {
                    return (Z.SavedData)formatter.Deserialize(dataStream);
                }
            } catch (Exception ex) {
                throw new Exception("Failed to deserialize replay!", ex);
            }
        }

        private static List<Z.Keyframe> AddFrames(Z.SavedData replayData) {
            if (replayData == null || replayData._keyframes == null) {
                return new List<Z.Keyframe>();
            }

            List<Z.Keyframe> keyframes = new List<Z.Keyframe>(replayData._keyframes.Length);
            for (int i = 0; i < replayData._keyframes.Length; i++) {
                Z.SavedData.KeyframeSerializable ks = replayData._keyframes[i];
                Z.Keyframe k = new Z.Keyframe {
                    _pos1 = new Vector3(ks._xPos1, ks._yPos1, ks._zPos1),
                    _pos2 = new Vector3(ks._xPos2, ks._yPos2, ks._zPos2),
                    _pos3 = new Vector3(ks._xPos3, ks._yPos3, ks._zPos3),
                    _rot1 = new Quaternion(ks._xRot1, ks._yRot1, ks._zRot1, ks._wRot1),
                    _rot2 = new Quaternion(ks._xRot2, ks._yRot2, ks._zRot2, ks._wRot2),
                    _rot3 = new Quaternion(ks._xRot3, ks._yRot3, ks._zRot3, ks._wRot3),
                    _time = ks._time,
                    score = ks.score,
                    combo = ks.combo
                };
                keyframes.Add(k);
            }

            return keyframes;
        }

        private Task<ReplayFile> LoadReplay(byte[] replay) => _replayFileCodec.Read(replay);

        private async Task StartReplay(ReplayFile replay, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, LoadOperation operation) {
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            EnsureCurrent(operation);

            PlayerData playerData = _playerDataModel.playerData;
            PlayerSpecificSettings localPlayerSettings = playerData.playerSpecificSettings;
            bool mirror = _settings.Current.replayOverrideHandedness && replay.metadata.LeftHanded != localPlayerSettings.leftHanded;
            if (mirror) {
                replay.MirrorMetadata();
            }

            bool useRecordedPlayerSettings = _settings.Current.useRecordedPlayerSettings && replay.metadata.HasPlaySettingsExtension;
            PlayerSpecificSettings playerSettings = PlayerSpecificSettingsFactory.Create(localPlayerSettings,
                replay.metadata.LeftHanded,
                replay.metadata.InitialHeight,
                replay.heightKeyframes.Count > 0,
                useRecordedPlayerSettings,
                replay.metadata.NoTextsAndHuds,
                replay.metadata.SaberTrailIntensity,
                replay.metadata.HideNoteSpawnEffect,
                replay.metadata.ArcsHapticFeedback,
                replay.metadata.ArcVisibility,
                replay.metadata.EnvironmentEffectsFilterDefaultPreset,
                replay.metadata.EnvironmentEffectsFilterExpertPlusPreset);
            EnsureCurrent(operation);
            ColorScheme replayColorScheme = useRecordedPlayerSettings
                ? ColorSchemeFactory.Create(
                    playerData,
                    replay.metadata.LeftSaberColor,
                    replay.metadata.RightSaberColor,
                    replay.metadata.ObstacleColor,
                    replay.metadata.EnvironmentColor0,
                    replay.metadata.EnvironmentColor1,
                    replay.metadata.EnvironmentColorW,
                    replay.metadata.EnvironmentColor0Boost,
                    replay.metadata.EnvironmentColor1Boost,
                    replay.metadata.EnvironmentColorWBoost,
                    replay.metadata.SupportsEnvironmentColorBoost)
                : null;
            EnsureCurrent(operation);
            OverrideEnvironmentSettings replayEnvironmentSettings = OverrideEnvironmentSettingsFactory.Create(
                playerData,
                _environmentsListModel,
                replay.metadata.Environment,
                useRecordedPlayerSettings);
            EnsureCurrent(operation);
            ColorScheme playerColorScheme = replayColorScheme ?? playerData.colorSchemesSettings.GetOverrideColorScheme();
            var environmentSettings = replayEnvironmentSettings ?? playerData.overrideEnvironmentSettings;
            bool overrideLightshowColors = replayColorScheme != null ? replayColorScheme.ShouldOverrideLightshowColors() : playerData.colorSchemesSettings.ShouldOverrideLightshowColors();
            var gameplayModifiers = ScoreSaberGameplayModifiers.FromCodes(replay.metadata.Modifiers, false).GameplayModifiers;
            EnsureCurrent(operation);

            var indexes = await ReplayPlaybackPreparation.Prepare(replay.poseKeyframes, replay.noteKeyframes,
                replay.comboKeyframes, mirror, operation.Token);
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            EnsureCurrent(operation);
            replay.playbackIndexes = indexes;

            StartReplayLevel(beatmapLevel, beatmapKey, environmentSettings, playerColorScheme, overrideLightshowColors,
                gameplayModifiers, playerSettings, operation, () => {
                    _replayState.LoadReplay(replay);
                    _scoreSubmissionService.SuspendForReplay();
                }, (setup, results) => {
                    if (ReferenceEquals(_replayState.LoadedReplayFile, replay) && _replayState.IsPlaybackEnabled) ReplayEnd(setup, results);
                });
        }

        private void StartReplayLevel(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey,
            OverrideEnvironmentSettings environmentSettings, ColorScheme colorScheme, bool overrideLightshowColors,
            GameplayModifiers gameplayModifiers, PlayerSpecificSettings playerSettings, LoadOperation operation,
            Action beforeSceneSwitch, Action<StandardLevelScenesTransitionSetupData, LevelCompletionResults> replayEnd) {
            EnsureCurrent(operation);
            EnsureNativeAdmission();
            operation.LaunchPending = true;
            _launchInvoking = true;
            try {
                Action installReplay = ReplayLaunchGuard.CreateCallback(() => IsCurrent(operation), () => {
                    operation.LaunchPending = false;
                    beforeSceneSwitch();
                }, EnterNativeLaunch, () => !_gameScenesManager.isInTransition);
                if (TryStartWithHeck(beatmapLevel, beatmapKey, environmentSettings, colorScheme,
                    overrideLightshowColors, gameplayModifiers, playerSettings, operation, installReplay, replayEnd)) return;
                EnsureCurrent(operation);
                EnsureNativeAdmission();

                _menuTransitionsHelper.StartStandardLevel(
                    "Replay", beatmapKey, beatmapLevel, environmentSettings, colorScheme, overrideLightshowColors,
                    gameplayModifiers, playerSettings, null, _environmentsListModel,
                    new GameplayAdditionalInformation("Exit Replay"), installReplay, null, replayEnd, null);
                EnsureCurrent(operation);
            } finally {
                _launchInvoking = false;
            }
        }

        private bool TryStartWithHeck(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey,
            OverrideEnvironmentSettings environmentSettings, ColorScheme colorScheme, bool overrideLightshowColors,
            GameplayModifiers gameplayModifiers, PlayerSpecificSettings playerSettings, LoadOperation operation,
            Action beforeSceneSwitch, Action<StandardLevelScenesTransitionSetupData, LevelCompletionResults> replayEnd) {
            var assembly = PluginManager.GetPluginFromId("Heck")?.Assembly;
            if (assembly == null) return false;
            try {
                var parametersType = assembly.GetType("Heck.PlayView.StartStandardLevelParameters", true);
                var managerType = assembly.GetType("Heck.PlayView.PlayViewManager", true);
                var constructor = parametersType.GetConstructors().Single(method => method.GetParameters().FirstOrDefault()?.Name == "gameMode");
                var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch {
                    "gameMode" => (object)"Replay",
                    "beatmapKey" => beatmapKey,
                    "beatmapLevel" => beatmapLevel,
                    "overrideEnvironmentSettings" => environmentSettings,
                    "overrideColorScheme" => colorScheme,
                    "playerOverrideLightshowColors" => overrideLightshowColors,
                    "gameplayModifiers" => gameplayModifiers,
                    "playerSpecificSettings" => playerSettings,
                    "environmentsListModel" => _environmentsListModel,
                    "gameplayAdditionalInformation" => new GameplayAdditionalInformation("Exit Replay"),
                    "beforeSceneSwitchToGameplayCallback" => beforeSceneSwitch,
                    "levelFinishedCallback" => replayEnd,
                    "practiceSettings" or "afterSceneSwitchToGameplayCallback" or "levelRestartedCallback" or "beatmapLevelData" => null,
                    _ => throw new NotSupportedException($"Unsupported Heck replay launch parameter: {parameter.Name}")
                }).ToArray();
                var startParameters = constructor.Invoke(arguments);
                EnsureCurrent(operation);
                var manager = _container.Resolve(managerType);
                EnsureCurrent(operation);
                EnsureNativeAdmission();
                managerType.GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, new object[] { startParameters, false });
                EnsureCurrent(operation);
                Plugin.Log.Info("Replay launch routed through Heck play views");
                return true;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                if (ReplayLaunchGuard.HasPushAdmission(beforeSceneSwitch)) throw;
                Plugin.Log.Warn($"Heck replay settings unavailable: {ex}");
                return false;
            }
        }

        private void ReplayEnd(StandardLevelScenesTransitionSetupData standardLevelSceneSetupData, LevelCompletionResults levelCompletionResults) {

            _replayState.EndPlayback();
            _scoreSubmissionService.ResumeAfterReplay();
        }
    }
}
