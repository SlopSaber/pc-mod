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
    internal static class ApiJsonDateLexicalPreparation {
        private delegate bool SpanMatch(CoreCharSpan span, string text);
        [ThreadStatic] private static string _text;
        [ThreadStatic] private static string _format;
        internal static readonly MethodInfo[] Targets = new MethodInfo[4];
        internal static readonly MethodInfo[] Roots = new MethodInfo[4];
        internal static readonly bool[] Rewritten = new bool[4];
        private static readonly int[] Counts = { 26, 41, 30, 38 };
        private static readonly MethodInfo[] Bridges = new MethodInfo[4];
        private static readonly SpanMatch Matcher = Initialize();
        private static MethodBase[] _guards;

        internal static bool Supported => Matcher != null && _guards != null;

        internal static string MatchText(CoreCharSpan span) {
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread
                    || !Supported || !IsUnchanged(0)) { return null; }
                return _text != null && Matcher(span, _text) ? _text
                    : _format != null && Matcher(span, _format) ? _format : null;
            } catch { return null; }
        }

        internal static Scope Begin(string text, string format) {
            if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread) { return default; }
            var state = new Scope(_text, _format, true);
            _text = text;
            _format = format;
            return state;
        }

        internal static void End(Scope state) {
            if (!state.Active) { return; }
            _text = state.Text;
            _format = state.Format;
        }

        internal static bool TryPrepare(CoreCharSpan span, int index, char current, int mode, out Result result) {
            result = default;
            try {
                if (!ApiJsonNumericPreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || mode < 0 || mode > 3 || !IsUnchanged(mode) || !ApiJsonUnicodeCategories.Supported
                    || span.Length < 524288) { return false; }
                string text = _text != null && Matcher(span, _text) ? _text
                    : _format != null && Matcher(span, _format) ? _format : null;
                if (text == null || !HasRun(text, index, current, mode)) { return false; }
                Task<Result> task = null;
                try { Queue(text, index, current, mode, out task); }
                catch { }
                if (task == null) { return false; }
                while (!task.IsCompleted) {
                    try { task.Wait(); }
                    catch (ThreadInterruptedException) { }
                    catch (AggregateException) { }
                }
                result = task.GetAwaiter().GetResult();
                return true;
            } catch { return false; }
        }

        private static bool IsUnchanged(int mode) {
            if (!Rewritten[mode] || !HasOnlyTranspiler(Targets[mode])) { return false; }
            foreach (MethodBase method in _guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static bool HasOnlyTranspiler(MethodBase method) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Count == 0 && info.Postfixes.Count == 0 && info.Finalizers.Count == 0
                && info.Transpilers.Count == 1 && info.Transpilers[0].PatchMethod == ApiJsonDateLexicalPatch.TranspilerMethod;
        }

        private static bool HasRun(string text, int index, char current, int mode) {
            int start;
            if (mode == 2) {
                for (int i = text.Length - 1; i >= text.Length - 1024; i--) {
                    if (!ApiJsonUnicodeCategories.IsWhiteSpace(text[i])) { return false; }
                }
                return true;
            }
            if (index < -1 || index >= text.Length) { return false; }
            if (mode == 3) {
                if (index < 0 || text.Length - index < 524288) { return false; }
                char value = text[index];
                for (int i = index + 1; i < index + 1024; i++) { if (text[i] != value) { return false; } }
                return true;
            }
            if (mode == 1 && !ApiJsonUnicodeCategories.IsWhiteSpace(current)) { return false; }
            start = index + 1;
            if (text.Length - start < 524288) { return false; }
            for (int i = start; i < start + 1024; i++) {
                if (!ApiJsonUnicodeCategories.IsWhiteSpace(text[i])) { return false; }
            }
            return true;
        }

        private static void Queue(string text, int index, char current, int mode, out Task<Result> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Compute(text, index, current, mode), out task);
        }

        private static Result Compute(string text, int index, char current, int mode) {
            int length = text.Length;
            int number = 0;
            bool value = false;
            if (mode == 0) {
                while (index + 1 < length && ApiJsonUnicodeCategories.IsWhiteSpace(text[index + 1])) { index++; }
            } else if (mode == 1) {
                while (ApiJsonUnicodeCategories.IsWhiteSpace(current)) {
                    index++;
                    if (index >= length) { return new Result(index, current, length, 0, false); }
                    current = text[index];
                }
                value = true;
            } else if (mode == 2) {
                while (length > 0 && ApiJsonUnicodeCategories.IsWhiteSpace(text[length - 1])) { length--; }
            } else {
                char repeated = text[index];
                int end = index + 1;
                while (end < length && text[end] == repeated) { end++; }
                number = end - index;
                index = end - 1;
            }
            return new Result(index, current, length, number, value);
        }

        private static SpanMatch Initialize() {
            try {
                Assembly core = typeof(char).Assembly;
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type dt = core.GetType("System.__DTString");
                Type extensions = core.GetType("System.MemoryExtensions");
                Type marshal = core.GetType("System.Runtime.InteropServices.MemoryMarshal");
                Type unsafeType = core.GetType("System.Runtime.CompilerServices.Unsafe");
                if (dt == null || extensions == null || marshal == null || unsafeType == null) { return null; }
                FieldInfo spanField = AccessTools.Field(dt, "Value");
                FieldInfo indexField = AccessTools.Field(dt, "Index");
                FieldInfo currentField = AccessTools.Field(dt, "m_current");
                if (spanField?.FieldType != typeof(CoreCharSpan) || indexField?.FieldType != typeof(int)
                    || currentField?.FieldType != typeof(char)) { return null; }
                string[] names = { "SkipWhiteSpaces", "SkipWhiteSpaceCurrent", "TrimTail", "GetRepeatCount" };
                int[] bytes = { 61, 97, 61, 77 };
                string[] hashes = {
                    "f9dc41d03c7fedb24ab537ea9eb6d825bb302a77d0ec5588a45f0b311765b4c0",
                    "6d200538382d4c5b94137c5b0a1d9bc0f1d110cdf1649fa985d74e2d20f2b357",
                    "ead3b73310e13764b17bcea5fb0feac326dda5b715e84ea86aa9664a2a0e2de1",
                    "ce916be46570cb70d38ce4067467158a72bf5d3e6322a1f492f67c84ea144ed8"
                };
                for (int i = 0; i < 4; i++) {
                    Targets[i] = AccessTools.Method(dt, names[i], Type.EmptyTypes);
                    if (!Matches(Targets[i], bytes[i], hashes[i])) { return null; }
                }
                MethodInfo whole = AccessTools.Method(extensions, "AsSpan", new[] { typeof(string) });
                MethodInfo reference = FindGeneric(marshal, "GetReference", new[] { typeof(CoreCharSpan) });
                MethodInfo same = FindGeneric(unsafeType, "AreSame", new[] { typeof(char).MakeByRefType(), typeof(char).MakeByRefType() });
                MethodInfo spanLength = typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod();
                MethodInfo stringLength = typeof(string).GetProperty("Length")?.GetGetMethod();
                MethodInfo stringChar = typeof(string).GetProperty("Chars")?.GetGetMethod();
                MethodInfo slice = typeof(CoreCharSpan).GetMethod("Slice", new[] { typeof(int), typeof(int) });
                if (!Matches(whole, 31, "259c87c59a0e7647d5abcddbaae0816da4abd49e488f9da8a52ebd538b46bde5")
                    || !Matches(same, 5, "7be2d00eb29bd16eb65a437b8d831a31e2106c44dd6b4210795d194f7c182a0e")
                    || !Matches(stringLength, 7, "4e983afa6e94b8b9e9d31249af4a4def459aabbf91df59838ddb074a0f98b7dc")
                    || !Matches(stringChar, 30, "9c79f192e9fd140272e02883da9d174ce63ef8cae7d3d29623361b083eb08e24")
                    || reference == null || spanLength == null || slice == null) { return null; }
                var guards = new List<MethodBase> { whole, reference, reference.GetGenericMethodDefinition(), same, same.GetGenericMethodDefinition(), spanLength,
                    typeof(CoreCharSpan).GetProperty("Item")?.GetGetMethod(),
                    stringLength, stringChar, slice, AccessTools.Method(dt, "get_Length"),
                    AccessTools.Method(typeof(string), "GetRawStringData"),
                    AccessTools.Method(typeof(ApiJsonUnicodeCategories), "IsWhiteSpace"),
                    AccessTools.Method(typeof(ApiJsonUnicodeCategories), "get_Supported") };
                Type tables = typeof(ApiJsonUnicodeCategories).GetNestedType("Tables", BindingFlags.NonPublic);
                Type unicode = core.GetType("System.Globalization.CharUnicodeInfo");
                Type byReference = core.GetType("System.ByReference`1")?.MakeGenericType(typeof(char));
                if (tables == null || unicode == null || byReference == null) { return null; }
                guards.Add(AccessTools.Method(tables, "Category"));
                guards.Add(AccessTools.Method(byReference, "get_Value"));
                guards.AddRange(byReference.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                guards.AddRange(typeof(CoreCharSpan).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                guards.AddRange(typeof(char).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                guards.AddRange(unicode.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                foreach (Type type in new[] { typeof(ApiJsonUnicodeCategories), typeof(char), unicode }) {
                    if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
                }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                _guards = guards.ToArray();
                Roots[0] = typeof(DateTime).GetMethod("TryParse", new[] { typeof(string), typeof(IFormatProvider), typeof(DateTimeStyles), typeof(DateTime).MakeByRefType() });
                Roots[1] = typeof(DateTimeOffset).GetMethod("TryParse", new[] { typeof(string), typeof(IFormatProvider), typeof(DateTimeStyles), typeof(DateTimeOffset).MakeByRefType() });
                Roots[2] = typeof(DateTime).GetMethod("TryParseExact", new[] { typeof(string), typeof(string), typeof(IFormatProvider), typeof(DateTimeStyles), typeof(DateTime).MakeByRefType() });
                Roots[3] = typeof(DateTimeOffset).GetMethod("TryParseExact", new[] { typeof(string), typeof(string), typeof(IFormatProvider), typeof(DateTimeStyles), typeof(DateTimeOffset).MakeByRefType() });
                foreach (MethodInfo method in Roots) { if (method == null) { return null; } }
                for (int i = 0; i < 4; i++) { Bridges[i] = BuildBridge(dt, spanField, indexField, currentField, slice, i); }
                var matcher = new DynamicMethod("MatchDateLexicalString", typeof(bool), new[] { typeof(CoreCharSpan), typeof(string) }, typeof(ApiJsonDateLexicalPreparation), true);
                ILGenerator il = matcher.GetILGenerator();
                Label fail = il.DefineLabel();
                il.Emit(OpCodes.Ldarga_S, (byte)0);
                il.Emit(OpCodes.Call, spanLength);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, stringLength);
                il.Emit(OpCodes.Bne_Un_S, fail);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, reference);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, whole);
                il.Emit(OpCodes.Call, reference);
                il.Emit(OpCodes.Call, same);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(fail);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                return (SpanMatch)matcher.CreateDelegate(typeof(SpanMatch));
            } catch { return null; }
        }

        private static MethodInfo BuildBridge(Type dt, FieldInfo span, FieldInfo index, FieldInfo current, MethodInfo slice, int mode) {
            Type returns = Targets[mode].ReturnType;
            Type[] parameters = returns == typeof(void) ? new[] { dt.MakeByRefType() }
                : new[] { dt.MakeByRefType(), returns.MakeByRefType() };
            var bridge = new DynamicMethod("PrepareDateLexical" + mode, typeof(bool), parameters, typeof(ApiJsonDateLexicalPreparation), true);
            ILGenerator il = bridge.GetILGenerator();
            LocalBuilder result = il.DeclareLocal(typeof(Result));
            Label fail = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, span);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, index);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, current);
            il.Emit(OpCodes.Ldc_I4, mode); il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateLexicalPreparation), "TryPrepare"));
            il.Emit(OpCodes.Brfalse, fail);
            if (mode == 2) {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, span);
                il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Length"));
                il.Emit(OpCodes.Call, slice); il.Emit(OpCodes.Stfld, span);
            } else {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Index")); il.Emit(OpCodes.Stfld, index);
                if (mode == 1) {
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloca, result);
                    il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), "Current")); il.Emit(OpCodes.Stfld, current);
                }
            }
            if (returns != typeof(void)) {
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldfld, AccessTools.Field(typeof(Result), mode == 1 ? "Value" : "Number"));
                il.Emit(mode == 1 ? OpCodes.Stind_I1 : OpCodes.Stind_I4);
            }
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
            il.MarkLabel(fail); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return bridge;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator) {
            int mode = Array.IndexOf(Targets, original);
            var code = new List<CodeInstruction>(instructions);
            if (mode < 0) { return code; }
            Rewritten[mode] = false;
            if (!Supported || code.Count != Counts[mode] || code[0].blocks.Count != 0) { return code; }
            Label fallback = generator.DefineLabel();
            code[0].labels.Add(fallback);
            var prefix = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldarg_0) };
            Type returns = Targets[mode].ReturnType;
            LocalBuilder output = returns == typeof(void) ? null : generator.DeclareLocal(returns);
            if (output != null) { prefix.Add(new CodeInstruction(OpCodes.Ldloca, output)); }
            prefix.Add(new CodeInstruction(OpCodes.Call, Bridges[mode]));
            prefix.Add(new CodeInstruction(OpCodes.Brfalse, fallback));
            if (output != null) { prefix.Add(new CodeInstruction(OpCodes.Ldloc, output)); }
            prefix.Add(new CodeInstruction(OpCodes.Ret));
            prefix.AddRange(code);
            Rewritten[mode] = true;
            return prefix;
        }

        private static MethodInfo FindGeneric(Type type, string name, Type[] parameters) {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (method.Name != name || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1) { continue; }
                MethodInfo closed = method.MakeGenericMethod(typeof(char));
                ParameterInfo[] actual = closed.GetParameters();
                if (actual.Length != parameters.Length) { continue; }
                bool match = true;
                for (int i = 0; i < actual.Length; i++) { if (actual[i].ParameterType != parameters[i]) { match = false; } }
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

        internal readonly struct Scope {
            internal readonly string Text;
            internal readonly string Format;
            internal readonly bool Active;
            internal Scope(string text, string format, bool active) { Text = text; Format = format; Active = active; }
        }

        internal readonly struct Result {
            internal readonly int Index;
            internal readonly char Current;
            internal readonly int Length;
            internal readonly int Number;
            internal readonly bool Value;
            internal Result(int index, char current, int length, int number, bool value) {
                Index = index; Current = current; Length = length; Number = number; Value = value;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateLexicalPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateLexicalPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateLexicalPreparation.Supported;
        private static IEnumerable<MethodBase> TargetMethods() => ApiJsonDateLexicalPreparation.Targets;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod, ILGenerator generator)
            => ApiJsonDateLexicalPreparation.Rewrite(instructions, __originalMethod, generator);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateStringScopePatch {
        private static bool Prepare() => ApiJsonDateLexicalPreparation.Supported;
        private static IEnumerable<MethodBase> TargetMethods() {
            yield return ApiJsonDateLexicalPreparation.Roots[0];
            yield return ApiJsonDateLexicalPreparation.Roots[1];
        }
        private static void Prefix(string __0, out ApiJsonDateLexicalPreparation.Scope __state)
            => __state = ApiJsonDateLexicalPreparation.Begin(__0, null);
        private static Exception Finalizer(Exception __exception, ApiJsonDateLexicalPreparation.Scope __state) {
            ApiJsonDateLexicalPreparation.End(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateExactScopePatch {
        private static bool Prepare() => ApiJsonDateLexicalPreparation.Supported;
        private static IEnumerable<MethodBase> TargetMethods() {
            yield return ApiJsonDateLexicalPreparation.Roots[2];
            yield return ApiJsonDateLexicalPreparation.Roots[3];
        }
        private static void Prefix(string __0, string __1, out ApiJsonDateLexicalPreparation.Scope __state)
            => __state = ApiJsonDateLexicalPreparation.Begin(__0, __1);
        private static Exception Finalizer(Exception __exception, ApiJsonDateLexicalPreparation.Scope __state) {
            ApiJsonDateLexicalPreparation.End(__state);
            return __exception;
        }
    }
}
