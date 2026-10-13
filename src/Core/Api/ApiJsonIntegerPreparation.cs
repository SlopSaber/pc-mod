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
    internal static class ApiJsonIntegerPreparation {
        private const int MinimumTokenLength = 512 * 1024;
        private const int PrefixLength = 1024;

        internal static bool TryPrepareInt32(char[] chars, int start, int length, ref int value, out int status) {
            if (!TryPrepare(chars, start, length, out status)) {
                return false;
            }
            value = 0;
            return true;
        }

        internal static bool TryPrepareInt64(char[] chars, int start, int length, ref long value, out int status) {
            if (!TryPrepare(chars, start, length, out status)) {
                return false;
            }
            value = 0;
            return true;
        }

        private static bool TryPrepare(char[] chars, int start, int length, out int status) {
            status = 0;
            if (!ApiJsonNumericPreparation.IsActive || length < MinimumTokenLength || chars == null
                || start < 0 || length > chars.Length - start || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }

            Task<int> task = null;
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
                status = task.GetAwaiter().GetResult();
                return true;
            } catch {
                return false;
            }
        }

        private static void TryQueue(char[] chars, int start, int length, out Task<int> task) {
            task = null;
            if (ValidateDigits(chars, start, PrefixLength) != 2) {
                return;
            }

            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            Func<int> prepare = () => ValidateDigits(ownedChars, 0, ownedChars.Length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        private static int ValidateDigits(char[] chars, int start, int length) {
            int end = start + length;
            if (chars[start] == '-') {
                start++;
            }
            for (int i = start; i < end; i++) {
                if ((uint)(chars[i] - '0') > 9) {
                    return 3;
                }
            }
            return 2;
        }

        internal static MethodInfo FindTarget(string name, int bodyLength, string bodyHash, Type valueType) {
            try {
                Type type = typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Utilities.ConvertUtils");
                MethodInfo method = type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(char[]), typeof(int), typeof(int), valueType.MakeByRefType() }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                Type resultType = method?.ReturnType;
                if (method == null || body == null || body.Length != bodyLength || resultType == null
                    || !resultType.IsEnum || Enum.GetUnderlyingType(resultType) != typeof(int)
                    || (int)resultType.GetField("Overflow").GetRawConstantValue() != 2
                    || (int)resultType.GetField("Invalid").GetRawConstantValue() != 3) {
                    return null;
                }

                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    return actual == bodyHash ? method : null;
                }
            } catch {
                return null;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonInt32ParsePatch {
        private static readonly MethodInfo Target = ApiJsonIntegerPreparation.FindTarget("Int32TryParse", 212,
            "c2b87ffe6734b1d1217f0f7f2a9068925006539cbd1834c7ec9ed9ced54752fc", typeof(int));

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            return ApiJsonNumericPreparation.PrependPreparation(instructions, generator,
                typeof(ApiJsonIntegerPreparation), nameof(ApiJsonIntegerPreparation.TryPrepareInt32));
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonInt64ParsePatch {
        private static readonly MethodInfo Target = ApiJsonIntegerPreparation.FindTarget("Int64TryParse", 205,
            "2f2f97ddf2239aad51ddfd1141f3b284145ea7b534d76a3601888f4ed0722b12", typeof(long));

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            return ApiJsonNumericPreparation.PrependPreparation(instructions, generator,
                typeof(ApiJsonIntegerPreparation), nameof(ApiJsonIntegerPreparation.TryPrepareInt64));
        }
    }
}
