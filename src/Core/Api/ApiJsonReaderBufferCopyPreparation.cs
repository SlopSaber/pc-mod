using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonReaderBufferCopyPreparation {
        private static readonly FieldInfo ReaderChars = AccessTools.Field(typeof(JsonTextReader), "_chars");
        internal static readonly MethodInfo Target = FindTarget();
        internal static bool Supported => ApiJsonBufferGrowthPreparation.Supported && Target != null
            && ReaderChars != null && ReaderChars.FieldType == typeof(char[]);

        internal static bool TryCopy(char[] source, int sourceIndex, char[] destination, int destinationIndex, int count) {
            if (count < 524288 || source == null || destination == null
                || sourceIndex < 0 || destinationIndex < 0 || sourceIndex > int.MaxValue / 2
                || destinationIndex > int.MaxValue / 2 || count > int.MaxValue / 2
                || count > source.Length - sourceIndex || count > destination.Length - destinationIndex
                || Thread.CurrentThread.IsThreadPoolThread || !CanLease(source, destination)) {
                return false;
            }
            Task<bool> task = null;
            try { Queue(source, sourceIndex, destination, destinationIndex, count, out task); }
            catch { }
            if (task == null) {
                return false;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
            return true;
        }

        private static bool CanLease(char[] source, char[] destination) {
            try {
                JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
                return Supported && reader != null && reader.ArrayPool == null
                    && ApiJsonBufferGrowthPreparation.IsPrivateBuffer(source)
                    && ApiJsonBufferGrowthPreparation.IsPrivateBuffer(destination)
                    && ReferenceEquals(ReaderChars.GetValue(reader), source);
            } catch { return false; }
        }

        private static void Queue(char[] source, int sourceIndex, char[] destination, int destinationIndex,
            int count, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                Buffer.BlockCopy(source, sourceIndex * 2, destination, destinationIndex * 2, count * 2);
                return true;
            }, out task);
        }

        private static MethodInfo FindTarget() {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                MethodInfo method = typeof(JsonTextReader).GetMethod("BlockCopyChars", BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(char[]), typeof(int), typeof(char[]), typeof(int), typeof(int) }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != 18) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant()
                        == "a99bbee36ab7cf8b4e6f18559d7efb18e3ecf3e5f50770ef1912d4d100b03108" ? method : null;
                }
            } catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonReaderBufferCopyPatch {
        private static bool Prepare() => ApiJsonReaderBufferCopyPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonReaderBufferCopyPreparation.Target;
        private static bool Prefix(char[] __0, int __1, char[] __2, int __3, int __4) =>
            !ApiJsonReaderBufferCopyPreparation.TryCopy(__0, __1, __2, __3, __4);
    }
}
