namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailPreparationScope {
        internal static void Enter(ref object previous, ref bool entered) {
            try { EnterFrame(ref previous, ref entered); }
            catch { }
        }

        internal static void Exit(object previous, bool entered) {
            ExitFrame(previous, entered);
        }

        static partial void EnterFrame(ref object previous, ref bool entered);
        static partial void ExitFrame(object previous, bool entered);
    }
}
