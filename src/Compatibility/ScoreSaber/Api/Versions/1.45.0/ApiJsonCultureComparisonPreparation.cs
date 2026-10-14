using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonCultureComparisonPreparation {
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static FieldInfo ModeChecked;
        private static FieldInfo ManagedMode;
        private static FieldInfo Collator;
        private static FieldInfo InvariantMode;
        private static HashSet<MethodBase> OwnedPatches;
        private static readonly MethodBase[] Guards = Initialize();
        internal static bool Supported => Guards != null;

        private static bool TryPrepare(CompareInfo info, string first, int offset1, int length1, string second,
            int offset2, int length2, CompareOptions options, out int result) {
            result = 0;
            CultureInfo culture;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || info == null || first == null || second == null || offset1 != 0 || offset2 != 0
                    || length1 < 524288 || length2 < 524288 || ((int)options & ~0x2000001f) != 0
                    || !IsUnchanged() || info.GetType() != typeof(CompareInfo)
                    || length1 != first.Length || length2 != second.Length
                    || !(bool)ModeChecked.GetValue(null) || (bool)InvariantMode.GetValue(null)
                    || ((bool)ManagedMode.GetValue(null) && Collator.GetValue(info) == null)) { return false; }
                for (int at = 0; at < 1024; at++) { if (first[at] != second[at]) { return false; } }
                culture = CultureInfo.GetCultureInfo(info.Name);
                if (culture.GetType() != typeof(CultureInfo) || !culture.IsReadOnly
                    || !ReferenceEquals(culture.CompareInfo, info) || !IsUnchanged()) { return false; }
            } catch { return false; }
            Task<int> task = null;
            try { Queue(first, second, culture, options, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            result = task.GetAwaiter().GetResult();
            return true;
        }

        private static void Queue(string first, string second, CultureInfo culture, CompareOptions options, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => string.Compare(first, second, culture, options), out task);
        }

        private static bool IsUnchanged() {
            if (!Rewritten) { return false; }
            HarmonyLib.Patches target = Harmony.GetPatchInfo(Target);
            if (target == null || target.Prefixes.Count != 0 || target.Postfixes.Count != 0 || target.Finalizers.Count != 0
                || target.Transpilers.Count != 1 || target.Transpilers[0].PatchMethod != ApiJsonCultureComparisonPatch.TranspilerMethod) { return false; }
            foreach (MethodBase guard in Guards) { if (Harmony.GetPatchInfo(guard) != null) { return false; } }
            foreach (MethodBase method in Harmony.GetAllPatchedMethods()) {
                if (method.DeclaringType == typeof(ApiJsonCultureComparisonPreparation) || OwnedPatches.Contains(method)
                    || (method.DeclaringType == typeof(ReplayStorageService) && method.Name == "TryQueueOwnedPreparationWhenIdle")) { return false; }
                if (method.Module != typeof(string).Module || method == Target) { continue; }
                HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) { continue; }
                foreach (Patch patch in patches.Prefixes) { if (!OwnedPatches.Contains(patch.PatchMethod)) { return false; } }
                foreach (Patch patch in patches.Postfixes) { if (!OwnedPatches.Contains(patch.PatchMethod)) { return false; } }
                foreach (Patch patch in patches.Finalizers) { if (!OwnedPatches.Contains(patch.PatchMethod)) { return false; } }
                foreach (Patch patch in patches.Transpilers) { if (!OwnedPatches.Contains(patch.PatchMethod)) { return false; } }
            }
            return true;
        }

        private static MethodBase[] Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type mode = typeof(string).Assembly.GetType("System.Globalization.GlobalizationMode");
                Target = AccessTools.Method(typeof(CompareInfo), "internal_compare_switch");
                ModeChecked = AccessTools.Field(typeof(CompareInfo), "managedCollationChecked");
                ManagedMode = AccessTools.Field(typeof(CompareInfo), "managedCollation");
                Collator = AccessTools.Field(typeof(CompareInfo), "collator");
                InvariantMode = mode == null ? null : AccessTools.Field(mode, "<Invariant>k__BackingField");
                if (ModeChecked == null || !ModeChecked.IsStatic || ModeChecked.FieldType != typeof(bool)
                    || ManagedMode == null || !ManagedMode.IsStatic || ManagedMode.FieldType != typeof(bool)
                    || Collator == null || Collator.IsStatic
                    || InvariantMode == null || !InvariantMode.IsStatic || !InvariantMode.IsInitOnly || InvariantMode.FieldType != typeof(bool)) { return null; }
                if (!Matches(Target, 42, "b8274dc68a66b4f8d027aebb71ebfc0a8a011c9f415978b36f9c17e1f0b9c16d")) { return null; }
                var guards = new MethodBase[] {
                    AccessTools.Method(typeof(string), "Compare", new[] { typeof(string), typeof(string), typeof(CultureInfo), typeof(CompareOptions) }),
                    AccessTools.Method(typeof(CompareInfo), "Compare", new[] { typeof(string), typeof(string), typeof(CompareOptions) }),
                    typeof(CultureInfo).GetProperty("CompareInfo")?.GetGetMethod(),
                    typeof(CompareInfo).GetProperty("Name")?.GetGetMethod(),
                    AccessTools.Method(typeof(CultureInfo), "GetCultureInfo", new[] { typeof(string) }),
                    mode == null ? null : AccessTools.Method(mode, "get_Invariant"),
                    typeof(CultureInfo).GetProperty("IsReadOnly")?.GetGetMethod(),
                    mode?.TypeInitializer,
                    mode == null ? null : AccessTools.Method(mode, "GetGlobalizationInvariantMode"),
                    typeof(string).GetProperty("Length")?.GetGetMethod(), typeof(string).GetProperty("Chars")?.GetGetMethod()
                };
                int[] sizes = { 29, 160, 81, 50, 87, 6, 7, 11, 2 };
                string[] hashes = {
                    "c07f3feb445249385a0290e7172997e2e935952008b5818285c0641004af50f3",
                    "384d874a27a98e6b8ae95149e60965c3e94c1acce9059cfaf5236ce3bd0ea61a",
                    "d086af1a59f22f7b2ebd5c823f2508acfdb3472cfdeb3d727e80949d43883608",
                    "48aa02fb32bde501876b4c7ffc0428b4f6521d7df8a87188f1cf06d3c9010827",
                    "351ee4fca3ec010c88046f0b9c95848cb0de1e91ff5deaed3153d67d57d51e54",
                    "476407865147b6fb6a7f27e361edd0334bc1add7047d767538ca1dac17a40427",
                    "8e06a9003fc67ac8d4bb87a765c3398e548384255b0a00b8de1be0346d94ae7b",
                    "fd35a53b9309e02cdfb3da0e5a5405ec26585657d7f88b22183488c85feeb8f6",
                    "84a0936ff1fba6d2811d094757231685673447aed2170738872305076026a316"
                };
                for (int at = 0; at < sizes.Length; at++) { if (!Matches(guards[at], sizes[at], hashes[at])) { return null; } }
                foreach (MethodBase guard in guards) { if (guard == null) { return null; } }
                OwnedPatches = new HashSet<MethodBase>();
                string[] names = {
                    "ApiJsonBooleanPatch.Transpiler",
                    "ApiJsonBufferAppendPatch.Transpiler",
                    "ApiJsonBufferRentPatch.Postfix",
                    "ApiJsonBufferReturnPatch.Prefix",
                    "ApiJsonBufferGrowthPatch.Transpiler",
                    "ApiJsonDateBufferPatch.Transpiler",
                    "ApiJsonDateOffsetBufferPatch.Transpiler",
                    "ApiJsonDateSignSearchPatch.Transpiler",
                    "ApiJsonEscapeScanPatch.Prefix",
                    "ApiJsonInt32ParsePatch.Transpiler",
                    "ApiJsonInt64ParsePatch.Transpiler",
                    "ApiJsonLineCallPatch.Transpiler",
                    "ApiJsonNameComparisonPatch.Transpiler",
                    "ApiJsonNameHashPatch.Transpiler",
                    "ApiJsonDecimalParsePatch.Transpiler",
                    "ApiJsonPropertyNamePatch.Transpiler",
                    "ApiJsonReaderBufferCopyPatch.Prefix",
                    "ApiJsonSpecialScanPatch.Transpiler",
                    "ApiJsonStringMaterializationPatch.Transpiler",
                    "ApiJsonStringScanPatch.Transpiler",
                    "ApiJsonNumberScanPatch.Transpiler",
                    "ApiJsonUnicodeEscapePatch.Transpiler",
                    "ApiJsonWhitespacePatch.Transpiler",
                    "ApiJsonBase64StringPatch.Prefix",
                    "ApiJsonBase64ArrayPatch.Prefix",
                    "ApiJsonCultureEqualRunPatch.Transpiler",
                    "ApiJsonDateLexicalPatch.Transpiler",
                    "ApiJsonDateStringScopePatch.Prefix",
                    "ApiJsonDateStringScopePatch.Finalizer",
                    "ApiJsonDateExactScopePatch.Prefix",
                    "ApiJsonDateExactScopePatch.Finalizer",
                    "ApiJsonDateOrdinalWordPatch.Transpiler",
                    "ApiJsonDateQuotePatch.Transpiler",
                    "ApiJsonDateRegularTokenPatch.Transpiler",
                    "ApiJsonDateSpanStringPatch.Transpiler",
                    "ApiJsonDateWordSearchPatch.Transpiler",
                    "ApiJsonDateWordWhitespacePatch.Transpiler",
                    "ApiJsonFloatingParsePatch.Prefix",
                    "ApiJsonQuotedDecimalPatch.Prefix",
                    "ApiJsonQuotedIntegerPatch.Prefix"
                };
                foreach (string name in names) {
                    int dot = name.LastIndexOf('.');
                    Type type = typeof(ApiJsonCultureComparisonPreparation).Assembly.GetType("ScoreSaber.Core.Api." + name.Substring(0, dot));
                    MethodInfo method = type == null ? null : AccessTools.Method(type, name.Substring(dot + 1));
                    if (method == null) { return null; }
                    OwnedPatches.Add(method);
                }
                return guards;
            } catch { return null; }
        }

        private static bool Matches(MethodBase method, int size, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != size) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 21 || code[0].opcode != OpCodes.Call) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            LocalBuilder result = generator.DeclareLocal(typeof(int));
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[0].labels); code[0].labels.Clear(); code[0].labels.Add(fallback);
            code.InsertRange(0, new[] {
                first, new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)4), new CodeInstruction(OpCodes.Ldarg_S, (byte)5),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)6), new CodeInstruction(OpCodes.Ldarg_S, (byte)7),
                new CodeInstruction(OpCodes.Ldloca, result),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonCultureComparisonPreparation), "TryPrepare")),
                new CodeInstruction(OpCodes.Brfalse, fallback), new CodeInstruction(OpCodes.Ldloc, result), new CodeInstruction(OpCodes.Ret)
            });
            Rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonCultureComparisonPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonCultureComparisonPatch), "Transpiler");
        private static bool Prepare() => ApiJsonCultureComparisonPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonCultureComparisonPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonCultureComparisonPreparation.Rewrite(instructions, generator);
    }
}
