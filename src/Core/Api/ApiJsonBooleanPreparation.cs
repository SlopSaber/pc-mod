using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonBooleanPreparation {
        private delegate bool RangeParser(string text, int start, int length, out bool value);
        internal static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.JsonReader", "ReadBooleanString", new[] { typeof(string) },
            93, "f17349e8e881737c12a64815a144255a9fd7822e1caad3779aa95b89d065bb54");
        internal static readonly MethodInfo Original = AccessTools.Method(typeof(bool), nameof(bool.TryParse),
            new[] { typeof(string), typeof(bool).MakeByRefType() });
        private static readonly MethodInfo LengthGetter = typeof(string).GetProperty("Length")?.GetGetMethod();
        private static readonly MethodInfo CharGetter = typeof(string).GetProperty("Chars")?.GetGetMethod();
        private static MethodBase[] _kernelMethods;
        private static readonly RangeParser Parser = Initialize();

        internal static bool Supported => Target != null && Parser != null && ApiJsonUnicodeCategories.Supported;

        internal static bool TryParse(string text, out bool value) {
            if (text != null && ApiJsonNumericPreparation.CurrentReader != null
                && !Thread.CurrentThread.IsThreadPoolThread && Parser != null
                && Harmony.GetPatchInfo(LengthGetter) == null && Harmony.GetPatchInfo(CharGetter) == null
                && text.Length >= 524288 && CanPrepare() && HasPadding(text)) {
                Task<TrimRange> task = null;
                try { Queue(text, out task); }
                catch { }
                if (task != null) {
                    while (!task.IsCompleted) {
                        try { task.Wait(); }
                        catch (ThreadInterruptedException) { }
                        catch (AggregateException) { }
                    }
                    try {
                        TrimRange range = task.GetAwaiter().GetResult();
                        if (range.Length != 4 && range.Length != 5) {
                            value = false;
                            return false;
                        }
                        return Parser(text, range.Start, range.Length, out value);
                    } catch { }
                }
            }
            return bool.TryParse(text, out value);
        }

        private static bool CanPrepare() {
            try {
                if (!Supported || !ApiJsonBooleanPatch.Rewritten) { return false; }
                HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
                if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                    || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonBooleanPatch.TranspilerMethod) {
                    return false;
                }
                foreach (MethodBase method in _kernelMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                return true;
            } catch { return false; }
        }

        private static bool HasPadding(string text) {
            bool left = true;
            for (int i = 0; i < 1024; i++) {
                char value = text[i];
                if (value != '\0' && !ApiJsonUnicodeCategories.IsWhiteSpace(value)) { left = false; break; }
            }
            if (left) { return true; }
            for (int i = text.Length - 1; i >= text.Length - 1024; i--) {
                char value = text[i];
                if (value != '\0' && !ApiJsonUnicodeCategories.IsWhiteSpace(value)) { return false; }
            }
            return true;
        }

        private static void Queue(string text, out Task<TrimRange> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Trim(text), out task);
        }

        private static TrimRange Trim(string text) {
            int start = 0;
            while (start < text.Length) {
                char value = text[start];
                if (!ApiJsonUnicodeCategories.IsWhiteSpace(value) && value != '\0') { break; }
                start++;
            }
            int end = text.Length - 1;
            while (end >= start) {
                char value = text[end];
                if (!ApiJsonUnicodeCategories.IsWhiteSpace(value) && value != '\0') { break; }
                end--;
            }
            return new TrimRange(start, end - start + 1);
        }

        private static RangeParser Initialize() {
            try {
                Assembly core = typeof(bool).Assembly;
                if (typeof(bool).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type span = core.GetType("System.ReadOnlySpan`1")?.MakeGenericType(typeof(char));
                Type extensions = core.GetType("System.MemoryExtensions");
                if (span == null || extensions == null) { return null; }
                MethodInfo parse = AccessTools.Method(typeof(bool), nameof(bool.TryParse), new[] { span, typeof(bool).MakeByRefType() });
                MethodInfo trim = AccessTools.Method(typeof(bool), "TrimWhiteSpaceAndNull", new[] { span });
                MethodInfo equal = AccessTools.Method(extensions, "EqualsOrdinalIgnoreCase", new[] { span, span });
                MethodInfo range = AccessTools.Method(extensions, "AsSpan", new[] { typeof(string), typeof(int), typeof(int) });
                MethodInfo whole = AccessTools.Method(extensions, "AsSpan", new[] { typeof(string) });
                MethodInfo spanLength = span.GetProperty("Length")?.GetGetMethod();
                if (range == null || whole == null || spanLength == null
                    || !Matches(Original, 21, "cc8286ff0d6b8ad0c1999622658ca8c8a000468f08f0634dde3424deccf47b7e")
                    || !Matches(parse, 91, "0895886b9a05d183a36fa396d4e5da1ea5d83eabf0c7ddb6c332ef8c8d0de445")
                    || !Matches(trim, 106, "13a6a02994268ba6220cc2d03dce1eda15d5299e4443620b16836d18b7104217")
                    || !Matches(equal, 40, "abaa837897d948fccb76d84dd87299392591392ac561a3eff00cd2a3e99afcef")
                    || !Matches(LengthGetter, 7, "4e983afa6e94b8b9e9d31249af4a4def459aabbf91df59838ddb074a0f98b7dc")
                    || !Matches(CharGetter, 30, "9c79f192e9fd140272e02883da9d174ce63ef8cae7d3d29623361b083eb08e24")) {
                    return null;
                }
                var methods = new List<MethodBase> { Original, parse, trim, equal, range, whole, spanLength, LengthGetter, CharGetter };
                Type unicode = core.GetType("System.Globalization.CharUnicodeInfo");
                Type tables = typeof(ApiJsonUnicodeCategories).GetNestedType("Tables", BindingFlags.NonPublic);
                if (unicode == null || tables == null) { return null; }
                methods.AddRange(typeof(char).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                methods.AddRange(unicode.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                methods.Add(AccessTools.Method(typeof(ApiJsonUnicodeCategories), "IsWhiteSpace"));
                methods.Add(AccessTools.Method(typeof(ApiJsonUnicodeCategories), "get_Supported"));
                methods.Add(AccessTools.Method(tables, "Category"));
                if (typeof(ApiJsonUnicodeCategories).TypeInitializer != null) { methods.Add(typeof(ApiJsonUnicodeCategories).TypeInitializer); }
                if (typeof(char).TypeInitializer != null) { methods.Add(typeof(char).TypeInitializer); }
                if (unicode.TypeInitializer != null) { methods.Add(unicode.TypeInitializer); }
                foreach (MethodBase method in methods) { if (method == null) { return null; } }
                _kernelMethods = methods.ToArray();
                var bridge = new DynamicMethod("ParsePreparedApiBooleanRange", typeof(bool),
                    new[] { typeof(string), typeof(int), typeof(int), typeof(bool).MakeByRefType() }, typeof(ApiJsonBooleanPreparation), true);
                ILGenerator generator = bridge.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Call, range);
                generator.Emit(OpCodes.Ldarg_3);
                generator.Emit(OpCodes.Call, parse);
                generator.Emit(OpCodes.Ret);
                return (RangeParser)bridge.CreateDelegate(typeof(RangeParser));
            } catch { return null; }
        }

        private static bool Matches(MethodInfo method, int length, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != length) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }

        private readonly struct TrimRange {
            internal readonly int Start;
            internal readonly int Length;
            internal TrimRange(int start, int length) { Start = start; Length = length; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonBooleanPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonBooleanPatch), "Transpiler");
        internal static volatile bool Rewritten;
        private static bool Prepare() => ApiJsonBooleanPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonBooleanPreparation.Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            if (code.Count != 37 || code[14].opcode != OpCodes.Call
                || !Equals(code[14].operand, ApiJsonBooleanPreparation.Original)
                || code[14].labels.Count != 0 || code[14].blocks.Count != 0) {
                return code;
            }
            code[14].operand = AccessTools.Method(typeof(ApiJsonBooleanPreparation), "TryParse");
            Rewritten = true;
            return code;
        }
    }
}
