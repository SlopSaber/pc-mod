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
    internal static class ApiJsonValueStringOutputPreparation {
        private delegate string Allocate(CoreCharSpan span);
        internal static readonly MethodInfo Target = Initialize();
        private static ConstructorInfo Constructor;
        private static FieldInfo RentedArray;
        private static FieldInfo Position;
        private static Allocate Original;
        private static MethodBase[] Guards;
        private static bool Rewritten;
        internal static bool Supported => Target != null && Original != null && Guards != null;

        internal static bool IsAccepted() {
            if (Target == null) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonValueStringOutputPatch.TranspilerMethod;
        }

        private static bool IsUnchanged() {
            if (!Rewritten || !IsAccepted()) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static string Prepare(CoreCharSpan span, char[] rented, int count) {
            bool admitted = false;
            try {
                if (rented != null && count >= 524288 && count <= int.MaxValue / 2 && count <= rented.Length
                    && Supported && IsUnchanged() && ApiJsonNumberGroupCopyPreparation.CanUseOwnedArrayCopy()
                    && ApiJsonValueStringAppendPreparation.CanReadOwnedRange() && span.Length == count) {
                    CoreCharSpan owned = new CoreSpan(rented, 0, count);
                    admitted = ScoreSaberCore::System.MemoryExtensions.Overlaps(owned, span, out int offset) && offset == 0;
                }
            } catch { }
            if (!admitted) { return Original(span); }
            Task<string> task = null;
            // Complete every accepted allocation before the caller can dispose its source buffer.
            try { }
            finally {
                try {
                    try { Queue(rented, count, out task); }
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
            return task == null ? Original(span) : task.GetAwaiter().GetResult();
        }

        private static void Queue(char[] rented, int count, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Original(new CoreSpan(rented, 0, count)), out task);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonValueStringOutputPreparation), "Prepare");
            if (!Supported || code.Count != 11 || wrapper == null || code[7].opcode != OpCodes.Newobj
                || !Equals(code[7].operand, Constructor) || code[9].opcode != OpCodes.Call) { return code; }
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, RentedArray),
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Position)
            };
            prefix[0].labels.AddRange(code[7].labels);
            code[7].labels.Clear();
            code[7].opcode = OpCodes.Call;
            code[7].operand = wrapper;
            code.InsertRange(7, prefix);
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type builder = typeof(string).Assembly.GetType("System.Text.ValueStringBuilder");
                MethodInfo target = AccessTools.Method(builder, "ToString", Type.EmptyTypes);
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 35) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "caa663ba737210a5399ac0d9cd3af4e67be928aac4805db3983e438ea400ef60") { return null; }
                }
                Constructor = typeof(string).GetConstructor(new[] { typeof(CoreCharSpan) });
                RentedArray = AccessTools.Field(builder, "_arrayToReturnToPool");
                Position = AccessTools.Field(builder, "_pos");
                if (Constructor == null || (Constructor.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0
                    || RentedArray == null || RentedArray.IsStatic || RentedArray.FieldType != typeof(char[])
                    || Position == null || Position.IsStatic || Position.FieldType != typeof(int)) { return null; }
                var guards = new List<MethodBase>();
                foreach (Type type in new[] { typeof(ApiJsonValueStringOutputPreparation), typeof(ApiJsonValueStringOutputPatch),
                    typeof(ApiJsonNumberGroupCopyPreparation), typeof(ApiJsonValueStringAppendPreparation) }) { AddDeclaredGuards(guards, type); }
                if (guards.Exists(method => method == null)) { return null; }
                var allocator = new DynamicMethod("AllocatePrivateValueString", typeof(string), new[] { typeof(CoreCharSpan) }, typeof(ApiJsonValueStringOutputPreparation), true);
                ILGenerator il = allocator.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, Constructor); il.Emit(OpCodes.Ret);
                Original = (Allocate)allocator.CreateDelegate(typeof(Allocate));
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
    internal static class ApiJsonValueStringOutputPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonValueStringOutputPatch), "Transpiler");
        private static bool Prepare() => ApiJsonValueStringOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonValueStringOutputPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonValueStringOutputPreparation.Rewrite(instructions);
    }
}
