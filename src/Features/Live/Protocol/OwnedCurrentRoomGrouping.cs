using IPA.Utilities;
using ScoreSaber.Features.Replays;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Protocol {
    internal static class OwnedCurrentRoomGrouping {
        internal static bool TryGroup(IEnumerable<LiveRoomPlayerState> states,
            out Dictionary<string, LiveRoomPlayerState> result) {
            result = null;
            if (!(states is List<LiveRoomPlayerState> list)
                || list.GetType() != typeof(List<LiveRoomPlayerState>)
                || list.Count < 4096 || !UnityGame.OnMainThread) {
                return false;
            }
            try {
                for (int i = 0; i < list.Count; i++) {
                    LiveRoomPlayerState state = list[i];
                    if (state == null || state.GetType() != typeof(LiveRoomPlayerState)) {
                        return false;
                    }
                }
            } catch {
                return false;
            }

            var captured = new List<KeyValuePair<string, LiveRoomPlayerState>>();
            foreach (LiveRoomPlayerState state in list) {
                if (!string.IsNullOrEmpty(state.PlayerId)) {
                    captured.Add(new KeyValuePair<string, LiveRoomPlayerState>(state.PlayerId, state));
                }
            }
            result = Prepare(captured.ToArray());
            return true;
        }

        private static Dictionary<string, LiveRoomPlayerState> Prepare(
            KeyValuePair<string, LiveRoomPlayerState>[] snapshot) {
            if (snapshot.Length < 4096 || !ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Build(snapshot), out Task<Dictionary<string, LiveRoomPlayerState>> task)) {
                return Build(snapshot);
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            return task.GetAwaiter().GetResult();
        }

        private static Dictionary<string, LiveRoomPlayerState> Build(
            KeyValuePair<string, LiveRoomPlayerState>[] snapshot) {
            return snapshot.GroupBy(item => item.Key)
                .ToDictionary(group => group.Key, group => group.First().Value);
        }
    }
}
