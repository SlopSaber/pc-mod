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
    internal static class ApiJsonBufferAppendPreparation {
        private static readonly FieldInfo ReaderChars = AccessTools.Field(typeof(JsonTextReader), "_chars");
        private static readonly FieldInfo ReaderBuffer = AccessTools.Field(typeof(JsonTextReader), "_stringBuffer");
        private static readonly FieldInfo BufferArray = ReaderBuffer == null ? null : AccessTools.Field(ReaderBuffer.FieldType, "_buffer");
        internal static readonly MethodInfo Target = FindTarget();
        internal static bool Supported => ApiJsonBufferGrowthPreparation.Supported && Target != null
            && ReaderChars != null && ReaderChars.FieldType == typeof(char[])
            && ReaderBuffer != null && BufferArray != null && BufferArray.FieldType == typeof(char[]);

        internal static void Copy(Array source, int sourceIndex, Array destination, int destinationIndex,
            int count, IArrayPool<char> pool) {
            if (pool != null || count < 524288 || !(source is char[] from) || !(destination is char[] to)
                || ReferenceEquals(from, to) || sourceIndex < 0 || destinationIndex < 0
                || count > from.Length - sourceIndex || count > to.Length - destinationIndex
                || Thread.CurrentThread.IsThreadPoolThread || !CanLease(from, to)) {
                Array.Copy(source, sourceIndex, destination, destinationIndex, count);
                return;
            }
            Task<bool> task = null;
            try { Queue(from, sourceIndex, to, destinationIndex, count, out task); }
            catch { }
            if (task == null) {
                Array.Copy(source, sourceIndex, destination, destinationIndex, count);
                return;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
        }

        private static bool CanLease(char[] source, char[] destination) {
            try {
                JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
                return Supported && reader != null && reader.ArrayPool == null
                    && ApiJsonBufferGrowthPreparation.IsPrivateBuffer(source)
                    && ApiJsonBufferGrowthPreparation.IsPrivateBuffer(destination)
                    && ReferenceEquals(ReaderChars.GetValue(reader), source)
                    && ReferenceEquals(BufferArray.GetValue(ReaderBuffer.GetValue(reader)), destination);
            } catch { return false; }
        }

        private static void Queue(char[] source, int sourceIndex, char[] destination, int destinationIndex,
            int count, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                Array.Copy(source, sourceIndex, destination, destinationIndex, count);
                return true;
            }, out task);
        }

        private static MethodInfo FindTarget() {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                Type type = typeof(JsonTextReader).Assembly.GetType("Newtonsoft.Json.Utilities.StringBuffer");
                MethodInfo method = type?.GetMethod("Append", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(IArrayPool<char>), typeof(char[]), typeof(int), typeof(int) }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != 65) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant()
                        == "0bac61ee3229f6e0ab2db0d3687b11c40e6e245ad2493c115a96e819030ba105" ? method : null;
                }
            } catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonBufferAppendPatch {
        private static bool Prepare() => ApiJsonBufferAppendPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonBufferAppendPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo copy = AccessTools.Method(typeof(Array), nameof(Array.Copy),
                new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) });
            if (code.Count != 28 || code[20].opcode != OpCodes.Call || !Equals(code[20].operand, copy)
                || code[20].blocks.Count != 0) {
                return code;
            }
            var pool = new CodeInstruction(OpCodes.Ldarg_1);
            pool.labels.AddRange(code[20].labels);
            code[20].labels.Clear();
            code[20].operand = AccessTools.Method(typeof(ApiJsonBufferAppendPreparation), nameof(ApiJsonBufferAppendPreparation.Copy));
            code.Insert(20, pool);
            return code;
        }
    }
}
