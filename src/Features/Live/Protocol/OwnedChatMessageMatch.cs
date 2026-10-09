using ScoreSaber.Live.V1;
using System;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatMessageMatch {
        private readonly LiveChatMessage _message;
        private readonly string _matchId;
        private readonly string _currentMatchId;
        private readonly bool _matches;

        private OwnedChatMessageMatch(LiveChatMessage message, string matchId, string currentMatchId, bool matches) {
            _message = message;
            _matchId = matchId;
            _currentMatchId = currentMatchId;
            _matches = matches;
        }

        internal static OwnedChatMessageMatch Prepare(LiveChatMessage message, string currentMatchId) {
            try {
                if (message == null || message.GetType() != typeof(LiveChatMessage) || string.IsNullOrEmpty(currentMatchId)) {
                    return null;
                }
                string matchId = message.MatchId;
                if (matchId == null || ReferenceEquals(matchId, currentMatchId) || matchId.Length != currentMatchId.Length
                    || matchId.Length < 1024 * 1024) {
                    return null;
                }
                return new OwnedChatMessageMatch(message, matchId, currentMatchId,
                    string.Equals(matchId, currentMatchId, StringComparison.Ordinal));
            } catch {
                return null;
            }
        }

        internal bool TryGet(LiveChatMessage message, string matchId, string currentMatchId, out bool matches) {
            matches = false;
            if (!ReferenceEquals(message, _message) || !ReferenceEquals(matchId, _matchId)
                || !ReferenceEquals(currentMatchId, _currentMatchId)) {
                return false;
            }
            matches = _matches;
            return true;
        }
    }
}
