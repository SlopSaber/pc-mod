using ScoreSaber.Features.Replays.Format;
using System;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Replays {

    internal class ReplayService {

        public event Action<byte[]> ReplaySerialized;
        internal event Action<object, byte[]> ReplaySerializedForPlay;

        private readonly ReplayFileCodec _replayFileCodec;
        private string _currentPlayId;
        private Recorder _replayRecorder;
        private object _currentResultPlay;
        private byte[] _serializedReplay;

        internal object CurrentResultPlay => _currentResultPlay;

        internal byte[] GetSerializedReplay(object play) => play != null && ReferenceEquals(play, _currentResultPlay) ? _serializedReplay : null;

        internal void RetireResultPlay(object play) {
            if (play == null || !ReferenceEquals(play, _currentResultPlay)) return;
            _currentResultPlay = null;
            _serializedReplay = null;
        }

        public ReplayService(ReplayFileCodec replayFileCodec) {
            _replayFileCodec = replayFileCodec;
        }

        public void NewPlayStarted(string playId, Recorder replayRecorder) {
            _currentPlayId = playId;
            _replayRecorder = replayRecorder;
            _currentResultPlay = new object();
            _serializedReplay = null;
            Plugin.Log.Debug($"New play started with id: {playId}");
        }

        public void DiscardReplay() {
            Recorder recorder = _replayRecorder;
            string playId = _currentPlayId;
            if (recorder == null) {
                return;
            }

            _currentResultPlay = null;
            _serializedReplay = null;
            Plugin.Log.Debug($"Discarding replay with id: {playId}");
            recorder.StopRecording();
            ClearRecorder(playId, recorder);
        }

        public async Task<ReplaySerializationResult> WriteReplay() {
            Recorder recorder = _replayRecorder;
            string playId = _currentPlayId;
            object resultPlay = _currentResultPlay;
            if (recorder == null) {
                Plugin.Log.Debug("Skipping replay write because no recorder is active");
                return null;
            }

            recorder.StopRecording();

            Plugin.Log.Debug($"Writing replay with id: {playId}");
            var replayFile = recorder.Export();
            float failTime = replayFile.metadata.FailTime;
            try {
                byte[] serializedReplay = await _replayFileCodec.Write(replayFile);
                if (serializedReplay == null) {
                    Plugin.Log.Warn($"Replay serialization failed: {playId}");
                    return null;
                }
                Plugin.Log.Debug($"Replay written: {playId}");
                ReplaySerialized?.Invoke(serializedReplay);
                if (resultPlay != null && ReferenceEquals(resultPlay, _currentResultPlay)) {
                    _serializedReplay = serializedReplay;
                    ReplaySerializedForPlay?.Invoke(resultPlay, serializedReplay);
                }
                return new ReplaySerializationResult(serializedReplay, failTime);
            } finally {
                ClearRecorder(playId, recorder);
            }
        }

        private void ClearRecorder(string playId, Recorder recorder) {
            if (_currentPlayId != playId || !ReferenceEquals(_replayRecorder, recorder)) {
                return;
            }

            _currentPlayId = null;
            _replayRecorder = null;
        }
    }

    internal class ReplaySerializationResult {
        internal ReplaySerializationResult(byte[] replay, float failTime) {
            Replay = replay;
            FailTime = failTime;
        }

        internal byte[] Replay { get; }
        internal float FailTime { get; }
    }
}
