using IPA.Utilities;
using ScoreSaber.Features.Replays;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Protocol {
    internal static class OwnedCurrentActiveIds {
        internal static bool TryAdd(LiveMatchRoomState room, List<string> source, HashSet<string> destination) {
            if (!CanPrepare(room, source) || !HasMaterialKeys(source)) {
                return false;
            }
            var captured = new List<string>();
            foreach (string id in source) {
                if (!string.IsNullOrEmpty(id)) {
                    captured.Add(id);
                }
            }
            AddCaptured(captured.ToArray(), destination);
            return true;
        }

        internal static bool TryAdd(LiveMatchRoomState room, List<LiveRoomPlayerState> source,
            HashSet<string> destination) {
            if (!CanPrepare(room, source)) {
                return false;
            }
            int count = source.Count;
            bool material = count >= 4096;
            long bytes = 0;
            try {
                for (int i = 0; i < count; i++) {
                    LiveRoomPlayerState state = source[i];
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
            var captured = new List<string>();
            foreach (LiveRoomPlayerState state in source) {
                if (!string.IsNullOrEmpty(state.PlayerId)) {
                    captured.Add(state.PlayerId);
                }
            }
            AddCaptured(captured.ToArray(), destination);
            return true;
        }

        private static bool CanPrepare<T>(LiveMatchRoomState room, List<T> source) {
            return source != null && source.GetType() == typeof(List<T>)
                && room.GetType() == typeof(LiveMatchRoomState) && UnityGame.OnMainThread;
        }

        private static void AddCaptured(string[] snapshot, HashSet<string> destination) {
            if (!HasMaterialKeys(snapshot) || !ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                Add(snapshot, destination);
                return true;
            }, out Task<bool> task)) {
                Add(snapshot, destination);
                return;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
        }

        private static void Add(string[] snapshot, HashSet<string> destination) {
            foreach (string id in snapshot) {
                destination.Add(id);
            }
        }

        private static bool HasMaterialKeys(IReadOnlyList<string> source) {
            try {
                int count = source.Count;
                if (count >= 4096) {
                    return true;
                }
                long bytes = 0;
                for (int i = 0; i < count; i++) {
                    string id = source[i];
                    if (id != null) {
                        bytes += (long)id.Length * 2;
                        if (bytes >= 1048576) {
                            return true;
                        }
                    }
                }
            } catch {
                return false;
            }
            return false;
        }
    }
}
