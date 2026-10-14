extern alias ScoreSaberCore;

using HarmonyLib;
using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScoreSaber.Features.Replays;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonQuotedDecimalPreparation {
        private delegate bool SpanParser(CoreCharSpan text, NumberStyles styles, NumberFormatInfo info, out decimal value);
        private static SpanParser _parser;
        private static MethodBase[] _kernelMethods;

        internal static bool TryInitialize(MethodInfo method, MethodInfo specialized) {
            try {
                if (_parser != null) { return true; }
                if (method == null || specialized == null) { return false; }
                Type number = method.DeclaringType;
                Type buffer = number.GetNestedType("NumberBuffer", BindingFlags.NonPublic);
                if (buffer == null) { return false; }
                var methods = new System.Collections.Generic.List<MethodBase> { specialized };
                string[] names = { "TryStringToNumber", "ParseNumber", "MatchChars", "IsWhite", "IsDigit", "TrailingZeros" };
                foreach (string name in names) {
                    bool found = false;
                    foreach (MethodInfo leaf in number.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                        if (leaf.Name == name) { methods.Add(leaf); found = true; }
                    }
                    if (!found) { return false; }
                }
                methods.AddRange(buffer.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                foreach (PropertyInfo property in typeof(NumberFormatInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    MethodInfo getter = property.GetGetMethod(true);
                    if (getter != null) { methods.Add(getter); }
                }
                methods.Add(AccessTools.Method(typeof(string), "op_Equality", new[] { typeof(string), typeof(string) }));
                methods.Add(AccessTools.Method(typeof(string), "Equals", new[] { typeof(string), typeof(string) }));
                methods.Add(typeof(string).GetProperty("Length")?.GetGetMethod());
                methods.Add(typeof(string).GetProperty("Chars")?.GetGetMethod());
                methods.Add(typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod());
                methods.Add(typeof(CoreCharSpan).GetProperty("Item")?.GetGetMethod());
                methods.Add(AccessTools.Method(typeof(CoreCharSpan), "Slice", new[] { typeof(int) }));
                methods.Add(AccessTools.Method(typeof(CoreCharSpan), "ToArray", Type.EmptyTypes));
                methods.Add(typeof(CoreCharSpan).GetConstructor(new[] { typeof(char[]) }));
                methods.Add(AccessTools.Method(typeof(Math), "Clamp", new[] { typeof(int), typeof(int), typeof(int) }));
                methods.Add(AccessTools.Method(typeof(Math), "Min", new[] { typeof(int), typeof(int) }));
                Type marshal = typeof(decimal).Assembly.GetType("System.Runtime.InteropServices.MemoryMarshal");
                if (marshal == null) { return false; }
                bool referenceFound = false;
                foreach (MethodInfo leaf in marshal.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (leaf.Name != "GetReference" || !leaf.IsGenericMethodDefinition || leaf.GetGenericArguments().Length != 1) { continue; }
                    MethodInfo closed = leaf.MakeGenericMethod(typeof(char));
                    ParameterInfo[] parameters = closed.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CoreCharSpan)) {
                        methods.Add(closed);
                        referenceFound = true;
                    }
                }
                if (!referenceFound) { return false; }
                methods.Add(typeof(decimal).GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(byte) }));
                foreach (MethodBase leaf in methods) { if (leaf == null) { return false; } }
                _kernelMethods = methods.ToArray();
                _parser = (SpanParser)method.CreateDelegate(typeof(SpanParser));
                return true;
            } catch { return false; }
        }

        internal static bool TryPrepare(CoreCharSpan text, NumberStyles styles, NumberFormatInfo info,
            ref decimal value, out bool parsed) {
            parsed = false;
            if (Thread.CurrentThread.IsThreadPoolThread || !ApiJsonNumericPreparation.IsActive || _parser == null
                || styles != NumberStyles.Number || info == null || info.GetType() != typeof(NumberFormatInfo)
                || !IsUnchanged() || text.Length < 524288 || !info.IsReadOnly
                || !HasPrefix(text)) {
                return false;
            }
            Task<ParseResult> task = null;
            try { TryQueue(text, styles, info, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                ParseResult result = task.GetAwaiter().GetResult();
                value = result.Value;
                parsed = result.Parsed;
                return true;
            } catch { return false; }
        }

        private static bool IsUnchanged() {
            try {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(ApiJsonQuotedDecimalPatch.Target);
                if (info == null || info.Postfixes.Count != 0 || info.Finalizers.Count != 0 || info.Transpilers.Count != 0
                    || info.Prefixes.Count != 1 || info.Prefixes[0].PatchMethod != ApiJsonQuotedDecimalPatch.PrefixMethod) {
                    return false;
                }
                foreach (MethodBase method in _kernelMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                return true;
            } catch { return false; }
        }

        private static bool HasPrefix(CoreCharSpan text) {
            bool whitespace = true;
            bool digits = true;
            for (int i = 0; i < 1024; i++) {
                char current = text[i];
                whitespace &= current == ' ' || (current >= '\t' && current <= '\r');
                digits &= current >= '0' && current <= '9';
                if (!whitespace && !digits) { return false; }
            }
            return true;
        }

        private static void TryQueue(CoreCharSpan text, NumberStyles styles, NumberFormatInfo info,
            out Task<ParseResult> task) {
            task = null;
            char[] ownedChars = text.ToArray();
            SpanParser parser = _parser;
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                bool parsed = parser(new CoreCharSpan(ownedChars), styles, info, out decimal value);
                return new ParseResult(parsed, value);
            }, out task);
        }

        private readonly struct ParseResult {
            internal readonly bool Parsed;
            internal readonly decimal Value;
            internal ParseResult(bool parsed, decimal value) { Parsed = parsed; Value = value; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonQuotedDecimalPatch {
        internal static readonly MethodInfo Target = Find(false, 44,
            "31c2adcba94b2cc74bde29267dd2c4995523522ee693de2e604b0dd273cd56a2");
        private static readonly MethodInfo Converter = Find(true, 474,
            "97f6d52d605afbad0563abed3a772cdc8db8bbffe9001a4a4b3c974fba86fc8e");
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonQuotedDecimalPatch), "Prefix");

        private static bool Prepare() => ApiJsonQuotedDecimalPreparation.TryInitialize(Target, Converter);
        private static MethodBase TargetMethod() => Target;

        private static MethodInfo Find(bool converter, int length, string expected) {
            try {
                if (typeof(decimal).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type number = typeof(decimal).Assembly.GetType("System.Number");
                Type buffer = number?.GetNestedType("NumberBuffer", BindingFlags.NonPublic);
                if (number == null || buffer == null) { return null; }
                Type[] parameters = converter
                    ? new[] { buffer.MakeByRefType(), typeof(decimal).MakeByRefType() }
                    : new[] { typeof(CoreCharSpan), typeof(NumberStyles), typeof(NumberFormatInfo), typeof(decimal).MakeByRefType() };
                MethodInfo method = number.GetMethod(converter ? "NumberBufferToDecimal" : "TryParseDecimal",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || method.ReturnType != typeof(bool) || body == null || body.Length != length) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant() == expected ? method : null;
                }
            } catch { return null; }
        }

        private static bool Prefix(CoreCharSpan __0, NumberStyles __1, NumberFormatInfo __2, ref decimal __3, ref bool __result) {
            if (!ApiJsonQuotedDecimalPreparation.TryPrepare(__0, __1, __2, ref __3, out bool parsed)) { return true; }
            __result = parsed;
            return false;
        }
    }
}
