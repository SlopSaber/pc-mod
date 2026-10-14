using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonUnicodeEscapePreparation {
        private const int MinimumLength = 524288;
        private const int PrefixGroups = 1024;
        private delegate bool HexParser(char[] chars, int start, int end, out int value);
        internal static readonly MethodInfo Kernel = Find("Newtonsoft.Json.Utilities.ConvertUtils",
            "TryHexTextToInt", new[] { typeof(char[]), typeof(int), typeof(int), typeof(int).MakeByRefType() },
            94, "bcc8478299abe9c7974d13c748a99813f70ff118814b0ec6ffcb87e9c44709cd");
        private static readonly HexParser Parser = CreateParser();

        [ThreadStatic]
        private static Cache _cache;

        internal static bool Supported => Parser != null;

        internal static void Clear() => _cache = null;

        internal static bool TryHex(char[] chars, int start, int end, out int value,
            JsonTextReader reader, int used) {
            if (ReferenceEquals(reader, ApiJsonNumericPreparation.CurrentReader)
                && !Thread.CurrentThread.IsThreadPoolThread && chars != null && start >= 2
                && used <= chars.Length && start < used - 4 && end - start == 4
                && (_cache != null || used - start >= MinimumLength)
                && Harmony.GetPatchInfo(Kernel) == null) {
                Cache cache = _cache;
                if (cache == null || !ReferenceEquals(cache.Reader, reader)
                    || !ReferenceEquals(cache.Source, chars) || cache.Used != used) {
                    _cache = null;
                    cache = Prepare(chars, start, used, reader);
                    if (cache == null) {
                        try { _cache = cache = new Cache(reader, chars, used, start - 2, null); }
                        catch { }
                    }
                }
                if (cache != null && cache.Batch != null) {
                    int at = start - cache.Start;
                    Batch batch = cache.Batch;
                    if (at >= 0 && at < batch.Chars.Length - 4 && batch.Values[at] != 0
                        && chars[start] == batch.Chars[at] && chars[start + 1] == batch.Chars[at + 1]
                        && chars[start + 2] == batch.Chars[at + 2] && chars[start + 3] == batch.Chars[at + 3]) {
                        value = batch.Values[at] - 1;
                        return true;
                    }
                }
            }
            return Parser(chars, start, end, out value);
        }

        private static Cache Prepare(char[] chars, int start, int used, JsonTextReader reader) {
            if (used - start < MinimumLength) {
                return null;
            }
            int from = start - 2;
            for (int group = 0; group < PrefixGroups; group++) {
                int at = from + group * 6;
                if (chars[at] != '\\' || chars[at + 1] != 'u'
                    || Digit(chars[at + 2]) < 0 || Digit(chars[at + 3]) < 0
                    || Digit(chars[at + 4]) < 0 || Digit(chars[at + 5]) < 0) {
                    return null;
                }
            }
            Task<Batch> task = null;
            try {
                RuntimeHelpers.RunClassConstructor(Kernel.DeclaringType.TypeHandle);
                Queue(chars, from, used - from, out task);
            } catch { }
            if (task == null) {
                return null;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                var cache = new Cache(reader, chars, used, from, task.GetAwaiter().GetResult());
                _cache = cache;
                return cache;
            } catch {
                return null;
            }
        }

        private static void Queue(char[] chars, int start, int length, out Task<Batch> task) {
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Scan(ownedChars), out task);
        }

        private static Batch Scan(char[] chars) {
            var values = new int[chars.Length];
            for (int i = 0; i < chars.Length - 6; i++) {
                if (chars[i] != '\\' || chars[i + 1] != 'u') {
                    continue;
                }
                int a = Digit(chars[i + 2]);
                int b = Digit(chars[i + 3]);
                int c = Digit(chars[i + 4]);
                int d = Digit(chars[i + 5]);
                if (a >= 0 && b >= 0 && c >= 0 && d >= 0) {
                    values[i + 2] = ((a << 12) + (b << 8) + (c << 4) + d) + 1;
                }
            }
            return new Batch(chars, values);
        }

        private static int Digit(char value) {
            if (value >= '0' && value <= '9') { return value - '0'; }
            if (value >= 'A' && value <= 'F') { return value - 'A' + 10; }
            if (value >= 'a' && value <= 'f') { return value - 'a' + 10; }
            return -1;
        }

        private static HexParser CreateParser() {
            try { return Kernel == null ? null : (HexParser)Delegate.CreateDelegate(typeof(HexParser), Kernel); }
            catch { return null; }
        }

        internal static MethodInfo Find(string type, string name, Type[] parameters, int length, string expected) {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                MethodInfo method = typeof(JsonTextReader).Assembly.GetType(type)?.GetMethod(name,
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, parameters, null);
                byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != length) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected
                        ? method : null;
                }
            } catch { return null; }
        }

        private sealed class Batch {
            internal readonly char[] Chars;
            internal readonly int[] Values;
            internal Batch(char[] chars, int[] values) { Chars = chars; Values = values; }
        }

        private sealed class Cache {
            internal readonly JsonTextReader Reader;
            internal readonly char[] Source;
            internal readonly int Used;
            internal readonly int Start;
            internal readonly Batch Batch;
            internal Cache(JsonTextReader reader, char[] source, int used, int start, Batch batch) {
                Reader = reader; Source = source; Used = used; Start = start; Batch = batch;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonUnicodeEscapePatch {
        private static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.JsonTextReader", "ConvertUnicode", new[] { typeof(bool) }, 105,
            "de7dcf93596ceba234331b4a844f34f91c387b2d110bb236c4dde4b2b7bc071a");

        private static bool Prepare() => Target != null && ApiJsonUnicodeEscapePreparation.Supported;
        private static MethodBase TargetMethod() => Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            FieldInfo used = AccessTools.Field(typeof(JsonTextReader), "_charsUsed");
            if (code.Count != 38 || used?.FieldType != typeof(int) || code[11].opcode != OpCodes.Call
                || !Equals(code[11].operand, ApiJsonUnicodeEscapePreparation.Kernel)
                || code[11].labels.Count != 0 || code[11].blocks.Count != 0) {
                return code;
            }
            code[11].operand = AccessTools.Method(typeof(ApiJsonUnicodeEscapePreparation), "TryHex");
            code.InsertRange(11, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, used)
            });
            return code;
        }
    }
}
