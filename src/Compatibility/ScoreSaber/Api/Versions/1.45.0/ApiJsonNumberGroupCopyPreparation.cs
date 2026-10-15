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
    internal static class ApiJsonNumberGroupCopyPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static FieldInfo RentedArray;
        private static MethodBase[] Guards;
        private static Action<string, IntPtr> Copy;
        private static bool Rewritten;
        internal static bool Supported => Target != null && RentedArray != null && Copy != null && Guards != null;

        internal static bool IsAccepted() {
            if (Target == null) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonNumberGroupCopyPatch.TranspilerMethod;
        }

        private static bool IsUnchanged() {
            if (!Rewritten || !IsAccepted()) { return false; }
            foreach (MethodBase method in Guards) {
                if (method == ApiJsonValueStringAppendPreparation.Target) {
                    if (!ApiJsonValueStringAppendPreparation.IsAccepted()) { return false; }
                } else if (method == ApiJsonValueStringOutputPreparation.Target) {
                    if (!ApiJsonValueStringOutputPreparation.IsAccepted()) { return false; }
                } else if (method == ApiJsonValueStringGrowthPreparation.Target) {
                    if (!ApiJsonValueStringGrowthPreparation.IsAccepted()) { return false; }
                } else if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static bool TryCopyGroup(string text, IntPtr destination, char[] rented) {
            bool admitted = false;
            try {
                admitted = text != null && text.Length >= 524288 && text.Length <= int.MaxValue / 2
                    && rented != null && rented.Length >= text.Length && destination != IntPtr.Zero
                    && ApiJsonFormattingPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && IsUnchanged() && ApiJsonDateBuilderOutputPreparation.CanUseFormattingOutput();
            } catch { }
            if (!admitted) { return false; }
            Task<int> task = null;
            // Queue and retirement must remain inside the caller's existing pin, including unwind paths.
            try { }
            finally {
                try {
                    try {
                        Queue(text, destination, out task);
                    } catch { }
                } finally {
                    if (task != null) {
                        while (!task.IsCompleted) {
                            try { task.Wait(); }
                            catch (ThreadInterruptedException) { }
                            catch (AggregateException) { }
                        }
                    }
                    GC.KeepAlive(rented);
                }
            }
            if (task == null) { return false; }
            task.GetAwaiter().GetResult();
            return true;
        }

        private static void Queue(string text, IntPtr destination, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                Copy(text, destination);
                return 0;
            }, out task);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo length = AccessTools.PropertyGetter(typeof(string), "Length");
            MethodInfo copy = AccessTools.Method(typeof(ApiJsonNumberGroupCopyPreparation), "TryCopyGroup");
            if (!Supported || code.Count != 268 || length == null || copy == null
                || code[136].opcode != OpCodes.Ldarg_S || code[137].opcode != OpCodes.Callvirt
                || !Equals(code[137].operand, length) || code[100].opcode != OpCodes.Stloc_S
                || code[140].opcode != OpCodes.Stloc_S || code[150].opcode != OpCodes.Stind_I2
                || code[158].opcode != OpCodes.Ldloc_2) { return code; }
            Label fallback = generator.DefineLabel();
            Label complete = generator.DefineLabel();
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_S, code[136].operand),
                new CodeInstruction(OpCodes.Ldloc_S, code[100].operand),
                new CodeInstruction(OpCodes.Conv_I),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, RentedArray),
                new CodeInstruction(OpCodes.Call, copy),
                new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(OpCodes.Ldloc_S, code[100].operand),
                new CodeInstruction(OpCodes.Ldarg_S, code[136].operand),
                new CodeInstruction(OpCodes.Callvirt, length),
                new CodeInstruction(OpCodes.Conv_I),
                new CodeInstruction(OpCodes.Ldc_I4_2),
                new CodeInstruction(OpCodes.Mul),
                new CodeInstruction(OpCodes.Sub),
                new CodeInstruction(OpCodes.Stloc_S, code[100].operand),
                new CodeInstruction(OpCodes.Ldc_I4_M1),
                new CodeInstruction(OpCodes.Stloc_S, code[140].operand),
                new CodeInstruction(OpCodes.Br, complete)
            };
            prefix[0].labels.AddRange(code[136].labels);
            code[136].labels.Clear();
            code[136].labels.Add(fallback);
            code[158].labels.Add(complete);
            code.InsertRange(136, prefix);
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (IntPtr.Size != 8 || typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Assembly core = typeof(string).Assembly;
                Type number = core.GetType("System.Number");
                Type builder = core.GetType("System.Text.ValueStringBuilder");
                Type span = core.GetType("System.Span`1")?.MakeGenericType(typeof(char));
                Type marshal = core.GetType("System.Runtime.InteropServices.MemoryMarshal");
                if (number == null || builder == null || span == null || marshal == null) { return null; }
                MethodInfo target = AccessTools.Method(number, "FormatFixed");
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 437 || !target.GetMethodBody().LocalVariables[9].IsPinned) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "1bb20e8ae2ccf46d42ca067d3709e30818132712151d474e10caae4f46629d57") { return null; }
                }
                RentedArray = AccessTools.Field(builder, "_arrayToReturnToPool");
                if (RentedArray == null || RentedArray.IsStatic || RentedArray.FieldType != typeof(char[])) { return null; }
                var guards = new List<MethodBase> {
                    AccessTools.PropertyGetter(typeof(string), "Length"), AccessTools.PropertyGetter(typeof(string), "Chars"),
                    AccessTools.PropertyGetter(typeof(ApiJsonNumericPreparation), "IsActive")
                };
                foreach (Type type in new[] { builder, span, marshal, typeof(ApiJsonNumberGroupCopyPreparation),
                    typeof(ApiJsonNumberGroupCopyPatch), typeof(ReplayStorageService), typeof(Action<string, IntPtr>),
                    typeof(ApiJsonValueStringAppendPreparation), typeof(ApiJsonValueStringAppendPatch),
                    typeof(ApiJsonValueStringOutputPreparation), typeof(ApiJsonValueStringOutputPatch),
                    typeof(ApiJsonValueStringGrowthPreparation), typeof(ApiJsonValueStringGrowthPatch) }) { AddDeclaredGuards(guards, type); }
                AddDeclaredGuards(guards, typeof(ApiJsonFormattingPreparation));
                if (!AddPoolGuards(guards, core)) { return null; }
                if (guards.Exists(method => method == null)) { return null; }
                Guards = guards.ToArray();
                Copy = CreateCopy();
                return target;
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            guards.AddRange(type.GetMethods(flags));
            guards.AddRange(type.GetConstructors(flags));
            if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                AddDeclaredGuards(guards, nested);
            }
        }

        private static Action<string, IntPtr> CreateCopy() {
            var method = new DynamicMethod("CopyPinnedNumberSeparator", typeof(void), new[] { typeof(string), typeof(IntPtr) }, typeof(ApiJsonNumberGroupCopyPreparation), true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder pointer = il.DeclareLocal(typeof(IntPtr));
            LocalBuilder index = il.DeclareLocal(typeof(int));
            Label body = il.DefineLabel();
            Label condition = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stloc, pointer);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(string), "Length"));
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, index); il.Emit(OpCodes.Br, condition);
            il.MarkLabel(body);
            il.Emit(OpCodes.Ldloc, pointer); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(string), "Chars")); il.Emit(OpCodes.Stind_I2);
            il.Emit(OpCodes.Ldloc, pointer); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, pointer);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, index);
            il.MarkLabel(condition);
            il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Bge, body); il.Emit(OpCodes.Ret);
            return (Action<string, IntPtr>)method.CreateDelegate(typeof(Action<string, IntPtr>));
        }

        internal static bool CanUseOwnedArrayCopy() => Supported && Rewritten && ApiJsonFormattingPreparation.IsActive
            && !Thread.CurrentThread.IsThreadPoolThread && IsUnchanged() && ApiJsonDateBuilderOutputPreparation.CanUseFormattingOutput();

        private static bool AddPoolGuards(List<MethodBase> guards, Assembly core) {
            foreach (string name in new[] { "System.Buffers.ArrayPool`1", "System.Buffers.TlsOverPerCoreLockedStacksArrayPool`1" }) {
                Type definition = core.GetType(name);
                if (definition == null) { return false; }
                Type pool = definition.MakeGenericType(typeof(char));
                AddDeclaredGuards(guards, pool);
                foreach (Type nested in pool.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (nested.IsGenericTypeDefinition && nested.GetGenericArguments().Length == 1) {
                        AddDeclaredGuards(guards, nested.MakeGenericType(typeof(char)));
                    }
                }
            }
            MethodInfo hash = AccessTools.Method(typeof(object), "GetHashCode", Type.EmptyTypes);
            MethodInfo nativeHash = AccessTools.Method(typeof(object), "InternalGetHashCode", new[] { typeof(object) });
            if (hash == null || nativeHash == null) { return false; }
            guards.Add(hash);
            guards.Add(nativeHash);
            return true;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonNumberGroupCopyPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonNumberGroupCopyPatch), "Transpiler");
        private static bool Prepare() => ApiJsonNumberGroupCopyPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonNumberGroupCopyPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonNumberGroupCopyPreparation.Rewrite(instructions, generator);
    }
}
