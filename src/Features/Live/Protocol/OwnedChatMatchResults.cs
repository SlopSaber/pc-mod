using ScoreSaber.Features.Replays;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Protocol {
    internal sealed class OwnedChatMatchResults {
        private readonly List<LiveChatMessage> _source;
        private readonly LiveChatMessage[] _messages;
        private readonly string[] _matchIds;
        private readonly bool[] _matches;

        private OwnedChatMatchResults(List<LiveChatMessage> source, LiveChatMessage[] messages, string[] matchIds, bool[] matches) {
            _source = source;
            _messages = messages;
            _matchIds = matchIds;
            _matches = matches;
        }

        internal static OwnedChatMatchResults Prepare(List<LiveChatMessage> source, string currentMatchId) {
            if (source == null || source.GetType() != typeof(List<LiveChatMessage>) || Thread.CurrentThread.IsThreadPoolThread) {
                return null;
            }
            try {
                if (!HasMaterialMatches(source, currentMatchId)) {
                    return null;
                }
            } catch {
                return null;
            }
            return PrepareOwned(source, currentMatchId);
        }

        private static bool HasMaterialMatches(List<LiveChatMessage> source, string currentMatchId) {
            long bytes = 0;
            foreach (LiveChatMessage message in source) {
                if (message == null) {
                    continue;
                }
                if (message.GetType() != typeof(LiveChatMessage)) {
                    return false;
                }
                string matchId = message.MatchId;
                if (matchId != null && !ReferenceEquals(matchId, currentMatchId) && matchId.Length == currentMatchId.Length) {
                    bytes += (long)matchId.Length * sizeof(char);
                    if (bytes >= 1024 * 1024) {
                        return true;
                    }
                }
            }
            return false;
        }

        private static OwnedChatMatchResults PrepareOwned(List<LiveChatMessage> source, string currentMatchId) {
            Task<bool[]> task = null;
            LiveChatMessage[] messages = null;
            string[] matchIds = null;
            try {
                messages = source.ToArray();
                matchIds = new string[messages.Length];
                for (int i = 0; i < messages.Length; i++) {
                    LiveChatMessage message = messages[i];
                    if (message != null && message.GetType() != typeof(LiveChatMessage)) {
                        return null;
                    }
                    matchIds[i] = message?.MatchId;
                }
                Func<bool[]> prepare = () => CompareMatches(matchIds, currentMatchId);
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)
                    && !ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task)) {
                    return null;
                }
            } catch {
                if (task == null) {
                    return null;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                return new OwnedChatMatchResults(source, messages, matchIds, task.GetAwaiter().GetResult());
            } catch {
                return null;
            }
        }

        private static bool[] CompareMatches(string[] matchIds, string currentMatchId) {
            var matches = new bool[matchIds.Length];
            for (int i = 0; i < matchIds.Length; i++) {
                matches[i] = string.Equals(matchIds[i], currentMatchId, StringComparison.Ordinal);
            }
            return matches;
        }

        internal bool TryGet(List<LiveChatMessage> source, int position, LiveChatMessage message, string matchId, out bool matches) {
            matches = false;
            if (!ReferenceEquals(source, _source) || (uint)position >= (uint)_messages.Length
                || !ReferenceEquals(message, _messages[position]) || !ReferenceEquals(matchId, _matchIds[position])) {
                return false;
            }
            matches = _matches[position];
            return true;
        }
    }
}
