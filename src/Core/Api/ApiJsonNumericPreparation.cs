using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonNumericPreparation {
        private const int MinimumTokenLength = 512 * 1024;
        private const int PrefixLength = 1024;
        private delegate int DecimalParser(char[] chars, int start, int length, out decimal value);
        private static DecimalParser _parser;

        [ThreadStatic]
        private static bool _active;

        [ThreadStatic]
        private static JsonTextReader _reader;

        internal static JsonTextReader CurrentReader => _reader;

        internal static T Deserialize<T>(JsonSerializer serializer, JsonReader reader) {
            bool previous = _active;
            JsonTextReader previousReader = _reader;
            _active = true;
            _reader = reader?.GetType() == typeof(JsonTextReader) ? (JsonTextReader)reader : null;
            try {
                ApiJsonUnicodeEscapePreparation.Clear();
                return serializer.Deserialize<T>(reader);
            } finally {
                ApiJsonUnicodeEscapePreparation.Clear();
                _active = previous;
                _reader = previousReader;
            }
        }

        internal static bool TryInitialize(MethodInfo method) {
            try {
                if (_parser != null) {
                    return true;
                }

                // The guarded private enum has the same unboxed stack representation as Int32.
                var bridge = new DynamicMethod("ParseOwnedApiDecimal", typeof(int),
                    new[] { typeof(char[]), typeof(int), typeof(int), typeof(decimal).MakeByRefType() },
                    typeof(ApiJsonNumericPreparation), true);
                ILGenerator generator = bridge.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldarg_3);
                generator.Emit(OpCodes.Call, method);
                generator.Emit(OpCodes.Conv_I4);
                generator.Emit(OpCodes.Ret);
                _parser = (DecimalParser)bridge.CreateDelegate(typeof(DecimalParser));
                return true;
            } catch {
                return false;
            }
        }

        internal static bool TryPrepareDecimal(char[] chars, int start, int length, ref decimal value, out int status) {
            status = 0;
            if (!_active || _parser == null || length < MinimumTokenLength || chars == null
                || start < 0 || length > chars.Length - start || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }

            Task<DecimalResult> task = null;
            try {
                TryQueue(chars, start, length, out task);
            } catch { }
            if (task == null) {
                return false;
            }

            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                DecimalResult result = task.GetAwaiter().GetResult();
                value = result.Value;
                status = result.Status;
                return true;
            } catch {
                return false;
            }
        }

        private static void TryQueue(char[] chars, int start, int length, out Task<DecimalResult> task) {
            task = null;
            DecimalParser parser = _parser;
            int prefixStatus = parser(chars, start, PrefixLength, out _);
            if (prefixStatus != 1 && prefixStatus != 2) {
                return;
            }

            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            Func<DecimalResult> prepare = () => {
                int status = parser(ownedChars, 0, ownedChars.Length, out decimal value);
                return new DecimalResult(status, value);
            };
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        internal static bool IsActive => _active;

        internal static IEnumerable<CodeInstruction> PrependPreparation(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, Type prepareOwner, string prepareName) {
            var original = new List<CodeInstruction>(instructions);
            if (original.Count == 0) {
                return original;
            }

            Label fallback = generator.DefineLabel();
            LocalBuilder status = generator.DeclareLocal(typeof(int));
            original[0].labels.Add(fallback);
            var result = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldloca, status),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(prepareOwner, prepareName)),
                new CodeInstruction(OpCodes.Brfalse, fallback),
                new CodeInstruction(OpCodes.Ldloc, status),
                new CodeInstruction(OpCodes.Ret)
            };
            result.AddRange(original);
            return result;
        }

        private readonly struct DecimalResult {
            internal readonly int Status;
            internal readonly decimal Value;

            internal DecimalResult(int status, decimal value) {
                Status = status;
                Value = value;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDecimalParsePatch {
        private const string ExpectedBodyHash = "7605b7349930d19a9abb3fdc593f6d1e436aeacd33a70f694ddb9daed79c60a1";
        private static readonly MethodInfo Target = FindTarget();

        private static bool Prepare() => Target != null && ApiJsonNumericPreparation.TryInitialize(Target);

        private static MethodBase TargetMethod() => Target;

        private static MethodInfo FindTarget() {
            try {
                Type type = typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Utilities.ConvertUtils");
                MethodInfo method = type?.GetMethod("DecimalTryParse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(char[]), typeof(int), typeof(int), typeof(decimal).MakeByRefType() }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                Type resultType = method?.ReturnType;
                if (method == null || body == null || body.Length != 1161 || resultType == null
                    || !resultType.IsEnum || Enum.GetUnderlyingType(resultType) != typeof(int)
                    || (int)resultType.GetField("None").GetRawConstantValue() != 0
                    || (int)resultType.GetField("Success").GetRawConstantValue() != 1
                    || (int)resultType.GetField("Overflow").GetRawConstantValue() != 2
                    || (int)resultType.GetField("Invalid").GetRawConstantValue() != 3) {
                    return null;
                }

                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    return actual == ExpectedBodyHash ? method : null;
                }
            } catch {
                return null;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            return ApiJsonNumericPreparation.PrependPreparation(instructions, generator,
                typeof(ApiJsonNumericPreparation), nameof(ApiJsonNumericPreparation.TryPrepareDecimal));
        }
    }
}
