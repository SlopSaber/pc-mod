using ScoreSaber.Features.Live.Ludus.Domain;
using ScoreSaber.Features.Live.Protocol;
using ScoreSaber.Live.V1;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Ludus.Packets {
    internal sealed class LudusChatMessageBuffer {
        private const int MaxMessages = 200;
        private readonly List<LiveChatEntry> _messages = new List<LiveChatEntry>();
        private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
        private Dictionary<string, int> _replaceKeyIndex;
        private PreparedKeyComparer _replaceKeyComparer;
        private OwnedChatKeyPreparation _replacePreparation;
        private OwnedChatKeyPreparation.KeySlots _replaceKeySlots;
        private bool _replaceIndexAllowed;
        private bool _replaceIndexAttempted;

        internal IReadOnlyList<LiveChatEntry> CurrentMessages => TryPrepareSnapshot(null, out LiveChatEntry[] entries)
            ? entries : _messages.ToArray();

        internal IReadOnlyList<LiveChatEntry> MessagesFor(string matchId) {
            if (string.IsNullOrEmpty(matchId)) {
                return Array.Empty<LiveChatEntry>();
            }

            if (TryPrepareSnapshot(matchId, out LiveChatEntry[] entries)) {
                return entries;
            }
            return FilterMessages(matchId);
        }

        private LiveChatEntry[] FilterMessages(string matchId) {
            return _messages.FindAll(message => string.Equals(message.MatchId, matchId, StringComparison.Ordinal)).ToArray();
        }

        private bool TryPrepareSnapshot(string matchId, out LiveChatEntry[] entries) {
            entries = null;
            if (Thread.CurrentThread.IsThreadPoolThread || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                return false;
            }
            if (_messages.Count < 4096) {
                if (matchId == null || (long)matchId.Length * _messages.Count < 1024 * 1024) {
                    return false;
                }
                long comparisonLength = 0;
                for (int i = 0; i < _messages.Count; i++) {
                    string value = _messages[i].MatchId;
                    if (!ReferenceEquals(value, matchId) && value.Length == matchId.Length) {
                        comparisonLength += value.Length;
                    }
                }
                if (comparisonLength < 1024 * 1024) {
                    return false;
                }
            }
            return TryPrepareOwnedSnapshot(matchId, out entries);
        }

        private bool TryPrepareOwnedSnapshot(string matchId, out LiveChatEntry[] entries) {
            entries = null;
            Task<OwnedSnapshot> task = null;
            try {
                if (!TryQueueBufferPreparation(() => CreateOwnedSnapshot(matchId), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            OwnedSnapshot result = task.GetAwaiter().GetResult();
            result.Error?.Throw();
            entries = result.Entries;
            return true;
        }

        private OwnedSnapshot CreateOwnedSnapshot(string matchId) {
            try {
                return new OwnedSnapshot(matchId == null ? _messages.ToArray() : FilterMessages(matchId), null);
            } catch (Exception error) {
                return new OwnedSnapshot(null, ExceptionDispatchInfo.Capture(error));
            }
        }

        private sealed class OwnedSnapshot {
            internal readonly LiveChatEntry[] Entries;
            internal readonly ExceptionDispatchInfo Error;

            internal OwnedSnapshot(LiveChatEntry[] entries, ExceptionDispatchInfo error) {
                Entries = entries;
                Error = error;
            }
        }

        internal bool Apply(LiveChatMessage message, string currentMatchId) {
            return Apply(message, currentMatchId, null);
        }

        internal bool Apply(LiveChatMessage message, string currentMatchId, OwnedChatMessageMatch preparedMatch) {
            ClearReplaceKeyIndex();
            LiveChatEntry entry = EntryForCurrentMatch(message, currentMatchId, preparedMatch);
            if (entry == null) {
                return false;
            }

            Upsert(entry);
            SortAndTrim();
            return true;
        }

        internal void Replace(LiveChatSnapshot snapshot, string currentMatchId) {
            Replace(snapshot, currentMatchId, null);
        }

        internal void Replace(LiveChatSnapshot snapshot, string currentMatchId, OwnedChatKeyPreparation preparedKeys) {
            if (string.IsNullOrEmpty(currentMatchId)) {
                return;
            }

            if (snapshot?.Messages == null) {
                return;
            }

            _replaceIndexAllowed = true;
            _replaceIndexAttempted = false;
            _replacePreparation = preparedKeys;
            try {
                List<LiveChatMessage> source = snapshot.Messages;
                byte[] matchCache = preparedKeys?.CreateMatchCache(currentMatchId);
                OwnedChatMatchResults matchResults = preparedKeys == null && Thread.CurrentThread.ManagedThreadId == _ownerThread
                    ? OwnedChatMatchResults.Prepare(source, currentMatchId) : null;
                int position = 0;
                foreach (LiveChatMessage message in source) {
                    LiveChatEntry entry = EntryForCurrentMatch(message, currentMatchId, preparedKeys, source, position, matchCache, matchResults);
                    if (entry != null) {
                        int hash = 0;
                        string key = null;
                        bool prepared = preparedKeys != null && preparedKeys.TryGetKey(source, position, message, entry, out key, out hash);
                        Upsert(entry, prepared, key, hash, position);
                    }
                    position++;
                }

                SortAndTrim();
            } finally {
                _replaceIndexAllowed = false;
                _replacePreparation = null;
                ClearReplaceKeyIndex();
            }
        }

        internal bool Clear() {
            if (_messages.Count == 0) {
                return false;
            }

            ClearReplaceKeyIndex();
            if (!TryClearLargeOwnedBuffer()) {
                _messages.Clear();
            }
            return true;
        }

        private bool TryClearLargeOwnedBuffer() {
            if (_messages.Count < 4096 || Thread.CurrentThread.IsThreadPoolThread
                || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                return false;
            }
            return TryClearOwnedBuffer();
        }

        private bool TryClearOwnedBuffer() {
            Task<ExceptionDispatchInfo> task = null;
            try {
                if (!TryQueueBufferPreparation(ClearOwnedBuffer, out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult()?.Throw();
            return true;
        }

        private ExceptionDispatchInfo ClearOwnedBuffer() {
            try {
                _messages.Clear();
                return null;
            } catch (Exception error) {
                return ExceptionDispatchInfo.Capture(error);
            }
        }

        private static LiveChatEntry EntryForCurrentMatch(LiveChatMessage message, string currentMatchId) {
            return EntryForCurrentMatch(message, currentMatchId, null);
        }

        private static LiveChatEntry EntryForCurrentMatch(LiveChatMessage message, string currentMatchId, OwnedChatMessageMatch preparedMatch) {
            if (message == null || string.IsNullOrEmpty(message.MatchId)) {
                return null;
            }
            string matchId = message.MatchId;
            bool matches;
            if (preparedMatch == null || !preparedMatch.TryGet(message, matchId, currentMatchId, out matches)) {
                matches = string.Equals(matchId, currentMatchId, StringComparison.Ordinal);
            }
            return matches ? LiveChatEntry.FromProto(message) : null;
        }

        private static LiveChatEntry EntryForCurrentMatch(LiveChatMessage message, string currentMatchId,
            OwnedChatKeyPreparation preparedKeys, List<LiveChatMessage> source, int position, byte[] matchCache) {
            return EntryForCurrentMatch(message, currentMatchId, preparedKeys, source, position, matchCache, null);
        }

        private static LiveChatEntry EntryForCurrentMatch(LiveChatMessage message, string currentMatchId,
            OwnedChatKeyPreparation preparedKeys, List<LiveChatMessage> source, int position, byte[] matchCache, OwnedChatMatchResults matchResults) {
            if (message == null || string.IsNullOrEmpty(message.MatchId)) {
                return null;
            }
            string matchId = message.MatchId;
            bool matches;
            if ((matchResults == null || !matchResults.TryGet(source, position, message, matchId, out matches))
                && (preparedKeys == null || !preparedKeys.TryMatches(source, position, message, matchId, currentMatchId, matchCache, out matches))) {
                matches = string.Equals(matchId, currentMatchId, StringComparison.Ordinal);
            }
            return matches ? LiveChatEntry.FromProto(message) : null;
        }

        private void Upsert(LiveChatEntry entry) {
            Upsert(entry, false, null, 0, -1);
        }

        private void Upsert(LiveChatEntry entry, bool prepared, string preparedKey, int hash, int position) {
            try {
                UpsertCore(entry, prepared, preparedKey, hash, position);
            } finally {
                _replaceKeyComparer?.Clear();
            }
        }

        private void UpsertCore(LiveChatEntry entry, bool prepared, string preparedKey, int hash, int position) {
            bool indexed = TryFindReplaceKey(entry, prepared, preparedKey, hash, position, out int index, out string key);
            if (!indexed && !TryFindOwnedEntry(entry, out index)) {
                index = _messages.FindIndex(item => item.Key == entry.Key);
            }
            if (index >= 0) {
                _messages[index] = entry;
            } else {
                index = _messages.Count;
                _messages.Add(entry);
            }
            if (indexed) {
                try {
                    if (_replaceKeySlots != null) {
                        _replaceKeySlots.Set(position, index);
                    } else {
                        _replaceKeyIndex[key] = index;
                    }
                }
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
            _replaceKeyComparer = null;
            _replaceKeySlots = null;
        }

        private bool TryFindReplaceKey(LiveChatEntry entry, bool prepared, string preparedKey, int hash, int position,
            out int index, out string key) {
            index = -1;
            key = null;
            if (!_replaceIndexAllowed || Thread.CurrentThread.IsThreadPoolThread
                || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                if (_replaceKeySlots != null) {
                    ClearReplaceKeyIndex();
                }
                return false;
            }
            if (_replaceKeyIndex == null) {
                if (_replaceIndexAttempted || Thread.CurrentThread.IsThreadPoolThread
                    || Thread.CurrentThread.ManagedThreadId != _ownerThread || !HasMaterialKeyVolume()) {
                    return false;
                }
                _replaceIndexAttempted = true;
                if (!TryBuildReplaceKeyIndex()) {
                    return false;
                }
            }
            if (_replaceKeySlots != null) {
                if (prepared && _replaceKeySlots.TryFind(position, out index)) {
                    return true;
                }
                ClearReplaceKeyIndex();
                return false;
            }
            key = prepared ? preparedKey : entry.Key;
            if (prepared) {
                _replaceKeyComparer.Set(key, hash);
            }
            if (!_replaceKeyIndex.TryGetValue(key, out index)) {
                index = -1;
            }
            return true;
        }

        private bool TryBuildReplaceKeyIndex() {
            Task<OwnedReplaceIndex> task = null;
            try {
                OwnedChatKeyPreparation preparation = _replacePreparation;
                if (!TryQueueBufferPreparation(() => BuildOwnedReplaceIndex(preparation), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                OwnedReplaceIndex result = task.GetAwaiter().GetResult();
                _replaceKeyIndex = result.Index;
                _replaceKeySlots = result.Slots;
                _replaceKeyComparer = _replaceKeyIndex.Comparer as PreparedKeyComparer;
                return true;
            } catch {
                ClearReplaceKeyIndex();
                return false;
            }
        }

        private OwnedReplaceIndex BuildOwnedReplaceIndex(OwnedChatKeyPreparation preparation) {
            Dictionary<string, int> index = BuildOwnedKeyIndex();
            return new OwnedReplaceIndex(index, preparation?.CreateKeySlots(index));
        }

        private sealed class OwnedReplaceIndex {
            internal readonly Dictionary<string, int> Index;
            internal readonly OwnedChatKeyPreparation.KeySlots Slots;

            internal OwnedReplaceIndex(Dictionary<string, int> index, OwnedChatKeyPreparation.KeySlots slots) {
                Index = index;
                Slots = slots;
            }
        }

        private Dictionary<string, int> BuildOwnedKeyIndex() {
            var index = new Dictionary<string, int>(_messages.Count, new PreparedKeyComparer());
            for (int i = 0; i < _messages.Count; i++) {
                string key = _messages[i].Key;
                if (!index.ContainsKey(key)) {
                    index.Add(key, i);
                }
            }
            return index;
        }

        private sealed class PreparedKeyComparer : IEqualityComparer<string> {
            private string _key;
            private int _hash;

            internal void Set(string key, int hash) {
                _key = key;
                _hash = hash;
            }

            internal void Clear() {
                _key = null;
            }

            public bool Equals(string left, string right) => StringComparer.Ordinal.Equals(left, right);

            public int GetHashCode(string key) => _key != null && ReferenceEquals(key, _key)
                ? _hash : StringComparer.Ordinal.GetHashCode(key);
        }

        private bool TrySortLargeOwnedBuffer() {
            if (Thread.CurrentThread.IsThreadPoolThread
                || Thread.CurrentThread.ManagedThreadId != _ownerThread || !HasMaterialKeyVolume()) {
                return false;
            }

            return TrySortOwnedBuffer();
        }

        private bool TrySortOwnedBuffer() {
            Task<ExceptionDispatchInfo> task = null;
            try {
                if (!TryQueueBufferPreparation(SortOwnedBuffer, out task)) {
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

        private ExceptionDispatchInfo SortOwnedBuffer() {
            try {
                _messages.Sort(CompareEntries);
                if (_messages.Count > MaxMessages) {
                    _messages.RemoveRange(0, _messages.Count - MaxMessages);
                }
                return null;
            } catch (Exception error) {
                return ExceptionDispatchInfo.Capture(error);
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

        private bool HasMaterialKeyVolume() {
            if (_messages.Count >= 4096) {
                return true;
            }
            long bytes = 0;
            foreach (LiveChatEntry entry in _messages) {
                if (entry == null) {
                    return false;
                }
                bytes += (long)(entry.MessageId?.Length ?? 0) * sizeof(char);
                bytes += (long)(entry.MatchId?.Length ?? 0) * sizeof(char);
                if (bytes >= 1024 * 1024) {
                    return true;
                }
            }
            return false;
        }

        private bool TryFindOwnedEntry(LiveChatEntry entry, out int index) {
            index = -1;
            if (Thread.CurrentThread.IsThreadPoolThread || Thread.CurrentThread.ManagedThreadId != _ownerThread
                || !HasMaterialFind(entry)) {
                return false;
            }
            return TryFindOwnedEntryCore(entry, out index);
        }

        private bool HasMaterialFind(LiveChatEntry entry) {
            if (entry == null || _messages.Count == 0) {
                return false;
            }
            bool entryFallback = string.IsNullOrEmpty(entry.MessageId);
            long entryLength = entryFallback ? (long)entry.MatchId.Length + 21 : entry.MessageId.Length;
            long bytes = 0;
            foreach (LiveChatEntry item in _messages) {
                if (item == null) {
                    return false;
                }
                bool itemFallback = string.IsNullOrEmpty(item.MessageId);
                if (!itemFallback && !entryFallback && ReferenceEquals(item.MessageId, entry.MessageId)) {
                    return false;
                }
                long itemLength = itemFallback ? (long)item.MatchId.Length + 21 : item.MessageId.Length;
                if (itemFallback) {
                    bytes += itemLength * sizeof(char);
                }
                if (entryFallback) {
                    bytes += entryLength * sizeof(char);
                }
                if (itemLength == entryLength) {
                    bytes += itemLength * sizeof(char);
                }
                if (bytes >= 1024 * 1024) {
                    return true;
                }
            }
            return false;
        }

        private bool TryFindOwnedEntryCore(LiveChatEntry entry, out int index) {
            index = -1;
            Task<OwnedFindResult> task = null;
            try {
                if (!TryQueueBufferPreparation(() => FindOwnedEntry(entry), out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            OwnedFindResult result = task.GetAwaiter().GetResult();
            result.Error?.Throw();
            index = result.Index;
            return true;
        }

        private OwnedFindResult FindOwnedEntry(LiveChatEntry entry) {
            try {
                return new OwnedFindResult(_messages.FindIndex(item => item.Key == entry.Key), null);
            } catch (Exception error) {
                return new OwnedFindResult(-1, ExceptionDispatchInfo.Capture(error));
            }
        }

        private sealed class OwnedFindResult {
            internal readonly int Index;
            internal readonly ExceptionDispatchInfo Error;

            internal OwnedFindResult(int index, ExceptionDispatchInfo error) {
                Index = index;
                Error = error;
            }
        }

        private static bool TryQueueBufferPreparation<T>(Func<T> prepare, out Task<T> task) {
            if (ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)) {
                return true;
            }
            return ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task);
        }
    }
}
