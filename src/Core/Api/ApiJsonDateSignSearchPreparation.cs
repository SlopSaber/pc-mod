using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateSignSearchPreparation {
        private static readonly ConditionalWeakTable<char[], Receipt> Fresh = new ConditionalWeakTable<char[], Receipt>();
        private static readonly Receipt PrivateArray = new Receipt();
        internal static readonly MethodInfo Target = FindTarget();
        internal static readonly MethodInfo Search = FindGeneric("IndexOf", true);
        private static readonly Type ComparerType = typeof(char).Assembly.GetType("System.Collections.Generic.GenericEqualityComparer`1")?.MakeGenericType(typeof(char));
        private static readonly MethodBase[] KernelMethods = FindKernelMethods();
        private static readonly MethodBase[] ReferenceMethods = FindReferenceMethods();
        private static readonly FieldInfo ReaderChars = AccessTools.Field(typeof(JsonTextReader), "_chars");
        private static readonly FieldInfo ReaderBuffer = AccessTools.Field(typeof(JsonTextReader), "_stringBuffer");
        private static readonly FieldInfo BufferChars = ReaderBuffer == null ? null : AccessTools.Field(ReaderBuffer.FieldType, "_buffer");
        private static readonly MethodInfo PoolGetter = typeof(JsonTextReader).GetProperty("ArrayPool")?.GetGetMethod();
        private static readonly MethodInfo RentPostfix = AccessTools.Method(typeof(ApiJsonBufferRentPatch), "Postfix");
        private static readonly MethodInfo ReturnPrefix = AccessTools.Method(typeof(ApiJsonBufferReturnPatch), "Prefix");

        internal static bool Supported => Target != null && Search != null && ComparerType != null
            && KernelMethods != null && ReferenceMethods != null && ReaderChars?.FieldType == typeof(char[])
            && BufferChars?.FieldType == typeof(char[]) && PoolGetter != null && ApiJsonBufferGrowthPreparation.Supported;

        internal static void Record(char[] chars) {
            try {
                if (chars != null && ApiJsonDateBufferPreparation.CanPrepare() && IsUnchanged()) {
                    Fresh.Remove(chars);
                    Fresh.Add(chars, PrivateArray);
                }
            } catch { }
        }

        internal static int IndexOf(char[] chars, char value, int start, int count) {
            bool leased = chars != null && ApiJsonNumericPreparation.IsActive && CanLease(chars);
            if (chars != null && count >= 524288 && start >= 0 && count <= chars.Length && start <= chars.Length - count
                && !Thread.CurrentThread.IsThreadPoolThread && leased) {
                Task<int> task = null;
                try { Queue(chars, value, start, count, out task); }
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
            return Array.IndexOf(chars, value, start, count);
        }

        private static bool CanLease(char[] chars) {
            try {
                if (!IsUnchanged()) { return Reject(chars); }
                if (Fresh.TryGetValue(chars, out _)) { return ApiJsonDateBufferPreparation.CanPrepare() || Reject(chars); }
                JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
                if (reader == null || Harmony.GetPatchInfo(PoolGetter) != null || reader.ArrayPool != null
                    || !ApiJsonBufferGrowthPreparation.IsPrivateBuffer(chars)
                    || !HasOnlyPatch(ApiJsonBufferGrowthPreparation.Rent, RentPostfix, false)
                    || !HasOnlyPatch(ApiJsonBufferGrowthPreparation.Return, ReturnPrefix, true)) { return Reject(chars); }
                return ReferenceEquals(ReaderChars.GetValue(reader), chars)
                    || ReferenceEquals(BufferChars.GetValue(ReaderBuffer.GetValue(reader)), chars) || Reject(chars);
            } catch { return Reject(chars); }
        }

        private static bool Reject(char[] chars) {
            try {
                Fresh.Remove(chars);
                ApiJsonBufferGrowthPreparation.Revoke(chars);
            } catch { }
            return false;
        }

        private static bool IsUnchanged() {
            try {
                if (!ApiJsonNumericPreparation.IsActive || !Supported || !ApiJsonDateSignSearchPatch.Rewritten) { return false; }
                HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
                if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                    || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonDateSignSearchPatch.TranspilerMethod) { return false; }
                foreach (MethodBase method in ReferenceMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                foreach (MethodBase method in KernelMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                return EqualityComparer<char>.Default.GetType() == ComparerType;
            } catch { return false; }
        }

        private static void Queue(char[] chars, char value, int start, int count, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Array.IndexOf(chars, value, start, count), out task);
        }

        private static bool HasOnlyPatch(MethodBase method, MethodInfo patch, bool prefix) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return patch != null && info != null && info.Transpilers.Count == 0 && info.Finalizers.Count == 0
                && (prefix ? info.Postfixes.Count == 0 && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == patch
                    : info.Prefixes.Count == 0 && info.Postfixes.Count == 1 && info.Postfixes[0].PatchMethod == patch);
        }

        private static MethodInfo FindTarget() {
            try {
                Assembly assembly = typeof(JsonReader).Assembly;
                if (assembly.ManifestModule.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) { return null; }
                Type reference = assembly.GetType("Newtonsoft.Json.Utilities.StringReference");
                Type extensions = assembly.GetType("Newtonsoft.Json.Utilities.StringReferenceExtensions");
                if (reference == null || extensions == null) { return null; }
                MethodInfo method = extensions.GetMethod("IndexOf", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { reference, typeof(char), typeof(int), typeof(int) }, null);
                return method?.ReturnType == typeof(int) && method.GetMethodBody()?.GetILAsByteArray()?.Length == 40 ? method : null;
            } catch { return null; }
        }

        private static MethodInfo FindGeneric(string name, bool closed) {
            try {
                if (typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                foreach (MethodInfo method in typeof(Array).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == name && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1
                        && method.GetParameters().Length == 4) { return closed ? method.MakeGenericMethod(typeof(char)) : method; }
                }
            } catch { }
            return null;
        }

        private static MethodBase[] FindKernelMethods() {
            try {
                if (ComparerType == null || Search == null) { return null; }
                var methods = new List<MethodBase> { Search, FindGeneric("IndexOf", false), FindGeneric("IndexOfImpl", true), FindGeneric("IndexOfImpl", false) };
                Type[] types = { typeof(EqualityComparer<char>), typeof(EqualityComparer<>), ComparerType, ComparerType.GetGenericTypeDefinition() };
                foreach (Type type in types) {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                        if (method.Name == "get_Default" || method.Name == "CreateComparer" || method.Name == "IndexOf" || method.Name == "Equals") {
                            methods.Add(method);
                        }
                    }
                    methods.AddRange(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                }
                methods.Add(typeof(char).GetMethod("Equals", new[] { typeof(char) }));
                foreach (MethodBase method in methods) { if (method == null) { return null; } }
                return methods.ToArray();
            } catch { return null; }
        }

        private static MethodBase[] FindReferenceMethods() {
            try {
                if (Target == null) { return null; }
                Type reference = Target.GetParameters()[0].ParameterType;
                var methods = new List<MethodBase>();
                methods.AddRange(reference.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                foreach (PropertyInfo property in reference.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    MethodInfo getter = property.GetGetMethod(true);
                    if (getter != null) { methods.Add(getter); }
                }
                foreach (MethodInfo method in Target.DeclaringType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method != Target) { methods.Add(method); }
                }
                Type dates = typeof(JsonReader).Assembly.GetType("Newtonsoft.Json.Utilities.DateTimeUtils");
                if (dates == null) { return null; }
                foreach (MethodInfo method in dates.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "TryParseMicrosoftDate" || method.Name == "TryParseDateTimeMicrosoft"
                        || method.Name == "TryParseDateTimeOffsetMicrosoft"
                        || ((method.Name == "TryParseDateTime" || method.Name == "TryParseDateTimeOffset")
                            && method.GetParameters()[0].ParameterType == reference)) { methods.Add(method); }
                }
                return methods.ToArray();
            } catch { return null; }
        }

        private sealed class Receipt { }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateSignSearchPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateSignSearchPatch), "Transpiler");
        internal static volatile bool Rewritten;
        private static bool Prepare() => ApiJsonDateSignSearchPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateSignSearchPreparation.Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            if (code.Count != 20 || code[8].opcode != OpCodes.Call
                || !Equals(code[8].operand, ApiJsonDateSignSearchPreparation.Search)
                || code[8].labels.Count != 0 || code[8].blocks.Count != 0) { return code; }
            MethodInfo replacement = AccessTools.Method(typeof(ApiJsonDateSignSearchPreparation), "IndexOf");
            if (replacement == null) { return code; }
            code[8].operand = replacement;
            Rewritten = true;
            return code;
        }
    }
}
