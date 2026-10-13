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

        internal static int Advance(char[] chars, int start, int used) => Advance(chars, start, used, false);

        internal static int AdvanceNumber(char[] chars, int start, int used) => Advance(chars, start, used, true);

        private static int Advance(char[] chars, int start, int used, bool number) {
            if (!ApiJsonNumericPreparation.IsActive || chars == null || start < 0
                || used > chars.Length || used - start < MinimumLength
                || Thread.CurrentThread.IsThreadPoolThread
                || Scan(chars, start, PrefixLength, number) != PrefixLength) {
                return start;
            }

            Task<int> task = null;
            try { Queue(chars, start, used - start, number, out task); }
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

        private static void Queue(char[] chars, int start, int length, bool number, out Task<int> task) {
            task = null;
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Scan(ownedChars, 0, ownedChars.Length, number), out task);
        }

        private static int Scan(char[] chars, int start, int length, bool number) {
            if (number) {
                for (int i = 0; i < length; i++) {
                    char value = chars[start + i];
                    if (!((uint)(value - '0') <= 9 || (uint)(value - 'A') <= 5
                        || (uint)(value - 'a') <= 5 || value == '+' || value == '-'
                        || value == '.' || value == 'X' || value == 'x')) {
                        return i;
                    }
                }
                return length;
            }
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
                    nameof(ApiJsonStringScanPreparation.Advance), new[] { typeof(char[]), typeof(int), typeof(int) })),
                new CodeInstruction(OpCodes.Stloc_0)
            };
            // Every ordinary-loop back edge must enter preparation before the original character load.
            prefix[0].labels.AddRange(original[13].labels);
            original[13].labels.Clear();
            original.InsertRange(13, prefix);
            return original;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonNumberScanPatch {
        private static readonly MethodInfo Target = FindTarget();

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        private static MethodInfo FindTarget() {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId
                    != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                MethodInfo method = typeof(JsonTextReader).GetMethod("ReadNumberIntoBuffer",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != 63) {
                    return null;
                }
                MethodInfo predicate = typeof(JsonTextReader).GetMethod("ReadNumberCharIntoBuffer",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(char), typeof(int) }, null);
                byte[] predicateBody = predicate?.GetMethodBody()?.GetILAsByteArray();
                if (predicateBody == null || predicateBody.Length != 246) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    string actualPredicate = BitConverter.ToString(hash.ComputeHash(predicateBody)).Replace("-", "").ToLowerInvariant();
                    return actual == "4bb2489f64eebf0d774e4147b5e532b0ffc168715e72889aa618216a29afd799"
                        && actualPredicate == "051a911494c673218a5007122b1442611909fec01cdef845711ac7103e6eed3f"
                        ? method : null;
                }
            } catch { return null; }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var original = new List<CodeInstruction>(instructions);
            FieldInfo chars = AccessTools.Field(typeof(JsonTextReader), "_chars");
            FieldInfo used = AccessTools.Field(typeof(JsonTextReader), "_charsUsed");
            if (original.Count != 34 || chars == null || used == null
                || original[3].opcode != OpCodes.Ldarg_0
                || original[4].opcode != OpCodes.Ldfld || !Equals(original[4].operand, chars)
                || original[5].opcode != OpCodes.Ldloc_0 || original[3].blocks.Count != 0) {
                return original;
            }
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, chars),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, used),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonStringScanPreparation),
                    nameof(ApiJsonStringScanPreparation.AdvanceNumber))),
                new CodeInstruction(OpCodes.Stloc_0)
            };
            prefix[0].labels.AddRange(original[3].labels);
            original[3].labels.Clear();
            original.InsertRange(3, prefix);
            return original;
        }
    }
}
