using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonSelectedStringAllocationPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static MethodInfo AllocationTarget;
        private static Func<int, string> Allocator;
        private static MethodBase[] Guards;
        private static bool Rewritten;
        internal static bool Supported => Target != null && Allocator != null && Guards != null;

        internal static bool IsAccepted() {
            if (!Supported) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info != null && (!Rewritten || info.Prefixes.Count != 0 || info.Postfixes.Count != 0
                || info.Finalizers.Count != 0 || info.Transpilers.Count != 1
                || info.Transpilers[0].PatchMethod != ApiJsonSelectedStringAllocationPatch.TranspilerMethod)) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        internal static string Allocate(int length) {
            bool admitted = false;
            try {
                admitted = length >= 524288 && length <= int.MaxValue / 2 && ApiJsonFormattingPreparation.IsSelected
                    && !Thread.CurrentThread.IsThreadPoolThread && Supported && Rewritten && IsAccepted();
            } catch { }
            if (!admitted) { return Allocator(length); }
            Task<string> task = null;
            try { }
            finally {
                try {
                    try { Queue(length, out task); }
                    catch { }
                } finally {
                    if (task != null) {
                        while (!task.IsCompleted) {
                            try { task.Wait(); }
                            catch (ThreadInterruptedException) { }
                            catch (AggregateException) { }
                        }
                    }
                }
            }
            if (task == null) { return Allocator(length); }
            return task.GetAwaiter().GetResult();
        }

        private static void Queue(int length, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => Allocator(length), out task);
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonSelectedStringAllocationPreparation), "Allocate");
            if (!Supported || code.Count != 77 || wrapper == null || code[7].opcode != OpCodes.Call
                || !Equals(code[7].operand, AllocationTarget)) { return code; }
            code[7].operand = wrapper;
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                MethodInfo target = AccessTools.Method(typeof(StringBuilder), "ToString", Type.EmptyTypes);
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 163) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        != "d296a535d7bd32e890da35d49e795a8e64fb490cb04710a8890be0851ac567ff") { return null; }
                }
                AllocationTarget = AccessTools.Method(typeof(string), "FastAllocateString", new[] { typeof(int) });
                if (AllocationTarget == null || !AllocationTarget.IsStatic || AllocationTarget.ReturnType != typeof(string)
                    || (AllocationTarget.MethodImplementationFlags & MethodImplAttributes.InternalCall) == 0
                    || AllocationTarget.GetMethodBody() != null) { return null; }
                var guards = new List<MethodBase> { AllocationTarget, AccessTools.PropertyGetter(typeof(ApiJsonFormattingPreparation), "IsSelected") };
                foreach (Type type in new[] { typeof(ApiJsonSelectedStringAllocationPreparation), typeof(ApiJsonSelectedStringAllocationPatch),
                    typeof(Func<int, string>), typeof(ReplayStorageService) }) {
                    AddDeclaredGuards(guards, type);
                    foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) { AddDeclaredGuards(guards, nested); }
                }
                foreach (MethodInfo method in typeof(ReplayStorageService).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "TryQueueOwnedPreparationWhenIdle" && method.IsGenericMethodDefinition) {
                        guards.Add(method.MakeGenericMethod(typeof(string)));
                    }
                }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Guards = guards.ToArray();
                var allocation = new DynamicMethod("AllocateSelectedFormatterString", typeof(string), new[] { typeof(int) }, typeof(ApiJsonSelectedStringAllocationPreparation), true);
                ILGenerator il = allocation.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, AllocationTarget); il.Emit(OpCodes.Ret);
                Allocator = (Func<int, string>)allocation.CreateDelegate(typeof(Func<int, string>));
                return target;
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            guards.AddRange(type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            guards.AddRange(type.GetConstructors(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonSelectedStringAllocationPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonSelectedStringAllocationPatch), "Transpiler");
        private static bool Prepare() => ApiJsonSelectedStringAllocationPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonSelectedStringAllocationPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonSelectedStringAllocationPreparation.Rewrite(instructions);
    }
}
