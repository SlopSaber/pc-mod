using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using IPA.Utilities.Async;
using ScoreSaber.Features.Players.Services;
using ScoreSaber.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace ScoreSaber.Features.Replays.UI {
    internal class ResultsViewReplayButtonController : IInitializable, IDisposable {
        [UIComponent("watch-replay-button")]
        protected readonly Button watchReplayButton = null;

        private ResultsViewController _resultsViewController;
        private BeatmapLevel _beatmapLevel;
        private BeatmapKey _beatmapKey;
        private LevelCompletionResults _levelCompletionResults;
        private readonly GameSessionService _gameSessionService;
        private readonly ReplayLoader _replayLoader;
        private readonly ReplayService _replayService;

        private byte[] _serializedReplay;
        private object _resultPlay;
        private bool _resultsActive;
        private bool _disposed;
        private int _waitForReplayVersion;
        private CancellationTokenSource _replayLoadCancellation;

        public ResultsViewReplayButtonController(ResultsViewController resultsViewController, GameSessionService gameSessionService, ReplayLoader replayLoader, ReplayService replayService) {

            _resultsViewController = resultsViewController;
            _gameSessionService = gameSessionService;
            _replayLoader = replayLoader;
            _replayService = replayService;
        }

        public void Initialize() {

            _resultsViewController.didActivateEvent += ResultsViewController_didActivateEvent;
            _resultsViewController.continueButtonPressedEvent += ResultsViewController_continueButtonPressedEvent;
            _resultsViewController.restartButtonPressedEvent += ResultsViewController_restartButtonPressedEvent;
            _replayService.ReplaySerializedForPlay += ReplayServiceReplaySerialized;
        }

        private void ResultsViewController_didActivateEvent(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling) {
            if (_disposed) return;

            int version = ++_waitForReplayVersion;
            _resultsActive = true;
            _resultPlay = _replayService.CurrentResultPlay;
            _serializedReplay = _replayService.GetSerializedReplay(_resultPlay);

            if (firstActivation) {
                BsmlParser.Instance.Parse(
                    "<button-with-icon id=\"watch-replay-button\" icon=\"ScoreSaber.Resources.replay.png\" hover-hint=\"Watch Replay\" pref-width=\"15\" pref-height=\"13\" interactable=\"false\" on-click=\"replay-click\" />",
                    _resultsViewController.gameObject,
                    this
                );
                watchReplayButton.transform.localScale *= 0.4f;
                watchReplayButton.transform.localPosition = new Vector2(42.5f, 27f);
            }
            if (_disposed || !_resultsActive || version != _waitForReplayVersion) return;
            watchReplayButton.interactable = false;
            if (!IsCurrentResult(version)) return;

            BeatmapLevel beatmapLevel = _resultsViewController.GetBeatmapLevel();
            BeatmapKey beatmapKey = _resultsViewController.GetBeatmapKey();
            LevelCompletionResults levelCompletionResults = _resultsViewController._levelCompletionResults;
            if (!IsCurrentResult(version)) return;

            _beatmapLevel = beatmapLevel;
            _beatmapKey = beatmapKey;
            _levelCompletionResults = levelCompletionResults;
            watchReplayButton.interactable = _serializedReplay != null;
            if (IsCurrentResult(version)) WaitForReplay(version).RunTask();
        }

        private void ResultsViewController_restartButtonPressedEvent(ResultsViewController obj) => RetireResult();

        private void ResultsViewController_continueButtonPressedEvent(ResultsViewController obj) => RetireResult();

        private void RetireResult() {
            object play = _resultPlay;
            _resultsActive = false;
            _resultPlay = null;
            _serializedReplay = null;
            _waitForReplayVersion++;
            _replayService.RetireResultPlay(play);
            _replayLoadCancellation?.Cancel();
        }

        private bool IsCurrentResult(int version) => !_disposed && _resultsActive && version == _waitForReplayVersion &&
            _resultPlay != null && ReferenceEquals(_resultPlay, _replayService.CurrentResultPlay);

        private void ReplayServiceReplaySerialized(object play, byte[] replay) {
            if (!ReferenceEquals(play, _resultPlay) || !IsCurrentResult(_waitForReplayVersion)) return;
            _serializedReplay = replay;
        }

        private async Task WaitForReplay(int version) {

            await ScoreSaber.Core.TaskExtensions.WaitUntil(() => _serializedReplay != null || !IsCurrentResult(version));
            if (IsCurrentResult(version) && _serializedReplay != null) {
                watchReplayButton.interactable = true;
            }
        }

        public void Dispose() {
            _disposed = true;
            RetireResult();
            _resultsViewController.didActivateEvent -= ResultsViewController_didActivateEvent;
            _resultsViewController.continueButtonPressedEvent -= ResultsViewController_continueButtonPressedEvent;
            _resultsViewController.restartButtonPressedEvent -= ResultsViewController_restartButtonPressedEvent;
            _replayService.ReplaySerializedForPlay -= ReplayServiceReplaySerialized;
        }

        [UIAction("replay-click")]
        protected void ClickedReplayButton() {
            if (!IsCurrentResult(_waitForReplayVersion) || _serializedReplay == null) return;

            watchReplayButton.interactable = false;
            WatchReplay().RunTask();
        }

        private async Task WatchReplay() {
            int version = _waitForReplayVersion;
            if (!IsCurrentResult(version) || _serializedReplay == null) return;
            _replayLoadCancellation?.Cancel();
            if (!IsCurrentResult(version) || _serializedReplay == null) return;
            var cancellation = new CancellationTokenSource();
            _replayLoadCancellation = cancellation;
            try {
                await _replayLoader.Load(_serializedReplay, _beatmapLevel, _beatmapKey, _levelCompletionResults.gameplayModifiers, _gameSessionService.LocalPlayerInfo.playerName, cancellation.Token);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                if (!IsCurrentResult(version) || cancellation.IsCancellationRequested) return;
                Plugin.Log.Error($"Failed to start replay: {ex}");
                if (IsCurrentResult(version) && !cancellation.IsCancellationRequested) watchReplayButton.interactable = true;
            } finally {
                if (ReferenceEquals(_replayLoadCancellation, cancellation)) _replayLoadCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
