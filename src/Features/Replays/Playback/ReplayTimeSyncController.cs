using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Replays.Playback {
    internal class ReplayTimeSyncController : TimeSynchronizer, ITickable, IDisposable {
        private static readonly FieldAccessor<BeatmapCallbacksController.InitData, float>.Accessor InitialStartFilterTime =
            FieldAccessor<BeatmapCallbacksController.InitData, float>.GetAccessor("startFilterTime");
        private static readonly BindingFlags DespawnFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo DespawnNote = typeof(BeatmapObjectManager).GetMethod("Despawn", DespawnFlags, null, new[] { typeof(NoteController) }, null);
        private static readonly MethodInfo DespawnSlider = typeof(BeatmapObjectManager).GetMethod("Despawn", DespawnFlags, null, new[] { typeof(SliderController) }, null);
        private static readonly MethodInfo DespawnObstacle = typeof(BeatmapObjectManager).GetMethod("Despawn", DespawnFlags, null, new[] { typeof(ObstacleController) }, null);
        private const float KeyboardSeekSeconds = 5f;

        private static readonly KeyCode[] TimeJumpKeys = {
            KeyCode.Alpha1,
            KeyCode.Alpha2,
            KeyCode.Alpha3,
            KeyCode.Alpha4,
            KeyCode.Alpha5,
            KeyCode.Alpha6,
            KeyCode.Alpha7,
            KeyCode.Alpha8,
            KeyCode.Alpha9,
            KeyCode.Alpha0
        };

        private readonly List<IScroller> _scrollers;
        private readonly AudioManager _audioManager;
        private readonly BeatmapObjectManager _beatmapObjectManager;
        private readonly BeatmapCallbacksUpdater _beatmapCallbacksUpdater;
        private readonly IReadonlyBeatmapData _beatmapData;
        private readonly ReplaySeekInterop _seekInterop;
        private readonly List<IBeatmapObjectController> _spawnedBeatmapObjects;
        private AudioTimeSyncController.InitData _audioInitData;
        private BasicBeatmapObjectManager _basicBeatmapObjectManager;
        private NoteCutSoundEffectManager _noteCutSoundEffectManager;
        private BeatmapCallbacksController.InitData _callbackInitData;
        private BeatmapCallbacksController _beatmapObjectCallbackController;
        private readonly BeatmapObjectSpawnController _beatmapObjectSpawnController;
        private bool _paused;
        private float _seekDiagnosticTime = float.NaN;
        private int _seekDiagnosticStage;

        public ReplayTimeSyncController(List<IScroller> scrollers, BasicBeatmapObjectManager basicBeatmapObjectManager, NoteCutSoundEffectManager noteCutSoundEffectManager, BeatmapObjectSpawnController beatmapObjectSpawnController, AudioTimeSyncController.InitData audioInitData, BeatmapCallbacksController.InitData initData, BeatmapCallbacksController beatmapObjectCallbackController, BeatmapCallbacksUpdater beatmapCallbacksUpdater, IReadonlyBeatmapData beatmapData, DiContainer container) {
            _scrollers = scrollers;
            _callbackInitData = initData;
            _audioInitData = audioInitData;
            _basicBeatmapObjectManager = basicBeatmapObjectManager;
            _noteCutSoundEffectManager = noteCutSoundEffectManager;
            _beatmapObjectSpawnController = beatmapObjectSpawnController;
            _beatmapObjectCallbackController = beatmapCallbacksUpdater.GetField<BeatmapCallbacksController, BeatmapCallbacksUpdater>("_beatmapCallbacksController");
            _beatmapCallbacksUpdater = beatmapCallbacksUpdater;
            _beatmapData = _beatmapObjectCallbackController.GetField<IReadonlyBeatmapData, BeatmapCallbacksController>("_beatmapData");
            _beatmapObjectManager = basicBeatmapObjectManager;
            _spawnedBeatmapObjects = _beatmapObjectManager.GetField<List<IBeatmapObjectController>, BeatmapObjectManager>("_allBeatmapObjects");
            _seekInterop = new ReplaySeekInterop(container, _beatmapData);
            Plugin.Log.Info($"Replay callback ownership: injected={ReferenceEquals(beatmapObjectCallbackController, _beatmapObjectCallbackController)}, spawner={ReferenceEquals(beatmapObjectSpawnController.GetField<BeatmapCallbacksController, BeatmapObjectSpawnController>("_beatmapCallbacksController"), _beatmapObjectCallbackController)}, beatmapData={ReferenceEquals(beatmapData, _beatmapData)}");
            _audioManager = noteCutSoundEffectManager._audioManager;
        }

        public void Tick() {
            int index = TimeJumpKeyIndex();
            if (index >= 0) {
                OverrideTime(audioTimeSyncController.songLength * (index * 0.1f));
            }

            if (Input.GetKeyDown(KeyCode.Minus) && audioTimeSyncController.timeScale > 0.1f) {
                OverrideTimeScale(audioTimeSyncController.timeScale - 0.1f);
            }

            if (Input.GetKeyDown(KeyCode.Equals) && audioTimeSyncController.timeScale < 2.0f) {
                OverrideTimeScale(audioTimeSyncController.timeScale + 0.1f);
            }

            if (Input.GetKeyDown(KeyCode.R)) {
                OverrideTime(0f);
            }

            if (Input.GetKeyDown(KeyCode.Space)) {
                if (_paused) {
                    audioTimeSyncController.Resume();
                } else {
                    CancelAllHitSounds();
                    audioTimeSyncController.Pause();
                }
                _paused = !_paused;
            }

            if (Input.GetKeyDown(KeyCode.LeftArrow)) {
                OverrideTime(Mathf.Max(0f, audioTimeSyncController.songTime - KeyboardSeekSeconds));
            }
            if (Input.GetKeyDown(KeyCode.RightArrow)) {
                OverrideTime(Mathf.Min(audioTimeSyncController.songLength, audioTimeSyncController.songTime + KeyboardSeekSeconds));
            }
            if (!float.IsNaN(_seekDiagnosticTime)) {
                var elapsed = audioTimeSyncController.songTime - _seekDiagnosticTime;
                if (_seekDiagnosticStage == 0 && elapsed >= 2f) {
                    LogSeekObjects("after 2s", audioTimeSyncController.songTime);
                    _seekDiagnosticStage = 1;
                } else if (_seekDiagnosticStage == 1 && elapsed >= 5f) {
                    LogSeekObjects("after 5s", audioTimeSyncController.songTime);
                    _seekDiagnosticStage = 2;
                } else if (_seekDiagnosticStage == 2 && elapsed >= 10f) {
                    LogSeekObjects("after 10s", audioTimeSyncController.songTime);
                    _seekDiagnosticTime = float.NaN;
                }
            }
        }

        private static int TimeJumpKeyIndex() {
            for (int i = 0; i < TimeJumpKeys.Length; i++) {
                if (Input.GetKeyDown(TimeJumpKeys[i])) {
                    return i;
                }
            }

            return -1;
        }

        private void UpdateTimes() {
            foreach (var scroller in _scrollers)
                scroller.TimeUpdate(audioTimeSyncController.songTime);
        }

        public void OverrideTime(float time) {
            if (!audioTimeSyncController.isReady || float.IsNaN(time) || float.IsInfinity(time)) return;
            time = Mathf.Clamp(time, 0f, audioTimeSyncController.songLength);
            if (Mathf.Abs(time - audioTimeSyncController.songTime) <= 0.001f) return;

            var _audioTimeSyncController = audioTimeSyncController; // UMBRAMEGALUL
            HarmonyPatches.CutSoundEffectOverride.Buffer = true;
            CancelAllHitSounds();
            bool wasPlaying = audioTimeSyncController.IsPlaying();
            audioTimeSyncController.Pause();
            _beatmapCallbacksUpdater.Pause();
            try {
                DespawnAllBeatmapObjects();
                _audioTimeSyncController.SetField("_prevAudioSamplePos", -1);
                audioTimeSyncController.SeekTo(time / audioTimeSyncController.timeScale);
                _audioTimeSyncController._songTime = time;
                audioTimeSyncController.Update();
                _beatmapObjectCallbackController.SetField("_songTime", time);
                if (_beatmapObjectCallbackController._callbacksInTimes.TryGetValue(0f, out var callbacks))
                    _seekInterop.Rebuild(callbacks, time);

                _beatmapObjectCallbackController.SetField("_prevSongTime", float.MinValue);
                var precedingItem = FindBeatmapItem(time);
                foreach (var callback in _beatmapObjectCallbackController._callbacksInTimes)
                    callback.Value.lastProcessedNode = precedingItem;

                if (wasPlaying) audioTimeSyncController.Resume();
                _beatmapCallbacksUpdater.LateUpdate();
                UpdateTimes();
                LogSeekObjects("immediate", time);
                _seekDiagnosticTime = time;
                _seekDiagnosticStage = 0;
            } finally {
                _beatmapCallbacksUpdater.Resume();
                if (wasPlaying && !audioTimeSyncController.IsPlaying()) audioTimeSyncController.Resume();
            }
        }

        private LinkedListNode<BeatmapDataItem> FindBeatmapItem(float time) {
            LinkedListNode<BeatmapDataItem> precedingItem = null;
            var startFilterTime = _beatmapObjectCallbackController.GetField<float, BeatmapCallbacksController>("_startFilterTime");
            for (var node = _beatmapData.allBeatmapDataItems.First; node != null; node = node.Next) {
                if (node.Value.time >= startFilterTime && node.Value.time >= time) break;
                precedingItem = node;
            }
            return precedingItem;
        }

        private void DespawnAllBeatmapObjects() {
            var argument = new object[1];
            foreach (var item in _spawnedBeatmapObjects.ToArray()) {
                item.Pause(false);
                argument[0] = item;
                var method = item is NoteController ? DespawnNote
                    : item is SliderController ? DespawnSlider
                    : item is ObstacleController ? DespawnObstacle : null;
                method?.Invoke(_beatmapObjectManager, argument);
            }
        }

        private void LogSeekObjects(string phase, float time) {
            var notes = _basicBeatmapObjectManager._basicGameNotePoolContainer.activeItems;
            var activeNotes = notes.Where(note => note != null && note.gameObject.activeInHierarchy).ToArray();
            var callbacks = string.Join(", ", _beatmapObjectCallbackController._callbacksInTimes.Take(5).Select(pair => $"{pair.Key:0.##}:{pair.Value.lastProcessedNode?.Value.time.ToString("0.##") ?? "none"}"));
            Plugin.Log.Info($"Replay seek objects {phase} at {time:0.##}s: notes={activeNotes.Length}, spawned={_spawnedBeatmapObjects.Count}, spawnDisabled={_beatmapObjectSpawnController.GetField<bool, BeatmapObjectSpawnController>("_disableSpawning")}, startFilter={_beatmapObjectCallbackController.GetField<float, BeatmapCallbacksController>("_startFilterTime"):0.##}, callbacks={callbacks}, sample={activeNotes.FirstOrDefault()?.noteTransform.position}");
        }

        public void Dispose() => _seekInterop.Dispose();

        public void OverrideTimeScale(float newScale) {

            CancelAllHitSounds();
            var _audioTimeSyncController = audioTimeSyncController; // UMBRAMEGALUL
            _audioTimeSyncController._audioSource.pitch = newScale;

            _audioTimeSyncController._timeScale = newScale;
            _audioTimeSyncController._audioStartTimeOffsetSinceStart
                = (Time.timeSinceLevelLoad * _audioTimeSyncController.timeScale) - (_audioTimeSyncController.songTime + _audioInitData.songTimeOffset);

            _audioManager.musicPitch = 1f / newScale;
            _audioTimeSyncController.Update();
        }

        public void CancelAllHitSounds() {

            var activeItems = _noteCutSoundEffectManager._noteCutSoundEffectPoolContainer.activeItems;
            for (int i = 0; i < activeItems.Count; i++) {
                var effect = activeItems[i];
                if (effect.isActiveAndEnabled)
                    effect.StopPlayingAndFinish();
            }
            _noteCutSoundEffectManager.SetField("_prevNoteATime", -1f);
            _noteCutSoundEffectManager.SetField("_prevNoteBTime", -1f);
        }
    }
}
