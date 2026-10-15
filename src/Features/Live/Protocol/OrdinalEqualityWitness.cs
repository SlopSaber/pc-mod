using HarmonyLib;
using System;
using System.Reflection;

namespace ScoreSaber.Features.Live.Protocol {
    internal static class OrdinalEqualityWitness {
        private const int MinimumLength = 65536;
        [ThreadStatic] private static Witness _operator;
        [ThreadStatic] private static Witness _ordinal;

        internal static bool OperatorEquals(string left, string right) => Compare(left, right, false);
        internal static bool OrdinalEquals(string left, string right, StringComparison comparison) =>
            comparison == StringComparison.Ordinal ? Compare(left, right, true) : string.Equals(left, right, comparison);

        private static bool Compare(string left, string right, bool ordinal) {
            Witness prior = ordinal ? _ordinal : _operator;
            bool matches = false;
            bool defaultBefore = false;
            try {
                matches = prior != null && prior.Matches(left, right);
                if (matches) {
                    defaultBefore = DefaultKernel.IsUnpatched();
                    if (defaultBefore && prior.Ready) {
                        return true;
                    }
                }
            } catch {
                matches = false;
            }

            bool result = ordinal ? string.Equals(left, right, StringComparison.Ordinal) : left == right;
            if (matches && (!defaultBefore || !result)) {
                if (ordinal) _ordinal = null;
                else _operator = null;
            }
            if (result && left is object && right is object && !ReferenceEquals(left, right)) {
                try {
                    if (DefaultKernel.IsUnpatched()) {
                        if (matches && defaultBefore) {
                            // The repeated pair has now completed an original default comparison.
                            prior.Ready = true;
                        } else if (left.Length >= MinimumLength && left.Length == right.Length) {
                            var observed = new Witness(left, right);
                            if (ordinal) _ordinal = observed;
                            else _operator = observed;
                        }
                    }
                } catch { }
            }
            return result;
        }

        private sealed class Witness {
            private readonly WeakReference<string> _left;
            private readonly WeakReference<string> _right;
            internal bool Ready;

            internal Witness(string left, string right) {
                _left = new WeakReference<string>(left);
                _right = new WeakReference<string>(right);
            }

            internal bool Matches(string left, string right) {
                return _left.TryGetTarget(out string previousLeft) && ReferenceEquals(left, previousLeft)
                    && _right.TryGetTarget(out string previousRight) && ReferenceEquals(right, previousRight);
            }
        }

        private static class DefaultKernel {
            private static readonly MethodBase[] Methods;

            static DefaultKernel() {
                Methods = FindMethods();
            }

            internal static bool IsUnpatched() {
                if (Methods == null) return false;
                foreach (MethodBase method in Methods) {
                    if (Harmony.GetPatchInfo(method) != null) return false;
                }
                return true;
            }

            private static MethodBase[] FindMethods() {
                Type text = typeof(string);
                if (text.Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) return null;
                Type span = text.Assembly.GetType("System.SpanHelpers");
                if (span == null) return null;
                const BindingFlags methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type[] pair = { text, text };
                MethodBase[] entries = {
                    text.GetMethod("op_Equality", methods, null, pair, null),
                    text.GetMethod("Equals", methods, null, pair, null),
                    text.GetMethod("Equals", methods, null, new[] { text, text, typeof(StringComparison) }, null),
                    text.GetMethod("EqualsHelper", methods, null, pair, null),
                    text.GetMethod("GetRawStringData", instance, null, Type.EmptyTypes, null),
                    text.GetMethod("get_Length", instance, null, Type.EmptyTypes, null),
                    span.GetMethod("SequenceEqual", methods, null,
                        new[] { typeof(byte).MakeByRefType(), typeof(byte).MakeByRefType(), typeof(ulong) }, null)
                };
                foreach (MethodBase entry in entries) {
                    if (entry == null) return null;
                }
                return entries;
            }
        }
    }
}
