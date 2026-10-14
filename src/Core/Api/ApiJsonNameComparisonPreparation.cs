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
    internal static class ApiJsonNameComparisonPreparation {
        private delegate bool TextComparison(string text, char[] chars, int start, int length);
        internal static readonly MethodInfo Original = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.DefaultJsonNameTable", "TextEquals",
            new[] { typeof(string), typeof(char[]), typeof(int), typeof(int) },
            46, "9cc5ae347664d5098b8fc13861aa193062061795bbf0ee8ff66ac0492b5f6f07");
        private static readonly MethodInfo LengthGetter = typeof(string).GetProperty("Length")?.GetGetMethod();
        private static readonly MethodInfo CharGetter = typeof(string).GetProperty("Chars")?.GetGetMethod();
        private static readonly TextComparison Fallback = Initialize();

        internal static bool Supported => Fallback != null;

        internal static bool Compare(string text, char[] chars, int start, int length, DefaultJsonNameTable table) {
            JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
            if (text != null && length >= 524288 && chars != null && start >= 0
                && length <= chars.Length && start <= chars.Length - length && reader != null
                && !Thread.CurrentThread.IsThreadPoolThread && reader.ArrayPool == null
                && table.GetType() == typeof(DefaultJsonNameTable)
                && ApiJsonPropertyNamePreparation.IsCurrent(table, chars, start, length)
                && ApiJsonBufferGrowthPreparation.IsPrivateBuffer(chars)
                && Harmony.GetPatchInfo(LengthGetter) == null && Harmony.GetPatchInfo(CharGetter) == null
                && text.Length == length) {
                for (int i = 0; i < 1024; i++) {
                    if (text[i] != chars[start + i]) { return false; }
                }
                Task<bool> task = null;
                try { Queue(text, chars, start, length, out task); }
                catch { }
                if (task != null) {
                    while (!task.IsCompleted) {
                        try { task.Wait(); }
                        catch (ThreadInterruptedException) { }
                        catch (AggregateException) { }
                    }
                    try { return task.GetAwaiter().GetResult(); }
                    catch { }
                }
            }
            return Fallback(text, chars, start, length);
        }

        private static void Queue(string text, char[] chars, int start, int length, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Equal(text, chars, start, length), out task);
        }

        private static bool Equal(string text, char[] chars, int start, int length) {
            for (int i = 0; i < length; i++) {
                if (text[i] != chars[start + i]) { return false; }
            }
            return true;
        }

        private static TextComparison Initialize() {
            try {
                if (Original == null || typeof(string).Module.ModuleVersionId
                    != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")
                    || !Matches(LengthGetter, 7, "4e983afa6e94b8b9e9d31249af4a4def459aabbf91df59838ddb074a0f98b7dc")
                    || !Matches(CharGetter, 30, "9c79f192e9fd140272e02883da9d174ce63ef8cae7d3d29623361b083eb08e24")) {
                    return null;
                }
                return (TextComparison)Delegate.CreateDelegate(typeof(TextComparison), Original);
            } catch { return null; }
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
    internal static class ApiJsonNameComparisonPatch {
        private static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.DefaultJsonNameTable", "Get", new[] { typeof(char[]), typeof(int), typeof(int) },
            158, "997c31ff86702e92604fc2a6f2c1b3e29bb7bc393d60594ca480e918075f5de5");
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonNameComparisonPatch), "Transpiler");
        internal static volatile bool Rewritten;

        private static bool Prepare() => Target != null && ApiJsonNameComparisonPreparation.Supported;
        private static MethodBase TargetMethod() => Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            if (code.Count != 97 && code.Count != 104) { return code; }
            int at = -1;
            for (int i = 0; i < code.Count; i++) {
                if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, ApiJsonNameComparisonPreparation.Original)) {
                    if (at >= 0) { return code; }
                    at = i;
                }
            }
            if (at < 0 || code[at].labels.Count != 0 || code[at].blocks.Count != 0) { return code; }
            code[at].operand = AccessTools.Method(typeof(ApiJsonNameComparisonPreparation), "Compare");
            code.Insert(at, new CodeInstruction(OpCodes.Ldarg_0));
            Rewritten = true;
            return code;
        }
    }
}
