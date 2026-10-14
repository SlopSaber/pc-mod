using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonPropertyNamePreparation {
        internal static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.JsonTextReader", "ParseProperty", Type.EmptyTypes, 314, "753a1a9f3fa9adf8e6a0dd4819063c6d683f1cb0e08c7e182669a06bc2d79e0c");
        private static readonly MethodInfo DefaultGet = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.DefaultJsonNameTable", "Get", new[] { typeof(char[]), typeof(int), typeof(int) },
            158, "997c31ff86702e92604fc2a6f2c1b3e29bb7bc393d60594ca480e918075f5de5");
        private static readonly MethodInfo TextEquals = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.DefaultJsonNameTable", "TextEquals",
            new[] { typeof(string), typeof(char[]), typeof(int), typeof(int) }, 46, "9cc5ae347664d5098b8fc13861aa193062061795bbf0ee8ff66ac0492b5f6f07");
        internal static volatile bool Rewritten;

        [ThreadStatic]
        private static LookupContext _current;

        internal static bool IsProtected {
            get {
                try {
                    if (!Rewritten || Target == null) { return false; }
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
                    if (info == null || info.Transpilers.Count == 0) { return false; }
                    foreach (Patch patch in info.Transpilers) {
                        if (patch.PatchMethod != ApiJsonPropertyNamePatch.TranspilerMethod) { return false; }
                    }
                    return true;
                } catch { return false; }
            }
        }

        internal static string Get(JsonNameTable table, char[] chars, int start, int length) {
            if (table != null && !KeepsPrivate(table)) {
                ApiJsonBufferGrowthPreparation.Revoke(chars);
            }
            LookupContext previous = _current;
            _current = new LookupContext(table, chars, start, length);
            try {
                return table.Get(chars, start, length);
            } finally {
                _current = previous;
            }
        }

        private static bool KeepsPrivate(JsonNameTable table) {
            try {
                return table.GetType() == typeof(DefaultJsonNameTable) && DefaultGet != null && TextEquals != null
                    && ApiJsonNameHashPatch.IsOnlyOwnPatch(Harmony.GetPatchInfo(DefaultGet))
                    && Harmony.GetPatchInfo(TextEquals) == null;
            } catch { return false; }
        }

        internal static bool IsCurrent(JsonNameTable table, char[] chars, int start, int length) =>
            ReferenceEquals(_current.Table, table) && ReferenceEquals(_current.Chars, chars)
            && _current.Start == start && _current.Length == length;

        private readonly struct LookupContext {
            internal readonly JsonNameTable Table;
            internal readonly char[] Chars;
            internal readonly int Start;
            internal readonly int Length;
            internal LookupContext(JsonNameTable table, char[] chars, int start, int length) {
                Table = table; Chars = chars; Start = start; Length = length;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonPropertyNamePatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonPropertyNamePatch), "Transpiler");

        private static bool Prepare() => ApiJsonPropertyNamePreparation.Target != null;
        private static MethodBase TargetMethod() => ApiJsonPropertyNamePreparation.Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            ApiJsonPropertyNamePreparation.Rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            MethodInfo original = AccessTools.Method(typeof(JsonNameTable), "Get",
                new[] { typeof(char[]), typeof(int), typeof(int) });
            if (code.Count != 116 || code[63].opcode != OpCodes.Callvirt || !Equals(code[63].operand, original)) {
                return code;
            }
            code[63].opcode = OpCodes.Call;
            code[63].operand = AccessTools.Method(typeof(ApiJsonPropertyNamePreparation), "Get");
            ApiJsonPropertyNamePreparation.Rewritten = true;
            return code;
        }
    }
}
