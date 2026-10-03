using ScoreSaber.Core.Gameplay;

namespace ScoreSaber.Features.Leaderboards.Domain {
    internal sealed class PreparedLeaderboardScore {
        internal LeaderboardScore Score { get; }
        internal string ModifierText { get; }
        internal PreparedGameplayModifiers Modifiers { get; }
        internal double Accuracy { get; }
        internal bool HasLocalReplay { get; }

        internal PreparedLeaderboardScore(LeaderboardScore score, string modifierText, PreparedGameplayModifiers modifiers, double accuracy, bool hasLocalReplay) {
            Score = score;
            ModifierText = modifierText;
            Modifiers = modifiers;
            Accuracy = accuracy;
            HasLocalReplay = hasLocalReplay;
        }
    }
}
