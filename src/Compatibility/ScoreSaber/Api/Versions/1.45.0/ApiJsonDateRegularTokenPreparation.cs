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

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateRegularTokenPreparation {
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static MethodInfo Bridge;
        private static readonly MethodBase[] Guards = Initialize();
        internal static bool Supported => Guards != null && ApiJsonDateLexicalPreparation.Supported;

        internal static bool TryPrepare(CoreCharSpan span, int index, char current, int number, int mode, out Result result) {
            result = default;
            string text;
            try {
                if (Thread.CurrentThread.IsThreadPoolThread || !Supported || mode < 0 || mode > 1
                    || !IsUnchanged() || !ApiJsonUnicodeCategories.Supported || span.Length < 524288) { return false; }
                text = ApiJsonDateLexicalPreparation.MatchText(span);
                if (text == null || index < 0 || text.Length - index - 1 < 524288
                    || (mode == 0 ? current < '0' || current > '9' : !ApiJsonUnicodeCategories.IsWhiteSpace(current))) { return false; }
                for (int at = index + 1; at < index + 1025; at++) {
                    char value = text[at];
                    if (mode == 0 ? value < '0' || value > '9' : !ApiJsonUnicodeCategories.IsWhiteSpace(value)) { return false; }
                }
            } catch { return false; }
            Task<Result> task = null;
            try { Queue(text, index, current, number, mode, out task); }
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
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateRegularTokenPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static void Queue(string text, int index, char current, int number, int mode, out Task<Result> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Compute(text, index, current, number, mode), out task);
        }

        private static Result Compute(string text, int index, char current, int number, int mode) {
            while (++index < text.Length) {
                current = text[index];
                if (mode == 0) {
                    int digit = current - '0';
                    if (digit < 0 || digit > 9) { break; }
                    number = unchecked(number * 10 + digit);
                } else if (!ApiJsonUnicodeCategories.IsWhiteSpace(current)) { break; }
            }
            return new Result(index, current, number, index >= text.Length);
        }

        private static MethodBase[] Initialize() {
            try {
                Assembly core = typeof(char).Assembly;
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type dt = core.GetType("System.__DTString");
                Type parse = core.GetType("System.DateTimeParse");
                if (dt == null || parse == null) { return null; }
                FieldInfo value = AccessTools.Field(dt, "Value");
                FieldInfo index = AccessTools.Field(dt, "Index");
                FieldInfo current = AccessTools.Field(dt, "m_current");
                if (value?.FieldType != typeof(CoreCharSpan) || index?.FieldType != typeof(int)
                    || current?.FieldType != typeof(char)) { return null; }
                Target = AccessTools.Method(dt, "GetRegularToken");
                byte[] bytes = Target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 376) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "a03e3df63cd598c4c0403e49808cfd457c42deaa2fcf064e64865a233583df89") { return null; }
                }
                var guards = new[] {
                    AccessTools.Method(dt, "get_Length"), AccessTools.Method(parse, "IsDigit", new[] { typeof(char) })
                };
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Bridge = BuildBridge(dt, value, index, current);
                return guards;
            } catch { return null; }
        }

        private static MethodInfo BuildBridge(Type dt, FieldInfo value, FieldInfo index, FieldInfo current) {
            var bridge = new DynamicMethod("PrepareRegularDateToken", typeof(bool),
                new[] { dt.MakeByRefType(), typeof(int).MakeByRefType(), typeof(int), typeof(bool).MakeByRefType() },
                typeof(ApiJsonDateRegularTokenPreparation), true);
            ILGenerator il = bridge.GetILGenerator();
            LocalBuilder result = il.DeclareLocal(typeof(Result));
            Label fail = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, value);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, index);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, current);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldind_I4);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateRegularTokenPreparation), "TryPrepare"));
            il.Emit(OpCodes.Brfalse, fail);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Index")); il.Emit(OpCodes.Stfld, index);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Current")); il.Emit(OpCodes.Stfld, current);
            Label whitespace = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Brtrue, whitespace);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Number")); il.Emit(OpCodes.Stind_I4);
            il.MarkLabel(whitespace);
            il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "End")); il.Emit(OpCodes.Stind_I1);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
            il.MarkLabel(fail); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return bridge;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 176 || code[28].opcode != OpCodes.Br_S || code[139].opcode != OpCodes.Br_S) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label digitEnd = generator.DefineLabel();
            Label whiteEnd = generator.DefineLabel();
            Label whiteCurrent = generator.DefineLabel();
            code[68].labels.Add(digitEnd);
            code[164].labels.Add(whiteEnd);
            code[148].labels.Add(whiteCurrent);
            LocalBuilder end = generator.DeclareLocal(typeof(bool));
            var rewritten = new List<CodeInstruction>();
            for (int at = 0; at < code.Count; at++) {
                if (at == 28 || at == 139) {
                    Label fallback = generator.DefineLabel();
                    var first = new CodeInstruction(OpCodes.Ldarg_0);
                    first.labels.AddRange(code[at].labels);
                    code[at].labels.Clear();
                    code[at].labels.Add(fallback);
                    rewritten.Add(first);
                    rewritten.Add(new CodeInstruction(OpCodes.Ldarg_2));
                    rewritten.Add(new CodeInstruction(at == 28 ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1));
                    rewritten.Add(new CodeInstruction(OpCodes.Ldloca, end));
                    rewritten.Add(new CodeInstruction(OpCodes.Call, Bridge));
                    rewritten.Add(new CodeInstruction(OpCodes.Brfalse, fallback));
                    if (at == 139) {
                        rewritten.Add(new CodeInstruction(OpCodes.Ldloc, end));
                        rewritten.Add(new CodeInstruction(OpCodes.Brtrue, whiteEnd));
                    }
                    rewritten.Add(new CodeInstruction(OpCodes.Br, at == 28 ? digitEnd : whiteCurrent));
                }
                rewritten.Add(code[at]);
            }
            Rewritten = true;
            return rewritten;
        }

        internal readonly struct Result {
            internal readonly int Index;
            internal readonly char Current;
            internal readonly int Number;
            internal readonly bool End;
            internal Result(int index, char current, int number, bool end) {
                Index = index; Current = current; Number = number; End = end;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateRegularTokenPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateRegularTokenPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateRegularTokenPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateRegularTokenPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => ApiJsonDateRegularTokenPreparation.Rewrite(instructions, generator);
    }
}
