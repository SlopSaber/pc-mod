using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonWriterCopyPreparation {
        private delegate char[] EnsureBuffer(IArrayPool<char> pool, int size, char[] previous);
        private delegate char[] RentBuffer(IArrayPool<char> pool, int size);
        private static MethodInfo EnsureTarget;
        private static MethodInfo RentTarget;
        private static MethodInfo CopyTarget;
        private static MethodInfo WriteTarget;
        private static MethodBase[] Guards;
        private static readonly MethodInfo RentPostfix = AccessTools.Method(typeof(ApiJsonBufferRentPatch), "Postfix");
        private static readonly Bindings Originals = Initialize();
        private static bool Rewritten;

        [ThreadStatic]
        private static Frame _frame;

        internal sealed class Frame {
            internal readonly string Text;
            internal char[] Buffer;

            internal Frame(string text) => Text = text;
        }

        private sealed class Bindings {
            internal readonly EnsureBuffer Ensure;
            internal readonly RentBuffer Rent;

            internal Bindings(EnsureBuffer ensure, RentBuffer rent) {
                Ensure = ensure;
                Rent = rent;
            }
        }

        internal static Frame Enter(string text, IArrayPool<char> pool) {
            Frame previous = _frame;
            _frame = null;
            try {
                if (ApiJsonEscapePreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread
                    && text != null && text.Length >= 524288 && pool == null && IsUnchanged()) {
                    _frame = new Frame(text);
                }
            } catch { }
            return previous;
        }

        internal static void Restore(Frame previous) => _frame = previous;

        private static char[] Ensure(IArrayPool<char> pool, int size, char[] previous) {
            Frame frame = _frame;
            if (frame != null) { frame.Buffer = null; }
            char[] result = Originals.Ensure(pool, size, previous);
            Record(frame, pool, result);
            return result;
        }

        private static char[] Rent(IArrayPool<char> pool, int size) {
            Frame frame = _frame;
            if (frame != null) { frame.Buffer = null; }
            char[] result = Originals.Rent(pool, size);
            Record(frame, pool, result);
            return result;
        }

        private static void Record(Frame frame, IArrayPool<char> pool, char[] buffer) {
            try {
                if (frame != null && ReferenceEquals(frame, _frame) && pool == null && buffer != null && IsUnchanged()) {
                    frame.Buffer = buffer;
                }
            } catch { }
        }

        private static void Write(TextWriter writer, char[] buffer, int start, int count) {
            Frame frame = _frame;
            bool privateInput = frame != null && ReferenceEquals(frame.Buffer, buffer);
            if (privateInput) { frame.Buffer = null; }
            if (ApiJsonOwnedOutputPreparation.TryWriteArray(writer, buffer, start, count, privateInput)) { return; }
            writer.Write(buffer, start, count);
        }

        private static void Copy(string text, int sourceIndex, char[] destination, int destinationIndex, int count,
            IArrayPool<char> pool) {
            bool eligible = false;
            try {
                Frame frame = _frame;
                eligible = frame != null && pool == null && ReferenceEquals(frame.Text, text)
                    && destination != null && ReferenceEquals(frame.Buffer, destination)
                    && sourceIndex >= 0 && destinationIndex >= 0 && count >= 524288 && count <= int.MaxValue / 2
                    && sourceIndex <= text.Length - count && destinationIndex <= destination.Length - count
                    && !Thread.CurrentThread.IsThreadPoolThread && IsUnchanged();
            } catch { }
            Task<bool> task = null;
            if (eligible) {
                try { Queue(text, sourceIndex, destination, destinationIndex, count, out task); }
                catch { }
            }
            if (task == null) {
                text.CopyTo(sourceIndex, destination, destinationIndex, count);
                return;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            task.GetAwaiter().GetResult();
        }

        private static void Queue(string text, int sourceIndex, char[] destination, int destinationIndex, int count,
            out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => {
                text.CopyTo(sourceIndex, destination, destinationIndex, count);
                return true;
            }, out task);
        }

        private static bool IsUnchanged() {
            if (!Rewritten || Originals == null || Guards == null
                || !ApiJsonUnicodeFormattingPatch.IsWriterUnchanged(ApiJsonUnicodeFormattingPreparation.Target)) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(RentTarget);
            if (info != null && (info.Prefixes.Count != 0 || info.Finalizers.Count != 0 || info.Transpilers.Count != 0
                || info.Postfixes.Count > 1 || (info.Postfixes.Count == 1 && info.Postfixes[0].PatchMethod != RentPostfix))) { return false; }
            foreach (MethodBase method in Guards) { if (Harmony.GetPatchInfo(method) != null) { return false; } }
            return true;
        }

        private static Bindings Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")
                    || typeof(JsonConvert).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) { return null; }
                Type buffers = typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Utilities.BufferUtils");
                EnsureTarget = Find(buffers, "EnsureBufferSize", 28, "f9ccaa8b5962490bb0e4560d90b94918f7e58226145baa4a401b32ed6ca3f7d4",
                    typeof(IArrayPool<char>), typeof(int), typeof(char[]));
                RentTarget = Find(buffers, "RentBuffer", 18, "56f93f9dacf17a2d51ccc69d6c9b2eaded31be3a0927e2d8d02a09b4b6f58ef5",
                    typeof(IArrayPool<char>), typeof(int));
                CopyTarget = Find(typeof(string), "CopyTo", 171, "2c96d27975aa2a661d8fbd40dc7dcacf79c93eb37285203fb68ef58e44507ca2",
                    typeof(int), typeof(char[]), typeof(int), typeof(int));
                WriteTarget = typeof(TextWriter).GetMethod("Write", new[] { typeof(char[]), typeof(int), typeof(int) });
                var guards = new List<MethodBase> { EnsureTarget, CopyTarget, RentPostfix,
                    AccessTools.Method(typeof(ApiJsonEscapePreparation), "Serialize"),
                    AccessTools.PropertyGetter(typeof(ApiJsonEscapePreparation), "IsActive"),
                    AccessTools.PropertyGetter(typeof(ApiJsonNumericPreparation), "CurrentReader") };
                AddCopyGuards(guards);
                guards.AddRange(typeof(ApiJsonWriterCopyPreparation).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                guards.AddRange(typeof(ApiJsonUnicodeFormattingPatch).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                guards.AddRange(typeof(ApiJsonBufferGrowthPreparation).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                foreach (Type owner in new[] { typeof(ApiJsonWriterCopyPreparation), typeof(ApiJsonBufferGrowthPreparation) }) {
                    foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                        guards.AddRange(nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                        guards.AddRange(nested.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    }
                }
                foreach (MethodInfo method in typeof(ReplayStorageService).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "TryQueueOwnedPreparationWhenIdle" && method.IsGenericMethodDefinition) {
                        guards.Add(method);
                        guards.Add(method.MakeGenericMethod(typeof(bool)));
                    }
                }
                if (EnsureTarget == null || RentTarget == null || CopyTarget == null || WriteTarget == null) { return null; }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Guards = guards.ToArray();
                return new Bindings(Bind<EnsureBuffer>(EnsureTarget), Bind<RentBuffer>(RentTarget));
            } catch { return null; }
        }

        private static void AddCopyGuards(List<MethodBase> guards) {
            Type bytes = typeof(byte).MakePointerType();
            Type chars = typeof(char).MakePointerType();
            guards.Add(Find(typeof(string), "get_Length", 7, "4e983afa6e94b8b9e9d31249af4a4def459aabbf91df59838ddb074a0f98b7dc"));
            guards.Add(Find(typeof(string), "wstrcpy", 11, "65659e9fe09fc14c371dd0b463b20fe4cc5b5e2dff19f789614193fbe29ad50b", chars, chars, typeof(int)));
            guards.Add(Find(typeof(Buffer), "Memmove", 36, "9386830bac6bcc24316b9f8712ecaae3bbf8f7757037b70d19db43ce88a3284c", bytes, bytes, typeof(uint)));
            guards.Add(Find(typeof(Buffer), "Memcpy", 150, "4f78506baa4a0b3e633075486380649f18f333947fd1fb2f35d6747ba8fb65b6", bytes, bytes, typeof(int)));
            guards.Add(AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) }));
            guards.Add(AccessTools.Method(typeof(string).Assembly.GetType("System.Runtime.RuntimeImports"), "Memmove", new[] { bytes, bytes, typeof(uint) }));
            guards.Add(AccessTools.Method(typeof(Interlocked), "Read", new[] { typeof(long).MakeByRefType() }));
            foreach (string name in new[] { "memcpy1", "memcpy2", "memcpy4" }) {
                guards.Add(AccessTools.Method(typeof(Buffer), name, new[] { bytes, bytes, typeof(int) }));
            }
        }

        private static T Bind<T>(MethodInfo method) where T : class {
            MethodInfo invoke = typeof(T).GetMethod("Invoke");
            ParameterInfo[] parameters = invoke.GetParameters();
            var types = new Type[parameters.Length];
            for (int index = 0; index < parameters.Length; index++) { types[index] = parameters[index].ParameterType; }
            var bridge = new DynamicMethod("CallOriginalWriterBuffer", invoke.ReturnType, types, typeof(ApiJsonWriterCopyPreparation), true);
            ILGenerator il = bridge.GetILGenerator();
            for (int index = 0; index < parameters.Length; index++) { il.Emit(OpCodes.Ldarg, (short)index); }
            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ret);
            return (T)(object)bridge.CreateDelegate(typeof(T));
        }

        private static MethodInfo Find(Type type, string name, int size, string expected, params Type[] parameters) {
            MethodInfo method = AccessTools.Method(type, name, parameters);
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != size) { return null; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected ? method : null;
            }
        }

        internal static List<CodeInstruction> Rewrite(List<CodeInstruction> code) {
            Rewritten = false;
            if (Originals == null || code.Count != 306) { return code; }
            int[] ensures = { 36, 155, 285 };
            int[] copies = { 44, 230, 293 };
            int[] writes = { 50, 238, 254, 299 };
            foreach (int index in ensures) { if (!Equals(code[index].operand, EnsureTarget) || code[index].opcode != OpCodes.Call) { return code; } }
            foreach (int index in copies) { if (!Equals(code[index].operand, CopyTarget) || code[index].opcode != OpCodes.Callvirt) { return code; } }
            foreach (int index in writes) { if (!Equals(code[index].operand, WriteTarget) || code[index].opcode != OpCodes.Callvirt) { return code; } }
            if (!Equals(code[206].operand, RentTarget) || code[206].opcode != OpCodes.Call) { return code; }
            MethodInfo ensure = AccessTools.Method(typeof(ApiJsonWriterCopyPreparation), "Ensure");
            MethodInfo rent = AccessTools.Method(typeof(ApiJsonWriterCopyPreparation), "Rent");
            MethodInfo copy = AccessTools.Method(typeof(ApiJsonWriterCopyPreparation), "Copy");
            MethodInfo write = AccessTools.Method(typeof(ApiJsonWriterCopyPreparation), "Write");
            if (ensure == null || rent == null || copy == null || write == null) { return code; }
            foreach (int index in ensures) { code[index].operand = ensure; }
            code[206].operand = rent;
            foreach (int index in writes) { code[index].opcode = OpCodes.Call; code[index].operand = write; }
            for (int at = copies.Length - 1; at >= 0; at--) {
                int index = copies[at];
                var pool = new CodeInstruction(OpCodes.Ldarg_S, (byte)6);
                pool.labels.AddRange(code[index].labels);
                code[index].labels.Clear();
                code[index].opcode = OpCodes.Call;
                code[index].operand = copy;
                code.Insert(index, pool);
            }
            Rewritten = true;
            return code;
        }
    }
}
