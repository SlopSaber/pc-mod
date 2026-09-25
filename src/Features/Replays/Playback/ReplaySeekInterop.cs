using HarmonyLib;
using IPA.Loader;
using IPA.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Replays.Playback {
    // Reconstructs modchart state when a replay jumps to an earlier song time.
    internal sealed class ReplaySeekInterop : IDisposable {
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly IReadonlyBeatmapData _beatmapData;
        private readonly Dictionary<Component, TransformState> _initialTransforms = new Dictionary<Component, TransformState>();
        private readonly Harmony _harmony = new Harmony("ScoreSaber.ReplaySeekInterop");
        private MethodInfo _transformOnEnable;
        private MethodInfo _noodleManualUpdate;
        private FieldInfo _noodlePrevSongTime;
        private FieldInfo _noodleCallbacksInTime;
        private bool _noodleReprocessRequested;
        private MethodInfo _nullTrackProperties;
        private IDictionary _tracks;
        private MonoBehaviour _coroutineDummy;
        private object _chromaManager;
        private IDictionary _chromaColorizers;
        private MethodInfo _resetChroma;
        private MethodInfo _finishChroma;
        private List<BeatmapDataItem> _animationEvents;
        private static ReplaySeekInterop _active;

        private struct TransformState {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;

            public TransformState(Transform transform) {
                Position = transform.localPosition;
                Rotation = transform.localRotation;
                Scale = transform.localScale;
            }
        }

        public ReplaySeekInterop(DiContainer container, IReadonlyBeatmapData beatmapData) {
            _beatmapData = beatmapData;
            _active = this;
            BeginHeck(container);
            BeginChroma(container);
            BeginNoodle();
            Plugin.Log.Info($"Replay seek map state: Heck tracks={_tracks?.Count ?? 0}, Chroma light groups={_chromaColorizers?.Count ?? 0}");
        }

        private void BeginHeck(DiContainer container) {
            var assembly = PluginManager.GetPluginFromId("Heck")?.Assembly;
            if (assembly == null) return;
            try {
                var trackType = assembly.GetType("Heck.Animation.Track", true);
                var dummyType = assembly.GetType("Heck.CoroutineDummy", true);
                var transformType = assembly.GetType("Heck.Animation.Transform.TransformController", true);
                _tracks = (IDictionary)container.Resolve(typeof(Dictionary<,>).MakeGenericType(typeof(string), trackType));
                _coroutineDummy = (MonoBehaviour)container.Resolve(dummyType);
                _nullTrackProperties = trackType.GetMethod("NullProperties", AllInstance);
                _transformOnEnable = transformType.GetMethod("OnEnable", AllInstance);
                if (_tracks == null || _coroutineDummy == null || _nullTrackProperties == null || _transformOnEnable == null)
                    throw new MissingMemberException("Heck replay seek members");

                _harmony.Patch(_transformOnEnable, prefix: new HarmonyMethod(typeof(ReplaySeekInterop).GetMethod(nameof(CaptureInitialTransform), BindingFlags.Static | BindingFlags.NonPublic)));
                foreach (var item in Resources.FindObjectsOfTypeAll(transformType)) {
                    if (item is Component component && component.gameObject.scene.IsValid())
                        Capture(component);
                }
            } catch (Exception ex) {
                Plugin.Log.Warn($"Heck replay seek setup unavailable: {ex.Message}");
                EndHeck();
            }
        }

        private void BeginChroma(DiContainer container) {
            var assembly = PluginManager.GetPluginFromId("Chroma")?.Assembly;
            if (assembly == null) return;
            try {
                var type = assembly.GetType("Chroma.Colorizer.LightColorizerManager", true);
                _chromaManager = container.Resolve(type);
                _chromaColorizers = (IDictionary)type.GetProperty("Colorizers", AllInstance)?.GetValue(_chromaManager);
                _resetChroma = type.GetMethod("ResetForReplaySeek", AllInstance);
                _finishChroma = type.GetMethod("FinishReplaySeek", AllInstance);
                if (_chromaColorizers == null || _resetChroma == null || _finishChroma == null)
                    throw new MissingMemberException("Chroma replay seek members");
            } catch (Exception ex) {
                Plugin.Log.Warn($"Chroma replay seek setup unavailable: {ex.Message}");
                EndChroma();
            }
        }

        private void BeginNoodle() {
            var assembly = PluginManager.GetPluginFromId("NoodleExtensions")?.Assembly;
            if (assembly == null) return;
            try {
                var type = assembly.GetType("NoodleExtensions.Managers.NoodleObjectsCallbacksManager", true);
                _noodlePrevSongTime = type.GetField("_prevSongtime", AllInstance);
                _noodleCallbacksInTime = type.GetField("_callbacksInTime", AllInstance);
                _noodleManualUpdate = type.GetMethod("ManualUpdate", AllInstance);
                if (_noodlePrevSongTime == null || _noodleCallbacksInTime == null || _noodleManualUpdate == null)
                    throw new MissingMemberException("Noodle replay seek members");
                _harmony.Patch(_noodleManualUpdate, prefix: new HarmonyMethod(typeof(ReplaySeekInterop).GetMethod(nameof(ResetNoodleCallbacksOnUpdate), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.Info("Noodle replay seek tracking ready");
            } catch (Exception ex) {
                Plugin.Log.Warn($"Noodle replay seek setup unavailable: {ex.Message}");
                if (_noodleManualUpdate != null)
                    _harmony.Unpatch(_noodleManualUpdate, HarmonyPatchType.Prefix, _harmony.Id);
                _noodleManualUpdate = null;
            }
        }

        public void RequestNoodleReprocess() {
            if (_noodleManualUpdate != null) _noodleReprocessRequested = true;
        }

        private static void ResetNoodleCallbacksOnUpdate(object __instance) {
            var active = _active;
            if (active == null || !active._noodleReprocessRequested) return;
            try {
                active._noodlePrevSongTime.SetValue(__instance, float.MinValue);
                ((CallbacksInTime)active._noodleCallbacksInTime.GetValue(__instance)).lastProcessedNode = null;
                active._noodleReprocessRequested = false;
                Plugin.Log.Debug("Reset Noodle callbacks for replay seek");
            } catch (Exception ex) {
                active._noodleReprocessRequested = false;
                Plugin.Log.Error($"Failed to reset Noodle callbacks for replay seek: {ex}");
            }
        }

        public void Rebuild(CallbacksInTime callbacks, float songTime) {
            if (_tracks == null && _chromaManager == null) return;
            try {
                _coroutineDummy?.StopAllCoroutines();
                _resetChroma?.Invoke(_chromaManager, null);
                if (_tracks != null) {
                    foreach (var track in _tracks.Values)
                        _nullTrackProperties.Invoke(track, null);
                    foreach (var pair in _initialTransforms) {
                        if (pair.Key == null) continue;
                        var transform = pair.Key.transform;
                        transform.localPosition = pair.Value.Position;
                        transform.localRotation = pair.Value.Rotation;
                        transform.localScale = pair.Value.Scale;
                    }
                }

                if (_animationEvents == null) _animationEvents = FindAnimationEvents();
                foreach (var item in _animationEvents) {
                    if (item.time >= songTime) break;
                    callbacks.CallCallbacks(item);
                }
                _finishChroma?.Invoke(_chromaManager, new object[] { songTime });
                Plugin.Log.Debug($"Rebuilt replay map state at {songTime:0.##}s");
            } catch (Exception ex) {
                Plugin.Log.Error($"Failed to rebuild replay map state: {ex}");
                EndHeck();
                EndChroma();
            }
        }

        private List<BeatmapDataItem> FindAnimationEvents() {
            var events = new List<BeatmapDataItem>();
            PropertyInfo eventTypeProperty = null;
            foreach (var item in _beatmapData.allBeatmapDataItems) {
                if (item is BasicBeatmapEventData lightEvent) {
                    if (_chromaColorizers?.Contains(lightEvent.basicBeatmapEventType) == true) events.Add(item);
                    continue;
                }
                if (item is ColorBoostBeatmapEventData && _chromaManager != null) {
                    events.Add(item);
                    continue;
                }
                if (_tracks == null || item.GetType().FullName != "CustomJSONData.CustomBeatmap.CustomEventData") continue;
                if (eventTypeProperty == null) eventTypeProperty = item.GetType().GetProperty("eventType");
                var eventType = eventTypeProperty?.GetValue(item) as string;
                if (eventType == "AnimateTrack" || eventType == "AssignPathAnimation" || eventType == "AnimateComponent")
                    events.Add(item);
            }
            return events;
        }

        private static void CaptureInitialTransform(object __instance) {
            if (__instance is Component component) _active?.Capture(component);
        }


        private void Capture(Component component) {
            if (!_initialTransforms.ContainsKey(component))
                _initialTransforms.Add(component, new TransformState(component.transform));
        }

        private void EndHeck() {
            if (_transformOnEnable != null)
                _harmony.Unpatch(_transformOnEnable, HarmonyPatchType.Prefix, _harmony.Id);
            _transformOnEnable = null;
            _tracks = null;
            _coroutineDummy = null;
            _nullTrackProperties = null;
            _initialTransforms.Clear();
        }

        private void EndChroma() {
            _chromaManager = null;
            _chromaColorizers = null;
            _resetChroma = null;
            _finishChroma = null;
        }

        public void Dispose() {
            EndHeck();
            EndChroma();
            if (_noodleManualUpdate != null)
                _harmony.Unpatch(_noodleManualUpdate, HarmonyPatchType.Prefix, _harmony.Id);
            if (_active == this) _active = null;
        }
    }
}
