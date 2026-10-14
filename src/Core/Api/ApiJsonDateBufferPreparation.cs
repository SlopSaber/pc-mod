using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonDateBufferPreparation {
        internal static readonly MethodInfo DateTarget = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.Utilities.DateTimeUtils", "TryParseDateTime",
            new[] { typeof(string), typeof(DateTimeZoneHandling), typeof(string), typeof(CultureInfo), typeof(DateTime).MakeByRefType() },
            223, "c89ee4c19dbe8b5d8c2b040cbb628736fe0ecd040a4c9ebd04fb8c2626e18934");
        internal static readonly MethodInfo OffsetTarget = ApiJsonUnicodeEscapePreparation.Find(
            "Newtonsoft.Json.Utilities.DateTimeUtils", "TryParseDateTimeOffset",
            new[] { typeof(string), typeof(string), typeof(CultureInfo), typeof(DateTimeOffset).MakeByRefType() },
            223, "ed7ed9a9c55bbf0349bf7df74c96c35478479836b5fb6374d473e44452cc9895");
        internal static readonly MethodInfo Copy = FindCopy();
        private static readonly MethodBase[] CopyMethods = FindCopyMethods();

        internal static bool Supported => DateTarget != null && OffsetTarget != null && Copy != null && CopyMethods != null;

        internal static char[] Create(string text) {
            if (text != null && !Thread.CurrentThread.IsThreadPoolThread && CanPrepare() && text.Length >= 524288) {
                Task<char[]> task = null;
                try { Queue(text, out task); }
                catch { }
                if (task != null) {
                    while (!task.IsCompleted) {
                        try { task.Wait(); }
                        catch (ThreadInterruptedException) { }
                        catch (AggregateException) { }
                    }
                    return task.GetAwaiter().GetResult();
                }
            }
            return text.ToCharArray();
        }

        private static bool CanPrepare() {
            try {
                if (!ApiJsonNumericPreparation.IsActive || !Supported || !ApiJsonDateBufferPatch.Rewritten
                    || !ApiJsonDateOffsetBufferPatch.Rewritten
                    || !HasOnlyTranspiler(DateTarget, ApiJsonDateBufferPatch.TranspilerMethod)
                    || !HasOnlyTranspiler(OffsetTarget, ApiJsonDateOffsetBufferPatch.TranspilerMethod)) { return false; }
                foreach (MethodBase method in CopyMethods) {
                    if (Harmony.GetPatchInfo(method) != null) { return false; }
                }
                return true;
            } catch { return false; }
        }

        private static void Queue(string text, out Task<char[]> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => text.ToCharArray(), out task);
        }

        private static bool HasOnlyTranspiler(MethodBase method, MethodInfo patch) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return patch != null && info != null && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1 && info.Transpilers[0].PatchMethod == patch;
        }

        private static MethodInfo FindCopy() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                MethodInfo method = typeof(string).GetMethod("ToCharArray", Type.EmptyTypes);
                byte[] body = method?.GetMethodBody()?.GetILAsByteArray();
                if (body == null || body.Length != 64) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(body)).Replace("-", "").ToLowerInvariant()
                        == "e3393e7e92f7abdab9e13245a32fccb3372b8c9b46b02b93470d092e15b06b46" ? method : null;
                }
            } catch { return null; }
        }

        private static MethodBase[] FindCopyMethods() {
            try {
                Type bytes = typeof(byte).MakePointerType();
                Type chars = typeof(char).MakePointerType();
                MethodBase[] methods = {
                    Copy,
                    typeof(string).GetProperty("Length")?.GetGetMethod(),
                    AccessTools.Method(typeof(string), "wstrcpy", new[] { chars, chars, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "Memmove", new[] { bytes, bytes, typeof(uint) }),
                    AccessTools.Method(typeof(Buffer), "Memcpy", new[] { bytes, bytes, typeof(int) }),
                    AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) })
                };
                foreach (MethodBase method in methods) {
                    if (method == null) { return null; }
                }
                return methods;
            } catch { return null; }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, int expectedCount,
            int expectedCalls, out bool rewritten) {
            rewritten = false;
            var code = new List<CodeInstruction>(instructions);
            if (!Supported || code.Count != expectedCount) { return code; }
            var calls = new List<int>();
            for (int i = 0; i < code.Count; i++) {
                if (code[i].opcode != OpCodes.Callvirt || !Equals(code[i].operand, Copy)) { continue; }
                if (code[i].labels.Count != 0 || code[i].blocks.Count != 0) { return code; }
                calls.Add(i);
            }
            if (calls.Count != expectedCalls) { return code; }
            MethodInfo create = AccessTools.Method(typeof(ApiJsonDateBufferPreparation), "Create");
            if (create == null) { return code; }
            foreach (int i in calls) {
                code[i].opcode = OpCodes.Call;
                code[i].operand = create;
            }
            rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateBufferPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateBufferPatch), "Transpiler");
        internal static volatile bool Rewritten;
        private static bool Prepare() => ApiJsonDateBufferPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBufferPreparation.DateTarget;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            IEnumerable<CodeInstruction> code = ApiJsonDateBufferPreparation.Rewrite(instructions, 84, 1, out bool rewritten);
            Rewritten = rewritten;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonDateOffsetBufferPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonDateOffsetBufferPatch), "Transpiler");
        internal static volatile bool Rewritten;
        private static bool Prepare() => ApiJsonDateBufferPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonDateBufferPreparation.OffsetTarget;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            Rewritten = false;
            IEnumerable<CodeInstruction> code = ApiJsonDateBufferPreparation.Rewrite(instructions, 85, 2, out bool rewritten);
            Rewritten = rewritten;
            return code;
        }
    }
}
