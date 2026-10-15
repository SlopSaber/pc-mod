using ScoreSaber.Live.V1;
using System;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatMessageMatch {
        private readonly LiveChatMessage _message;
        private readonly OwnedStringEquality _comparison;

        private OwnedChatMessageMatch(LiveChatMessage message, OwnedStringEquality comparison) {
            _message = message;
            _comparison = comparison;
        }

        internal static OwnedChatMessageMatch Prepare(LiveChatMessage message, string currentMatchId) {
            try {
                if (message == null || message.GetType() != typeof(LiveChatMessage) || string.IsNullOrEmpty(currentMatchId)) {
                    return null;
                }
                string matchId = message.MatchId;
                OwnedStringEquality comparison = OwnedStringEquality.Prepare(matchId, currentMatchId, true);
                return comparison.IsPrepared ? new OwnedChatMessageMatch(message, comparison) : null;
            } catch {
                return null;
            }
        }

        internal bool TryGet(LiveChatMessage message, string matchId, string currentMatchId, out bool matches) {
            matches = false;
            if (!ReferenceEquals(message, _message)) {
                return false;
            }
            return _comparison.TryGet(matchId, currentMatchId, out matches);
        }
    }
}
