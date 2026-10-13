using HarmonyLib;
using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonFloatingPreparation {
        private const int MinimumTextLength = 512 * 1024;
        private delegate bool SpanParser(ReadOnlySpan<char> text, NumberStyles styles, NumberFormatInfo info, out double value);
        private static SpanParser _parser;

        internal static bool TryInitialize(MethodInfo method) {
            try {
                if (_parser == null) {
                    _parser = (SpanParser)method.CreateDelegate(typeof(SpanParser));
                }
                return true;
            } catch {
                return false;
            }
        }

        internal static bool TryPrepare(ReadOnlySpan<char> text, NumberStyles styles, NumberFormatInfo info,
            ref double value, out bool parsed) {
            parsed = false;
            if (!ApiJsonNumericPreparation.IsActive || text.Length < MinimumTextLength || _parser == null
                || Thread.CurrentThread.IsThreadPoolThread || info == null || info.GetType() != typeof(NumberFormatInfo)
                || !info.IsReadOnly || (styles != NumberStyles.Float && styles != (NumberStyles.Float | NumberStyles.AllowThousands))) {
                return false;
            }

            Task<ParseResult> task = null;
            try {
                TryQueue(text, styles, info, out task);
            } catch { }
            if (task == null) {
                return false;
            }

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
            } catch {
                return false;
            }
        }

        private static void TryQueue(ReadOnlySpan<char> text, NumberStyles styles, NumberFormatInfo info,
            out Task<ParseResult> task) {
            task = null;
            // A span can refer to caller stack storage; retain only its private copy.
            char[] ownedChars = text.ToArray();
            SpanParser parser = _parser;
            Func<ParseResult> prepare = () => ParseOwned(ownedChars, styles, info, parser);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        private static ParseResult ParseOwned(char[] chars, NumberStyles styles, NumberFormatInfo info, SpanParser parser) {
            bool parsed = parser(new ReadOnlySpan<char>(chars), styles, info, out double value);
            return new ParseResult(parsed, value);
        }

        private readonly struct ParseResult {
            internal readonly bool Parsed;
            internal readonly double Value;

            internal ParseResult(bool parsed, double value) {
                Parsed = parsed;
                Value = value;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonFloatingParsePatch {
        private const string ExpectedCoreModule = "41d29b35-2f6a-475a-b1bf-7c6628b82790";
        private const string ExpectedBodyHash = "93afbdd03629a051c3f6964d1fc8adab80258e823916d9ce18075b614d3067fa";
        private static readonly MethodInfo Target = FindTarget();

        private static bool Prepare() => Target != null && ApiJsonFloatingPreparation.TryInitialize(Target);
        private static MethodBase TargetMethod() => Target;

        private static MethodInfo FindTarget() {
            try {
                if (typeof(double).Module.ModuleVersionId != new Guid(ExpectedCoreModule)) {
                    return null;
                }

                MethodInfo method = typeof(double).GetMethod("TryParse", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(ReadOnlySpan<char>), typeof(NumberStyles), typeof(NumberFormatInfo), typeof(double).MakeByRefType() }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || method.ReturnType != typeof(bool) || body == null || body.Length != 118) {
                    return null;
                }

                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    return actual == ExpectedBodyHash ? method : null;
                }
            } catch {
                return null;
            }
        }

        private static bool Prefix(ReadOnlySpan<char> __0, NumberStyles __1, NumberFormatInfo __2, ref double __3, ref bool __result) {
            if (!ApiJsonFloatingPreparation.TryPrepare(__0, __1, __2, ref __3, out bool parsed)) {
                return true;
            }
            __result = parsed;
            return false;
        }
    }
}
