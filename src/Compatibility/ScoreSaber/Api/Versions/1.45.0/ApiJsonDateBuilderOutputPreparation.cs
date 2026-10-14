extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateBuilderOutputPreparation {
        internal sealed class Frame {
            internal readonly Frame Previous;
            internal bool Retired;
            internal StringBuilder Builder;
            internal Frame(Frame previous, bool retired) { Previous = previous; Retired = retired; }
        }

        private delegate void ReleaseBuilder(StringBuilder builder);
        private delegate StringBuilder AcquireBuilder(int capacity);
        [ThreadStatic] private static Frame Current;
        [ThreadStatic] private static StringBuilder KnownCached;
        [ThreadStatic] private static ulong Generation;
        internal static MethodInfo AcquireTarget;
        internal static MethodInfo ReleaseTarget;
        internal static MethodInfo StringTarget;
        internal static MethodInfo BufferTarget;
        internal static MethodInfo FormatterTarget;
        internal static bool Rewritten;
        internal static bool FormatterRewritten;
        private static ConstructorInfo Constructor;
        private static FieldInfo CachedField;
        private static AcquireBuilder Acquire;
        private static MethodInfo[] DateMethods;
        private static MethodBase[] Guards;
        private static readonly ReleaseBuilder Release = Initialize();
        internal static bool Supported => Release != null && Guards != null;

        internal readonly struct ReleaseState {
            internal readonly StringBuilder Candidate;
            internal readonly ulong Version;
            internal ReleaseState(StringBuilder candidate, ulong version) { Candidate = candidate; Version = version; }
        }

        private static void InvalidateCache() {
            KnownCached = null;
            Generation = unchecked(Generation + 1);
        }

        internal static Frame Enter() {
            Frame previous = Current;
            Current = null;
            try {
                bool admitted = ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread && Supported;
                if (admitted || previous != null) { Current = new Frame(previous, !admitted); }
            } catch { Retire(previous); }
            return previous;
        }

        internal static void Exit(Frame previous) {
            if (Current != null) { Current.Retired = true; Current.Builder = null; }
            Current = previous;
        }

        private static void Retire(Frame frame) {
            InvalidateCache();
            for (; frame != null; frame = frame.Previous) { frame.Retired = true; frame.Builder = null; }
        }

        internal static void BeforeAcquire() {
            InvalidateCache();
            if (Current != null) { Current.Builder = null; }
        }

        internal static void Record(StringBuilder builder) {
            try {
                Frame frame = Current;
                if (frame != null && !frame.Retired && builder != null && builder.Length == 0) { frame.Builder = builder; }
            } catch { if (Current != null) { Current.Builder = null; } }
        }

        internal static void Revoke(StringBuilder builder) {
            InvalidateCache();
            for (Frame frame = Current; frame != null; frame = frame.Previous) {
                if (ReferenceEquals(frame.Builder, builder)) { frame.Retired = true; frame.Builder = null; }
            }
        }

        internal static ReleaseState BeforeRelease(StringBuilder builder) {
            StringBuilder candidate = null;
            try {
                Frame frame = Current;
                if (builder != null && frame != null && !frame.Retired && ReferenceEquals(frame.Builder, builder)
                    && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && Rewritten && FormatterRewritten && IsUnchanged()) { candidate = builder; }
            } catch { }
            Revoke(builder);
            return new ReleaseState(candidate, Generation);
        }

        internal static void AfterRelease(ReleaseState state) {
            try {
                if (state.Candidate != null && state.Version == Generation && IsUnchanged()
                    && ReferenceEquals(CachedField.GetValue(null), state.Candidate) && state.Version == Generation) {
                    KnownCached = state.Candidate;
                }
            } catch { InvalidateCache(); }
        }

        internal static StringBuilder AcquireForFormatter(int capacity) {
            StringBuilder candidate = null;
            Frame frame = Current;
            ulong version = Generation;
            try {
                if (frame != null && !frame.Retired && ApiJsonNumericPreparation.IsActive
                    && !Thread.CurrentThread.IsThreadPoolThread && Supported && Rewritten && FormatterRewritten && IsUnchanged()) {
                    candidate = KnownCached;
                }
            } catch { }
            KnownCached = null;
            StringBuilder builder = Acquire(capacity);
            try {
                if (candidate != null && ReferenceEquals(builder, candidate) && ReferenceEquals(Current, frame)
                    && !frame.Retired && Generation == unchecked(version + 1) && IsUnchanged()) { Record(builder); }
            } catch { }
            return builder;
        }

        internal static bool TryPrepare(StringBuilder builder, out string result) {
            result = null;
            bool admitted = false;
            try {
                Frame frame = Current;
                admitted = builder != null && frame != null && !frame.Retired && ReferenceEquals(frame.Builder, builder)
                    && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && Rewritten && builder.Length >= 524288 && builder.Length <= int.MaxValue / 2 && IsUnchanged();
            } catch { }
            if (!admitted) { return false; }
            Task<string> task = null;
            try { Queue(builder, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            result = task.GetAwaiter().GetResult();
            Release(builder);
            return true;
        }

        internal static bool TryAppendLiteral(StringBuilder builder, char[] values, int count) {
            bool admitted = false;
            try {
                Frame frame = Current;
                admitted = builder != null && values != null && count >= 524288 && count <= values.Length
                    && count <= int.MaxValue / 2 && frame != null && !frame.Retired && ReferenceEquals(frame.Builder, builder)
                    && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && Rewritten && IsUnchanged();
            } catch { }
            if (!admitted) { return false; }
            Task<string> task = null;
            try { QueueLiteral(builder, values, count, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
            return true;
        }

        private static void QueueLiteral(StringBuilder builder, char[] values, int count, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                for (int at = 0; at < count; at++) { builder.Append(values[at]); }
                return null;
            }, out task);
        }

        internal static StringBuilder AppendForFormatter(StringBuilder builder, string text) {
            bool admitted = false;
            try {
                Frame frame = Current;
                admitted = builder != null && text != null && text.Length >= 524288 && text.Length <= int.MaxValue / 2
                    && frame != null && !frame.Retired && ReferenceEquals(frame.Builder, builder)
                    && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && Rewritten && FormatterRewritten && IsUnchanged();
            } catch { }
            if (!admitted) { return builder.Append(text); }
            Task<string> task = null;
            try { QueueString(builder, text, out task); }
            catch { }
            if (task == null) { return builder.Append(text); }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
            return builder;
        }

        private static void QueueString(StringBuilder builder, string text, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                builder.Append(text);
                return null;
            }, out task);
        }

        internal static int TryAppendFormatLiteral(CoreCharSpan span, int position, StringBuilder builder) {
            string text = null;
            int start = 0;
            int length = 0;
            try {
                string candidate = ApiJsonDateRepeatPreparation.GetFormatRoot();
                Frame frame = Current;
                if (builder != null && candidate != null && frame != null && !frame.Retired && ReferenceEquals(frame.Builder, builder)
                    && ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && Supported && Rewritten && FormatterRewritten && IsUnchanged()
                    && ApiJsonDateRepeatPreparation.CanPrepareOwnedFormat() && ApiJsonDateSpanStringPreparation.CanReadImmutableSpan()) {
                    length = span.Length;
                    if (position >= 0 && position < length && length <= int.MaxValue / 2 && length - position >= 524288) {
                        CoreCharSpan whole = ScoreSaberCore::System.MemoryExtensions.AsSpan(candidate);
                        if (ScoreSaberCore::System.MemoryExtensions.Overlaps(whole, span, out start)
                            && start >= 0 && length <= whole.Length - start && LooksLongFormatLiteral(candidate, start, position)) { text = candidate; }
                    }
                }
            } catch { }
            if (text == null) { return 0; }
            Task<int> task = null;
            try { QueueFormatLiteral(builder, text, start, length, position, out task); }
            catch { }
            if (task == null) { return 0; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            return task.GetAwaiter().GetResult();
        }

        private static void QueueFormatLiteral(StringBuilder builder, string text, int start, int length, int position, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                int at = position;
                while (at < length && IsLiteralFormatCharacter(text[start + at])) {
                    builder.Append(text[start + at]);
                    at++;
                }
                return at - position;
            }, out task);
        }

        private static bool LooksLongFormatLiteral(string text, int start, int position) {
            for (int at = 0; at < 1024; at++) {
                if (!IsLiteralFormatCharacter(text[start + position + at])) { return false; }
            }
            return true;
        }

        private static bool IsLiteralFormatCharacter(char value) {
            switch (value) {
                case '"': case '%': case '\'': case '/': case ':': case 'F': case 'H': case 'K': case 'M': case '\\':
                case 'd': case 'f': case 'g': case 'h': case 'm': case 's': case 't': case 'y': case 'z': return false;
                default: return true;
            }
        }

        private static void Queue(StringBuilder builder, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => builder.ToString(), out task);
        }

        private static bool Exact(MethodBase target, MethodInfo prefix, MethodInfo postfix, MethodInfo transpiler, MethodInfo finalizer) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(target);
            return info != null && info.Prefixes.Count == (prefix == null ? 0 : 1)
                && info.Postfixes.Count == (postfix == null ? 0 : 1)
                && info.Transpilers.Count == (transpiler == null ? 0 : 1)
                && info.Finalizers.Count == (finalizer == null ? 0 : 1)
                && (prefix == null || info.Prefixes[0].PatchMethod == prefix)
                && (postfix == null || info.Postfixes[0].PatchMethod == postfix)
                && (transpiler == null || info.Transpilers[0].PatchMethod == transpiler)
                && (finalizer == null || info.Finalizers[0].PatchMethod == finalizer);
        }

        private static bool IsUnchanged() {
            if (!Exact(AcquireTarget, ApiJsonDateBuilderAcquirePatch.PrefixMethod, null, ApiJsonDateBuilderAcquirePatch.TranspilerMethod, null)
                || !Exact(ReleaseTarget, ApiJsonDateBuilderReleasePatch.PrefixMethod, ApiJsonDateBuilderReleasePatch.PostfixMethod, null, null)
                || !Exact(StringTarget, ApiJsonDateBuilderStringPatch.PrefixMethod, null, null, null)
                || !Exact(BufferTarget, ApiJsonDateBuilderBufferPatch.PrefixMethod, null, null, null)
                || !Exact(ApiJsonDateRepeatPreparation.FormatTarget, ApiJsonDateFormatScopePatch.PrefixMethod, null, null, ApiJsonDateFormatScopePatch.FinalizerMethod)
                || !Exact(ApiJsonDateRepeatPreparation.RepeatTarget, ApiJsonDateRepeatPatch.PrefixMethod, null, null, null)
                || !Exact(ApiJsonDateRepeatPreparation.ExpandedTarget, null, ApiJsonDateExpandedRootPatch.PostfixMethod, null, null)
                || !Exact(ApiJsonDateRepeatPreparation.GenitiveTarget, ApiJsonDateGenitivePatch.PrefixMethod, null, null, null)
                || !Exact(ApiJsonDateLiteralPreparation.Target, ApiJsonDateLiteralPatch.PrefixMethod, null, null, null)
                || !Exact(FormatterTarget, null, null, ApiJsonDateBuilderFormatterPatch.TranspilerMethod, null)) { return false; }
            foreach (MethodInfo method in DateMethods) {
                if (method != ApiJsonDateRepeatPreparation.FormatTarget && method != ApiJsonDateRepeatPreparation.RepeatTarget
                    && method != ApiJsonDateRepeatPreparation.ExpandedTarget && method != ApiJsonDateLiteralPreparation.Target && method != FormatterTarget
                    && method != ApiJsonDateRepeatPreparation.GenitiveTarget
                    && Harmony.GetPatchInfo(method) != null) { return false; }
            }
            foreach (MethodBase method in Guards) {
                if (method != BufferTarget && Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return ApiJsonOwnedOutputPreparation.CanUseOriginalBuilder();
        }

        private static ReleaseBuilder Initialize() {
            try {
                if (IntPtr.Size != 8 || typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Assembly core = typeof(char).Assembly;
                Type cache = core.GetType("System.Text.StringBuilderCache");
                Type date = core.GetType("System.DateTimeFormat");
                if (cache == null || date == null) { return null; }
                AcquireTarget = AccessTools.Method(cache, "Acquire", new[] { typeof(int) });
                ReleaseTarget = AccessTools.Method(cache, "Release", new[] { typeof(StringBuilder) });
                StringTarget = AccessTools.Method(cache, "GetStringAndRelease", new[] { typeof(StringBuilder) });
                BufferTarget = AccessTools.PropertyGetter(typeof(StringBuilder), "RemainingCurrentChunk");
                MethodInfo customized = AccessTools.Method(date, "FormatCustomized", new[] { typeof(DateTime), typeof(CoreCharSpan), typeof(System.Globalization.DateTimeFormatInfo), typeof(TimeSpan), typeof(StringBuilder) });
                FormatterTarget = customized;
                CachedField = AccessTools.Field(cache, "t_cachedInstance");
                Constructor = typeof(StringBuilder).GetConstructor(new[] { typeof(int) });
                if (Constructor == null
                    || CachedField == null || CachedField.FieldType != typeof(StringBuilder) || !CachedField.IsStatic
                    || !CachedField.IsDefined(typeof(ThreadStaticAttribute), false)
                    || !Matches(AcquireTarget, 48, "23dc66369232639fc9ac2ef31ee62d07d4399275b11fb76e7ecde0f05c32f254")
                    || !Matches(ReleaseTarget, 20, "e3deac9fc6f1fc1179028038c7c643b700cfe2e0a14850334fbe33edd5015742")
                    || !Matches(StringTarget, 13, "0e3b70cc753a9f635c0050a462b2770b08a012af977e45625008bd3a48c9d1af")
                    || !Matches(BufferTarget, 33, "e39ab25d207d5cdaf9809f34bc473746326631b534977db650ca677170757acb")
                    || !Matches(customized, 1707, "759faa2aa86c7ce2cf68d9bf3223679d3cf26203e3fbb355d150558b017818bf")) { return null; }
                var guards = new List<MethodBase> { AccessTools.PropertyGetter(typeof(ApiJsonNumericPreparation), "IsActive") };
                AddDeclaredGuards(guards, typeof(StringBuilder));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderOutputPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonOwnedOutputPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderAcquirePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderReleasePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderStringPatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderBufferPatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateBuilderFormatterPatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateRepeatPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonDateFormatScopePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateGenitivePatch));
                AddDeclaredGuards(guards, typeof(ApiJsonDateLiteralPreparation));
                AddDeclaredGuards(guards, typeof(ReplayStorageService));
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Guards = guards.ToArray();
                DateMethods = date.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var release = new DynamicMethod("ReleaseOwnedDateBuilder", typeof(void), new[] { typeof(StringBuilder) }, typeof(ApiJsonDateBuilderOutputPreparation), true);
                ILGenerator il = release.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, ReleaseTarget); il.Emit(OpCodes.Ret);
                var acquire = new DynamicMethod("AcquireTrackedDateBuilder", typeof(StringBuilder), new[] { typeof(int) }, typeof(ApiJsonDateBuilderOutputPreparation), true);
                ILGenerator acquireIl = acquire.GetILGenerator();
                acquireIl.Emit(OpCodes.Ldarg_0); acquireIl.Emit(OpCodes.Call, AcquireTarget); acquireIl.Emit(OpCodes.Ret);
                Acquire = (AcquireBuilder)acquire.CreateDelegate(typeof(AcquireBuilder));
                return (ReleaseBuilder)release.CreateDelegate(typeof(ReleaseBuilder));
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(flags)) {
                guards.Add(method);
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1) { guards.Add(method.MakeGenericMethod(typeof(string))); }
            }
            guards.AddRange(type.GetConstructors(flags));
            if (type.TypeInitializer != null) { guards.Add(type.TypeInitializer); }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                AddDeclaredGuards(guards, nested);
                if (nested.IsGenericTypeDefinition && nested.GetGenericArguments().Length == 1) { AddDeclaredGuards(guards, nested.MakeGenericType(typeof(string))); }
            }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 21 || code[19].opcode != OpCodes.Newobj || !Equals(code[19].operand, Constructor)) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            code.Insert(20, new CodeInstruction(OpCodes.Dup));
            code.Insert(21, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "Record")));
            Rewritten = true;
            return code;
        }

        internal static IEnumerable<CodeInstruction> RewriteFormatter(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            FormatterRewritten = false;
            if (!Supported || code.Count != 666 || code[10].opcode != OpCodes.Call || !Equals(code[10].operand, AcquireTarget)) { return code; }
            MethodInfo append = AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(string) });
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "AppendForFormatter");
            int[] sites = { 131, 225, 263, 342, 379, 419, 444, 452, 549, 577, 585 };
            if (append == null || wrapper == null) { return code; }
            foreach (int at in sites) {
                if (code[at].opcode != OpCodes.Callvirt || !Equals(code[at].operand, append)) { return code; }
            }
            MethodInfo appendCharacter = AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(char) });
            MethodInfo literal = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "TryAppendFormatLiteral");
            if (generator == null || literal == null || code[650].opcode != OpCodes.Ldarg_S || code[38].opcode != OpCodes.Ldloc_S
                || code[652].opcode != OpCodes.Callvirt || !Equals(code[652].operand, appendCharacter)
                || code[654].opcode != OpCodes.Ldc_I4_1 || code[655].opcode != OpCodes.Stloc_S
                || code[656].opcode != OpCodes.Ldloc_S || code[650].blocks.Count != 0 || code[656].blocks.Count != 0) { return code; }
            Label fallback = generator.DefineLabel();
            Label joined = generator.DefineLabel();
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(code[38].opcode, code[38].operand),
                new CodeInstruction(code[650].opcode, code[650].operand),
                new CodeInstruction(OpCodes.Call, literal),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(code[655].opcode, code[655].operand),
                new CodeInstruction(OpCodes.Br, joined),
                new CodeInstruction(OpCodes.Pop)
            };
            prefix[0].labels.AddRange(code[650].labels);
            code[650].labels.Clear();
            prefix[8].labels.Add(fallback);
            code[656].labels.Add(joined);
            code[10].operand = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "AcquireForFormatter");
            foreach (int at in sites) {
                code[at].opcode = OpCodes.Call;
                code[at].operand = wrapper;
            }
            code.InsertRange(650, prefix);
            FormatterRewritten = true;
            return code;
        }

        private static bool Matches(MethodInfo method, int length, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != length) { return false; }
            using (SHA256 hash = SHA256.Create()) { return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBuilderAcquirePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateBuilderAcquirePatch), "Prefix");
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateBuilderAcquirePatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateBuilderOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBuilderOutputPreparation.AcquireTarget;
        private static void Prefix() => ApiJsonDateBuilderOutputPreparation.BeforeAcquire();
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonDateBuilderOutputPreparation.Rewrite(instructions);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBuilderReleasePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateBuilderReleasePatch), "Prefix");
        internal static readonly MethodInfo PostfixMethod = AccessTools.Method(typeof(ApiJsonDateBuilderReleasePatch), "Postfix");
        private static bool Prepare() => ApiJsonDateBuilderOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBuilderOutputPreparation.ReleaseTarget;
        private static void Prefix(StringBuilder __0, out ApiJsonDateBuilderOutputPreparation.ReleaseState __state)
            => __state = ApiJsonDateBuilderOutputPreparation.BeforeRelease(__0);
        private static void Postfix(ApiJsonDateBuilderOutputPreparation.ReleaseState __state)
            => ApiJsonDateBuilderOutputPreparation.AfterRelease(__state);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBuilderFormatterPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateBuilderFormatterPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateBuilderOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBuilderOutputPreparation.FormatterTarget;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) => ApiJsonDateBuilderOutputPreparation.RewriteFormatter(instructions, generator);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBuilderBufferPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateBuilderBufferPatch), "Prefix");
        private static bool Prepare() => ApiJsonDateBuilderOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBuilderOutputPreparation.BufferTarget;
        private static void Prefix(StringBuilder __instance) => ApiJsonDateBuilderOutputPreparation.Revoke(__instance);
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBuilderStringPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonDateBuilderStringPatch), "Prefix");
        private static bool Prepare() => ApiJsonDateBuilderOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBuilderOutputPreparation.StringTarget;
        private static bool Prefix(StringBuilder __0, ref string __result) => !ApiJsonDateBuilderOutputPreparation.TryPrepare(__0, out __result);
    }
}
