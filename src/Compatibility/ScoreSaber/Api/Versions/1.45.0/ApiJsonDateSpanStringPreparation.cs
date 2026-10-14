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
    internal static class ApiJsonDateSpanStringPreparation {
        private delegate string Allocate(CoreCharSpan span);
        internal static readonly MethodInfo[] Targets = new MethodInfo[2];
        internal static readonly bool[] Rewritten = new bool[2];
        private static ConstructorInfo Constructor;
        private static MethodInfo SpanLength;
        private static MethodBase[] Guards;
        private static readonly Allocate Allocator = Initialize();
        internal static bool Supported => Allocator != null && Guards != null && ApiJsonDateLexicalPreparation.Supported;

        internal static bool CanReadImmutableSpan() {
            if (!Supported) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static string Prepare(CoreCharSpan span, string candidate, int mode) {
            string root = null;
            int start = 0;
            int length = 0;
            try {
                if (ApiJsonNumericPreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread && Supported
                    && mode >= 0 && mode < 2 && Harmony.GetPatchInfo(SpanLength) == null) {
                    length = span.Length;
                    if (length >= 524288 && IsUnchanged(mode)
                        && ApiJsonDateLexicalPreparation.GetCandidates(out var text, out var format)) {
                        if (TryMatch(span, candidate, out start)) { root = candidate; }
                        else if (TryMatch(span, text, out start)) { root = text; }
                        else if (TryMatch(span, format, out start)) { root = format; }
                    }
                }
            } catch { }
            Task<string> task = null;
            if (root != null) {
                try { Queue(root, start, length, out task); }
                catch { }
            }
            if (task == null) { return Allocator(span); }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            return task.GetAwaiter().GetResult();
        }

        private static bool TryMatch(CoreCharSpan span, string text, out int start) {
            start = 0;
            if (text == null) { return false; }
            CoreCharSpan whole = ScoreSaberCore::System.MemoryExtensions.AsSpan(text);
            return ScoreSaberCore::System.MemoryExtensions.Overlaps(whole, span, out start)
                && start >= 0 && span.Length <= whole.Length - start;
        }

        private static bool IsUnchanged(int mode) {
            if (!Rewritten[mode]) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Targets[mode]);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateSpanStringPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static void Queue(string root, int start, int length, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Allocator(ScoreSaberCore::System.MemoryExtensions.AsSpan(root).Slice(start, length)), out task);
        }

        private static Allocate Initialize() {
            try {
                Assembly core = typeof(char).Assembly;
                if (IntPtr.Size != 8 || typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type extensions = core.GetType("System.MemoryExtensions");
                Type unsafeType = core.GetType("System.Runtime.CompilerServices.Unsafe");
                Type helper = core.GetType("System.ThrowHelper");
                if (extensions == null || unsafeType == null || helper == null) { return null; }
                Constructor = typeof(string).GetConstructor(new[] { typeof(CoreCharSpan) });
                SpanLength = typeof(CoreCharSpan).GetProperty("Length")?.GetGetMethod();
                MethodInfo ctor = AccessTools.Method(typeof(string), "Ctor", new[] { typeof(CoreCharSpan) });
                MethodInfo create = AccessTools.Method(typeof(string), "CreateString", new[] { typeof(CoreCharSpan) });
                MethodInfo overlap = FindGeneric(extensions, "Overlaps", new[] { typeof(char) },
                    new[] { typeof(CoreCharSpan), typeof(CoreCharSpan), typeof(int).MakeByRefType() });
                Targets[0] = AccessTools.Method(typeof(CompareInfo), "CompareString", new[] { typeof(CoreCharSpan), typeof(string), typeof(CompareOptions) });
                Targets[1] = AccessTools.Method(typeof(CompareInfo), "CompareString", new[] { typeof(CoreCharSpan), typeof(CoreCharSpan), typeof(CompareOptions) });
                if (Constructor == null || (Constructor.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0
                    || !Matches(ctor, 66, "ff558a0b4e128788d529cef9183cbbfb1d7f2776a9c45bef0bd3f28a2385bdb6")
                    || !Matches(create, 7, "939baa60330fc2c9add12a1df0a7bb17733971e2f45a6c1ed00be2e6ea9e3987")
                    || overlap == null || !Matches(overlap.GetGenericMethodDefinition(), 222, "8150e4697250fe4d00cae2d6248cec68857ed7ec64538c1db69b191891860525")
                    || !Matches(Targets[0], 31, "9a3f764c336d2bd464a60fb6e51cc19bb9e0798235fb6770ab56fe3b204e3a96")
                    || !Matches(Targets[1], 48, "e4cd9749e6f01c85d3399ba8ce21e5b9649633e3834065dd268c9e4040754c1a")) { return null; }
                Type bytes = typeof(byte).MakePointerType();
                Type chars = typeof(char).MakePointerType();
                var guards = new List<MethodBase> {
                    Constructor, ctor, create, overlap, overlap.GetGenericMethodDefinition(),
                    typeof(CoreCharSpan).GetProperty("IsEmpty")?.GetGetMethod(),
                    SpanLength,
                    typeof(CoreCharSpan).GetMethod("Slice", new[] { typeof(int), typeof(int) }),
                    AccessTools.Method(typeof(string), "FastAllocateString", new[] { typeof(int) }),
                    AccessTools.Method(typeof(string), "wstrcpy", new[] { chars, chars, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "Memmove", new[] { bytes, bytes, typeof(uint) }),
                    AccessTools.Method(typeof(Buffer), "Memcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(helper, "ThrowArgumentException_OverlapAlignmentMismatch", Type.EmptyTypes)
                };
                MethodInfo offset = FindGeneric(unsafeType, "ByteOffset", new[] { typeof(char) },
                    new[] { typeof(char).MakeByRefType(), typeof(char).MakeByRefType() });
                MethodInfo charSize = FindGeneric(unsafeType, "SizeOf", new[] { typeof(char) }, Type.EmptyTypes);
                MethodInfo pointerSize = FindGeneric(unsafeType, "SizeOf", new[] { typeof(IntPtr) }, Type.EmptyTypes);
                foreach (MethodInfo method in new[] { offset, charSize, pointerSize }) {
                    if (method == null) { return null; }
                    guards.Add(method); guards.Add(method.GetGenericMethodDefinition());
                }
                bool foundMove = false;
                foreach (MethodInfo method in typeof(Buffer).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "Memmove" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1
                        && method.GetParameters().Length == 3) {
                        guards.Add(method); guards.Add(method.MakeGenericMethod(typeof(char))); foundMove = true;
                    }
                }
                if (!foundMove) { return null; }
                guards.AddRange(typeof(IntPtr).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                var allocator = new DynamicMethod("AllocateImmutableDateSlice", typeof(string), new[] { typeof(CoreCharSpan) }, typeof(ApiJsonDateSpanStringPreparation), true);
                ILGenerator il = allocator.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Newobj, Constructor); il.Emit(OpCodes.Ret);
                Guards = guards.ToArray();
                return (Allocate)allocator.CreateDelegate(typeof(Allocate));
            } catch { return null; }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original) {
            var code = new List<CodeInstruction>(instructions);
            int mode = Array.IndexOf(Targets, original);
            if (mode < 0) { return code; }
            Rewritten[mode] = false;
            if (!Supported || code.Count != (mode == 0 ? 15 : 20)) { return code; }
            int[] positions = mode == 0 ? new[] { 1 } : new[] { 1, 4, 13 };
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            foreach (int at in positions) {
                if (code[at].opcode != OpCodes.Newobj || !Equals(code[at].operand, Constructor)) { return code; }
            }
            var rewritten = new List<CodeInstruction>();
            for (int at = 0; at < code.Count; at++) {
                if (Array.IndexOf(positions, at) >= 0) {
                    var candidate = new CodeInstruction(mode == 0 ? OpCodes.Ldarg_2 : at == 13 ? OpCodes.Ldloc_1 : OpCodes.Ldnull);
                    candidate.labels.AddRange(code[at].labels); code[at].labels.Clear();
                    rewritten.Add(candidate);
                    rewritten.Add(new CodeInstruction(mode == 0 ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1));
                    code[at].opcode = OpCodes.Call;
                    code[at].operand = AccessTools.Method(typeof(ApiJsonDateSpanStringPreparation), "Prepare");
                }
                rewritten.Add(code[at]);
            }
            Rewritten[mode] = true;
            return rewritten;
        }

        private static MethodInfo FindGeneric(Type type, string name, Type[] arguments, Type[] parameters) {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (method.Name != name || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != arguments.Length) { continue; }
                MethodInfo closed = method.MakeGenericMethod(arguments);
                ParameterInfo[] actual = closed.GetParameters();
                if (actual.Length != parameters.Length) { continue; }
                bool match = true;
                for (int at = 0; at < actual.Length; at++) { if (actual[at].ParameterType != parameters[at]) { match = false; } }
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
    }

    [HarmonyPatch]
    internal static class ApiJsonDateSpanStringPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateSpanStringPatch), "Transpiler");
        private static bool Prepare() => ApiJsonDateSpanStringPreparation.Supported;
        private static IEnumerable<MethodBase> TargetMethods() => ApiJsonDateSpanStringPreparation.Targets;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            => ApiJsonDateSpanStringPreparation.Rewrite(instructions, __originalMethod);
    }
}
