using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonCompositeFormatAppendPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static bool Rewritten;
        internal static bool Supported => Target != null;

        internal static bool IsAccepted() {
            if (!Supported) { return false; }
            Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonCompositeFormatAppendPatch.TranspilerMethod;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo append = AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(string) });
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "AppendForFormatter");
            if (!Supported || code.Count != 498 || append == null || wrapper == null
                || code[483].opcode != OpCodes.Call || !Equals(code[483].operand, append)) { return code; }
            code[483].operand = wrapper;
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type parameters = typeof(string).Assembly.GetType("System.ParamsArray");
                MethodInfo target = AccessTools.Method(typeof(StringBuilder), "AppendFormatHelper", new[] { typeof(IFormatProvider), typeof(string), parameters });
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 988) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        == "570368ca8de8c65837b1e5470dcbd6dd53d8bcaccbcf46f074a7ac8e70b2cb34" ? target : null;
                }
            } catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonCompositeFormatAppendPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonCompositeFormatAppendPatch), "Transpiler");
        private static bool Prepare() => ApiJsonCompositeFormatAppendPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonCompositeFormatAppendPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonCompositeFormatAppendPreparation.Rewrite(instructions);
    }
}
