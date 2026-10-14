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
    internal static class ApiJsonQuotedIntegerPreparation {
        private delegate bool SpanParser(CoreCharSpan text, NumberStyles styles, NumberFormatInfo info, out int value);
        private static SpanParser _parser;
        private static MethodBase[] _kernelMethods;

        internal static bool TryInitialize(MethodInfo method, MethodInfo specialized) {
            try {
                if (_parser != null) { return true; }
                if (method == null || specialized == null) { return false; }
                Type number = method.DeclaringType;
                MethodBase[] methods = {
                    specialized,
                    AccessTools.Method(number, "IsWhite", new[] { typeof(int) }),
                    AccessTools.Method(number, "IsDigit", new[] { typeof(int) }),
                    AccessTools.Method(number, "TrailingZeros", new[] { typeof(CoreCharSpan), typeof(int) }),
                    typeof(NumberFormatInfo).GetProperty("PositiveSign")?.GetGetMethod(),
                    typeof(NumberFormatInfo).GetProperty("NegativeSign")?.GetGetMethod(),
                    typeof(NumberFormatInfo).GetProperty("IsReadOnly")?.GetGetMethod(),
                    AccessTools.Method(typeof(string), "op_Equality", new[] { typeof(string), typeof(string) }),
                    AccessTools.Method(typeof(string), "op_Inequality", new[] { typeof(string), typeof(string) }),
                    AccessTools.Method(typeof(string), "Equals", new[] { typeof(string), typeof(string) }),
                    typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod(),
                    typeof(CoreCharSpan).GetProperty("Item")?.GetGetMethod(),
                    AccessTools.Method(typeof(CoreCharSpan), "Slice", new[] { typeof(int) }),
                    AccessTools.Method(typeof(CoreCharSpan), "ToArray", Type.EmptyTypes),
                    typeof(CoreCharSpan).GetConstructor(new[] { typeof(char[]) })
                };
                foreach (MethodBase leaf in methods) { if (leaf == null) { return false; } }
                _kernelMethods = methods;
                _parser = (SpanParser)method.CreateDelegate(typeof(SpanParser));
                return true;
            } catch { return false; }
        }

        internal static bool TryPrepare(CoreCharSpan text, NumberStyles styles, NumberFormatInfo info,
            ref int value, out bool parsed) {
            parsed = false;
            if (Thread.CurrentThread.IsThreadPoolThread || !ApiJsonNumericPreparation.IsActive || _parser == null
                || styles != NumberStyles.Integer || info == null || info.GetType() != typeof(NumberFormatInfo)
                || !IsUnchanged() || text.Length < 524288 || !info.IsReadOnly
                || info.PositiveSign != "+" || info.NegativeSign != "-" || !HasPrefix(text)) {
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
                HarmonyLib.Patches info = Harmony.GetPatchInfo(ApiJsonQuotedIntegerPatch.Target);
                if (info == null || info.Postfixes.Count != 0 || info.Finalizers.Count != 0 || info.Transpilers.Count != 0
                    || info.Prefixes.Count != 1 || info.Prefixes[0].PatchMethod != ApiJsonQuotedIntegerPatch.PrefixMethod) {
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
                bool parsed = parser(new CoreCharSpan(ownedChars), styles, info, out int value);
                return new ParseResult(parsed, value);
            }, out task);
        }

        private readonly struct ParseResult {
            internal readonly bool Parsed;
            internal readonly int Value;
            internal ParseResult(bool parsed, int value) { Parsed = parsed; Value = value; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonQuotedIntegerPatch {
        internal static readonly MethodInfo Target = Find("TryParseInt32", 83,
            "98208dc97cb5755031ca2e21dfab32246aa8d949f82274c965339571323205f4", false);
        private static readonly MethodInfo Specialized = Find("TryParseInt32IntegerStyle", 709,
            "143a475d6ca799d66b5f30e4ae4397a9f066c5c61404e80a5399ed24ed905291", true);
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonQuotedIntegerPatch), "Prefix");

        private static bool Prepare() => ApiJsonQuotedIntegerPreparation.TryInitialize(Target, Specialized);
        private static MethodBase TargetMethod() => Target;

        private static MethodInfo Find(string name, int length, string expected, bool overflow) {
            try {
                if (typeof(int).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type number = typeof(int).Assembly.GetType("System.Number");
                Type[] parameters = overflow
                    ? new[] { typeof(CoreCharSpan), typeof(NumberStyles), typeof(NumberFormatInfo), typeof(int).MakeByRefType(), typeof(bool).MakeByRefType() }
                    : new[] { typeof(CoreCharSpan), typeof(NumberStyles), typeof(NumberFormatInfo), typeof(int).MakeByRefType() };
                MethodInfo method = number?.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, parameters, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || method.ReturnType != typeof(bool) || body == null || body.Length != length) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant() == expected ? method : null;
                }
            } catch { return null; }
        }

        private static bool Prefix(CoreCharSpan __0, NumberStyles __1, NumberFormatInfo __2, ref int __3, ref bool __result) {
            if (!ApiJsonQuotedIntegerPreparation.TryPrepare(__0, __1, __2, ref __3, out bool parsed)) { return true; }
            __result = parsed;
            return false;
        }
    }
}
