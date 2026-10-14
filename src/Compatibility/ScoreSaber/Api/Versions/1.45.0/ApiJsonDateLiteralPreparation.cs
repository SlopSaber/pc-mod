extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateLiteralPreparation {
        internal static MethodInfo Target;
        private static MethodBase[] Guards;
        private static readonly bool Ready = Initialize();
        internal static bool Supported => Ready && ApiJsonDateRepeatPreparation.Supported;

        private readonly struct Result {
            internal readonly int Consumed;
            internal readonly int Written;
            internal Result(int consumed, int written) { Consumed = consumed; Written = written; }
        }

        internal static bool TryPrepare(CoreCharSpan span, int position, StringBuilder builder, out int consumed) {
            consumed = 0;
            string text = null;
            char[] values = null;
            int start = 0;
            int length = 0;
            try {
                if (builder != null && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && IsUnchanged() && ApiJsonDateSpanStringPreparation.CanReadImmutableSpan()) {
                    string candidate = ApiJsonDateRepeatPreparation.GetFormatRoot();
                    length = span.Length;
                    if (candidate != null && position >= 0 && position < length && length - position >= 524288) {
                        CoreCharSpan whole = ScoreSaberCore::System.MemoryExtensions.AsSpan(candidate);
                        if (ScoreSaberCore::System.MemoryExtensions.Overlaps(whole, span, out start)
                            && start >= 0 && length <= whole.Length - start && LooksLong(candidate, start, length, position)) {
                            values = new char[length - position - 1];
                            text = candidate;
                        }
                    }
                }
            } catch { }
            if (text == null) { return false; }
            Task<Result> task = null;
            try { Queue(text, values, start, length, position, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            Result result = task.GetAwaiter().GetResult();
            if (result.Consumed == 0) { return false; }
            for (int at = 0; at < result.Written; at++) { builder.Append(values[at]); }
            consumed = result.Consumed;
            return true;
        }

        private static bool LooksLong(string text, int start, int length, int position) {
            char quote = text[start + position];
            if (quote != '\'' && quote != '"') { return false; }
            int cursor = position + 1;
            for (int at = 0; at < 1024; at++) {
                if (cursor >= length) { return false; }
                char value = text[start + cursor++];
                if (value == quote) { return false; }
                if (value == '\\') {
                    if (cursor >= length) { return false; }
                    cursor++;
                }
            }
            return true;
        }

        private static void Queue(string text, char[] values, int start, int length, int position, out Task<Result> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Decode(text, values, start, length, position), out task);
        }

        private static Result Decode(string text, char[] values, int start, int length, int position) {
            char quote = text[start + position];
            int cursor = position + 1;
            int written = 0;
            while (cursor < length) {
                char value = text[start + cursor++];
                if (value == quote) { return new Result(cursor - position, written); }
                if (value == '\\') {
                    if (cursor >= length) { return default; }
                    value = text[start + cursor++];
                }
                values[written++] = value;
            }
            return default;
        }

        private static bool IsUnchanged() {
            if (!ApiJsonDateRepeatPreparation.ExpandedUnchanged()) { return false; }
            HarmonyLib.Patches quote = Harmony.GetPatchInfo(Target);
            HarmonyLib.Patches format = Harmony.GetPatchInfo(ApiJsonDateRepeatPreparation.FormatTarget);
            if (quote == null || quote.Prefixes.Count != 1 || quote.Postfixes.Count != 0
                || quote.Transpilers.Count != 0 || quote.Finalizers.Count != 0
                || quote.Prefixes[0].PatchMethod != ApiJsonDateLiteralPatch.PrefixMethod
                || format == null || format.Prefixes.Count != 1 || format.Postfixes.Count != 0
                || format.Transpilers.Count != 0 || format.Finalizers.Count != 1
                || format.Prefixes[0].PatchMethod != ApiJsonDateFormatScopePatch.PrefixMethod
                || format.Finalizers[0].PatchMethod != ApiJsonDateFormatScopePatch.FinalizerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static bool Initialize() {
            try {
                if (IntPtr.Size != 8 || typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return false; }
                Type date = typeof(char).Assembly.GetType("System.DateTimeFormat");
                if (date == null) { return false; }
                Target = AccessTools.Method(date, "ParseQuoteString", new[] { typeof(CoreCharSpan), typeof(int), typeof(StringBuilder) });
                if (!Matches(Target, 145, "69a88d0f1a389365d25c64aac00ac989add5d5a35c10dd6261ff92f0490fd2b5")) { return false; }
                var guards = new List<MethodBase> {
                    typeof(string).GetProperty("Length")?.GetGetMethod(),
                    typeof(string).GetProperty("Chars")?.GetGetMethod(),
                    typeof(CoreCharSpan).GetProperty("Item")?.GetGetMethod(),
                    typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod(),
                    typeof(ScoreSaberCore::System.MemoryExtensions).GetMethod("AsSpan", new[] { typeof(string) }),
                    AccessTools.PropertyGetter(typeof(ApiJsonNumericPreparation), "IsActive"),
                    AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(char) }),
                    AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(char), typeof(int) }),
                    AccessTools.Method(typeof(StringBuilder), "ExpandByABlock", new[] { typeof(int) }),
                    AccessTools.PropertyGetter(typeof(StringBuilder), "Length")
                };
                AddDeclaredGuards(guards, typeof(ApiJsonDateLiteralPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonDateLiteralPatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateRepeatPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonDateFormatScopePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateExpandedRootPatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateSpanStringPreparation));
                AddDeclaredGuards(guards, typeof(ReplayStorageService));
                foreach (MethodBase method in guards) { if (method == null) { return false; } }
                Guards = guards.ToArray();
                return true;
            } catch { return false; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(flags)) {
                guards.Add(method);
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1) {
                    guards.Add(method.MakeGenericMethod(typeof(Result)));
                }
            }
            guards.AddRange(type.GetConstructors(flags));
            if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                AddDeclaredGuards(guards, nested);
                if (nested.IsGenericTypeDefinition && nested.GetGenericArguments().Length == 1) {
                    AddDeclaredGuards(guards, nested.MakeGenericType(typeof(Result)));
                }
            }
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
    internal static class ApiJsonDateLiteralPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateLiteralPatch), "Prefix");
        private static bool Prepare() => ApiJsonDateLiteralPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateLiteralPreparation.Target;
        private static bool Prefix(CoreCharSpan __0, int __1, StringBuilder __2, ref int __result)
            => !ApiJsonDateLiteralPreparation.TryPrepare(__0, __1, __2, out __result);
    }
}
