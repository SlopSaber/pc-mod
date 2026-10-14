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
    internal static class ApiJsonLinePreparation {
        internal static readonly MethodInfo LineFeed = Find("ProcessLineFeed", 27,
            "63cecbf284ce39225bead12da2ece85c93c8befbe8893ec0fe393306fa7ab93b", Type.EmptyTypes);
        internal static readonly MethodInfo CarriageReturn = Find("ProcessCarriageReturn", 29,
            "291263db07da8e6c3a2db9a1174533c75ef462a0670568abfb4f108bf87f9efd", new[] { typeof(bool) });
        private static Action<JsonTextReader> _lineFeed;
        private static Action<JsonTextReader, bool> _carriageReturn;

        internal static bool Initialize() {
            try {
                if (_lineFeed != null && _carriageReturn != null) {
                    return true;
                }
                if (LineFeed == null || CarriageReturn == null
                    || Find("OnNewLine", 22, "bc16bb2dfccf64d730a0b284fe7efbc76a348bc5c143417a53120fc82f7ccd8a", new[] { typeof(int) }) == null
                    || Find("SetNewLine", 47, "0f6748abb96dd039b4b07151c093ef1756c622436afae057a2a4636ff541fb4e", new[] { typeof(bool) }) == null
                    || Find("EnsureChars", 27, "9ca600543cf61fe6c72c4ef6e493de164397066ec577f44eb04f0d3cfd743f06", new[] { typeof(int), typeof(bool) }) == null) {
                    return false;
                }
                var lf = (Action<JsonTextReader>)CreateBridge(LineFeed, false).CreateDelegate(typeof(Action<JsonTextReader>));
                var cr = (Action<JsonTextReader, bool>)CreateBridge(CarriageReturn, true).CreateDelegate(typeof(Action<JsonTextReader, bool>));
                _lineFeed = lf;
                _carriageReturn = cr;
                return true;
            } catch { return false; }
        }

        internal static void ReadLineFeed(JsonTextReader reader) => _lineFeed(reader);
        internal static void ReadCarriageReturn(JsonTextReader reader, bool append) => _carriageReturn(reader, append);

        private static DynamicMethod CreateBridge(MethodInfo original, bool carriageReturn) {
            var parameters = carriageReturn ? new[] { typeof(JsonTextReader), typeof(bool) } : new[] { typeof(JsonTextReader) };
            var bridge = new DynamicMethod("PrepareBufferedLines", typeof(void), parameters, typeof(ApiJsonLinePreparation).Module, true);
            ILGenerator il = bridge.GetILGenerator();
            Label complete = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            EmitField(il, "_chars", typeof(char[]), false);
            EmitField(il, "_charsUsed", typeof(int), false);
            il.Emit(OpCodes.Ldc_I4, carriageReturn ? '\r' : '\n');
            EmitField(il, "_charPos", typeof(int), true);
            EmitField(il, "_lineNumber", typeof(int), true);
            EmitField(il, "_lineStartPos", typeof(int), true);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(ApiJsonLinePreparation), nameof(TryPrepare)));
            il.Emit(OpCodes.Brtrue, complete);
            il.Emit(OpCodes.Ldarg_0);
            if (carriageReturn) {
                il.Emit(OpCodes.Ldarg_1);
            }
            il.Emit(OpCodes.Call, original);
            il.MarkLabel(complete);
            il.Emit(OpCodes.Ret);
            return bridge;
        }

        private static void EmitField(ILGenerator il, string name, Type type, bool address) {
            FieldInfo field = AccessTools.Field(typeof(JsonTextReader), name);
            if (field == null || field.FieldType != type) {
                throw new InvalidOperationException();
            }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(address ? OpCodes.Ldflda : OpCodes.Ldfld, field);
        }

        private static bool TryPrepare(JsonTextReader reader, char[] chars, int used, char expected,
            ref int position, ref int lineNumber, ref int lineStart) {
            if (!ReferenceEquals(reader, ApiJsonNumericPreparation.CurrentReader)
                || Thread.CurrentThread.IsThreadPoolThread || chars == null || position < 0
                || used > chars.Length || used - position < 524288 || chars[position] != expected
                || !HasPrefix(chars, position)) {
                return false;
            }
            Task<Lines> task = null;
            try { Queue(chars, position, used - position, out task); }
            catch { }
            if (task == null) {
                return false;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            Lines result;
            try { result = task.GetAwaiter().GetResult(); }
            catch { return false; }
            if (result.Count == 0) {
                return false;
            }
            position += result.Characters;
            lineNumber = unchecked(lineNumber + result.Count);
            lineStart = position;
            return true;
        }

        private static bool HasPrefix(char[] chars, int start) {
            for (int i = 0; i < 1024; i++) {
                char value = chars[start + i];
                if (value != '\r' && value != '\n') {
                    return false;
                }
            }
            return true;
        }

        private static void Queue(char[] chars, int start, int length, out Task<Lines> task) {
            var ownedChars = new char[length];
            Array.Copy(chars, start, ownedChars, 0, length);
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Scan(ownedChars), out task);
        }

        private static Lines Scan(char[] chars) {
            int position = 0;
            int lines = 0;
            while (position < chars.Length) {
                if (chars[position] == '\n') {
                    position++;
                } else if (chars[position] == '\r') {
                    // Original EnsureChars(1) after CR requires two buffered characters ahead.
                    if (chars.Length - position <= 2) {
                        break;
                    }
                    position++;
                    if (chars[position] == '\n') {
                        position++;
                    }
                } else {
                    break;
                }
                lines++;
            }
            return new Lines(position, lines);
        }

        internal static MethodInfo Find(string name, int bytes, string expected, Type[] parameters) {
            try {
                if (typeof(JsonTextReader).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) {
                    return null;
                }
                MethodInfo method = typeof(JsonTextReader).GetMethod(name, BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic, null, parameters, null);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != bytes) {
                    return null;
                }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant() == expected ? method : null;
                }
            } catch { return null; }
        }

        private readonly struct Lines {
            internal readonly int Characters;
            internal readonly int Count;
            internal Lines(int characters, int count) { Characters = characters; Count = count; }
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonLineCallPatch {
        private static readonly Caller[] Callers = {
            new Caller("ParsePostValue", 158, 411, 9, "c8887e28866738e3a5dbf1a3d25c1fa18088ad4d4b3e30dcaa1fb0710d56d615", typeof(bool)),
            new Caller("ParseObject", 87, 223, 9, "67711e9f3698d297c59085d58b93ccc195d9bbb4b8893932791a7e53c6d85953"),
            new Caller("EatWhitespace", 53, 116, 9, "a7488ba22ad5b77b95367118d9ad97f31c9e899f40a0a34573e3cc4593f68445"),
            new Caller("ReadAsBytes", 214, 607, 9, "d256ccf33fed399218ffb420508732033b446c233aed3e7834c5e732d2a98773"),
            new Caller("ReadStringValue", 246, 770, 9, "a6cc3d4c2e64012e047832373d0fd406517b2b9a5b4a49ec9f9004c313864bbc", ReadType()),
            new Caller("ReadAsBoolean", 222, 768, 9, "0c8eea55f5f9aea0106aa89f93055153d6a1e46fbf0ce0164cd9175e8c630abb"),
            new Caller("ReadNumberValue", 185, 625, 9, "679744dfe8b4f41c5914cde91be0c3bbf0773ef30a2ea827fac73e24e01b026c", ReadType()),
            new Caller("ParseValue", 282, 692, 9, "5054fa3681cc4f4f95ebf881721e42e97d81211d9bdac2acb06c56eca55daf47"),
            new Caller("ParseComment", 174, 423, 10, "1200b35cf5e4b44a69eb4f428c0f3d5344ff775b9cb875e4ceecf308a7ed5ca2", typeof(bool)),
            new Caller("ReadStringIntoBuffer", 300, 759, 7, "f0a7f881376b6b65110e136070155d5dd8c5b02d4721cb7fb4b45a8b176dcb59", typeof(char)),
        };
        private static Type ReadType() => typeof(JsonTextReader).Assembly.GetType("Newtonsoft.Json.ReadType");

        private static bool Prepare() {
            if (!ApiJsonLinePreparation.Initialize()) {
                return false;
            }
            foreach (Caller caller in Callers) {
                if (caller.Method != null) {
                    return true;
                }
            }
            return false;
        }
        private static IEnumerable<MethodBase> TargetMethods() {
            var result = new List<MethodBase>();
            foreach (Caller caller in Callers) {
                if (caller.Method != null) { result.Add(caller.Method); }
            }
            return result;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
            var code = new List<CodeInstruction>(instructions);
            Caller selected = null;
            foreach (Caller caller in Callers) {
                if (Equals(caller.Method, __originalMethod)) { selected = caller; break; }
            }
            if (selected == null || (code.Count != selected.Count && code.Count != selected.Count + selected.Extra)) {
                return code;
            }
            int lf = -1;
            int cr = -1;
            for (int i = 0; i < code.Count; i++) {
                if (code[i].opcode != OpCodes.Call) { continue; }
                if (Equals(code[i].operand, ApiJsonLinePreparation.LineFeed)) {
                    if (lf >= 0) { return code; }
                    lf = i;
                } else if (Equals(code[i].operand, ApiJsonLinePreparation.CarriageReturn)) {
                    if (cr >= 0) { return code; }
                    cr = i;
                }
            }
            if (lf < 0 || cr < 0) { return code; }
            code[lf].operand = AccessTools.Method(typeof(ApiJsonLinePreparation), nameof(ApiJsonLinePreparation.ReadLineFeed));
            code[cr].operand = AccessTools.Method(typeof(ApiJsonLinePreparation), nameof(ApiJsonLinePreparation.ReadCarriageReturn));
            return code;
        }
        private sealed class Caller {
            internal readonly MethodInfo Method;
            internal readonly int Count;
            internal readonly int Extra;
            internal Caller(string name, int count, int bytes, int extra, string hash, params Type[] parameters) {
                Count = count;
                Extra = extra;
                Method = ApiJsonLinePreparation.Find(name, bytes, hash, parameters);
            }
        }
    }
}
