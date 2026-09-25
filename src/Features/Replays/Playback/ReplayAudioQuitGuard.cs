using System;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Replays.Playback {
    internal sealed class ReplayAudioQuitGuard : IInitializable, IDisposable {
        private readonly AudioTimeSyncController _audioTimeSyncController;
        private bool _stopped;

        public ReplayAudioQuitGuard(AudioTimeSyncController audioTimeSyncController) {
            _audioTimeSyncController = audioTimeSyncController;
        }

        public void Initialize() => Application.quitting += OnApplicationQuitting;

        private void OnApplicationQuitting() => StopReplaySong(true);

        private void StopReplaySong(bool quitting) {
            if (_stopped) return;
            var source = _audioTimeSyncController?._audioSource;
            if (source == null) return;

            bool wasPlaying = source.isPlaying;
            float volume = source.volume;
            if (quitting) source.mute = true;
            source.Stop();
            _stopped = true;
            if (quitting)
                Plugin.Log.Info($"Stopped replay song before quit: playing={wasPlaying}, volume={volume:0.###}");
        }

        public void Dispose() {
            Application.quitting -= OnApplicationQuitting;
            StopReplaySong(false);
        }
    }
}
