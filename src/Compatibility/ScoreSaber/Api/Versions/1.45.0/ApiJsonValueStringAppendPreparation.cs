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
    internal static class ApiJsonValueStringAppendPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static MethodInfo CopyTarget;
        private static FieldInfo RentedArray;
        private static FieldInfo SourceLength;
        private static MethodBase[] Guards;
        private static bool Rewritten;
        internal static bool Supported => Target != null && Guards != null;

        internal static bool IsAccepted() {
            if (Target == null) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonValueStringAppendPatch.TranspilerMethod;
        }

        private static bool IsUnchanged() {
            if (!Rewritten || !IsAccepted()) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static void CopyForString(ref CoreCharSpan source, CoreSpan destination, char[] rented, int position, string text, int count) {
            bool admitted = false;
            try {
                if (rented != null && text != null && count >= 524288 && count <= int.MaxValue / 2
                    && position >= 0 && position <= rented.Length - count && Supported && IsUnchanged()
                    && ApiJsonNumberGroupCopyPreparation.CanUseOwnedArrayCopy()
                    && ApiJsonDateSpanStringPreparation.CanReadImmutableSpan()
                    && text.Length == count && source.Length == count && destination.Length >= count) {
                    CoreCharSpan immutable = ScoreSaberCore::System.MemoryExtensions.AsSpan(text);
                    CoreCharSpan owned = new CoreSpan(rented, position, count);
                    admitted = ScoreSaberCore::System.MemoryExtensions.Overlaps(immutable, source, out int from) && from == 0
                        && ScoreSaberCore::System.MemoryExtensions.Overlaps(owned, (CoreCharSpan)destination, out int to) && to == 0;
                }
            } catch { }
            if (!admitted || !TryCopy(text, rented, position, count)) { source.CopyTo(destination); }
        }

        private static bool TryCopy(string text, char[] rented, int position, int count) {
            Task<int> task = null;
            // A published task must finish before the caller can return its rented array.
            try { }
            finally {
                try {
                    try { Queue(text, rented, position, count, out task); }
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

        private static void Queue(string text, char[] rented, int position, int count, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                CoreCharSpan source = ScoreSaberCore::System.MemoryExtensions.AsSpan(text);
                source.CopyTo(new CoreSpan(rented, position, count));
                return 0;
            }, out task);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonValueStringAppendPreparation), "CopyForString");
            if (!Supported || code.Count != 32 || wrapper == null || code[23].opcode != OpCodes.Call
                || !Equals(code[23].operand, CopyTarget) || code[18].opcode != OpCodes.Ldloca_S) { return code; }
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, RentedArray),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloca_S, code[18].operand),
                new CodeInstruction(OpCodes.Ldfld, SourceLength)
            };
            prefix[0].labels.AddRange(code[23].labels);
            code[23].labels.Clear();
            code[23].operand = wrapper;
            code.InsertRange(23, prefix);
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Assembly core = typeof(string).Assembly;
                Type builder = core.GetType("System.Text.ValueStringBuilder");
                Type byReference = core.GetType("System.ByReference`1")?.MakeGenericType(typeof(char));
                Type unsafeType = core.GetType("System.Runtime.CompilerServices.Unsafe");
                if (builder == null || byReference == null || unsafeType == null) { return null; }
                MethodInfo target = AccessTools.Method(builder, "AppendSlow", new[] { typeof(string) });
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 86) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "78e636fdefe881c41abaecde3b63adfcfc76f5da43ad0fbad4502b7f7971831f") { return null; }
                }
                CopyTarget = AccessTools.Method(typeof(CoreCharSpan), "CopyTo", new[] { typeof(CoreSpan) });
                RentedArray = AccessTools.Field(builder, "_arrayToReturnToPool");
                SourceLength = AccessTools.Field(typeof(CoreCharSpan), "_length");
                if (CopyTarget == null || RentedArray == null || RentedArray.IsStatic || RentedArray.FieldType != typeof(char[])
                    || SourceLength == null || SourceLength.IsStatic || SourceLength.FieldType != typeof(int)) { return null; }
                var guards = new List<MethodBase> {
                    AccessTools.Method(typeof(ScoreSaberCore::System.MemoryExtensions), "AsSpan", new[] { typeof(string) }),
                    AccessTools.Method(typeof(string), "GetRawStringData", Type.EmptyTypes),
                    AccessTools.Method(typeof(Array), "GetRawSzArrayData", Type.EmptyTypes)
                };
                foreach (Type type in new[] { typeof(CoreCharSpan), byReference, typeof(ApiJsonValueStringAppendPreparation),
                    typeof(ApiJsonValueStringAppendPatch), typeof(ApiJsonDateSpanStringPreparation) }) { AddDeclaredGuards(guards, type); }
                bool foundAs = false, foundAdd = false;
                foreach (MethodInfo method in unsafeType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (!method.IsGenericMethodDefinition) { continue; }
                    if (method.Name == "As" && method.GetGenericArguments().Length == 2) {
                        MethodInfo closed = method.MakeGenericMethod(typeof(byte), typeof(char));
                        ParameterInfo[] parameters = closed.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte).MakeByRefType()) {
                            guards.Add(method); guards.Add(closed); foundAs = true;
                        }
                    } else if (method.Name == "Add" && method.GetGenericArguments().Length == 1) {
                        MethodInfo closed = method.MakeGenericMethod(typeof(char));
                        ParameterInfo[] parameters = closed.GetParameters();
                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(char).MakeByRefType()
                            && parameters[1].ParameterType == typeof(int)) {
                            guards.Add(method); guards.Add(closed); foundAdd = true;
                        }
                    }
                }
                if (!foundAs || !foundAdd || guards.Exists(method => method == null)) { return null; }
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
    internal static class ApiJsonValueStringAppendPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonValueStringAppendPatch), "Transpiler");
        private static bool Prepare() => ApiJsonValueStringAppendPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonValueStringAppendPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonValueStringAppendPreparation.Rewrite(instructions);
    }
}
