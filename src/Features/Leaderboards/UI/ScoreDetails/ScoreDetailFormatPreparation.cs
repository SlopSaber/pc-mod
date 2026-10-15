using System;
using System.Threading;
using ScoreSaber.Core.Api;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailFormatPreparation {
        internal static string Format(string format, object first, object second) {
            object previous = null;
            bool entered = false;
            bool previousFormatting = false;
            bool formattingEntered = false;
            try {
                if (first is string text && text.Length >= 524288 && second is double
                    && !Thread.CurrentThread.IsThreadPoolThread) {
                    previousFormatting = ApiJsonFormattingPreparation.EnterSelected();
                    formattingEntered = true;
                    Enter(ref previous, ref entered);
                }
            } catch { }
            try {
                return string.Format(format, first, second);
            } finally {
                try {
                    Exit(previous, entered);
                } finally {
                    if (formattingEntered) { ApiJsonFormattingPreparation.ExitSelected(previousFormatting); }
                }
            }
        }

        static partial void Enter(ref object previous, ref bool entered);
        static partial void Exit(object previous, bool entered);

        internal static string FormatNumber(string format, object value) {
            bool previous = ApiJsonFormattingPreparation.EnterSelected();
            try {
                return string.Format(format, value);
            } finally {
                ApiJsonFormattingPreparation.ExitSelected(previous);
            }
        }
    }
}
