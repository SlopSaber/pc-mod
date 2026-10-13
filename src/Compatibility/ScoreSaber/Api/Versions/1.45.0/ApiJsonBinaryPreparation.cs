extern alias ScoreSaberCore;

using HarmonyLib;
using ScoreSaber.Features.Replays;
using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CoreCharSpan = ScoreSaberCore::System.ReadOnlySpan<char>;
using CoreByteSpan = ScoreSaberCore::System.Span<byte>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonBinaryPreparation {
        private const int MinimumTextLength = 512 * 1024;
        private const string CoreModule = "41d29b35-2f6a-475a-b1bf-7c6628b82790";

        internal static bool TryPrepare(string text, out byte[] bytes) {
            bytes = null;
            if (!ApiJsonNumericPreparation.IsActive || text == null || text.Length < MinimumTextLength
                || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }

            Task<DecodeResult> task = null;
            try { QueueString(text, out task); } catch { }
            return TryTake(task, out bytes);
        }

        internal static bool TryPrepare(char[] chars, int start, int length, out byte[] bytes) {
            bytes = null;
            if (!ApiJsonNumericPreparation.IsActive || length < MinimumTextLength || chars == null
                || start < 0 || length > chars.Length - start || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }

            Task<DecodeResult> task = null;
            try { QueueChars(chars, start, length, out task); } catch { }
            return TryTake(task, out bytes);
        }

        private static void QueueString(string text, out Task<DecodeResult> task) {
            Func<DecodeResult> prepare = () => Decode(text);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        private static void QueueChars(char[] chars, int start, int length, out Task<DecodeResult> task) {
            task = null;
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            Func<DecodeResult> prepare = () => Decode(new CoreCharSpan(ownedChars));
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        private static bool TryTake(Task<DecodeResult> task, out byte[] bytes) {
            bytes = null;
            if (task == null) {
                return false;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                bytes = task.GetAwaiter().GetResult().Bytes;
                return bytes != null;
            } catch {
                return false;
            }
        }

        private static DecodeResult Decode(CoreCharSpan chars) {
            int end = chars.Length;
            while (end > 0 && IsSpace(chars[end - 1])) {
                end--;
            }

            int useful = end;
            int padding = 0;
            for (int i = 0; i < end; i++) {
                char value = chars[i];
                if (value <= ' ') {
                    useful--;
                } else if (value == '=') {
                    useful--;
                    padding++;
                }
            }
            if (padding > 2) {
                return default;
            }

            int length = useful / 4 * 3 + (padding == 0 ? 0 : 3 - padding);
            var bytes = new byte[length];
            // Expected malformed input must throw only through the original caller path.
            return Convert.TryFromBase64Chars(chars.Slice(0, end), new CoreByteSpan(bytes), out _)
                ? new DecodeResult(bytes) : default;
        }

        private static bool IsSpace(char value) => value == ' ' || value == '\t' || value == '\r' || value == '\n';

        private readonly struct DecodeResult {
            internal readonly byte[] Bytes;
            internal DecodeResult(byte[] bytes) { Bytes = bytes; }
        }

        internal static MethodInfo FindTarget(string name, Type[] parameters, int size, string expectedHash) {
            try {
                if (typeof(Convert).Module.ModuleVersionId != new Guid(CoreModule)) {
                    return null;
                }
                MethodInfo method = typeof(Convert).GetMethod(name, BindingFlags.Public | BindingFlags.Static,
                    null, parameters, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || method.ReturnType != typeof(byte[]) || body == null || body.Length != size) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    return actual == expectedHash ? method : null;
                }
            } catch {
                return null;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonBase64StringPatch {
        private static readonly MethodInfo Target = ApiJsonBinaryPreparation.FindTarget("FromBase64String",
            new[] { typeof(string) }, 43, "acef765976bf48ae7975f2cab86ccffd2faf910c8b3d72f99b5c745a25e9d9c8");

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;
        private static bool Prefix(string __0, ref byte[] __result) {
            if (!ApiJsonBinaryPreparation.TryPrepare(__0, out byte[] bytes)) {
                return true;
            }
            __result = bytes;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonBase64ArrayPatch {
        private static readonly MethodInfo Target = ApiJsonBinaryPreparation.FindTarget("FromBase64CharArray",
            new[] { typeof(char[]), typeof(int), typeof(int) }, 110,
            "4900b303cb51a4cb88c88a1093be56859bdc06223b81d587d226a65f478b6289");

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;
        private static bool Prefix(char[] __0, int __1, int __2, ref byte[] __result) {
            if (!ApiJsonBinaryPreparation.TryPrepare(__0, __1, __2, out byte[] bytes)) {
                return true;
            }
            __result = bytes;
            return false;
        }
    }
}
