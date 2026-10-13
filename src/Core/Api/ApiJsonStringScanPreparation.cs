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
    internal static class ApiJsonStringScanPreparation {
        private const int MinimumLength = 524288;
        private const int PrefixLength = 1024;

        internal static int Advance(char[] chars, int start, int used) {
            if (!ApiJsonNumericPreparation.IsActive || chars == null || start < 0
                || used > chars.Length || used - start < MinimumLength
                || Thread.CurrentThread.IsThreadPoolThread) {
                return start;
            }

            Task<int> task = null;
            try { Queue(chars, start, used - start, out task); }
            catch { }
            if (task == null) {
                return start;
            }

            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try { return start + task.GetAwaiter().GetResult(); }
            catch { return start; }
        }

        private static void Queue(char[] chars, int start, int length, out Task<int> task) {
            task = null;
            if (Scan(chars, start, PrefixLength) != PrefixLength) {
                return;
            }
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Scan(ownedChars, 0, ownedChars.Length), out task);
        }

        private static int Scan(char[] chars, int start, int length) {
            for (int i = 0; i < length; i++) {
                char value = chars[start + i];
                if (value == '\0' || value == '\n' || value == '\r'
                    || value == '"' || value == '\'' || value == '\\') {
                    return i;
                }
            }
            return length;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonStringScanPatch {
        private static readonly MethodInfo Target = FindTarget();

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        private static MethodInfo FindTarget() {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId
                    != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                MethodInfo method = typeof(JsonTextReader).GetMethod("ReadStringIntoBuffer",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(char) }, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != 759) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    return actual == "f0a7f881376b6b65110e136070155d5dd8c5b02d4721cb7fb4b45a8b176dcb59"
                        ? method : null;
                }
            } catch { return null; }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var original = new List<CodeInstruction>(instructions);
            FieldInfo chars = AccessTools.Field(typeof(JsonTextReader), "_chars");
            FieldInfo used = AccessTools.Field(typeof(JsonTextReader), "_charsUsed");
            if (original.Count != 300 || chars == null || used == null
                || original[13].opcode != OpCodes.Ldarg_0
                || original[14].opcode != OpCodes.Ldfld || !Equals(original[14].operand, chars)
                || original[15].opcode != OpCodes.Ldloc_0 || original[13].blocks.Count != 0) {
                return original;
            }

            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, chars),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, used),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonStringScanPreparation),
                    nameof(ApiJsonStringScanPreparation.Advance))),
                new CodeInstruction(OpCodes.Stloc_0)
            };
            // Every ordinary-loop back edge must enter preparation before the original character load.
            prefix[0].labels.AddRange(original[13].labels);
            original[13].labels.Clear();
            original.InsertRange(13, prefix);
            return original;
        }
    }
}
