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
        [ThreadStatic] private static bool Active;
        internal static MethodInfo FormatTarget;
        internal static MethodInfo RepeatTarget;
        internal static MethodInfo ExpandedTarget;
        internal static MethodInfo GenitiveTarget;
        private static MethodBase[] Guards;
        private static readonly bool Ready = Initialize();
        internal static bool Supported => Ready && FormatTarget != null && RepeatTarget != null;

        internal static bool CanPrepareOwnedFormat() => Supported && IsUnchanged();
        internal static string GetFormatRoot() => Current;

        internal readonly struct Scope {
            internal readonly string Previous;
            internal readonly bool WasActive;
            internal readonly ApiJsonDateBuilderOutputPreparation.Frame PreviousOutput;
            internal Scope(string previous, bool wasActive, ApiJsonDateBuilderOutputPreparation.Frame previousOutput) {
                Previous = previous; WasActive = wasActive; PreviousOutput = previousOutput;
            }
        }

        internal static void Enter(string format, out Scope previous) {
            previous = new Scope(Current, Active, ApiJsonDateBuilderOutputPreparation.Enter());
            Current = null;
            Active = false;
            try {
                Active = ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread;
                if (Active && format != null && format.Length >= 524288) { Current = format; }
            } catch { }
        }

        internal static void Exit(Scope previous) {
            ApiJsonDateBuilderOutputPreparation.Exit(previous.PreviousOutput);
            Current = previous.Previous;
            Active = previous.WasActive;
        }

        internal static void RecordExpandedRoot(string text) {
            try {
                if (!Active) { return; }
                Current = null;
                if (ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && ExpandedUnchanged() && text != null && text.Length >= 524288) { Current = text; }
            } catch { Current = null; }
        }

        internal static bool ExpandedUnchanged() {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(ExpandedTarget);
            return info != null && info.Prefixes.Count == 0 && info.Postfixes.Count == 1
                && info.Transpilers.Count == 1 && info.Finalizers.Count == 0
                && info.Postfixes[0].PatchMethod == ApiJsonDateExpandedRootPatch.PostfixMethod
                && info.Transpilers[0].PatchMethod == ApiJsonDateExpandedRootPatch.TranspilerMethod;
        }

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

        internal static bool TryPrepareGenitive(CoreCharSpan span, int position, int tokenLength, char pattern, out bool result) {
            result = false;
            string text = null;
            int start = 0;
            int length = 0;
            try {
                string candidate = Current;
                if (candidate != null && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && IsUnchanged() && ApiJsonDateSpanStringPreparation.CanReadImmutableSpan()) {
                    length = span.Length;
                    if (length >= 524288 && length <= int.MaxValue / 2 && position >= 0 && position < length
                        && tokenLength >= 0 && tokenLength <= length - position) {
                        CoreCharSpan whole = ScoreSaberCore::System.MemoryExtensions.AsSpan(candidate);
                        if (ScoreSaberCore::System.MemoryExtensions.Overlaps(whole, span, out start)
                            && start >= 0 && length <= whole.Length - start
                            && ScanGenitive(candidate, start, length, position, tokenLength, pattern, 1024) == -1) { text = candidate; }
                    }
                }
            } catch { }
            if (text == null) { return false; }
            Task<int> task = null;
            try { QueueGenitive(text, start, length, position, tokenLength, pattern, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            result = task.GetAwaiter().GetResult() == 1;
            return true;
        }

        private static void QueueGenitive(string text, int start, int length, int position, int tokenLength, char pattern, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => ScanGenitive(text, start, length, position, tokenLength, pattern, int.MaxValue), out task);
        }

        private static int ScanGenitive(string text, int start, int length, int position, int tokenLength, char pattern, int budget) {
            int work = 0;
            int index = position - 1;
            int repeated = 0;
            while (index >= 0 && text[start + index] != pattern) {
                if (work++ == budget) { return -1; }
                index--;
            }
            if (index >= 0) {
                while (--index >= 0 && text[start + index] == pattern) {
                    if (work++ == budget) { return -1; }
                    repeated++;
                }
                if (repeated <= 1) { return 1; }
            }
            index = position + tokenLength;
            while (index < length && text[start + index] != pattern) {
                if (work++ == budget) { return -1; }
                index++;
            }
            if (index < length) {
                repeated = 0;
                while (++index < length && text[start + index] == pattern) {
                    if (work++ == budget) { return -1; }
                    repeated++;
                }
                if (repeated <= 1) { return 1; }
            }
            return 0;
        }

        private static bool GenitiveUnchanged() {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(GenitiveTarget);
            return info != null && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == ApiJsonDateGenitivePatch.PrefixMethod
                && info.Postfixes.Count == 0 && info.Transpilers.Count == 0 && info.Finalizers.Count == 0;
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
            if (!ExpandedUnchanged() || !GenitiveUnchanged()) { return false; }
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
                ExpandedTarget = AccessTools.Method(date, "GetRealFormat", new[] { typeof(CoreCharSpan), typeof(System.Globalization.DateTimeFormatInfo) });
                GenitiveTarget = AccessTools.Method(date, "IsUseGenitiveForm", new[] { typeof(CoreCharSpan), typeof(int), typeof(int), typeof(char) });
                if (!Matches(FormatTarget, 174, "83ff23a69ec7bf2812639135a4f558e58e9ed8e8c726fbf6d104f4023bdf624c")
                    || !Matches(RepeatTarget, 38, "779c428a81ba0725fd2e1f5a397bb944d7c08f970525f452151b9a6458ef88bd")
                    || !Matches(ExpandedTarget, 360, "1fc04d3912122e138f62e0f838608b962b8687cd155debab5adad3f682cad680")
                    || !Matches(GenitiveTarget, 148, "655321f8516546f90635c24ddf0ceadc677b535a380f7e9fb9b992dec021e412")) { return false; }
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
                AddDeclaredGuards(guards, typeof(ApiJsonDateGenitivePatch));
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
        private static void Prefix(string __1, out ApiJsonDateRepeatPreparation.Scope __state) => ApiJsonDateRepeatPreparation.Enter(__1, out __state);
        private static Exception Finalizer(ApiJsonDateRepeatPreparation.Scope __state, Exception __exception) {
            ApiJsonDateRepeatPreparation.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateExpandedRootPatch {
        internal static readonly MethodInfo PostfixMethod = AccessTools.Method(typeof(ApiJsonDateExpandedRootPatch), "Postfix");
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateExpandedRootPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateRepeatPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateRepeatPreparation.ExpandedTarget;
        private static void Postfix(string __result) => ApiJsonDateRepeatPreparation.RecordExpandedRoot(__result);
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonDateBuilderOutputPreparation.RewriteExpanded(instructions);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateGenitivePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateGenitivePatch), "Prefix");
        private static bool Prepare() => ApiJsonDateRepeatPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateRepeatPreparation.GenitiveTarget;
        private static bool Prefix(CoreCharSpan __0, int __1, int __2, char __3, ref bool __result)
            => !ApiJsonDateRepeatPreparation.TryPrepareGenitive(__0, __1, __2, __3, out __result);
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
