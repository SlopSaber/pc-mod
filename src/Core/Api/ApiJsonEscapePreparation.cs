using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;
using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonEscapePreparation {
        private const int MinimumTextLength = 512 * 1024;
        private const int PrefixLength = 1024;

        [ThreadStatic]
        private static bool _active;

        internal static string Serialize(object body, JsonSerializerSettings settings) {
            bool previous = _active;
            _active = true;
            try {
                return JsonConvert.SerializeObject(body, settings);
            } finally {
                _active = previous;
            }
        }

        internal static bool TryFindFirst(string text, bool[] flags, StringEscapeHandling handling, out int result) {
            result = -1;
            if (!_active || text == null || text.Length < MinimumTextLength
                || flags == null || flags.Length != 128 || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }

            Task<int> task = null;
            try {
                TryQueue(text, flags, handling, out task);
            } catch { }
            if (task == null) {
                return false;
            }

            // Keep the private snapshot alive until its physical worker has finished.
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                result = task.GetAwaiter().GetResult();
                return true;
            } catch {
                return false;
            }
        }

        private static void TryQueue(string text, bool[] flags, StringEscapeHandling handling, out Task<int> task) {
            task = null;
            var ownedFlags = new bool[128];
            Array.Copy(flags, ownedFlags, ownedFlags.Length);
            if (FindFirst(text, ownedFlags, handling, 0, PrefixLength) >= 0) {
                return;
            }

            Func<int> prepare = () => FindFirst(text, ownedFlags, handling, PrefixLength, text.Length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task);
        }

        private static int FindFirst(string text, bool[] flags, StringEscapeHandling handling, int start, int end) {
            for (int i = start; i < end; i++) {
                char value = text[i];
                if (value < flags.Length) {
                    if (flags[value]) {
                        return i;
                    }
                } else if (handling == StringEscapeHandling.EscapeNonAscii
                    || value == '\u0085' || value == '\u2028' || value == '\u2029') {
                    return i;
                }
            }
            return -1;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonEscapeScanPatch {
        private const string ExpectedBodyHash = "fbdf999e1ebf2b17915eeb099a68d38d079d669edce472e79bcdb3d6d1fba90c";
        private static readonly MethodInfo Target = FindTarget();

        private static bool Prepare() => Target != null;

        private static MethodBase TargetMethod() => Target;

        private static MethodInfo FindTarget() {
            try {
                Type type = typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Utilities.JavaScriptUtils");
                MethodInfo method = type?.GetMethod("FirstCharToEscape", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(bool[]), typeof(StringEscapeHandling) }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || method.ReturnType != typeof(int) || body == null || body.Length != 72) {
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

        private static bool Prefix(string __0, bool[] __1, StringEscapeHandling __2, ref int __result) {
            if (!ApiJsonEscapePreparation.TryFindFirst(__0, __1, __2, out int result)) {
                return true;
            }
            __result = result;
            return false;
        }
    }
}
