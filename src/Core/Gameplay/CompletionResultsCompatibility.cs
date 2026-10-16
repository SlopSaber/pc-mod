namespace ScoreSaber.Core.Gameplay {
    internal static class CompletionResultsCompatibility {
        internal static int GetModifiedScore(LevelCompletionResults results) {
#if BEAT_SABER_1453
            return results.score.modified;
#else
            return results.modifiedScore;
#endif
        }

        internal static int GetMultipliedScore(LevelCompletionResults results) {
#if BEAT_SABER_1453
            return results.score.multiplied;
#else
            return results.multipliedScore;
#endif
        }

        internal static int GetMissedCount(LevelCompletionResults results) {
#if BEAT_SABER_1453
            return results.missedCutsCount;
#else
            return results.missedCount;
#endif
        }
    }
}
