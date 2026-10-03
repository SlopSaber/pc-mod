using ScoreSaber.Features.Replays.Format;
using ScoreSaber.Features.Replays.Playback;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Replays {
    internal sealed class ReplayPlaybackIndexes {
        internal readonly NoteEvent[] SortedNoteEvents;
        internal readonly float[] ScoringNoteEventTimes;
        internal readonly float[] ComboLossTimes;

        internal ReplayPlaybackIndexes(NoteEvent[] sortedNoteEvents, float[] scoringNoteEventTimes, float[] comboLossTimes) {
            SortedNoteEvents = sortedNoteEvents;
            ScoringNoteEventTimes = scoringNoteEventTimes;
            ComboLossTimes = comboLossTimes;
        }
    }

    internal static class ReplayPlaybackPreparation {
        private static readonly SemaphoreSlim PhysicalSlot = new SemaphoreSlim(1, 1);
        private static Task<ReplayPlaybackIndexes> _physicalTask;

        private sealed class Request {
            internal readonly List<VRPoseGroup> PoseKeyframes;
            internal readonly List<NoteEvent> NoteKeyframes;
            internal readonly List<ComboEvent> ComboKeyframes;
            internal readonly bool Mirror;
            internal readonly CancellationToken CancellationToken;

            internal Request(List<VRPoseGroup> poseKeyframes, List<NoteEvent> noteKeyframes,
                List<ComboEvent> comboKeyframes, bool mirror, CancellationToken cancellationToken) {
                PoseKeyframes = poseKeyframes;
                NoteKeyframes = noteKeyframes;
                ComboKeyframes = comboKeyframes;
                Mirror = mirror;
                CancellationToken = cancellationToken;
            }
        }

        internal static async Task<ReplayPlaybackIndexes> Prepare(List<VRPoseGroup> poseKeyframes,
            List<NoteEvent> noteKeyframes, List<ComboEvent> comboKeyframes, bool mirror, CancellationToken cancellationToken) {
            var request = new Request(poseKeyframes, noteKeyframes, comboKeyframes, mirror, cancellationToken);
            await PhysicalSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                _physicalTask = Task.Run(() => Process(request));
                return await _physicalTask.ConfigureAwait(false);
            } finally {
                _physicalTask = null;
                PhysicalSlot.Release();
            }
        }

        private static ReplayPlaybackIndexes Process(Request request) {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (request.Mirror) {
                ReplayFile.MirrorFrames(request.PoseKeyframes, request.NoteKeyframes);
            }
            var indexes = new ReplayPlaybackIndexes(
                request.NoteKeyframes.OrderBy(noteEvent => noteEvent.Time).ToArray(),
                request.NoteKeyframes.Where(ReplayTimeSearch.IsScoringNoteEvent)
                    .Select(noteEvent => noteEvent.Time).OrderBy(time => time).ToArray(),
                request.ComboKeyframes.Where(comboEvent => comboEvent.Combo == 0)
                    .Select(comboEvent => comboEvent.Time).OrderBy(time => time).ToArray());
            request.CancellationToken.ThrowIfCancellationRequested();
            return indexes;
        }
    }
}
