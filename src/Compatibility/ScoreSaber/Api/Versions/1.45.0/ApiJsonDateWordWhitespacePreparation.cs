extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateWordWhitespacePreparation {
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static readonly MethodInfo Bridge = Initialize();
        internal static bool Supported => Bridge != null && ApiJsonDateLexicalPreparation.Supported;

        internal static bool TryPrepare(CoreCharSpan span, int start, out int end) {
            end = start;
            try {
                if (!Supported || !Rewritten || start < 0 || span.Length - start < 524288) { return false; }
                HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
                if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                    || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateWordWhitespacePatch.TranspilerMethod) { return false; }
                if (!ApiJsonDateLexicalPreparation.TryPrepare(span, start - 1, ' ', 1, out var result)) { return false; }
                end = result.Index;
                return true;
            } catch { return false; }
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type dt = typeof(char).Assembly.GetType("System.__DTString");
                if (dt == null) { return null; }
                FieldInfo value = AccessTools.Field(dt, "Value");
                if (value?.FieldType != typeof(CoreCharSpan)) { return null; }
                Target = AccessTools.Method(dt, "MatchSpecifiedWords");
                byte[] bytes = Target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 403) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "8e099ad6e15d928759b46933ba3e1360c5bd82387d1e073032779d298276e342") { return null; }
                }
                var bridge = new DynamicMethod("PrepareDateWordWhitespace", typeof(bool),
                    new[] { dt.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() },
                    typeof(ApiJsonDateWordWhitespacePreparation), true);
                ILGenerator il = bridge.GetILGenerator();
                LocalBuilder end = il.DeclareLocal(typeof(int));
                LocalBuilder count = il.DeclareLocal(typeof(int));
                Label fail = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, value);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Ldloca, end);
                il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateWordWhitespacePreparation), "TryPrepare"));
                il.Emit(OpCodes.Brfalse, fail);
                il.Emit(OpCodes.Ldloc, end); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, count);
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, end); il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldind_I4);
                il.Emit(OpCodes.Ldloc, count); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stind_I4);
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
                il.MarkLabel(fail); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
                return bridge;
            } catch { return null; }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 189 || code[101].opcode != OpCodes.Br_S
                || code[100].opcode != OpCodes.Stloc_1 || code[102].opcode != OpCodes.Ldloc_2) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            Label finished = generator.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[101].labels);
            code[101].labels.Clear();
            code[101].labels.Add(fallback);
            code[124].labels.Add(finished);
            var prefix = new[] {
                first, CodeInstruction.LoadLocal(2, useAddress: true), new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, Bridge), new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(OpCodes.Br, finished)
            };
            code.InsertRange(101, prefix);
            Rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateWordWhitespacePatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateWordWhitespacePatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateWordWhitespacePreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateWordWhitespacePreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonDateWordWhitespacePreparation.Rewrite(instructions, generator);
    }
}
