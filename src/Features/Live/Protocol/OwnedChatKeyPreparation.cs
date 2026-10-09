using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Globalization;
using ScoreSaber.Features.Live.Ludus.Domain;
using ScoreSaber.Features.Replays;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatKeyPreparation {
        private readonly List<LiveChatMessage> _source;
        private readonly LiveChatMessage[] _messages;
        private readonly string[] _keys;
        private readonly int[] _hashes;
        private readonly string[] _matchIds;
        private readonly int[] _matchGroups;
        private readonly string[] _groupIds;
        private readonly string[] _fallbackMatchIds;
        private readonly ulong[] _fallbackSequences;

        private OwnedChatKeyPreparation(List<LiveChatMessage> source, LiveChatMessage[] messages, string[] keys, int[] hashes,
            string[] matchIds, int[] matchGroups, string[] groupIds, string[] fallbackMatchIds, ulong[] fallbackSequences) {
            _source = source;
            _messages = messages;
            _keys = keys;
            _hashes = hashes;
            _matchIds = matchIds;
            _matchGroups = matchGroups;
            _groupIds = groupIds;
            _fallbackMatchIds = fallbackMatchIds;
            _fallbackSequences = fallbackSequences;
        }

        internal static OwnedChatKeyPreparation Prepare(LiveChatSnapshot snapshot) {
            try {
                List<LiveChatMessage> source = snapshot?.Messages;
                if (source == null || source.Count < 4096 || source.Count > 65536) {
                    return null;
                }
                long keyLength = 0;
                long matchLength = 0;
                long fallbackLength = 0;
                for (int i = 0; i < source.Count; i++) {
                    keyLength += source[i]?.MessageId?.Length ?? 0;
                    matchLength += source[i]?.MatchId?.Length ?? 0;
                    if (source[i] != null && string.IsNullOrEmpty(source[i].MessageId)) {
                        fallbackLength += source[i].MatchId?.Length ?? 0;
                    }
                }
                bool prepareKeys = keyLength >= 1024 * 1024;
                bool prepareMatches = matchLength >= 1024 * 1024;
                bool prepareFallbacks = fallbackLength >= 1024 * 1024;
                if (!prepareKeys && !prepareMatches && !prepareFallbacks) {
                    return null;
                }
                var messages = new LiveChatMessage[source.Count];
                var keys = prepareKeys || prepareFallbacks ? new string[source.Count] : null;
                var hashes = prepareKeys || prepareFallbacks ? new int[source.Count] : null;
                var fallbackMatchIds = prepareFallbacks ? new string[source.Count] : null;
                var fallbackSequences = prepareFallbacks ? new ulong[source.Count] : null;
                var matchIds = prepareMatches ? new string[source.Count] : null;
                var matchGroups = prepareMatches ? new int[source.Count] : null;
                var groups = prepareMatches ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
                var groupIds = prepareMatches ? new List<string>() : null;
                for (int i = 0; i < source.Count; i++) {
                    LiveChatMessage message = source[i];
                    messages[i] = message;
                    if (prepareKeys) {
                        string key = message?.MessageId;
                        keys[i] = key;
                        if (!string.IsNullOrEmpty(key)) {
                            hashes[i] = StringComparer.Ordinal.GetHashCode(key);
                        }
                    }
                    if (prepareFallbacks && message != null && string.IsNullOrEmpty(message.MessageId)) {
                        string matchId = message.MatchId ?? string.Empty;
                        ulong sequence = message.RoomSequence;
                        string key = string.Format(CultureInfo.InvariantCulture, "{0}:{1}", matchId, sequence);
                        fallbackMatchIds[i] = matchId;
                        fallbackSequences[i] = sequence;
                        keys[i] = key;
                        hashes[i] = StringComparer.Ordinal.GetHashCode(key);
                    }
                    if (prepareMatches) {
                        string matchId = message?.MatchId;
                        matchIds[i] = matchId;
                        matchGroups[i] = -1;
                        if (!string.IsNullOrEmpty(matchId)) {
                            if (!groups.TryGetValue(matchId, out int group)) {
                                group = groupIds.Count;
                                groups.Add(matchId, group);
                                groupIds.Add(matchId);
                            }
                            matchGroups[i] = group;
                        }
                    }
                }
                return new OwnedChatKeyPreparation(source, messages, keys, hashes, matchIds, matchGroups, groupIds?.ToArray(),
                    fallbackMatchIds, fallbackSequences);
            } catch {
                return null;
            }
        }

        internal bool TryGetKey(List<LiveChatMessage> source, int position, LiveChatMessage message, LiveChatEntry entry,
            out string key, out int hash) {
            key = null;
            hash = 0;
            if (_keys == null || !ReferenceEquals(source, _source) || (uint)position >= (uint)_messages.Length
                || !ReferenceEquals(message, _messages[position])) {
                return false;
            }
            if (string.IsNullOrEmpty(entry.MessageId)) {
                if (_fallbackMatchIds == null || _fallbackMatchIds[position] == null
                    || !ReferenceEquals(entry.MatchId, _fallbackMatchIds[position])
                    || entry.RoomSequence != _fallbackSequences[position]) {
                    return false;
                }
            } else if (!ReferenceEquals(entry.MessageId, _keys[position])) {
                return false;
            }
            key = _keys[position];
            if (key == null) {
                return false;
            }
            hash = _hashes[position];
            return true;
        }

        internal bool TryGetHash(List<LiveChatMessage> source, int position, LiveChatMessage message, string key, out int hash) {
            hash = 0;
            if (_keys == null || !ReferenceEquals(source, _source) || (uint)position >= (uint)_messages.Length
                || !ReferenceEquals(message, _messages[position]) || string.IsNullOrEmpty(key)
                || !ReferenceEquals(key, _keys[position])) {
                return false;
            }
            hash = _hashes[position];
            return true;
        }

        internal byte[] CreateMatchCache() {
            try { return _groupIds == null ? null : new byte[_groupIds.Length]; }
            catch { return null; }
        }

        internal KeySlots CreateKeySlots(Dictionary<string, int> currentIndex) {
            try {
                if (_keys == null) {
                    return null;
                }
                var rows = new int[_keys.Length];
                var groups = new Dictionary<string, int>(StringComparer.Ordinal);
                var indices = new List<int>();
                for (int i = 0; i < _keys.Length; i++) {
                    string key = _keys[i];
                    rows[i] = -1;
                    if (key == null) {
                        continue;
                    }
                    if (!groups.TryGetValue(key, out int group)) {
                        group = indices.Count;
                        groups.Add(key, group);
                        indices.Add(currentIndex.TryGetValue(key, out int index) ? index : -1);
                    }
                    rows[i] = group;
                }
                return new KeySlots(rows, indices.ToArray());
            } catch {
                return null;
            }
        }

        internal sealed class KeySlots {
            private readonly int[] _rows;
            private readonly int[] _indices;

            internal KeySlots(int[] rows, int[] indices) {
                _rows = rows;
                _indices = indices;
            }

            internal bool TryFind(int position, out int index) {
                index = -1;
                if ((uint)position >= (uint)_rows.Length || _rows[position] < 0) {
                    return false;
                }
                index = _indices[_rows[position]];
                return true;
            }

            internal void Set(int position, int index) {
                _indices[_rows[position]] = index;
            }
        }

        internal bool TryMatches(List<LiveChatMessage> source, int position, LiveChatMessage message,
            string matchId, string currentMatchId, byte[] cache, out bool matches) {
            matches = false;
            if (cache == null || !ReferenceEquals(source, _source) || (uint)position >= (uint)_messages.Length
                || !ReferenceEquals(message, _messages[position]) || !ReferenceEquals(matchId, _matchIds[position])) {
                return false;
            }
            int group = _matchGroups[position];
            if (group < 0) {
                return false;
            }
            if (cache[group] == 0) {
                cache[group] = string.Equals(_groupIds[group], currentMatchId, StringComparison.Ordinal) ? (byte)1 : (byte)2;
            }
            matches = cache[group] == 1;
            return true;
        }

        internal byte[] CreateMatchCache(string currentMatchId) {
            byte[] cache = CreateMatchCache();
            if (cache == null || Thread.CurrentThread.IsThreadPoolThread || !HasMaterialMatches(currentMatchId)) {
                return cache;
            }
            FillOwnedMatchCache(_groupIds, currentMatchId, cache);
            return cache;
        }

        private bool HasMaterialMatches(string currentMatchId) {
            if (string.IsNullOrEmpty(currentMatchId)) {
                return false;
            }
            long bytes = 0;
            foreach (string groupId in _groupIds) {
                if (groupId != null && !ReferenceEquals(groupId, currentMatchId) && groupId.Length == currentMatchId.Length) {
                    bytes += (long)groupId.Length * sizeof(char);
                    if (bytes >= 1024 * 1024) {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void FillOwnedMatchCache(string[] groupIds, string currentMatchId, byte[] cache) {
            Task<bool> task = null;
            try {
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                    () => FillMatchCache(groupIds, currentMatchId, cache), out task)) {
                    return;
                }
            } catch {
                if (task == null) {
                    return;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try { task.GetAwaiter().GetResult(); }
            catch { }
        }

        private static bool FillMatchCache(string[] groupIds, string currentMatchId, byte[] cache) {
            for (int i = 0; i < groupIds.Length; i++) {
                cache[i] = string.Equals(groupIds[i], currentMatchId, StringComparison.Ordinal) ? (byte)1 : (byte)2;
            }
            return true;
        }
    }
}
