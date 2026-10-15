extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;
using CoreSpan = ScoreSaberCore::System.Span<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonValueStringGrowthPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static MethodInfo CopyTarget;
        private static FieldInfo RentedArray;
        private static FieldInfo Chars;
        private static FieldInfo Length;
        private static MethodBase[] Guards;
        private static bool Rewritten;
        internal static bool Supported => Target != null && Guards != null;

        internal static bool IsAccepted() {
            if (Target == null) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonValueStringGrowthPatch.TranspilerMethod;
        }

        private static bool IsUnchanged() {
            if (!Rewritten || !IsAccepted()) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static void CopyForGrowth(ref CoreSpan source, CoreSpan destination, char[] previous, char[] next, int count) {
            bool admitted = false;
            try {
                if (previous != null && next != null && previous != next
                    && count >= 524288 && count <= int.MaxValue / 2 && count == previous.Length && count <= next.Length
                    && Supported && IsUnchanged() && ApiJsonNumberGroupCopyPreparation.CanUseOwnedArrayCopy()
                    && ApiJsonValueStringAppendPreparation.CanReadOwnedRange()
                    && source.Length == count && destination.Length >= count) {
                    CoreCharSpan oldRange = new CoreSpan(previous, 0, count);
                    CoreCharSpan newRange = new CoreSpan(next, 0, count);
                    admitted = ScoreSaberCore::System.MemoryExtensions.Overlaps(oldRange, (CoreCharSpan)source, out int from) && from == 0
                        && ScoreSaberCore::System.MemoryExtensions.Overlaps(newRange, (CoreCharSpan)destination, out int to) && to == 0;
                }
            } catch { }
            if (!admitted || !TryCopy(previous, next, count)) { source.CopyTo(destination); }
        }

        private static bool TryCopy(char[] previous, char[] next, int count) {
            Task<int> task = null;
            // Finish accepted copies before the caller publishes or returns either buffer.
            try { }
            finally {
                try {
                    try { Queue(previous, next, count, out task); }
                    catch { }
                } finally {
                    if (task != null) {
                        while (!task.IsCompleted) {
                            try { task.Wait(); }
                            catch (ThreadInterruptedException) { }
                            catch (AggregateException) { }
                        }
                    }
                }
            }
            if (task == null) { return false; }
            task.GetAwaiter().GetResult();
            return true;
        }

        private static void Queue(char[] previous, char[] next, int count, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                CoreSpan source = new CoreSpan(previous, 0, count);
                source.CopyTo(new CoreSpan(next, 0, count));
                return 0;
            }, out task);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonValueStringGrowthPreparation), "CopyForGrowth");
            if (!Supported || code.Count != 37 || wrapper == null || code[17].opcode != OpCodes.Call
                || !Equals(code[17].operand, CopyTarget) || code[12].opcode != OpCodes.Stloc_0) { return code; }
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, RentedArray),
                new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldflda, Chars), new CodeInstruction(OpCodes.Ldfld, Length)
            };
            prefix[0].labels.AddRange(code[17].labels);
            code[17].labels.Clear();
            code[17].operand = wrapper;
            code.InsertRange(17, prefix);
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type builder = typeof(string).Assembly.GetType("System.Text.ValueStringBuilder");
                MethodInfo target = AccessTools.Method(builder, "Grow", new[] { typeof(int) });
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 98) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "42c5aee39ea5e2ebbea0f047dd937f5a7effff03854c2ae0a7a2eeb46c2c2518") { return null; }
                }
                CopyTarget = AccessTools.Method(typeof(CoreSpan), "CopyTo", new[] { typeof(CoreSpan) });
                RentedArray = AccessTools.Field(builder, "_arrayToReturnToPool");
                Chars = AccessTools.Field(builder, "_chars");
                Length = AccessTools.Field(typeof(CoreSpan), "_length");
                if (CopyTarget == null || RentedArray == null || RentedArray.IsStatic || RentedArray.FieldType != typeof(char[])
                    || Chars == null || Chars.IsStatic || Chars.FieldType != typeof(CoreSpan)
                    || Length == null || Length.IsStatic || Length.FieldType != typeof(int)) { return null; }
                var guards = new List<MethodBase>();
                foreach (Type type in new[] { typeof(ApiJsonValueStringGrowthPreparation), typeof(ApiJsonValueStringGrowthPatch),
                    typeof(ApiJsonNumberGroupCopyPreparation), typeof(ApiJsonValueStringAppendPreparation) }) { AddDeclaredGuards(guards, type); }
                if (guards.Exists(method => method == null)) { return null; }
                Guards = guards.ToArray();
                return target;
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            guards.AddRange(type.GetMethods(flags));
            guards.AddRange(type.GetConstructors(flags));
            if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) { AddDeclaredGuards(guards, nested); }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonValueStringGrowthPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonValueStringGrowthPatch), "Transpiler");
        private static bool Prepare() => ApiJsonValueStringGrowthPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonValueStringGrowthPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonValueStringGrowthPreparation.Rewrite(instructions);
    }
}
