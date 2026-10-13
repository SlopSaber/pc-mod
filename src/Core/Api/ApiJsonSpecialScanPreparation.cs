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
    internal static class ApiJsonSpecialScanPreparation {
        private const int MinimumLength = 524288;
        private const int PrefixLength = 1024;

        internal static int Advance(char[] chars, int start, int used, int mode) {
            if (!ApiJsonNumericPreparation.IsActive || chars == null || start < 0
                || used > chars.Length || used - start < MinimumLength
                || Thread.CurrentThread.IsThreadPoolThread
                || Scan(chars, start, PrefixLength, mode) != PrefixLength) {
                return start;
            }
            Task<int> task = null;
            try { Queue(chars, start, used - start, mode, out task); }
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

        private static void Queue(char[] chars, int start, int length, int mode, out Task<int> task) {
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Scan(ownedChars, 0, ownedChars.Length, mode), out task);
        }

        private static int Scan(char[] chars, int start, int length, int mode) {
            for (int i = 0; i < length; i++) {
                char value = chars[start + i];
                if (mode == 2) {
                    if (value == '\0' || value == '\n' || value == '\r' || value == '*') {
                        return i;
                    }
                } else if (!((uint)(value - '0') <= 9 || (uint)(value - 'A') <= 25
                    || (uint)(value - 'a') <= 25 || (mode == 0 && (value == '_' || value == '$')))) {
                    return i;
                }
            }
            return length;
        }

    }

    [HarmonyPatch]
    internal static class ApiJsonSpecialScanPatch {
        private static readonly TargetRecord[] Records = {
            new TargetRecord("ParseUnquotedProperty", 41, 97, 3, 0, "49bdb0bd764bf6510c418c099b73eb4be16b1d94c51eca2973a871885cda3fd2"),
            new TargetRecord("ParseConstructor", 156, 404, 9, 1, "6aead7d5a2eece185804413043e97b87d58677710be14edb76a13330fbfa0bc7"),
            new TargetRecord("ParseComment", 174, 423, 56, 2, "1200b35cf5e4b44a69eb4f428c0f3d5344ff775b9cb875e4ceecf308a7ed5ca2", typeof(bool)),
        };

        private static bool Prepare() {
            foreach (TargetRecord record in Records) {
                if (record.Method != null) {
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<MethodBase> TargetMethods() {
            var targets = new List<MethodBase>();
            foreach (TargetRecord record in Records) {
                if (record.Method != null) {
                    targets.Add(record.Method);
                }
            }
            return targets;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod) {
            var code = new List<CodeInstruction>(instructions);
            TargetRecord selected = null;
            foreach (TargetRecord record in Records) {
                if (Equals(record.Method, __originalMethod)) {
                    selected = record;
                    break;
                }
            }
            if (selected == null || code.Count != selected.Count) {
                return code;
            }
            int at = selected.Anchor;
            FieldInfo chars = AccessTools.Field(typeof(JsonTextReader), "_chars");
            FieldInfo position = AccessTools.Field(typeof(JsonTextReader), "_charPos");
            FieldInfo used = AccessTools.Field(typeof(JsonTextReader), "_charsUsed");
            if (chars == null || position == null || used == null
                || code[at].opcode != OpCodes.Ldarg_0 || code[at].blocks.Count != 0
                || code[at + 1].opcode != OpCodes.Ldfld || !Equals(code[at + 1].operand, chars)
                || code[at + 2].opcode != OpCodes.Ldarg_0
                || code[at + 3].opcode != OpCodes.Ldfld || !Equals(code[at + 3].operand, position)) {
                return code;
            }
            var prefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, chars),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, position),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, used),
                new CodeInstruction(OpCodes.Ldc_I4, selected.Mode),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonSpecialScanPreparation),
                    nameof(ApiJsonSpecialScanPreparation.Advance))),
                new CodeInstruction(OpCodes.Stfld, position)
            };
            // Back edges must prepare the next run before the original character dispatch.
            prefix[0].labels.AddRange(code[at].labels);
            code[at].labels.Clear();
            code.InsertRange(at, prefix);
            return code;
        }

        private sealed class TargetRecord {
            internal readonly MethodInfo Method;
            internal readonly int Count;
            internal readonly int Anchor;
            internal readonly int Mode;

            internal TargetRecord(string name, int count, int bytes, int anchor, int mode, string bodyHash,
                params Type[] parameters) {
                Count = count;
                Anchor = anchor;
                Mode = mode;
                try {
                    if (typeof(JsonTextReader).Module.ModuleVersionId
                        != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")
                        || typeof(char).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) {
                        return;
                    }
                    MethodInfo method = typeof(JsonTextReader).GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, parameters, null);
                    byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                    if (body == null || body.Length != bytes) {
                        return;
                    }
                    using (SHA256 hash = SHA256.Create()) {
                        string actual = BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                        if (actual == bodyHash) {
                            Method = method;
                        }
                    }
                } catch { }
            }
        }
    }
}
