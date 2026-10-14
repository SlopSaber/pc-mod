extern alias ScoreSaberCore;
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
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateOrdinalWordPreparation {
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static MethodInfo Bridge;
        private static readonly MethodBase[] Guards = Initialize();
        internal static bool Supported => Guards != null && ApiJsonDateLexicalPreparation.Supported;

        internal static bool TryPrepare(CoreCharSpan span, int start, string word, out bool equal) {
            equal = false;
            string text;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported || word == null || start < 0) { return false; }
                text = ApiJsonDateLexicalPreparation.MatchText(span);
                if (text == null || word.Length < 524288 || span.Length - start < word.Length || !IsUnchanged()) { return false; }
                for (int at = 0; at < 1024; at++) { if (text[start + at] != word[at]) { return false; } }
            } catch { return false; }
            Task<bool> task = null;
            try { Queue(text, start, word, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            equal = task.GetAwaiter().GetResult();
            return true;
        }

        private static bool IsUnchanged() {
            if (!Rewritten) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateOrdinalWordPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static void Queue(string text, int start, string word, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => string.CompareOrdinal(text, start, word, 0, word.Length) == 0, out task);
        }

        private static MethodBase[] Initialize() {
            try {
                Assembly core = typeof(char).Assembly;
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type dt = core.GetType("System.__DTString");
                Type helpers = core.GetType("System.SpanHelpers");
                Type unsafeType = core.GetType("System.Runtime.CompilerServices.Unsafe");
                Type vector = core.GetType("System.Numerics.Vector");
                Type vectorType = core.GetType("System.Numerics.Vector`1")?.MakeGenericType(typeof(ushort));
                if (dt == null || helpers == null || unsafeType == null || vector == null || vectorType == null) { return null; }
                FieldInfo value = AccessTools.Field(dt, "Value");
                FieldInfo index = AccessTools.Field(dt, "Index");
                FieldInfo info = AccessTools.Field(dt, "m_info");
                if (value?.FieldType != typeof(CoreCharSpan) || index?.FieldType != typeof(int)
                    || info?.FieldType != typeof(CompareInfo)) { return null; }
                Target = AccessTools.Method(dt, "Match", new[] { typeof(string) });
                MethodInfo compare = AccessTools.Method(typeof(CompareInfo), "Compare", new[] { typeof(CoreCharSpan), typeof(string), typeof(CompareOptions) });
                MethodInfo spanOrdinal = AccessTools.Method(typeof(string), "CompareOrdinal", new[] { typeof(CoreCharSpan), typeof(CoreCharSpan) });
                MethodInfo indexed = typeof(string).GetMethod("CompareOrdinal", new[] { typeof(string), typeof(int), typeof(string), typeof(int), typeof(int) });
                MethodInfo indexedHelper = AccessTools.Method(typeof(string), "CompareOrdinalHelper", new[] { typeof(string), typeof(int), typeof(int), typeof(string), typeof(int), typeof(int) });
                MethodInfo sequence = AccessTools.Method(helpers, "SequenceCompareTo", new[] { typeof(char).MakeByRefType(), typeof(int), typeof(char).MakeByRefType(), typeof(int) });
                if (!Matches(Target, 122, "c5d6b9d04f1c6ca0d671abdc8b72f3547d745f2c1a2bc6a4cc4a8dafd1fb8277")
                    || !Matches(compare, 145, "efdcef9c0416ff0a8bc140b477a7f40ff5f4d923383439f9abd43cb58ca527e6")
                    || !Matches(spanOrdinal, 32, "f800064ae5581e6eaf0f18c2401a5a0c43bb569bdb4364d0f304072ab21ed1a3")
                    || !Matches(indexed, 168, "487f15954f1077e60883e36cc4c724828822f93884f5576355b2ba776a5d1bcd")
                    || !Matches(indexedHelper, 34, "5d622d78d3cdaddc256d0f11b291480e67227559b922afa4067f247e150fa8a6")
                    || !Matches(sequence, 376, "7fcb950b9c10cbf51984865aef8e5fc3a2955a4c752b3f9ec530b1a1ccbafd66")) { return null; }
                var guards = new List<MethodBase> {
                    compare, spanOrdinal, indexed, indexedHelper, sequence,
                    AccessTools.Method(typeof(Math), "Min", new[] { typeof(int), typeof(int) }),
                    AccessTools.Method(typeof(char), "CompareTo", new[] { typeof(char) }),
                    AccessTools.Method(dt, "get_Length"),
                    AccessTools.Method(typeof(string), "GetRawStringData"),
                    typeof(CoreCharSpan).GetMethod("Slice", new[] { typeof(int), typeof(int) }),
                    typeof(string).GetProperty("Length")?.GetGetMethod(),
                    typeof(string).GetProperty("Chars")?.GetGetMethod()
                };
                int closedStart = guards.Count;
                Type charRef = typeof(char).MakeByRefType();
                guards.Add(FindGeneric(unsafeType, "AreSame", new[] { typeof(char) }, new[] { charRef, charRef }));
                guards.Add(FindGeneric(unsafeType, "Add", new[] { typeof(char) }, new[] { charRef, typeof(int) }));
                guards.Add(FindGeneric(unsafeType, "Add", new[] { typeof(char) }, new[] { charRef, typeof(IntPtr) }));
                guards.Add(FindGeneric(unsafeType, "As", new[] { typeof(char), typeof(byte) }, new[] { charRef }));
                foreach (Type type in new[] { vectorType, typeof(UIntPtr), typeof(int) }) {
                    guards.Add(FindGeneric(unsafeType, "ReadUnaligned", new[] { type }, new[] { typeof(byte).MakeByRefType() }));
                }
                int closedCount = guards.Count;
                for (int at = closedStart; at < closedCount; at++) {
                    MethodInfo closed = guards[at] as MethodInfo;
                    if (closed == null) { return null; }
                    guards.Add(closed.GetGenericMethodDefinition());
                }
                foreach (Type type in new[] { vector, vectorType, typeof(IntPtr), typeof(UIntPtr) }) {
                    guards.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
                }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Bridge = BuildBridge(dt, value, index, info);
                return guards.ToArray();
            } catch { return null; }
        }

        private static MethodInfo BuildBridge(Type dt, FieldInfo value, FieldInfo index, FieldInfo info) {
            var bridge = new DynamicMethod("PrepareOrdinalDateWord", typeof(bool),
                new[] { dt.MakeByRefType(), typeof(string), typeof(bool).MakeByRefType() }, typeof(ApiJsonDateOrdinalWordPreparation), true);
            ILGenerator il = bridge.GetILGenerator();
            Label fail = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, info); il.Emit(OpCodes.Brfalse, fail);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, value);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, index);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateOrdinalWordPreparation), "TryPrepare"));
            il.Emit(OpCodes.Ret);
            il.MarkLabel(fail); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return bridge;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 51 || code[25].opcode != OpCodes.Ldarg_0 || code[37].opcode != OpCodes.Brtrue_S) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            Label success = generator.DefineLabel();
            Label failure = generator.DefineLabel();
            code[38].labels.Add(success);
            code[49].labels.Add(failure);
            LocalBuilder equal = generator.DeclareLocal(typeof(bool));
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[25].labels);
            code[25].labels.Clear();
            code[25].labels.Add(fallback);
            code.InsertRange(25, new[] {
                first, new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldloca, equal),
                new CodeInstruction(OpCodes.Call, Bridge), new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(OpCodes.Ldloc, equal), new CodeInstruction(OpCodes.Brtrue, success),
                new CodeInstruction(OpCodes.Br, failure)
            });
            Rewritten = true;
            return code;
        }

        private static MethodInfo FindGeneric(Type type, string name, Type[] arguments, Type[] parameters) {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (method.Name != name || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != arguments.Length) { continue; }
                MethodInfo closed = method.MakeGenericMethod(arguments);
                ParameterInfo[] actual = closed.GetParameters();
                if (actual.Length != parameters.Length) { continue; }
                bool match = true;
                for (int at = 0; at < actual.Length; at++) { if (actual[at].ParameterType != parameters[at]) { match = false; } }
                if (match) { return closed; }
            }
            return null;
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
    internal static class ApiJsonDateOrdinalWordPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateOrdinalWordPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateOrdinalWordPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateOrdinalWordPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonDateOrdinalWordPreparation.Rewrite(instructions, generator);
    }
}
