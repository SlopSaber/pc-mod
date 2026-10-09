using ScoreSaber.Features.Live.Ludus.Domain;
using ScoreSaber.Live.V1;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Ludus.Packets {
    internal sealed class LudusChatMessageBuffer {
        private const int MaxMessages = 200;
        private readonly List<LiveChatEntry> _messages = new List<LiveChatEntry>();
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private Dictionary<string, int> _replaceKeyIndex;
        private CultureInfo _replaceKeyCulture;
        private bool _replaceIndexAllowed;
        private bool _replaceIndexAttempted;

        internal IReadOnlyList<LiveChatEntry> CurrentMessages => _messages.ToArray();

        internal IReadOnlyList<LiveChatEntry> MessagesFor(string matchId) {
            if (string.IsNullOrEmpty(matchId)) {
                return Array.Empty<LiveChatEntry>();
            }

            return _messages.FindAll(message => string.Equals(message.MatchId, matchId, StringComparison.Ordinal)).ToArray();
        }

        internal bool Apply(LiveChatMessage message, string currentMatchId) {
            ClearReplaceKeyIndex();
            LiveChatEntry entry = EntryForCurrentMatch(message, currentMatchId);
            if (entry == null) {
                return false;
            }

            Upsert(entry);
            SortAndTrim();
            return true;
        }

        internal void Replace(LiveChatSnapshot snapshot, string currentMatchId) {
            if (string.IsNullOrEmpty(currentMatchId)) {
                return;
            }

            if (snapshot?.Messages == null) {
                return;
            }

            _replaceIndexAllowed = true;
            _replaceIndexAttempted = false;
            try {
                foreach (LiveChatMessage message in snapshot.Messages) {
                    LiveChatEntry entry = EntryForCurrentMatch(message, currentMatchId);
                    if (entry != null) {
                        Upsert(entry);
                    }
                }

                SortAndTrim();
            } finally {
                _replaceIndexAllowed = false;
                ClearReplaceKeyIndex();
            }
        }

        internal bool Clear() {
            if (_messages.Count == 0) {
                return false;
            }

            ClearReplaceKeyIndex();
            _messages.Clear();
            return true;
        }

        private static LiveChatEntry EntryForCurrentMatch(LiveChatMessage message, string currentMatchId) {
            if (message == null || string.IsNullOrEmpty(message.MatchId) || !string.Equals(message.MatchId, currentMatchId, StringComparison.Ordinal)) {
                return null;
            }

            return LiveChatEntry.FromProto(message);
        }

        private void Upsert(LiveChatEntry entry) {
            bool indexed = TryFindReplaceKey(entry, out int index, out string key);
            if (!indexed) {
                index = _messages.FindIndex(item => item.Key == entry.Key);
            }
            if (index >= 0) {
                _messages[index] = entry;
            } else {
                index = _messages.Count;
                _messages.Add(entry);
            }
            if (indexed) {
                try { _replaceKeyIndex[key] = index; }
                catch { ClearReplaceKeyIndex(); }
            }
        }

        private void SortAndTrim() {
            ClearReplaceKeyIndex();
            if (!TrySortLargeOwnedBuffer()) {
                _messages.Sort(CompareEntries);
            }
            if (_messages.Count > MaxMessages) {
                _messages.RemoveRange(0, _messages.Count - MaxMessages);
            }
        }

        private void ClearReplaceKeyIndex() {
            _replaceKeyIndex = null;
            _replaceKeyCulture = null;
        }

        private bool TryFindReplaceKey(LiveChatEntry entry, out int index, out string key) {
            index = -1;
            key = null;
            if (!_replaceIndexAllowed || Thread.CurrentThread.IsThreadPoolThread
                || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                return false;
            }
            if (_replaceKeyIndex == null) {
                if (_replaceIndexAttempted || _messages.Count < 4096 || Thread.CurrentThread.IsThreadPoolThread
                    || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                    return false;
                }
                _replaceIndexAttempted = true;
                if (!TryBuildReplaceKeyIndex()) {
                    return false;
                }
            }
            if (!ReferenceEquals(CultureInfo.CurrentCulture, _replaceKeyCulture)) {
                ClearReplaceKeyIndex();
                return false;
            }
            key = entry.Key;
            if (!_replaceKeyIndex.TryGetValue(key, out index)) {
                index = -1;
            }
            return true;
        }

        private bool TryBuildReplaceKeyIndex() {
            Task<Dictionary<string, int>> task = null;
            CultureInfo culture;
            try {
                culture = CultureInfo.CurrentCulture;
                if (culture.GetType() != typeof(CultureInfo) || !culture.IsReadOnly
                    || culture.NumberFormat.GetType() != typeof(NumberFormatInfo) || !culture.NumberFormat.IsReadOnly) {
                    return false;
                }
                CultureInfo ownedCulture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => BuildOwnedKeyIndex(ownedCulture), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
                culture = CultureInfo.CurrentCulture;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                _replaceKeyIndex = task.GetAwaiter().GetResult();
                _replaceKeyCulture = culture;
                return true;
            } catch {
                ClearReplaceKeyIndex();
                return false;
            }
        }

        private Dictionary<string, int> BuildOwnedKeyIndex(CultureInfo culture) {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = culture;
                var index = new Dictionary<string, int>(_messages.Count, StringComparer.Ordinal);
                for (int i = 0; i < _messages.Count; i++) {
                    string key = _messages[i].Key;
                    if (!index.ContainsKey(key)) {
                        index.Add(key, i);
                    }
                }
                return index;
            } finally {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private bool TrySortLargeOwnedBuffer() {
            if (_messages.Count < 4096 || Thread.CurrentThread.IsThreadPoolThread
                || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                return false;
            }

            return TrySortOwnedBuffer();
        }

        private bool TrySortOwnedBuffer() {
            Task<ExceptionDispatchInfo> task = null;
            try {
                CultureInfo culture = CultureInfo.CurrentCulture;
                if (culture.GetType() != typeof(CultureInfo) || !culture.IsReadOnly
                    || culture.NumberFormat.GetType() != typeof(NumberFormatInfo) || !culture.NumberFormat.IsReadOnly) {
                    return false;
                }
                CultureInfo ownedCulture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
                if (!ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => SortOwnedBuffer(ownedCulture), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }

            // Keep the private list exclusively leased until the physical sort completes.
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult()?.Throw();
            return true;
        }

        private ExceptionDispatchInfo SortOwnedBuffer(CultureInfo culture) {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = culture;
                try {
                    _messages.Sort(CompareEntries);
                    return null;
                } catch (Exception error) {
                    return ExceptionDispatchInfo.Capture(error);
                }
            } finally {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static int CompareEntries(LiveChatEntry left, LiveChatEntry right) {
            if (left == null && right == null) {
                return 0;
            }
            if (left == null) {
                return -1;
            }
            if (right == null) {
                return 1;
            }

            if (left.CreatedAtUnixMs > 0 && right.CreatedAtUnixMs > 0 && left.CreatedAtUnixMs != right.CreatedAtUnixMs) {
                return left.CreatedAtUnixMs.CompareTo(right.CreatedAtUnixMs);
            }

            int matchComparison = string.CompareOrdinal(left.MatchId, right.MatchId);
            if (matchComparison != 0) {
                return matchComparison;
            }

            int sequenceComparison = left.RoomSequence.CompareTo(right.RoomSequence);
            if (sequenceComparison != 0) {
                return sequenceComparison;
            }

            return string.CompareOrdinal(left.Key, right.Key);
        }
    }
}
