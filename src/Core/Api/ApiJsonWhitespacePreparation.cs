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
    internal static class ApiJsonWhitespacePreparation {
        private const int MinimumLength = 524288;
        private const int PrefixLength = 1024;

        internal static int Advance(char[] chars, int start, int used) {
            if (!ApiJsonNumericPreparation.IsActive || chars == null || start < 0
                || used > chars.Length || used - start < MinimumLength
                || Thread.CurrentThread.IsThreadPoolThread) {
                return start;
            }
            bool unicode = ApiJsonUnicodeCategories.Supported;
            if (Scan(chars, start, PrefixLength, unicode) != PrefixLength) {
                return start;
            }
            Task<int> task = null;
            try { Queue(chars, start, used - start, unicode, out task); }
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

        private static void Queue(char[] chars, int start, int length, bool unicode, out Task<int> task) {
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(
                () => Scan(ownedChars, 0, ownedChars.Length, unicode), out task);
        }

        private static int Scan(char[] chars, int start, int length, bool unicode) {
            for (int i = 0; i < length; i++) {
                char value = chars[start + i];
                if (value != ' ' && value != '\t' && value != '\v' && value != '\f'
                    && !(unicode && value > 127 && ApiJsonUnicodeCategories.IsWhiteSpace(value))) {
                    return i;
                }
            }
            return length;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonWhitespacePatch {
        private static readonly TargetRecord[] Records = {
            new TargetRecord("ParsePostValue", 158, 411, 0, "c8887e28866738e3a5dbf1a3d25c1fa18088ad4d4b3e30dcaa1fb0710d56d615", typeof(bool)),
            new TargetRecord("ParseObject", 87, 223, 0, "67711e9f3698d297c59085d58b93ccc195d9bbb4b8893932791a7e53c6d85953"),
            new TargetRecord("EatWhitespace", 53, 116, 0, "a7488ba22ad5b77b95367118d9ad97f31c9e899f40a0a34573e3cc4593f68445"),
            new TargetRecord("ReadAsBytes", 214, 607, 16, "d256ccf33fed399218ffb420508732033b446c233aed3e7834c5e732d2a98773"),
            new TargetRecord("ReadStringValue", 246, 770, 14, "a6cc3d4c2e64012e047832373d0fd406517b2b9a5b4a49ec9f9004c313864bbc", ReadType()),
            new TargetRecord("ReadAsBoolean", 222, 768, 16, "0c8eea55f5f9aea0106aa89f93055153d6a1e46fbf0ce0164cd9175e8c630abb"),
            new TargetRecord("ReadNumberValue", 185, 625, 14, "679744dfe8b4f41c5914cde91be0c3bbf0773ef30a2ea827fac73e24e01b026c", ReadType()),
            new TargetRecord("ParseValue", 282, 692, 0, "5054fa3681cc4f4f95ebf881721e42e97d81211d9bdac2acb06c56eca55daf47")
        };

        private static Type ReadType() => typeof(JsonTextReader).Assembly.GetType("Newtonsoft.Json.ReadType");

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
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonWhitespacePreparation),
                    nameof(ApiJsonWhitespacePreparation.Advance))),
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

            internal TargetRecord(string name, int count, int bytes, int anchor, string bodyHash,
                params Type[] parameters) {
                Count = count;
                Anchor = anchor;
                try {
                    if (typeof(JsonTextReader).Module.ModuleVersionId
                        != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
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
