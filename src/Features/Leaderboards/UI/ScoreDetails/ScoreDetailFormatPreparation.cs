using System;
using System.Threading;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailFormatPreparation {
        internal static string Format(string format, object first, object second) {
            object previous = null;
            bool entered = false;
            try {
                if (first is string text && text.Length >= 524288 && second is double
                    && !Thread.CurrentThread.IsThreadPoolThread) {
                    Enter(ref previous, ref entered);
                }
            } catch { }
            try {
                return string.Format(format, first, second);
            } finally {
                Exit(previous, entered);
            }
        }

        static partial void Enter(ref object previous, ref bool entered);
        static partial void Exit(object previous, bool entered);
    }
}
