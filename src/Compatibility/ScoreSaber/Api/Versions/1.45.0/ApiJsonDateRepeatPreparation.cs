extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateRepeatPreparation {
        [ThreadStatic] private static string Current;
        internal static MethodInfo FormatTarget;
        internal static MethodInfo RepeatTarget;
        private static MethodBase[] Guards;
        private static readonly bool Ready = Initialize();
        internal static bool Supported => Ready && FormatTarget != null && RepeatTarget != null;

        internal static string GetFormatRoot() => Current;

        internal static void Enter(string format, out string previous) {
            previous = Current;
            Current = null;
            try {
                if (ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && format != null && format.Length >= 524288) { Current = format; }
            } catch { }
        }

        internal static void Exit(string previous) => Current = previous;

        internal static bool TryPrepare(CoreCharSpan span, int position, char pattern, out int result) {
            result = 0;
            string text = null;
            int start = 0;
            int length = 0;
            try {
                string candidate = Current;
                if (candidate != null && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && IsUnchanged() && ApiJsonDateSpanStringPreparation.CanReadImmutableSpan()) {
                    length = span.Length;
                    if (position >= 0 && position < length && length - position >= 524288) {
                        CoreCharSpan whole = ScoreSaberCore::System.MemoryExtensions.AsSpan(candidate);
                        if (ScoreSaberCore::System.MemoryExtensions.Overlaps(whole, span, out start)
                            && start >= 0 && length <= whole.Length - start) { text = candidate; }
                    }
                }
            } catch { }
            if (text == null) { return false; }
            Task<int> task = null;
            try { Queue(text, start, length, position, pattern, out task); }
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

        private static void Queue(string text, int start, int length, int position, char pattern, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Scan(text, start, length, position, pattern), out task);
        }

        private static int Scan(string text, int start, int length, int position, char pattern) {
            int index = position + 1;
            while (index < length && text[start + index] == pattern) { index++; }
            return index - position;
        }

        private static bool IsUnchanged() {
            HarmonyLib.Patches format = Harmony.GetPatchInfo(FormatTarget);
            HarmonyLib.Patches repeat = Harmony.GetPatchInfo(RepeatTarget);
            if (format == null || format.Prefixes.Count != 1 || format.Postfixes.Count != 0
                || format.Transpilers.Count != 0 || format.Finalizers.Count != 1
                || format.Prefixes[0].PatchMethod != ApiJsonDateFormatScopePatch.PrefixMethod
                || format.Finalizers[0].PatchMethod != ApiJsonDateFormatScopePatch.FinalizerMethod
                || repeat == null || repeat.Prefixes.Count != 1 || repeat.Postfixes.Count != 0
                || repeat.Transpilers.Count != 0 || repeat.Finalizers.Count != 0
                || repeat.Prefixes[0].PatchMethod != ApiJsonDateRepeatPatch.PrefixMethod) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static bool Initialize() {
            try {
                if (IntPtr.Size != 8 || typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return false; }
                Type date = typeof(char).Assembly.GetType("System.DateTimeFormat");
                if (date == null) { return false; }
                FormatTarget = AccessTools.Method(date, "Format", new[] { typeof(DateTime), typeof(string), typeof(IFormatProvider), typeof(TimeSpan) });
                RepeatTarget = AccessTools.Method(date, "ParseRepeatPattern", new[] { typeof(CoreCharSpan), typeof(int), typeof(char) });
                if (!Matches(FormatTarget, 174, "83ff23a69ec7bf2812639135a4f558e58e9ed8e8c726fbf6d104f4023bdf624c")
                    || !Matches(RepeatTarget, 38, "779c428a81ba0725fd2e1f5a397bb944d7c08f970525f452151b9a6458ef88bd")) { return false; }
                var guards = new List<MethodBase> {
                    typeof(string).GetProperty("Length")?.GetGetMethod(),
                    typeof(string).GetProperty("Chars")?.GetGetMethod(),
                    typeof(CoreCharSpan).GetProperty("Item")?.GetGetMethod(),
                    typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod(),
                    typeof(ScoreSaberCore::System.MemoryExtensions).GetMethod("AsSpan", new[] { typeof(string) }),
                    AccessTools.PropertyGetter(typeof(ApiJsonNumericPreparation), "IsActive")
                };
                AddDeclaredGuards(guards, typeof(ApiJsonDateRepeatPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonDateFormatScopePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateRepeatPatch));
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
                    guards.Add(method.MakeGenericMethod(typeof(int)));
                }
            }
            guards.AddRange(type.GetConstructors(flags));
            if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                AddDeclaredGuards(guards, nested);
                if (nested.IsGenericTypeDefinition && nested.GetGenericArguments().Length == 1) {
                    AddDeclaredGuards(guards, nested.MakeGenericType(typeof(int)));
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
    internal static class ApiJsonDateFormatScopePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateFormatScopePatch), "Prefix");
        internal static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(ApiJsonDateFormatScopePatch), "Finalizer");
        private static bool Prepare() => ApiJsonDateRepeatPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateRepeatPreparation.FormatTarget;
        private static void Prefix(string __1, out string __state) => ApiJsonDateRepeatPreparation.Enter(__1, out __state);
        private static Exception Finalizer(string __state, Exception __exception) {
            ApiJsonDateRepeatPreparation.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateRepeatPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateRepeatPatch), "Prefix");
        private static bool Prepare() => ApiJsonDateRepeatPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateRepeatPreparation.RepeatTarget;
        private static bool Prefix(CoreCharSpan __0, int __1, char __2, ref int __result)
            => !ApiJsonDateRepeatPreparation.TryPrepare(__0, __1, __2, out __result);
    }
}
