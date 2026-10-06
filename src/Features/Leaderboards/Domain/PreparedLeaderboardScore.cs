using ScoreSaber.Core.Gameplay;

namespace ScoreSaber.Features.Leaderboards.Domain {
    internal sealed class PreparedLeaderboardScore {
        internal LeaderboardScore Score { get; }
        internal string ModifierText { get; }
        internal PreparedGameplayModifiers Modifiers { get; }
        internal double Accuracy { get; }
        internal bool HasLocalReplay { get; }
        internal PreparedLeaderboardPlayerName PlayerName { get; }

        internal PreparedLeaderboardScore(LeaderboardScore score, string modifierText, PreparedGameplayModifiers modifiers, double accuracy, bool hasLocalReplay) :
            this(score, modifierText, modifiers, accuracy, hasLocalReplay, null) {
        }

        internal PreparedLeaderboardScore(LeaderboardScore score, string modifierText, PreparedGameplayModifiers modifiers, double accuracy, bool hasLocalReplay, PreparedLeaderboardPlayerName playerName) {
            Score = score;
            ModifierText = modifierText;
            Modifiers = modifiers;
            Accuracy = accuracy;
            HasLocalReplay = hasLocalReplay;
            PlayerName = playerName;
        }
    }
}
