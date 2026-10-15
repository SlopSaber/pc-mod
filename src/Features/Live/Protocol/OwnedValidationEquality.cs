using HarmonyLib;
using System;
using System.Reflection;

namespace ScoreSaber.Features.Live.Protocol {
    internal struct OwnedValidationEquality {
        private readonly string _expected;
        private string _left;
        private bool _ready;
        private bool _disabled;

        internal OwnedValidationEquality(string expected) {
            _expected = expected;
            _left = null;
            _ready = false;
            _disabled = false;
        }

        internal static string FindMaterialId(string[] states, string[] players) {
            try {
                if (!DefaultKernel.LengthIsUnpatched()) return null;
                return Find(states) ?? Find(players);
            } catch {
                return null;
            }
        }

        private static string Find(string[] values) {
            if (values == null) return null;
            foreach (string value in values) {
                if (value != null && value.Length >= 1024 * 1024) return value;
            }
            return null;
        }

        internal bool NotEqual(string left, string right) {
            if (_disabled || !ReferenceEquals(right, _expected) || left == null || ReferenceEquals(left, right)) {
                return left != right;
            }
            bool defaultBefore = false;
            try {
                if (_ready) {
                    if (ReferenceEquals(left, _left)) {
                        if (DefaultKernel.IsUnpatched()) return false;
                        _disabled = true;
                    }
                } else if (!DefaultKernel.LengthIsUnpatched()) {
                    _disabled = true;
                } else if (left.Length == right.Length) {
                    defaultBefore = DefaultKernel.IsUnpatched();
                    if (!defaultBefore) _disabled = true;
                }
            } catch {
                _disabled = true;
            }
            bool different = left != right;
            if (!different && defaultBefore) {
                try {
                    if (DefaultKernel.IsUnpatched()) {
                        _left = left;
                        _ready = true;
                    } else {
                        _disabled = true;
                    }
                } catch {
                    _disabled = true;
                }
            }
            return different;
        }

        private static class DefaultKernel {
            private static readonly MethodBase[] Methods;

            static DefaultKernel() {
                try { Methods = FindMethods(); }
                catch { Methods = null; }
            }

            internal static bool LengthIsUnpatched() => Methods != null && Harmony.GetPatchInfo(Methods[4]) == null;

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
                    text.GetMethod("op_Inequality", methods, null, pair, null),
                    text.GetMethod("Equals", methods, null, pair, null),
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
