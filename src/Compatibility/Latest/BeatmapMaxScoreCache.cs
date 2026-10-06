#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Legato.Beatmaps {
    internal class BeatmapMaxScoreCache {
        private readonly Dictionary<BeatmapKey, int> _cache = new Dictionary<BeatmapKey, int>();
        private readonly BeatmapLevelLoader _beatmapLevelLoader;
        private readonly BeatmapDataLoader _beatmapDataLoader;
        private readonly BeatmapLevelsEntitlementModel _beatmapLevelsEntitlementModel;

        public BeatmapMaxScoreCache(BeatmapLevelLoader beatmapLevelLoader, BeatmapDataLoader beatmapDataLoader, BeatmapLevelsEntitlementModel beatmapLevelsEntitlementModel) {
            _beatmapLevelLoader = beatmapLevelLoader;
            _beatmapDataLoader = beatmapDataLoader;
            _beatmapLevelsEntitlementModel = beatmapLevelsEntitlementModel;
        }

        public async Task<int> GetMaxScore(BeatmapLevel beatmapLevel, BeatmapKey beatmapKey) {
            if (_cache.TryGetValue(beatmapKey, out int cachedScore)) {
                return cachedScore;
            }

            var beatmapLevelDataVersion = await _beatmapLevelsEntitlementModel.GetLevelDataVersionAsync(beatmapKey.levelId, CancellationToken.None);
            var beatmapLevelData = (await _beatmapLevelLoader.LoadBeatmapLevelDataAsync(beatmapLevel, beatmapLevelDataVersion, CancellationToken.None)).beatmapLevelData;
            if (beatmapLevelData == null) {
                throw new InvalidOperationException($"Beatmap data is unavailable for {beatmapKey.levelId}");
            }

            var beatmapData = await _beatmapDataLoader.LoadBeatmapDataAsync(
                beatmapLevelData: beatmapLevelData,
                beatmapKey: beatmapKey,
                startBpm: beatmapLevel.beatsPerMinute,
                loadingForDesignatedEnvironment: false,
                originalEnvironmentInfo: null,
                targetEnvironmentInfo: null,
                beatmapLevelDataVersion: beatmapLevelDataVersion,
                gameplayModifiers: null,
                playerSpecificSettings: null);
            if (beatmapData == null) {
                throw new InvalidOperationException($"Beatmap could not be loaded for {beatmapKey.levelId}");
            }

            int maxScore = ComputeMaximumScore(beatmapData);
            _cache[beatmapKey] = maxScore;
            return maxScore;
        }

        private static int ComputeMaximumScore(IReadonlyBeatmapData beatmap)
        {
            if (Thread.CurrentThread.IsThreadPoolThread || HasPatchedScoreCalculation())
            {
                return ScoreModel.ComputeMaxMultipliedScoreForBeatmap(beatmap);
            }

            RuntimeHelpers.RunClassConstructor(typeof(ScoreModel).TypeHandle);
            var notes = beatmap.GetBeatmapDataItems<NoteData>(0);
            var sliders = beatmap.GetBeatmapDataItems<SliderData>(0);
            var captured = new List<CapturedScoreInput>(1000);

            foreach (var note in notes)
            {
                if ((int)note.scoringType == -1 || (int)note.scoringType == 0) continue;
                var scoringType = note.scoringType;
                float time = note.time;
                captured.Add(new CapturedScoreInput(time, ScoreModel.GetNoteScoreDefinition(scoringType)));
            }

            foreach (var slider in sliders)
            {
                if ((int)slider.sliderType != 1) continue;
                for (int slice = 1; slice < slider.sliceCount; slice++)
                {
                    float t = (float)slice / (slider.sliceCount - 1);
                    float time = Mathf.LerpUnclamped(slider.time, slider.tailTime, t);
                    captured.Add(new CapturedScoreInput(time, ScoreModel.GetNoteScoreDefinition((NoteData.ScoringType)5)));
                }
            }

            // Enumerators may change score definitions, so copy scores after both finish.
            var owned = new OwnedScoreInput[captured.Count];
            for (int index = 0; index < captured.Count; index++)
            {
                var input = captured[index];
                owned[index] = new OwnedScoreInput(input.Time, input.Definition.maxCutScore);
            }

            if (owned.Length < 128) return ComputeOwnedMaximumScore(owned);

            Task<int> task;
            if (ExecutionContext.IsFlowSuppressed())
            {
                task = StartScorePreparation(owned);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    task = StartScorePreparation(owned);
                }
            }

            return task.GetAwaiter().GetResult();
        }

        private static Task<int> StartScorePreparation(OwnedScoreInput[] owned)
        {
            return Task.Factory.StartNew(
                static state => ComputeOwnedMaximumScore((OwnedScoreInput[])state!),
                owned,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private static int ComputeOwnedMaximumScore(OwnedScoreInput[] owned)
        {
            Array.Sort(owned);
            int score = 0;
            int multiplier = 1;
            int progress = 0;
            int maxProgress = 2;
            foreach (var input in owned)
            {
                if (multiplier < 8)
                {
                    if (progress < maxProgress) progress++;
                    if (progress >= maxProgress)
                    {
                        multiplier *= 2;
                        progress = 0;
                        maxProgress = multiplier * 2;
                    }
                }

                score = unchecked(score + input.MaxCutScore * multiplier);
            }

            return score;
        }

        private static bool HasPatchedScoreCalculation()
        {
            foreach (var method in global::HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                var type = method.DeclaringType;
                string name = method.Name;
                if (type == typeof(ScoreModel) &&
                    (name == nameof(ScoreModel.ComputeMaxMultipliedScoreForBeatmap) ||
                     name == nameof(ScoreModel.GetNoteScoreDefinition) || name == ".cctor")) return true;
                if (type == typeof(ScoreModel.NoteScoreDefinition) &&
                    (name == "get_maxCutScore" || name == "get_executionOrder" || name == ".ctor")) return true;
                if (type?.FullName == typeof(ScoreModel).FullName + "+MaxScoreCounterElement") return true;
                if (type == typeof(ScoreMultiplierCounter) &&
                    (name == nameof(ScoreMultiplierCounter.Reset) ||
                     name == nameof(ScoreMultiplierCounter.ProcessMultiplierEvent) ||
                     name == "get_multiplier" || name == ".ctor")) return true;
            }

            return false;
        }

        private readonly struct CapturedScoreInput
        {
            public float Time { get; }
            public ScoreModel.NoteScoreDefinition Definition { get; }

            public CapturedScoreInput(float time, ScoreModel.NoteScoreDefinition definition)
            {
                Time = time;
                Definition = definition;
            }
        }

        private readonly struct OwnedScoreInput : IComparable<OwnedScoreInput>
        {
            public float Time { get; }
            public int MaxCutScore { get; }

            public OwnedScoreInput(float time, int maxCutScore)
            {
                Time = time;
                MaxCutScore = maxCutScore;
            }

            public int CompareTo(OwnedScoreInput other)
            {
                int comparison = Time.CompareTo(other.Time);
                // Equal-time execution order is the maximum cut score.
                return comparison == 0 ? MaxCutScore.CompareTo(other.MaxCutScore) : comparison;
            }
        }
    }
}
