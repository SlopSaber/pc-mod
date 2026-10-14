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
using CoreMutableSpan = ScoreSaberCore::System.Span<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateQuotePreparation {
        internal static readonly MethodInfo[] Targets = new MethodInfo[2];
        internal static readonly bool[] Rewritten = new bool[2];
        private static readonly MethodInfo[] Bridges = new MethodInfo[2];
        private static readonly MethodBase[] Guards = Initialize();
        internal static bool Supported => Guards != null && ApiJsonDateLexicalPreparation.Supported;

        internal static bool TryPrepare(CoreCharSpan span, int mode, out char[] result) {
            result = null;
            string text;
            try {
                if (Thread.CurrentThread.IsThreadPoolThread || !Supported || mode < 0 || mode > 1
                    || !IsUnchanged(mode) || !ApiJsonUnicodeCategories.Supported || span.Length < 524288) { return false; }
                text = ApiJsonDateLexicalPreparation.MatchText(span);
                if (text == null || !HasEdge(text, mode)) { return false; }
            } catch { return false; }
            Task<char[]> task = null;
            try { Queue(text, mode, out task); }
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

        private static bool IsUnchanged(int mode) {
            if (!Rewritten[mode]) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Targets[mode]);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateQuotePatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static bool HasEdge(string text, int mode) {
            int at = mode == 0 ? text.Length - 1 : 0;
            char quote = text[at];
            return (quote == '\'' || quote == '"')
                && ApiJsonUnicodeCategories.IsWhiteSpace(text[mode == 0 ? at - 1 : 1]);
        }

        private static void Queue(string text, int mode, out Task<char[]> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Compute(text, mode), out task);
        }

        private static char[] Compute(string text, int mode) {
            if (mode == 0) {
                int at = text.Length - 2;
                while (at >= 1 && ApiJsonUnicodeCategories.IsWhiteSpace(text[at - 1])) { at--; }
                var chars = new char[at + 1];
                chars[at] = text[text.Length - 1];
                text.CopyTo(0, chars, 0, at);
                return chars;
            }
            int skip = 0;
            while (skip + 1 < text.Length && ApiJsonUnicodeCategories.IsWhiteSpace(text[skip + 1])) { skip++; }
            var result = new char[text.Length - skip];
            result[0] = text[0];
            text.CopyTo(skip + 1, result, 1, result.Length - 1);
            return result;
        }

        private static MethodBase[] Initialize() {
            try {
                Assembly core = typeof(char).Assembly;
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type dt = core.GetType("System.__DTString");
                if (dt == null) { return null; }
                FieldInfo value = AccessTools.Field(dt, "Value");
                if (value?.FieldType != typeof(CoreCharSpan)) { return null; }
                Targets[0] = AccessTools.Method(dt, "RemoveTrailingInQuoteSpaces", Type.EmptyTypes);
                Targets[1] = AccessTools.Method(dt, "RemoveLeadingInQuoteSpaces", Type.EmptyTypes);
                MethodInfo copy = typeof(string).GetMethod("CopyTo", new[] { typeof(int), typeof(char[]), typeof(int), typeof(int) });
                if (!Matches(Targets[0], 155, "032d5b2e2bf81a89ef68465b3943128c8afc18921b2a5a895c994667472ec6ca")
                    || !Matches(Targets[1], 155, "faf842a84e2c4c9cacbe15469b5682961923854e2eda2ddcd4f8c5556d9120d4")
                    || !Matches(copy, 171, "2c96d27975aa2a661d8fbd40dc7dcacf79c93eb37285203fb68ef58e44507ca2")) { return null; }
                ConstructorInfo resultConstructor = typeof(CoreCharSpan).GetConstructor(new[] { typeof(char[]) });
                Type bytes = typeof(byte).MakePointerType();
                Type chars = typeof(char).MakePointerType();
                var methods = new List<MethodBase> {
                    copy, resultConstructor,
                    typeof(CoreCharSpan).GetMethod("CopyTo", new[] { typeof(CoreMutableSpan) }),
                    typeof(CoreMutableSpan).GetProperty("Length")?.GetGetMethod(),
                    AccessTools.Method(typeof(Array), "GetRawSzArrayData", Type.EmptyTypes),
                    AccessTools.Method(typeof(string), "wstrcpy", new[] { chars, chars, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "Memmove", new[] { bytes, bytes, typeof(uint) }),
                    AccessTools.Method(typeof(Buffer), "Memcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) })
                };
                bool foundMove = false;
                foreach (MethodInfo method in typeof(Buffer).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "Memmove" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1
                        && method.GetParameters().Length == 3) {
                        methods.Add(method);
                        methods.Add(method.MakeGenericMethod(typeof(char)));
                        foundMove = true;
                    }
                }
                if (!foundMove) { return null; }
                methods.AddRange(typeof(CoreMutableSpan).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
                foreach (MethodInfo method in typeof(CoreMutableSpan).GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    if (method.Name == "op_Implicit" || method.Name == "Slice" || method.Name == "get_Item") { methods.Add(method); }
                }
                foreach (MethodBase method in methods) { if (method == null) { return null; } }
                for (int i = 0; i < 2; i++) { Bridges[i] = BuildBridge(dt, value, resultConstructor, i); }
                return methods.ToArray();
            } catch { return null; }
        }

        private static MethodInfo BuildBridge(Type dt, FieldInfo value, ConstructorInfo constructor, int mode) {
            var bridge = new DynamicMethod("PrepareDateQuote" + mode, typeof(bool), new[] { dt.MakeByRefType() }, typeof(ApiJsonDateQuotePreparation), true);
            ILGenerator il = bridge.GetILGenerator();
            LocalBuilder result = il.DeclareLocal(typeof(char[]));
            Label fail = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, value);
            il.Emit(OpCodes.Ldc_I4, mode); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateQuotePreparation), "TryPrepare"));
            il.Emit(OpCodes.Brfalse, fail);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Newobj, constructor); il.Emit(OpCodes.Stfld, value);
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
            il.MarkLabel(fail); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return bridge;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator) {
            int mode = Array.IndexOf(Targets, original);
            var code = new List<CodeInstruction>(instructions);
            if (mode < 0) { return code; }
            Rewritten[mode] = false;
            if (!Supported || code.Count != (mode == 0 ? 76 : 71) || code[0].blocks.Count != 0) { return code; }
            Label fallback = generator.DefineLabel();
            code[0].labels.Add(fallback);
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, Bridges[mode]),
                new CodeInstruction(OpCodes.Brfalse, fallback), new CodeInstruction(OpCodes.Ret)
            };
            prefix.AddRange(code);
            Rewritten[mode] = true;
            return prefix;
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
    internal static class ApiJsonDateQuotePatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateQuotePatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateQuotePreparation.Supported;
        private static IEnumerable<MethodBase> TargetMethods() => ApiJsonDateQuotePreparation.Targets;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
            => ApiJsonDateQuotePreparation.Rewrite(instructions, __originalMethod, generator);
    }
}
