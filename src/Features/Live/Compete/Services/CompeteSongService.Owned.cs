using IPA.Utilities.Async;
using ScoreSaber.Core.Api.Generated;
using ScoreSaber.Core.BeatSaver;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Replays;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Services {
    internal partial class CompeteSongService {
        private sealed class SongCapture<T> {
            internal T Owned { get; }
            internal Func<bool> Matches { get; }
            internal OwnedDirectoryPreparation.CultureCapture Culture { get; }
            internal SongCapture(T owned, Func<bool> matches, OwnedDirectoryPreparation.CultureCapture culture) {
                Owned = owned;
                Matches = matches;
                Culture = culture;
            }
        }

        private sealed class ApiSongInput {
            internal MapDetailsResponse Map;
            internal LiveSongCommand Song;
        }

        private sealed class BeatSaverSongInput {
            internal BeatSaverMap Map;
            internal BeatSaverVersion Version;
            internal LiveSongCommand Song;
            internal LiveSongDetails ScoreSaber;
        }

        private sealed class NativeSongInput {
            internal string Name, SubName, Difficulty, Characteristic, Stars, Hash;
            internal string[] Mappers;
            internal float Duration, Bpm, Njs, Offset;
            internal int? CuttableObjects, Notes, Obstacles, Bombs;
        }

        private static Task<T> OnSongOwner<T>(Func<T> action) {
            return UnityMainThreadTaskScheduler.Factory.StartNew(action);
        }

        private static bool SongPreparationIsCurrent(CancellationToken token, Func<bool> isCurrent) {
            token.ThrowIfCancellationRequested();
            return isCurrent();
        }

        private static LiveSongCommand CopySongCommand(LiveSongCommand song) {
            return song != null && song.GetType() == typeof(LiveSongCommand)
                ? new LiveSongCommand { Hash = song.Hash, Difficulty = song.Difficulty, Characteristic = song.Characteristic }
                : null;
        }

        private static bool SongCommandMatches(LiveSongCommand source, LiveSongCommand owned) {
            return source != null && source.GetType() == typeof(LiveSongCommand) &&
                source.Hash == owned.Hash && source.Difficulty == owned.Difficulty && source.Characteristic == owned.Characteristic;
        }

        private static SongCapture<ApiSongInput> CaptureApiSong(LiveSongCommand song, MapDetailsResponse map) {
            LiveSongCommand command = CopySongCommand(song);
            if (command == null || map == null || map.GetType() != typeof(MapDetailsResponse) ||
                !OwnedDirectoryPreparation.TryCaptureCulture(out var culture) ||
                !OwnedDirectoryPreparation.TryCaptureLeaderboards(map.Leaderboards, out var leaderboards)) {
                return null;
            }
            var copy = new MapDetailsResponse {
                Hash = map.Hash, SongName = map.SongName, SongSubName = map.SongSubName,
                SongAuthorName = map.SongAuthorName, LevelAuthorName = map.LevelAuthorName,
                Bpm = map.Bpm, CoverUrl = map.CoverUrl,
                Leaderboards = new List<MapDetailsResponseLeaderboardsItem>(leaderboards.Owned)
            };
            return new SongCapture<ApiSongInput>(new ApiSongInput { Map = copy, Song = command }, () =>
                SongCommandMatches(song, command) && map.Hash == copy.Hash && map.SongName == copy.SongName &&
                map.SongSubName == copy.SongSubName && map.SongAuthorName == copy.SongAuthorName &&
                map.LevelAuthorName == copy.LevelAuthorName && map.Bpm.Equals(copy.Bpm) && map.CoverUrl == copy.CoverUrl &&
                leaderboards.Matches(map.Leaderboards), culture);
        }

        private static BeatSaverDifficulty CopyBeatSaverDifficulty(BeatSaverDifficulty diff) {
            return diff == null ? null : new BeatSaverDifficulty {
                Difficulty = diff.Difficulty, Characteristic = diff.Characteristic, Nps = diff.Nps,
                Notes = diff.Notes, Obstacles = diff.Obstacles, Bombs = diff.Bombs, Njs = diff.Njs, Offset = diff.Offset
            };
        }

        private static bool BeatSaverDifficultyMatches(BeatSaverDifficulty source, BeatSaverDifficulty owned) {
            return source.Difficulty == owned.Difficulty && source.Characteristic == owned.Characteristic &&
                source.Nps.Equals(owned.Nps) && source.Notes.Equals(owned.Notes) && source.Obstacles.Equals(owned.Obstacles) &&
                source.Bombs.Equals(owned.Bombs) && source.Njs.Equals(owned.Njs) && source.Offset.Equals(owned.Offset);
        }

        private static SongCapture<BeatSaverSongInput> CaptureBeatSaverSong(
            LiveSongCommand song, BeatSaverMap map, BeatSaverVersion version, LiveSongDetails scoreSaber) {
            LiveSongCommand command = CopySongCommand(song);
            if (command == null || !OwnedDirectoryPreparation.TryCaptureCulture(out var culture)) {
                return null;
            }
            BeatSaverMapMetadata metadata = map?.Metadata;
            BeatSaverUploader uploader = map?.Uploader;
            BeatSaverDifficulty[] diffs = version?.Diffs;
            BeatSaverDifficulty[] references = diffs == null ? null : (BeatSaverDifficulty[])diffs.Clone();
            if (references != null && references.Any(diff => diff == null)) {
                return null;
            }
            BeatSaverMap copyMap = map == null ? null : new BeatSaverMap {
                Name = map.Name,
                Metadata = metadata == null ? null : new BeatSaverMapMetadata {
                    SongName = metadata.SongName, SongSubName = metadata.SongSubName,
                    SongAuthorName = metadata.SongAuthorName, LevelAuthorName = metadata.LevelAuthorName,
                    Duration = metadata.Duration, Bpm = metadata.Bpm
                },
                Uploader = uploader == null ? null : new BeatSaverUploader { Name = uploader.Name }
            };
            BeatSaverVersion copyVersion = version == null ? null : new BeatSaverVersion {
                Hash = version.Hash, CoverUrl = version.CoverUrl, DownloadUrl = version.DownloadUrl,
                Diffs = references?.Select(CopyBeatSaverDifficulty).ToArray()
            };
            return new SongCapture<BeatSaverSongInput>(new BeatSaverSongInput {
                Map = copyMap, Version = copyVersion, Song = command, ScoreSaber = scoreSaber
            }, () => {
                if (!SongCommandMatches(song, command) || map != null &&
                    (map.Name != copyMap.Name || !ReferenceEquals(map.Metadata, metadata) || !ReferenceEquals(map.Uploader, uploader))) {
                    return false;
                }
                if (metadata != null && (metadata.SongName != copyMap.Metadata.SongName || metadata.SongSubName != copyMap.Metadata.SongSubName ||
                    metadata.SongAuthorName != copyMap.Metadata.SongAuthorName || metadata.LevelAuthorName != copyMap.Metadata.LevelAuthorName ||
                    !metadata.Duration.Equals(copyMap.Metadata.Duration) || !metadata.Bpm.Equals(copyMap.Metadata.Bpm))) {
                    return false;
                }
                if (uploader != null && uploader.Name != copyMap.Uploader.Name) {
                    return false;
                }
                if (version == null) {
                    return true;
                }
                if (version.Hash != copyVersion.Hash || version.CoverUrl != copyVersion.CoverUrl ||
                    version.DownloadUrl != copyVersion.DownloadUrl || !ReferenceEquals(version.Diffs, diffs)) {
                    return false;
                }
                if (diffs != null) {
                    for (int i = 0; i < diffs.Length; i++) {
                        if (!ReferenceEquals(diffs[i], references[i]) || !BeatSaverDifficultyMatches(diffs[i], copyVersion.Diffs[i])) {
                            return false;
                        }
                    }
                }
                return true;
            }, culture);
        }

        private static bool CopySongMappers(IEnumerable<string> source, out string[] owned) {
            owned = null;
            if (source is string[] array && array.GetType() == typeof(string[])) {
                owned = (string[])array.Clone();
                return true;
            }
            if (source != null && source.GetType() == typeof(List<string>)) {
                owned = ((List<string>)source).ToArray();
                return true;
            }
            return false;
        }

        private static bool SongMappersMatch(IEnumerable<string> source, string[] owned) {
            if (source is string[] array) {
                if (array.Length != owned.Length) return false;
                for (int i = 0; i < array.Length; i++) if (array[i] != owned[i]) return false;
                return true;
            }
            var list = (List<string>)source;
            if (list.Count != owned.Length) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] != owned[i]) return false;
            return true;
        }

        private static SongCapture<NativeSongInput> CaptureNativeSong(
            BeatmapLevel level, BeatmapKey key, LiveSongCommand song, LiveSongDetails scoreSaber) {
            LiveSongCommand command = CopySongCommand(song);
            if (command == null || level.GetType() != typeof(BeatmapLevel) ||
                !OwnedDirectoryPreparation.TryCaptureCulture(out var culture) ||
                !level.TryGetDifficultyDetails(key, out BeatmapDifficultyDetails details) ||
                !CopySongMappers(details.Mappers, out string[] mappers)) {
                return null;
            }
            IEnumerable<string> sourceMappers = details.Mappers;
            var input = new NativeSongInput {
                Name = level.songName, SubName = level.songSubName,
                Difficulty = key.difficulty.ToString(), Characteristic = key.CharacteristicSerializedName(),
                Mappers = mappers, Duration = level.songDuration, Bpm = level.beatsPerMinute,
                Njs = key.difficulty.NoteJumpMovementSpeed(details.NoteJumpMovementSpeed, false),
                Offset = details.NoteJumpStartBeatOffset, CuttableObjects = details.CuttableObjectsCount,
                Notes = details.NotesCount, Obstacles = details.ObstaclesCount, Bombs = details.BombsCount,
                Stars = scoreSaber?.Stars, Hash = SongHash(song)
            };
            return new SongCapture<NativeSongInput>(input, () => {
                if (!SongCommandMatches(song, command) || !level.TryGetDifficultyDetails(key, out BeatmapDifficultyDetails current) ||
                    !ReferenceEquals(current.Mappers, sourceMappers) || !SongMappersMatch(sourceMappers, mappers)) {
                    return false;
                }
                return level.songName == input.Name && level.songSubName == input.SubName &&
                    level.songDuration.Equals(input.Duration) && level.beatsPerMinute.Equals(input.Bpm) &&
                    key.difficulty.NoteJumpMovementSpeed(current.NoteJumpMovementSpeed, false).Equals(input.Njs) &&
                    current.NoteJumpStartBeatOffset.Equals(input.Offset) && current.CuttableObjectsCount.Equals(input.CuttableObjects) &&
                    current.NotesCount.Equals(input.Notes) && current.ObstaclesCount.Equals(input.Obstacles) && current.BombsCount.Equals(input.Bombs) &&
                    key.CharacteristicSerializedName() == input.Characteristic && scoreSaber?.Stars == input.Stars;
            }, culture);
        }

        private static LiveSongDetails RunSongCulture(CultureInfo culture, Func<LiveSongDetails> prepare) {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = culture;
                return prepare();
            } finally {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static Task<LiveSongDetails> QueueApiSong(ApiSongInput input, CultureInfo culture) {
            return ReplayStorageService.QueueOwnedPreparation(() => RunSongCulture(culture,
                () => BuildScoreSaberSongDetails(input.Song, input.Map)));
        }

        private static Task<LiveSongDetails> QueueBeatSaverSong(BeatSaverSongInput input, CultureInfo culture) {
            return ReplayStorageService.QueueOwnedPreparation(() => RunSongCulture(culture, () => {
                BeatSaverDifficulty[] diffs = input.Version?.Diffs ?? Array.Empty<BeatSaverDifficulty>();
                string difficulty = BeatSaverService.NormalizeDifficulty(input.Song.Difficulty);
                BeatSaverDifficulty diff = diffs.FirstOrDefault(item => string.Equals(
                    BeatSaverService.NormalizeDifficulty(item.Difficulty), difficulty, StringComparison.OrdinalIgnoreCase)) ?? diffs.FirstOrDefault();
                return MergeSongDetails(input.ScoreSaber, BuildBeatSaverSongDetailsCore(input.Song, input.Map, input.Version, diff));
            }));
        }

        private static Task<LiveSongDetails> QueueNativeSong(NativeSongInput input, CultureInfo culture) {
            return ReplayStorageService.QueueOwnedPreparation(() => RunSongCulture(culture, () => new LiveSongDetails {
                Name = DisplaySongName(input.Name, input.SubName), Mapper = MapperName(input.Mappers),
                Difficulty = input.Difficulty.Replace("Plus", "+"), Characteristic = input.Characteristic,
                Duration = FormatDuration(input.Duration), Bpm = Math.Round(input.Bpm).ToString(CultureInfo.InvariantCulture),
                Nps = NotesPerSecond(input.CuttableObjects, input.Duration), Notes = FormatInt(input.Notes),
                Obstacles = FormatInt(input.Obstacles), Bombs = FormatInt(input.Bombs),
                Njs = input.Njs.ToString("0.0#", CultureInfo.InvariantCulture),
                JumpDistance = JumpDistance(input.Bpm, input.Njs, input.Offset).ToString("0.0#", CultureInfo.InvariantCulture),
                Stars = FirstDetailValue(input.Stars, "--"), Hash = input.Hash
            }));
        }

        private static async Task<TResult> CompleteSongPreparation<TInput, TResult>(SongCapture<TInput> capture,
            Task<LiveSongDetails> worker, Func<TResult> fallback, Func<LiveSongDetails, TResult> complete,
            CancellationToken token, Func<bool> isCurrent) where TResult : class {
            LiveSongDetails prepared = null;
            ExceptionDispatchInfo failure = null;
            try {
                prepared = await worker;
            } catch (Exception ex) {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            return await OnSongOwner(() => {
                if (!SongPreparationIsCurrent(token, isCurrent)) return null;
                if (!capture.Culture.Matches() || !capture.Matches()) {
                    return SongPreparationIsCurrent(token, isCurrent) ? fallback() : null;
                }
                if (!SongPreparationIsCurrent(token, isCurrent)) return null;
                failure?.Throw();
                return complete(prepared);
            });
        }

        private static async Task<LiveSongDetails> PrepareApiSong(LiveSongCommand song, MapDetailsResponse map,
            CancellationToken token, Func<bool> isCurrent) {
            SongCapture<ApiSongInput> capture = await OnSongOwner(() =>
                SongPreparationIsCurrent(token, isCurrent) ? CaptureApiSong(song, map) : null);
            if (capture == null) {
                return await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent) ? BuildScoreSaberSongDetails(song, map) : null);
            }
            return await CompleteSongPreparation(capture, QueueApiSong(capture.Owned, capture.Culture.Owned),
                () => BuildScoreSaberSongDetails(song, map), prepared => prepared, token, isCurrent);
        }

        private async Task<LiveSongDetails> PrepareBeatSaverSong(LiveSongCommand song, BeatSaverMap map, BeatSaverVersion version,
            LiveSongDetails scoreSaber, CancellationToken token, Func<bool> isCurrent) {
            SongCapture<BeatSaverSongInput> capture = await OnSongOwner(() =>
                SongPreparationIsCurrent(token, isCurrent) ? CaptureBeatSaverSong(song, map, version, scoreSaber) : null);
            if (capture == null) {
                return await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent)
                    ? MergeSongDetails(scoreSaber, BuildBeatSaverSongDetails(song, map, version)) : null);
            }
            return await CompleteSongPreparation(capture, QueueBeatSaverSong(capture.Owned, capture.Culture.Owned),
                () => MergeSongDetails(scoreSaber, BuildBeatSaverSongDetails(song, map, version)), prepared => prepared, token, isCurrent);
        }

        private static async Task<CompeteSongSelection> PrepareNativeSong(BeatmapLevel level, BeatmapKey key,
            LiveSongCommand song, LiveSongDetails scoreSaber, CancellationToken token, Func<bool> isCurrent) {
            SongCapture<NativeSongInput> capture = await OnSongOwner(() =>
                SongPreparationIsCurrent(token, isCurrent) ? CaptureNativeSong(level, key, song, scoreSaber) : null);
            if (capture == null) {
                return await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent) ? CreateSongSelection(level, key, song, scoreSaber) : null);
            }
            return await CompleteSongPreparation(capture, QueueNativeSong(capture.Owned, capture.Culture.Owned),
                () => CreateSongSelection(level, key, song, scoreSaber), prepared => new CompeteSongSelection(
                    level, key, prepared.Name, prepared.Mapper, prepared.Difficulty, prepared.Characteristic, string.Empty,
                    prepared.Duration, prepared.Bpm, prepared.Nps, prepared.Notes, prepared.Obstacles, prepared.Bombs,
                    prepared.Njs, prepared.JumpDistance, prepared.Stars, prepared.Hash, string.Empty), token, isCurrent);
        }

        private async Task<CompeteSongSelection> ResolveInstalledOwned(LiveSongCommand song, LiveSongDetails scoreSaber,
            CancellationToken token, Func<bool> isCurrent) {
            string hash = SongHash(song);
            if (string.IsNullOrEmpty(hash)) return null;
            BeatmapLevel level;
            try {
                level = await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent)
                    ? _beatmapLevelsModel.GetLevelByHash(hash, token) : Task.FromResult<BeatmapLevel>(null)).Unwrap();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) {
                if (!await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent))) return null;
                throw;
            }
            if (level == null) return null;
            BeatmapKey? key = await OnSongOwner(() => SongPreparationIsCurrent(token, isCurrent) ? FindBeatmapKey(level, song) : null);
            if (!key.HasValue) return null;
            return await PrepareNativeSong(level, key.Value, song, scoreSaber, token, isCurrent);
        }
    }
}
