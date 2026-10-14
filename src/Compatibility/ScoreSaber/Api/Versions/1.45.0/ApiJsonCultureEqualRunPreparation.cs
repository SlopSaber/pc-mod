using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonCultureEqualRunPreparation {
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static readonly MethodBase[] Guards = Initialize();
        internal static bool Supported => Guards != null;

        internal static bool TryPrepare(string first, int start1, int end1, string second, int start2, int end2, out int count) {
            count = 0;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || first == null || second == null || start1 < 0 || start2 < 0
                    || end1 < start1 || end2 < start2
                    || end1 - start1 < 524288 || end2 - start2 < 524288 || !IsUnchanged()
                    || end1 > first.Length || end2 > second.Length) { return false; }
                for (int at = 0; at < 1024; at++) { if (first[start1 + at] != second[start2 + at]) { return false; } }
            } catch { return false; }
            Task<int> task = null;
            try { Queue(first, start1, end1, second, start2, end2, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            count = task.GetAwaiter().GetResult();
            return true;
        }

        private static bool IsUnchanged() {
            if (!Rewritten) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonCultureEqualRunPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static void Queue(string first, int start1, int end1, string second, int start2, int end2, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Compute(first, start1, end1, second, start2, end2), out task);
        }

        private static int Compute(string first, int start1, int end1, string second, int start2, int end2) {
            int count = 0;
            while (start1 + count < end1 && start2 + count < end2 && first[start1 + count] == second[start2 + count]) { count++; }
            return count;
        }

        private static MethodBase[] Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type collator = typeof(string).Assembly.GetType("Mono.Globalization.Unicode.SimpleCollator");
                if (collator == null) { return null; }
                Target = AccessTools.Method(collator, "CompareInternal");
                byte[] bytes = Target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 2458) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "a9d8a461aaeea2971985a6f37974154457c7399b2da94fb6449a19d289625b48") { return null; }
                }
                var guards = new MethodBase[] {
                    typeof(string).GetProperty("Length")?.GetGetMethod(), typeof(string).GetProperty("Chars")?.GetGetMethod()
                };
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                return guards;
            } catch { return null; }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 1171 || code[174].opcode != OpCodes.Br_S
                || code[175].opcode != OpCodes.Ldarg_2 || code[196].opcode != OpCodes.Ldarg_2) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            Label finished = generator.DefineLabel();
            code[196].labels.Add(finished);
            LocalBuilder count = generator.DeclareLocal(typeof(int));
            var first = new CodeInstruction(OpCodes.Ldarg_1);
            first.labels.AddRange(code[174].labels); code[174].labels.Clear();
            code[174].labels.Add(fallback);
            code.InsertRange(174, new[] {
                first, new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)4), new CodeInstruction(OpCodes.Ldarg_S, (byte)5),
                CodeInstruction.LoadLocal(4), new CodeInstruction(OpCodes.Ldloca, count),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonCultureEqualRunPreparation), "TryPrepare")),
                new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc, count), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)2),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)5), new CodeInstruction(OpCodes.Ldloc, count), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)5), new CodeInstruction(OpCodes.Br, finished)
            });
            Rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonCultureEqualRunPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonCultureEqualRunPatch), "Transpiler");
        private static bool Prepare() => ApiJsonCultureEqualRunPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonCultureEqualRunPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonCultureEqualRunPreparation.Rewrite(instructions, generator);
    }
}
