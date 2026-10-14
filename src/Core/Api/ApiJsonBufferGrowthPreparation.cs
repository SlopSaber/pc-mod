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
    internal static class ApiJsonBufferGrowthPreparation {
        private static readonly ConditionalWeakTable<char[], Receipt> Fresh = new ConditionalWeakTable<char[], Receipt>();
        private static readonly Receipt PrivateBuffer = new Receipt();
        private static readonly FieldInfo ReaderBuffer = AccessTools.Field(typeof(JsonTextReader), "_stringBuffer");
        private static readonly FieldInfo BufferArray = ReaderBuffer == null ? null : AccessTools.Field(ReaderBuffer.FieldType, "_buffer");
        internal static readonly MethodInfo Rent = Find("Newtonsoft.Json.Utilities.BufferUtils", "RentBuffer", 18,
            "56f93f9dacf17a2d51ccc69d6c9b2eaded31be3a0927e2d8d02a09b4b6f58ef5", typeof(IArrayPool<char>), typeof(int));
        internal static readonly MethodInfo Return = Find("Newtonsoft.Json.Utilities.BufferUtils", "ReturnBuffer", 11,
            "326fa0163210edc24c57f1a42cdb0e1025e555c7fd84e0d2bb16bb9bd9cf1307", typeof(IArrayPool<char>), typeof(char[]));
        internal static readonly MethodInfo Growth = Find("Newtonsoft.Json.Utilities.StringBuffer", "EnsureSize", 63,
            "b729ffa8722678c80859d373ff402a42c11a3fc6364408ecc81132d148fb56b3", typeof(IArrayPool<char>), typeof(int));
        internal static bool Supported => Rent != null && Return != null && Growth != null
            && ReaderBuffer != null && BufferArray != null && BufferArray.FieldType == typeof(char[]);

        internal static void Record(IArrayPool<char> pool, char[] buffer) {
            if (pool != null || buffer == null || ApiJsonNumericPreparation.CurrentReader == null
                || !ApiJsonPropertyNamePreparation.IsProtected) {
                return;
            }
            try { Fresh.Add(buffer, PrivateBuffer); }
            catch { }
        }

        internal static void Revoke(IArrayPool<char> pool, char[] buffer) {
            if (pool != null && buffer != null) {
                Fresh.Remove(buffer);
            }
        }

        internal static void Revoke(char[] buffer) {
            if (buffer != null) {
                Fresh.Remove(buffer);
            }
        }

        internal static void Copy(Array source, Array destination, int count, IArrayPool<char> pool) {
            if (pool != null || count < 524288 || !(source is char[] from) || !(destination is char[] to)
                || ReferenceEquals(from, to) || count > from.Length || count > to.Length
                || Thread.CurrentThread.IsThreadPoolThread || !CanLease(from, to)) {
                Array.Copy(source, destination, count);
                return;
            }
            Task<bool> task = null;
            try { Queue(from, to, count, out task); }
            catch { }
            if (task == null) {
                Array.Copy(source, destination, count);
                return;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            // Once copying starts, an error must propagate after physical completion without retrying writes.
            task.GetAwaiter().GetResult();
        }

        private static bool CanLease(char[] source, char[] destination) {
            try {
                JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
                return Supported && ApiJsonPropertyNamePreparation.IsProtected && reader != null && reader.ArrayPool == null
                    && Fresh.TryGetValue(source, out _) && Fresh.TryGetValue(destination, out _)
                    && ReferenceEquals(BufferArray.GetValue(ReaderBuffer.GetValue(reader)), source);
            } catch { return false; }
        }

        private static void Queue(char[] source, char[] destination, int count, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                Array.Copy(source, destination, count);
                return true;
            }, out task);
        }

        private static MethodInfo Find(string typeName, string name, int bytes, string expected, params Type[] parameters) {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                Type type = typeof(JsonTextReader).Assembly.GetType(typeName);
                MethodInfo method = type?.GetMethod(name, BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != bytes) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant() == expected
                        ? method : null;
                }
            } catch { return null; }
        }

        internal static bool IsPrivateBuffer(char[] buffer) => ApiJsonPropertyNamePreparation.IsProtected
            && buffer != null && Fresh.TryGetValue(buffer, out _);

        private sealed class Receipt { }
    }

    [HarmonyPatch]
    internal static class ApiJsonBufferRentPatch {
        private static bool Prepare() => ApiJsonBufferGrowthPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonBufferGrowthPreparation.Rent;
        private static void Postfix(IArrayPool<char> __0, char[] __result) => ApiJsonBufferGrowthPreparation.Record(__0, __result);
    }

    [HarmonyPatch]
    internal static class ApiJsonBufferReturnPatch {
        private static bool Prepare() => ApiJsonBufferGrowthPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonBufferGrowthPreparation.Return;
        private static void Prefix(IArrayPool<char> __0, char[] __1) => ApiJsonBufferGrowthPreparation.Revoke(__0, __1);
    }

    [HarmonyPatch]
    internal static class ApiJsonBufferGrowthPatch {
        private static bool Prepare() => ApiJsonBufferGrowthPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonBufferGrowthPreparation.Growth;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo copy = AccessTools.Method(typeof(Array), nameof(Array.Copy), new[] { typeof(Array), typeof(Array), typeof(int) });
            if (code.Count != 26 || code[17].opcode != OpCodes.Call || !Equals(code[17].operand, copy)
                || code[17].blocks.Count != 0) {
                return code;
            }
            var pool = new CodeInstruction(OpCodes.Ldarg_1);
            pool.labels.AddRange(code[17].labels);
            code[17].labels.Clear();
            code[17].operand = AccessTools.Method(typeof(ApiJsonBufferGrowthPreparation), nameof(ApiJsonBufferGrowthPreparation.Copy));
            code.Insert(17, pool);
            return code;
        }
    }
}
