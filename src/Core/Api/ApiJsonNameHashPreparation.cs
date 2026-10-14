using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonNameHashPreparation {
        internal static bool TryHash(DefaultJsonNameTable table, char[] chars, int start, int length, ref int hash) {
            JsonTextReader reader = ApiJsonNumericPreparation.CurrentReader;
            if (reader == null || Thread.CurrentThread.IsThreadPoolThread || length < 524288
                || chars == null || start < 0 || length > chars.Length || start > chars.Length - length
                || table.GetType() != typeof(DefaultJsonNameTable) || reader.ArrayPool != null
                || !ApiJsonPropertyNamePreparation.IsCurrent(table, chars, start, length)
                || !ApiJsonBufferGrowthPreparation.IsPrivateBuffer(chars)) {
                return false;
            }
            Task<int> task = null;
            try { Queue(chars, start, length, hash, out task); }
            catch { }
            if (task == null) { return false; }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                hash = task.GetAwaiter().GetResult();
                return true;
            } catch { return false; }
        }

        private static void Queue(char[] chars, int start, int length, int seed, out Task<int> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Hash(chars, start, length, seed), out task);
        }

        private static int Hash(char[] chars, int start, int length, int hash) {
            unchecked {
                for (int i = 0; i < length; i++) {
                    hash += (hash << 7) ^ chars[start + i];
                }
                hash -= hash >> 17;
                hash -= hash >> 11;
                hash -= hash >> 5;
                return hash;
            }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonNameHashPatch {
        private static readonly MethodInfo Target = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.DefaultJsonNameTable", "Get", new[] { typeof(char[]), typeof(int), typeof(int) },
            158, "997c31ff86702e92604fc2a6f2c1b3e29bb7bc393d60594ca480e918075f5de5");
        private static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonNameHashPatch), "Transpiler");
        private static volatile bool _rewritten;

        internal static bool IsOnlyOwnPatch(HarmonyLib.Patches info) {
            if (info == null) { return true; }
            return _rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == TranspilerMethod;
        }

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            _rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            FieldInfo mask = AccessTools.Field(typeof(DefaultJsonNameTable), "_mask");
            if (code.Count != 97 || code[7].opcode != OpCodes.Stloc_0 || code[8].opcode != OpCodes.Ldloc_0
                || code[8].blocks.Count != 0 || code[62].opcode != OpCodes.Ldarg_0
                || code[62].blocks.Count != 0 || code[63].opcode != OpCodes.Ldflda
                || !Equals(code[63].operand, mask)) {
                return code;
            }
            Label lookup = generator.DefineLabel();
            code[62].labels.Add(lookup);
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldloca_S, (byte)0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonNameHashPreparation), "TryHash")),
                new CodeInstruction(OpCodes.Brtrue, lookup)
            };
            prefix[0].labels.AddRange(code[8].labels);
            code[8].labels.Clear();
            code.InsertRange(8, prefix);
            _rewritten = true;
            return code;
        }
    }
}
