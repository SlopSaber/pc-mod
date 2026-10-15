using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using HarmonyLib;
using ScoreSaber.Core.Api;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    [HarmonyPatch]
    internal static class ScoreDetailFormatterScopePatch {
        internal static readonly MethodInfo Target = ScoreDetailFormatPreparation.GetFormatHelperTarget();
        private static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ScoreDetailFormatterScopePatch), "Prefix");
        private static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(ScoreDetailFormatterScopePatch), "Finalizer");
        private static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ScoreDetailFormatterScopePatch), "Transpiler");
        private static bool Rewritten;

        internal struct State {
            internal object Previous;
            internal bool Entered;
        }

        private static bool Prepare() => Target != null;
        private static MethodBase TargetMethod() => Target;

        private static void Prefix(ref State __state) {
            __state = default;
            if (ApiJsonFormattingPreparation.IsSelected) {
                ScoreDetailPreparationScope.Enter(ref __state.Previous, ref __state.Entered);
            }
        }

        private static void Finalizer(State __state) {
            if (__state.Entered) { ScoreDetailPreparationScope.Exit(__state.Previous, true); }
        }

        internal static bool IsAccepted() {
            if (Target == null) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            return info == null || info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == PrefixMethod
                && info.Finalizers.Count == 1 && info.Finalizers[0].PatchMethod == FinalizerMethod
                && info.Postfixes.Count == 0 && Rewritten && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == TranspilerMethod;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "AcquireForFormatter");
            if (code.Count != 19 || wrapper == null || code[12].opcode != OpCodes.Call
                || !Equals(code[12].operand, ApiJsonDateBuilderOutputPreparation.AcquireTarget)) { return code; }
            code[12].operand = wrapper;
            Rewritten = true;
            return code;
        }
    }
}
