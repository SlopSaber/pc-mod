using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonUnicodeFormattingPreparation {
        private const int BatchLength = 65536;
        internal static MethodInfo Target;
        internal static bool Rewritten;
        private static MethodInfo Formatter;
        private static MethodBase[] Guards;
        private static readonly Action<char, char[]> Original = Initialize();
        internal static bool Supported => Original != null && Guards != null;

        [ThreadStatic]
        private static Batch _batch;

        internal sealed class Batch {
            internal readonly string Text;
            internal readonly int Start;
            internal readonly char[] Output;

            internal Batch(string text, int start, char[] output) {
                Text = text;
                Start = start;
                Output = output;
            }
        }

        internal static Batch Begin() {
            Batch previous = _batch;
            _batch = null;
            return previous;
        }

        internal static void End(Batch previous) => _batch = previous;

        internal static void Write(char value, char[] buffer, string text, int index, StringEscapeHandling handling) {
            if (TryPrepare(value, buffer, text, index, handling, out Batch batch)) {
                int offset = (index - batch.Start) * 6;
                buffer[0] = batch.Output[offset];
                buffer[1] = batch.Output[offset + 1];
                buffer[2] = batch.Output[offset + 2];
                buffer[3] = batch.Output[offset + 3];
                buffer[4] = batch.Output[offset + 4];
                buffer[5] = batch.Output[offset + 5];
                return;
            }
            Original(value, buffer);
        }

        private static bool TryPrepare(char value, char[] buffer, string text, int index,
            StringEscapeHandling handling, out Batch batch) {
            batch = null;
            try {
                if (!ApiJsonEscapePreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                    || handling != StringEscapeHandling.EscapeNonAscii || text == null || text.Length < 524288
                    || index < 0 || index >= text.Length || text[index] != value || buffer == null || buffer.Length < 6
                    || !IsUnchanged()) { return false; }
                Batch cached = _batch;
                if (cached != null && ReferenceEquals(cached.Text, text)
                    && index >= cached.Start && index - cached.Start < BatchLength) {
                    batch = cached;
                    return true;
                }
                if (text.Length - index < BatchLength) { return false; }
                for (int at = index; at < index + 1024; at++) {
                    char current = text[at];
                    if (current < 128 || current == '\u0085' || current == '\u2028' || current == '\u2029') { return false; }
                }
            } catch { return false; }

            Task<Batch> task = null;
            try { Queue(text, index, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            Batch prepared = task.GetAwaiter().GetResult();
            if (!IsUnchanged()) { return false; }
            _batch = prepared;
            batch = prepared;
            return true;
        }

        private static void Queue(string text, int start, out Task<Batch> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Compute(text, start), out task);
        }

        private static Batch Compute(string text, int start) {
            var output = new char[BatchLength * 6];
            for (int index = 0; index < BatchLength; index++) {
                char value = text[start + index];
                int offset = index * 6;
                output[offset] = '\\';
                output[offset + 1] = 'u';
                output[offset + 2] = Hex((value >> 12) & 15);
                output[offset + 3] = Hex((value >> 8) & 15);
                output[offset + 4] = Hex((value >> 4) & 15);
                output[offset + 5] = Hex(value & 15);
            }
            return new Batch(text, start, output);
        }

        private static char Hex(int value) => (char)(value <= 9 ? value + 48 : value - 10 + 97);

        private static bool IsUnchanged() {
            if (!Rewritten) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null || info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Finalizers.Count != 0
                || info.Transpilers.Count != 1 || info.Transpilers[0].PatchMethod != ApiJsonUnicodeFormattingPatch.TranspilerMethod) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static Action<char, char[]> Initialize() {
            try {
                Assembly json = typeof(JsonConvert).Assembly;
                if (json.ManifestModule.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) { return null; }
                Target = AccessTools.Method(json.GetType("Newtonsoft.Json.Utilities.JavaScriptUtils"), "WriteEscapedJavaScriptString",
                    new[] { typeof(TextWriter), typeof(string), typeof(char), typeof(bool), typeof(bool[]),
                        typeof(StringEscapeHandling), typeof(IArrayPool<char>), typeof(char[]).MakeByRefType() });
                Formatter = AccessTools.Method(json.GetType("Newtonsoft.Json.Utilities.StringUtils"), "ToCharAsUnicode", new[] { typeof(char), typeof(char[]) });
                MethodInfo hex = AccessTools.Method(json.GetType("Newtonsoft.Json.Utilities.MathUtils"), "IntToHex", new[] { typeof(int) });
                if (!Matches(Target, 648, "d75a85c2ce195cc5f1a56687346eff03f1f0eab7d7a759b3f6994a67fe95a67f")
                    || !Matches(Formatter, 66, "fce72d1e573514b853f87a7a9809fe368a99f41bccd43963ab6486f6f0b2dad6")
                    || !Matches(hex, 20, "f5cad3fe7e20061dce42519337609641eab992a24a08d2b3d8feb180255fd2c1")
                    || Formatter.ReturnType != typeof(void) || hex.ReturnType != typeof(char)) { return null; }
                var guards = new List<MethodBase> { Formatter, hex,
                    typeof(string).GetProperty("Length")?.GetGetMethod(), typeof(string).GetProperty("Chars")?.GetGetMethod(),
                    AccessTools.Method(typeof(ApiJsonEscapePreparation), "Serialize"),
                    AccessTools.PropertyGetter(typeof(ApiJsonEscapePreparation), "IsActive") };
                guards.AddRange(typeof(ApiJsonUnicodeFormattingPreparation).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                guards.AddRange(typeof(Batch).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                foreach (MethodInfo method in typeof(ReplayStorageService).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "TryQueueOwnedPreparationWhenIdle" && method.IsGenericMethodDefinition) {
                        guards.Add(method.MakeGenericMethod(typeof(Batch)));
                    }
                }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                var original = new DynamicMethod("WriteOriginalUnicodeEscape", typeof(void),
                    new[] { typeof(char), typeof(char[]) }, typeof(ApiJsonUnicodeFormattingPreparation), true);
                ILGenerator il = original.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, Formatter); il.Emit(OpCodes.Ret);
                Guards = guards.ToArray();
                return (Action<char, char[]>)original.CreateDelegate(typeof(Action<char, char[]>));
            } catch { return null; }
        }

        private static bool Matches(MethodInfo method, int length, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != length) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 303 || code[157].opcode != OpCodes.Ldloc_3
                || code[159].opcode != OpCodes.Ldind_Ref || code[160].opcode != OpCodes.Call
                || !Equals(code[160].operand, Formatter)) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            var first = new CodeInstruction(OpCodes.Ldarg_1);
            first.labels.AddRange(code[160].labels);
            code[160].labels.Clear();
            code[160].operand = AccessTools.Method(typeof(ApiJsonUnicodeFormattingPreparation), "Write");
            code.InsertRange(160, new[] { first, new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldarg_S, (byte)5) });
            Rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonUnicodeFormattingPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonUnicodeFormattingPatch), "Transpiler");
        private static bool Prepare() => ApiJsonUnicodeFormattingPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonUnicodeFormattingPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => ApiJsonUnicodeFormattingPreparation.Rewrite(instructions);
    }
}
