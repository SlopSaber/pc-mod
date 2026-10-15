using System.Reflection;
using HarmonyLib;
using ScoreSaber.Core.Api;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    [HarmonyPatch]
    internal static class ScoreDetailFormatterScopePatch {
        internal static readonly MethodInfo Target = ScoreDetailFormatPreparation.GetFormatHelperTarget();
        private static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ScoreDetailFormatterScopePatch), "Prefix");
        private static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(ScoreDetailFormatterScopePatch), "Finalizer");

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
                && info.Postfixes.Count == 0 && info.Transpilers.Count == 0;
        }
    }
}
