using System;
using System.Globalization;

namespace ScoreSaber.Features.Leaderboards.Domain {
    internal sealed class PreparedLeaderboardPlayerName {
        private readonly string _name, _modifiers, _withoutPP, _withPP;
        private readonly double _pp, _accuracy;
        private readonly int _rank, _rankDigits;

        private PreparedLeaderboardPlayerName(string name, string modifiers, double pp, double accuracy, string withoutPP, string withPP, int rank, int rankDigits) {
            _name = name;
            _modifiers = modifiers;
            _pp = pp;
            _accuracy = accuracy;
            _withoutPP = withoutPP;
            _withPP = withPP;
            _rank = rank;
            _rankDigits = rankDigits;
        }

        internal static bool CanUseCulture(CultureInfo culture) => culture != null && culture.GetType() == typeof(CultureInfo) &&
            culture.IsReadOnly && culture.NumberFormat.GetType() == typeof(NumberFormatInfo) && culture.NumberFormat.IsReadOnly;

        internal static PreparedLeaderboardPlayerName TryCreate(LeaderboardScore score, string modifierText, double accuracyValue) {
            try {
                if (score?.Player == null) return null;
                string playerName = score.Player.Name;
                double ppValue = score.PP;
                bool hasMods = !string.IsNullOrEmpty(modifierText);
                string name = $"<size=80%>{playerName}</size>";
                string accuracy = $"<size=70%>(<color=#FFD42A>{accuracyValue}%</color>)</size>";
                string pp = $"<size=70%>(<color=#6772E5>{ppValue}<size=45%>pp</size></color>)</size>";
                string modifiers = $"<size=70%><color=#6F6F6F>[{modifierText}]</color></size>";
                string withoutPP = $"{name} - {accuracy}";
                string withPP = ppValue > 0 ? $"{withoutPP} - {pp}" : withoutPP;
                if (hasMods) {
                    withoutPP = $"{withoutPP} {modifiers}";
                    withPP = $"{withPP} {modifiers}";
                }
                int rank = score.Rank;
                int rankDigits = rank <= 0 ? 1 : rank.ToString().Length;
                return new PreparedLeaderboardPlayerName(playerName, modifierText, ppValue, accuracyValue, withoutPP, withPP, rank, rankDigits);
            } catch (Exception) {
                return null;
            }
        }

        internal bool TryGetRankDigitCount(int rank, out int digits) {
            digits = _rankDigits;
            return rank == _rank;
        }

        internal bool TryGet(LeaderboardScore score, string modifierText, double accuracy, out string withoutPP, out string withPP) {
            withoutPP = null;
            withPP = null;
            if (score?.Player == null || !ReferenceEquals(_name, score.Player.Name) || !ReferenceEquals(_modifiers, modifierText) ||
                BitConverter.DoubleToInt64Bits(_pp) != BitConverter.DoubleToInt64Bits(score.PP) ||
                BitConverter.DoubleToInt64Bits(_accuracy) != BitConverter.DoubleToInt64Bits(accuracy)) return false;
            withoutPP = _withoutPP;
            withPP = _withPP;
            return true;
        }
    }
}
