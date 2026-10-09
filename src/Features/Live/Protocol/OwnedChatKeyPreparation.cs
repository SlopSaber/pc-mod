using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatKeyPreparation {
        private readonly List<LiveChatMessage> _source;
        private readonly LiveChatMessage[] _messages;
        private readonly string[] _keys;
        private readonly int[] _hashes;

        private OwnedChatKeyPreparation(List<LiveChatMessage> source, LiveChatMessage[] messages, string[] keys, int[] hashes) {
            _source = source;
            _messages = messages;
            _keys = keys;
            _hashes = hashes;
        }

        internal static OwnedChatKeyPreparation Prepare(LiveChatSnapshot snapshot) {
            try {
                List<LiveChatMessage> source = snapshot?.Messages;
                if (source == null || source.Count < 4096 || source.Count > 65536) {
                    return null;
                }
                long keyLength = 0;
                for (int i = 0; i < source.Count; i++) {
                    keyLength += source[i]?.MessageId?.Length ?? 0;
                }
                if (keyLength < 1024 * 1024) {
                    return null;
                }
                var messages = new LiveChatMessage[source.Count];
                var keys = new string[source.Count];
                var hashes = new int[source.Count];
                for (int i = 0; i < source.Count; i++) {
                    LiveChatMessage message = source[i];
                    string key = message?.MessageId;
                    messages[i] = message;
                    keys[i] = key;
                    if (!string.IsNullOrEmpty(key)) {
                        hashes[i] = StringComparer.Ordinal.GetHashCode(key);
                    }
                }
                return new OwnedChatKeyPreparation(source, messages, keys, hashes);
            } catch {
                return null;
            }
        }

        internal bool TryGetHash(List<LiveChatMessage> source, int position, LiveChatMessage message, string key, out int hash) {
            hash = 0;
            if (!ReferenceEquals(source, _source) || (uint)position >= (uint)_messages.Length
                || !ReferenceEquals(message, _messages[position]) || string.IsNullOrEmpty(key)
                || !ReferenceEquals(key, _keys[position])) {
                return false;
            }
            hash = _hashes[position];
            return true;
        }
    }
}
