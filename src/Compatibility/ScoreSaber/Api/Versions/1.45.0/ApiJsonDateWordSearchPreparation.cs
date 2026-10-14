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
    internal static class ApiJsonDateWordSearchPreparation {
        private delegate int Search(string text, char first, char second, int start, int count);
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static MethodBase[] Guards;
        private static readonly Search Kernel = Initialize();
        internal static bool Supported => Kernel != null && Guards != null;

        internal static bool TryPrepare(string text, char first, char second, int start, int count, out int result) {
            result = -1;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || text == null || start < 0 || count < 524288 || !IsUnchanged() || count > text.Length - start) { return false; }
                for (int at = start; at < start + 1024; at++) {
                    char value = text[at];
                    if (value == first || value == second) { return false; }
                }
            } catch { return false; }
            Task<int> task = null;
            try { Queue(text, first, second, start, count, out task); }
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

        private static bool IsUnchanged() {
            if (!Rewritten) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateWordSearchPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static void Queue(string text, char first, char second, int start, int count, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Kernel(text, first, second, start, count), out task);
        }

        private static Search Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Target = AccessTools.Method(typeof(string), "IndexOfAny", new[] { typeof(char), typeof(char), typeof(int), typeof(int) });
                MethodInfo two = typeof(string).GetMethod("IndexOfAny", new[] { typeof(char[]), typeof(int) });
                MethodInfo three = typeof(string).GetMethod("IndexOfAny", new[] { typeof(char[]), typeof(int), typeof(int) });
                if (!Matches(Target, 88, "a378f617d41dbc4f5f478f51acbdd455368fa69c5009313af1e55dec691f94bb")
                    || !Matches(two, 17, "4e82a672fa8f743f6fd3debf66cf8f869337067c2e2b7bded3d67d2d57f826bc")
                    || !Matches(three, 147, "0344080285d7faf6a026654f442e3409161a8a0db615d8fdfb32c95df7924667")
                    || AccessTools.Field(typeof(string), "_firstChar")?.FieldType != typeof(char)) { return null; }
                var guards = new MethodBase[] {
                    two, three, typeof(string).GetProperty("Length")?.GetGetMethod(), typeof(string).GetProperty("Chars")?.GetGetMethod()
                };
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                var kernel = new DynamicMethod("SearchImmutableDateWord", typeof(int),
                    new[] { typeof(string), typeof(char), typeof(char), typeof(int), typeof(int) }, typeof(ApiJsonDateWordSearchPreparation), true);
                ILGenerator il = kernel.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Call, Target); il.Emit(OpCodes.Ret);
                Guards = guards;
                return (Search)kernel.CreateDelegate(typeof(Search));
            } catch { return null; }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 73 || code[0].opcode != OpCodes.Ldarg_0 || code[1].opcode != OpCodes.Ldflda) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            LocalBuilder result = generator.DeclareLocal(typeof(int));
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[0].labels);
            code[0].labels.Clear();
            code[0].labels.Add(fallback);
            code.InsertRange(0, new[] {
                first, new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3), new CodeInstruction(OpCodes.Ldarg_S, (byte)4),
                new CodeInstruction(OpCodes.Ldloca, result),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateWordSearchPreparation), "TryPrepare")),
                new CodeInstruction(OpCodes.Brfalse, fallback), new CodeInstruction(OpCodes.Ldloc, result),
                new CodeInstruction(OpCodes.Ret)
            });
            Rewritten = true;
            return code;
        }

        private static bool Matches(MethodInfo method, int length, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != length) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateWordSearchPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateWordSearchPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateWordSearchPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateWordSearchPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonDateWordSearchPreparation.Rewrite(instructions, generator);
    }
}
