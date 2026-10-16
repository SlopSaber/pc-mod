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
                || !UnityGame.OnMainThread) {
                return false;
            }
            bool material = list.Count >= 4096;
            long bytes = 0;
            try {
                for (int i = 0; i < list.Count; i++) {
                    LiveRoomPlayerState state = list[i];
                    if (state == null || state.GetType() != typeof(LiveRoomPlayerState)) {
                        return false;
                    }
                    if (!material) {
                        string id = state.PlayerId;
                        if (id != null) {
                            bytes += (long)id.Length * 2;
                            material = bytes >= 1048576;
                        }
                    }
                }
            } catch {
                return false;
            }
            if (!material) {
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
            if (!HasMaterialKeys(snapshot) || !ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
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

        private static bool HasMaterialKeys(KeyValuePair<string, LiveRoomPlayerState>[] snapshot) {
            if (snapshot.Length >= 4096) {
                return true;
            }
            long bytes = 0;
            foreach (var item in snapshot) {
                if (item.Key != null) {
                    bytes += (long)item.Key.Length * 2;
                    if (bytes >= 1048576) {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
