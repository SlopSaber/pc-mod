using System;

namespace ScoreSaber.Features.Live.Protocol {
    internal readonly struct OwnedStringEquality {
        private readonly string _left;
        private readonly string _right;
        private readonly bool _equals;
        internal bool IsPrepared { get; }

        private OwnedStringEquality(string left, string right, bool equals) {
            _left = left;
            _right = right;
            _equals = equals;
            IsPrepared = true;
        }

        internal static OwnedStringEquality Prepare(string left, string right, bool ordinal) {
            try {
                if (left == null || right == null || ReferenceEquals(left, right)
                    || left.Length != right.Length || left.Length < 1024 * 1024) return default;
                return new OwnedStringEquality(left, right,
                    ordinal ? string.Equals(left, right, StringComparison.Ordinal) : left == right);
            } catch {
                return default;
            }
        }

        internal bool TryGet(string left, string right, out bool equals) {
            equals = false;
            if (!IsPrepared || !ReferenceEquals(left, _left) || !ReferenceEquals(right, _right)) return false;
            equals = _equals;
            return true;
        }
    }
}
