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
        private static FieldInfo Locale;
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

        private struct HeadingCounts {
            internal int First;
            internal int Second;
        }

        private static bool TryPrepareHeadings(string first, int start1, int end1, string second, int start2, int end2,
            int locale, out int count1, out int count2) {
            count1 = 0;
            count2 = 0;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || first == null || second == null || start1 < 0 || start2 < 0
                    || end1 < start1 || end2 < start2
                    || end1 - start1 < 524288 || end2 - start2 < 524288 || !IsUnchanged()
                    || end1 > first.Length || end2 > second.Length) { return false; }
                for (int at = 0; at < 1024; at++) {
                    if (!IsExtender(first[start1 + at], locale) || !IsExtender(second[start2 + at], locale)) { return false; }
                }
            } catch { return false; }
            Task<HeadingCounts> task = null;
            try { QueueHeadings(first, start1, end1, second, start2, end2, locale, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            HeadingCounts result = task.GetAwaiter().GetResult();
            count1 = result.First;
            count2 = result.Second;
            return true;
        }

        private static bool IsExtender(char value, int locale) {
            return (value == 8213 && locale == 16) || value == 12293 || value == 12337 || value == 12338
                || value == 12445 || value == 12446 || value == 12540 || value == 12541 || value == 12542
                || value == 65148 || value == 65149 || value == 65392;
        }

        private static HeadingCounts ComputeHeadings(string first, int start1, int end1, string second,
            int start2, int end2, int locale) {
            int count1 = 0;
            int count2 = 0;
            while (start1 + count1 < end1 && IsExtender(first[start1 + count1], locale)) { count1++; }
            while (start2 + count2 < end2 && IsExtender(second[start2 + count2], locale)) { count2++; }
            return new HeadingCounts { First = count1, Second = count2 };
        }

        private static void QueueHeadings(string first, int start1, int end1, string second, int start2, int end2,
            int locale, out Task<HeadingCounts> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => ComputeHeadings(first, start1, end1, second, start2, end2, locale), out task);
        }

        private static bool TryPrepareNuls(string value, int start, int end, out int count) {
            count = 0;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || value == null || start < 0 || end < start || end - start < 524288
                    || !IsUnchanged() || end > value.Length) { return false; }
                for (int at = 0; at < 1024; at++) { if (value[start + at] != '\0') { return false; } }
            } catch { return false; }
            Task<int> task = null;
            try { QueueNuls(value, start, end, out task); }
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

        private static void QueueNuls(string value, int start, int end, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => ComputeNuls(value, start, end), out task);
        }

        private static int ComputeNuls(string value, int start, int end) {
            int count = 0;
            while (start + count < end && value[start + count] == '\0') { count++; }
            return count;
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
                MethodInfo classifier = AccessTools.Method(collator, "GetExtenderType", new[] { typeof(int) });
                Locale = AccessTools.Field(collator, "lcid");
                if (Locale == null || Locale.IsStatic || !Locale.IsInitOnly || Locale.FieldType != typeof(int)) { return null; }
                bytes = classifier?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 174) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "f0b723a552e46931af04d3e18b87ee0609e354230c930b4d565324dbeaf301cf") { return null; }
                }
                MethodInfo ignorable = AccessTools.Method(collator, "IsIgnorable", new[] { typeof(int), typeof(System.Globalization.CompareOptions) });
                Type table = typeof(string).Assembly.GetType("Mono.Globalization.Unicode.MSCompatUnicodeTable");
                MethodInfo tableIgnorable = table == null ? null : AccessTools.Method(table, "IsIgnorable", new[] { typeof(int), typeof(byte) });
                MethodInfo[] nulMethods = { ignorable, tableIgnorable };
                int[] nulSizes = { 41, 73 };
                string[] nulHashes = {
                    "ada63657178fb6ea84e309e476d02c324e82ff55834a92e66422d870fbc1a0bd",
                    "4ebb2d9e7fe6f28e50e0800b2f68a48f0fb11d8b141cfc8360275aedecf51636"
                };
                for (int at = 0; at < nulMethods.Length; at++) {
                    bytes = nulMethods[at]?.GetMethodBody()?.GetILAsByteArray();
                    if (bytes == null || bytes.Length != nulSizes[at]) { return null; }
                    using (SHA256 hash = SHA256.Create()) {
                        if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != nulHashes[at]) { return null; }
                    }
                }
                var guards = new MethodBase[] {
                    classifier, ignorable, tableIgnorable, typeof(string).GetProperty("Length")?.GetGetMethod(), typeof(string).GetProperty("Chars")?.GetGetMethod()
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
            if (code[38].opcode != OpCodes.Br_S || code[66].opcode != OpCodes.Ldc_I4_0) { return code; }
            if (code[90].opcode != OpCodes.Br_S || code[104].opcode != OpCodes.Br_S
                || code[96].opcode != OpCodes.Brfalse_S || !Equals(code[96].operand, code[104].operand)) { return code; }
            CodeInstruction firstNonIgnorable = code[96];
            CodeInstruction afterHeadings = code[66];
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
            Label secondNuls = generator.DefineLabel();
            Label secondNulFallback = generator.DefineLabel();
            var secondNulFirst = new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
            secondNulFirst.labels.Add(secondNuls);
            secondNulFirst.labels.AddRange(code[104].labels); code[104].labels.Clear();
            code[104].labels.Add(secondNulFallback);
            firstNonIgnorable.operand = secondNuls;
            code.InsertRange(104, new[] {
                secondNulFirst, new CodeInstruction(OpCodes.Ldarg_S, (byte)5), CodeInstruction.LoadLocal(4),
                new CodeInstruction(OpCodes.Ldloca, count),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonCultureEqualRunPreparation), "TryPrepareNuls")),
                new CodeInstruction(OpCodes.Brfalse, secondNulFallback),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)5), new CodeInstruction(OpCodes.Ldloc, count), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)5)
            });
            Label firstNulFallback = generator.DefineLabel();
            var firstNulFirst = new CodeInstruction(OpCodes.Ldarg_1);
            firstNulFirst.labels.AddRange(code[90].labels); code[90].labels.Clear();
            code[90].labels.Add(firstNulFallback);
            code.InsertRange(90, new[] {
                firstNulFirst, new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Ldloca, count),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonCultureEqualRunPreparation), "TryPrepareNuls")),
                new CodeInstruction(OpCodes.Brfalse, firstNulFallback),
                new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc, count), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)2)
            });
            Label headingFallback = generator.DefineLabel();
            Label headingsFinished = generator.DefineLabel();
            afterHeadings.labels.Add(headingsFinished);
            LocalBuilder secondCount = generator.DeclareLocal(typeof(int));
            var headingFirst = new CodeInstruction(OpCodes.Ldarg_1);
            headingFirst.labels.AddRange(code[38].labels); code[38].labels.Clear();
            code[38].labels.Add(headingFallback);
            code.InsertRange(38, new[] {
                headingFirst, new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)4), new CodeInstruction(OpCodes.Ldarg_S, (byte)5),
                CodeInstruction.LoadLocal(4), new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Locale),
                new CodeInstruction(OpCodes.Ldloca, count), new CodeInstruction(OpCodes.Ldloca, secondCount),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonCultureEqualRunPreparation), "TryPrepareHeadings")),
                new CodeInstruction(OpCodes.Brfalse, headingFallback),
                new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldloc, count), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)2),
                new CodeInstruction(OpCodes.Ldarg_S, (byte)5), new CodeInstruction(OpCodes.Ldloc, secondCount), new CodeInstruction(OpCodes.Add),
                new CodeInstruction(OpCodes.Starg_S, (byte)5), new CodeInstruction(OpCodes.Br, headingsFinished)
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
