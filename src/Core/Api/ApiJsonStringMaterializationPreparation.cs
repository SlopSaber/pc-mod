using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonStringMaterializationPreparation {
        internal static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.Utilities.StringReference", "ToString", Type.EmptyTypes,
            24, "01d132922a1a0e3739211da26cb6a345c95435618f455ce2758c48d0c81895bc");
        internal static readonly ConstructorInfo Constructor = typeof(string).GetConstructor(
            new[] { typeof(char[]), typeof(int), typeof(int) });
        private static readonly FieldInfo ReaderChars = AccessTools.Field(typeof(JsonTextReader), "_chars");
        private static readonly FieldInfo ReaderBuffer = AccessTools.Field(typeof(JsonTextReader), "_stringBuffer");
        private static readonly FieldInfo BufferChars = ReaderBuffer == null ? null : AccessTools.Field(ReaderBuffer.FieldType, "_buffer");
        private static readonly MethodInfo PoolGetter = typeof(JsonTextReader).GetProperty("ArrayPool")?.GetGetMethod();
        private static readonly MethodInfo RentPostfix = AccessTools.Method(typeof(ApiJsonBufferRentPatch), "Postfix");
        private static readonly MethodInfo ReturnPrefix = AccessTools.Method(typeof(ApiJsonBufferReturnPatch), "Prefix");
        private static readonly MethodBase[] ReferenceMethods = FindReferenceMethods();
        private static readonly MethodBase[] ConstructorMethods = FindConstructorMethods();

        internal static bool Supported => Target != null && Constructor != null
            && ReaderChars?.FieldType == typeof(char[]) && BufferChars?.FieldType == typeof(char[])
            && ReferenceMethods != null && ConstructorMethods != null && PoolGetter != null
            && ApiJsonBufferGrowthPreparation.Supported
            && typeof(string).Module.ModuleVersionId == new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790");

        internal static string Create(char[] chars, int start, int length) {
            if (length >= 524288 && chars != null && start >= 0 && length <= chars.Length
                && start <= chars.Length - length && !Thread.CurrentThread.IsThreadPoolThread
                && CanLease(chars)) {
                Task<string> task = null;
                try { Queue(chars, start, length, out task); }
                catch { }
                if (task != null) {
                    while (!task.IsCompleted) {
                        try { task.Wait(); }
                        catch (ThreadInterruptedException) { }
                        catch (AggregateException) { }
                    }
                    return task.GetAwaiter().GetResult();
                }
            }
            return new string(chars, start, length);
        }

        private static bool CanLease(char[] chars) {
            try {
                JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
                if (!Supported || reader == null || Harmony.GetPatchInfo(PoolGetter) != null
                    || reader.ArrayPool != null || !ApiJsonBufferGrowthPreparation.IsPrivateBuffer(chars)
                    || !HasOnlyPatch(ApiJsonBufferGrowthPreparation.Rent, RentPostfix, false)
                    || !HasOnlyPatch(ApiJsonBufferGrowthPreparation.Return, ReturnPrefix, true)
                    || !ApiJsonStringMaterializationPatch.Rewritten
                    || !HasOnlyTranspiler(Target, ApiJsonStringMaterializationPatch.TranspilerMethod)) {
                    return false;
                }
                foreach (MethodBase method in ReferenceMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                foreach (MethodBase method in ConstructorMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                return ReferenceEquals(ReaderChars.GetValue(reader), chars)
                    || ReferenceEquals(BufferChars.GetValue(ReaderBuffer.GetValue(reader)), chars);
            } catch { return false; }
        }

        private static void Queue(char[] chars, int start, int length, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => new string(chars, start, length), out task);
        }

        private static bool HasOnlyPatch(MethodBase method, MethodInfo patch, bool prefix) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return patch != null && info != null && info.Transpilers.Count == 0 && info.Finalizers.Count == 0
                && (prefix ? info.Postfixes.Count == 0 && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == patch
                    : info.Prefixes.Count == 0 && info.Postfixes.Count == 1 && info.Postfixes[0].PatchMethod == patch);
        }

        private static bool HasOnlyTranspiler(MethodBase method, MethodInfo patch) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Count == 0 && info.Postfixes.Count == 0 && info.Finalizers.Count == 0
                && info.Transpilers.Count == 1 && info.Transpilers[0].PatchMethod == patch;
        }

        private static MethodBase[] FindReferenceMethods() {
            try {
                if (Target == null) { return null; }
                var methods = new List<MethodBase>();
                foreach (MethodInfo method in Target.DeclaringType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                    if (method != Target) { methods.Add(method); }
                }
                methods.AddRange(Target.DeclaringType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
                return methods.ToArray();
            } catch { return null; }
        }

        private static MethodBase[] FindConstructorMethods() {
            try {
                Type bytes = typeof(byte).MakePointerType();
                Type chars = typeof(char).MakePointerType();
                MethodBase[] methods = {
                    Constructor,
                    AccessTools.Method(typeof(string), "Ctor", new[] { typeof(char[]), typeof(int), typeof(int) }),
                    AccessTools.Method(typeof(string), "FastAllocateString", new[] { typeof(int) }),
                    AccessTools.Method(typeof(string), "wstrcpy", new[] { chars, chars, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "Memmove", new[] { bytes, bytes, typeof(uint) }),
                    AccessTools.Method(typeof(Buffer), "Memcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(typeof(Interlocked), "Read", new[] { typeof(long).MakeByRefType() })
                };
                foreach (MethodBase method in methods) {
                    if (method == null) { return null; }
                }
                return methods;
            } catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonStringMaterializationPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonStringMaterializationPatch), "Transpiler");
        internal static volatile bool Rewritten;
        private static bool Prepare() => ApiJsonStringMaterializationPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonStringMaterializationPreparation.Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            if (code.Count != 8 || code[6].opcode != OpCodes.Newobj
                || !Equals(code[6].operand, ApiJsonStringMaterializationPreparation.Constructor)
                || code[6].labels.Count != 0 || code[6].blocks.Count != 0) {
                return code;
            }
            code[6].opcode = OpCodes.Call;
            code[6].operand = AccessTools.Method(typeof(ApiJsonStringMaterializationPreparation), "Create");
            Rewritten = true;
            return code;
        }
    }
}
