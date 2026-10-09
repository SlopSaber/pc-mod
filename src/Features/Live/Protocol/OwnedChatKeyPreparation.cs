using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatKeyPreparation {
        private readonly List<LiveChatMessage> _source;
        private readonly LiveChatMessage[] _messages;
        private readonly string[] _keys;
        private readonly int[] _hashes;
        private readonly string[] _matchIds;
        private readonly int[] _matchGroups;
        private readonly string[] _groupIds;

        private OwnedChatKeyPreparation(List<LiveChatMessage> source, LiveChatMessage[] messages, string[] keys, int[] hashes,
            string[] matchIds, int[] matchGroups, string[] groupIds) {
            _source = source;
            _messages = messages;
            _keys = keys;
            _hashes = hashes;
            _matchIds = matchIds;
            _matchGroups = matchGroups;
            _groupIds = groupIds;
        }

        internal static OwnedChatKeyPreparation Prepare(LiveChatSnapshot snapshot) {
            try {
                List<LiveChatMessage> source = snapshot?.Messages;
                if (source == null || source.Count < 4096 || source.Count > 65536) {
                    return null;
                }
                long keyLength = 0;
                long matchLength = 0;
                for (int i = 0; i < source.Count; i++) {
                    keyLength += source[i]?.MessageId?.Length ?? 0;
                    matchLength += source[i]?.MatchId?.Length ?? 0;
                }
                bool prepareKeys = keyLength >= 1024 * 1024;
                bool prepareMatches = matchLength >= 1024 * 1024;
                if (!prepareKeys && !prepareMatches) {
                    return null;
                }
                var messages = new LiveChatMessage[source.Count];
                var keys = prepareKeys ? new string[source.Count] : null;
                var hashes = prepareKeys ? new int[source.Count] : null;
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
                return new OwnedChatKeyPreparation(source, messages, keys, hashes, matchIds, matchGroups, groupIds?.ToArray());
            } catch {
                return null;
            }
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
    }
}
