using IPA.Utilities.Async;
using ScoreSaber.Core.Api;
using ScoreSaber.Core.Api.Generated;
using ScoreSaber.Core.BeatSaver;
using ScoreSaber.Features.Live.Ludus.Domain;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Live.Compete.Services;
using ScoreSaber.Features.Players.Domain;
using ScoreSaber.Live.V1;
using ScoreSaber.Features.Live.Ludus.UI;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ScoreSaber.Features.Live.Ludus.Services {
    internal enum LiveChatLinkKind {
        ExternalUrl,
        BeatSaverId,
        BeatSaverHash,
        ScoreSaberMapId,
        ScoreSaberLeaderboardId
    }

    internal sealed class LiveChatLinkTarget {
        internal LiveChatLinkKind Kind { get; set; }
        internal string Value { get; set; }
        internal string SecondaryValue { get; set; }
        internal string Url { get; set; }
    }

    internal sealed class LiveChatLinkService {
        private static readonly Regex LinkPattern = new Regex(
            @"(https?:\/\/[^\s<>""]+)|(?:\b(?:bsr|bsid)[:\s#-]*([0-9a-f]{1,8})\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HashPattern = new Regex(@"\b[0-9a-f]{40}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RawPlayerRoomLogPattern = new Regex(@"^(\d{15,20}) (joined|left) the room$", RegexOptions.Compiled);
        private static readonly Regex NamedPlayerRoomLogPattern = new Regex(@"^(.+) (joined|left) the room$", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex RawLoadedMapLogPattern = new Regex(@"^Loaded map\s+([A-Fa-f0-9]{40})(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex DisplayMarkupTagPattern = new Regex(@"<[^>\r\n]{1,128}>", RegexOptions.Compiled);

        private readonly BeatSaverService _beatSaver;
        private readonly IScoreSaberApiClient _apiClient;
        private readonly CompeteSongService _songService;
        private readonly LiveChatSongNavigator _songNavigator;
        private readonly LudusSessionService _ludusSession;
        private readonly Dictionary<string, string> _playerNames = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _mapNames = new Dictionary<string, string>();
        private readonly HashSet<string> _pendingPlayerNames = new HashSet<string>();
        private readonly HashSet<string> _pendingMapNames = new HashSet<string>();
        internal long TextCacheVersion { get; private set; }

        internal event Action<string> StatusChanged;
        internal event Action ResolvedTextChanged;

        internal LiveChatLinkService(
            BeatSaverService beatSaver,
            IScoreSaberApiClient apiClient,
            CompeteSongService songService,
            LiveChatSongNavigator songNavigator,
            LudusSessionService ludusSession) {

            _beatSaver = beatSaver;
            _apiClient = apiClient;
            _songService = songService;
            _songNavigator = songNavigator;
            _ludusSession = ludusSession;
        }

        internal LiveChatLinkTarget FirstLink(string text) => FindFirstLink(text);

        private static LiveChatLinkTarget FindFirstLink(string text) {
            if (string.IsNullOrWhiteSpace(text)) {
                return null;
            }

            foreach (Match match in LinkPattern.Matches(text)) {
                string token = TrimToken(match.Value);
                if (TryCreateTarget(match, token, out LiveChatLinkTarget target)) {
                    return target;
                }
            }

            return null;
        }

        internal string DisplaySenderName(LiveChatEntry entry) => DisplaySenderName(entry, ResolvedPlayerName);

        private static string DisplaySenderName(LiveChatEntry entry, Func<string, string> playerName) {
            string senderName = CleanDisplayName(entry?.SenderName);
            if (!string.IsNullOrWhiteSpace(senderName) && senderName != "Player") {
                return senderName;
            }

            if (string.IsNullOrWhiteSpace(entry?.SenderPlayerId)) {
                return "Unknown";
            }

            return FirstNonEmpty(playerName(entry.SenderPlayerId), "Loading player");
        }

        internal string DisplayText(LiveChatEntry entry) => DisplayText(entry, ResolvedPlayerName, ResolvedMapName);

        private static string DisplayText(LiveChatEntry entry, Func<string, string> playerName, Func<string, string> mapName) {
            if (entry == null || entry.IsChat) {
                return entry?.Text ?? string.Empty;
            }

            Match playerLog = RawPlayerRoomLogPattern.Match(entry.Text);
            if (playerLog.Success) {
                string name = LogPlayerName(entry, string.Empty, playerLog.Groups[1].Value, playerName);
                return $"{name} {playerLog.Groups[2].Value} the room";
            }

            Match namedPlayerLog = NamedPlayerRoomLogPattern.Match(entry.Text);
            if (namedPlayerLog.Success) {
                string name = LogPlayerName(entry, namedPlayerLog.Groups[1].Value, string.Empty, playerName);
                return $"{name} {namedPlayerLog.Groups[2].Value} the room";
            }

            Match mapLog = RawLoadedMapLogPattern.Match(entry.Text);
            if (mapLog.Success) {
                string hash = mapLog.Groups[1].Value.ToUpperInvariant();
                string name = FirstNonEmpty(mapName(hash), "map");
                return $"Loaded {name}{NormalizeLogSuffix(mapLog.Groups[2].Value)}";
            }

            return StripDisplayMarkup(entry.Text);
        }

        private static string LogPlayerName(LiveChatEntry entry, string fallbackName, string fallbackPlayerId, Func<string, string> playerName) {
            string senderName = CleanDisplayName(entry?.SenderName);
            if (!string.IsNullOrWhiteSpace(senderName) && senderName != "Ludus" && senderName != "Player") {
                return senderName;
            }

            string playerId = FirstNonEmpty(fallbackPlayerId, entry?.SenderPlayerId);
            if (!string.IsNullOrWhiteSpace(playerId)) {
                return FirstNonEmpty(playerName(playerId), "Loading player");
            }

            string fallback = CleanDisplayName(fallbackName);
            return !string.IsNullOrWhiteSpace(fallback) && fallback != "Player" ? fallback : "Unknown player";
        }

        internal async Task Open(LiveChatLinkTarget target, CancellationToken cancellationToken) {
            if (target == null) {
                return;
            }

            if (target.Kind == LiveChatLinkKind.ExternalUrl) {
                Application.OpenURL(target.Url);
                return;
            }

            try {
                StatusChanged?.Invoke("Resolving linked map...");
                LiveSongCommand song = await SongFromTarget(target, cancellationToken);
                if (string.IsNullOrEmpty(song?.Hash)) {
                    StatusChanged?.Invoke("Could not resolve linked map.");
                    return;
                }

                StatusChanged?.Invoke("Checking linked map...");
                CompeteSongSelection selection = await _songService.ResolveInstalled(song, cancellationToken);
                if (selection == null) {
                    StatusChanged?.Invoke("Downloading linked map...");
                    selection = await _songService.ResolveOrDownload(song, cancellationToken);
                }

                if (selection == null) {
                    StatusChanged?.Invoke("Linked map downloaded.");
                    return;
                }

                bool roomUpdated = _ludusSession.TrySetLinkedSong(selection);
                bool focused = await _songNavigator.TryFocusSong(selection, cancellationToken);
                if (focused) {
                    StatusChanged?.Invoke($"Opened linked map: {selection.Name}");
                } else if (roomUpdated) {
                    StatusChanged?.Invoke($"Linked map ready in room: {selection.Name}");
                } else {
                    StatusChanged?.Invoke($"Linked map ready: {selection.Name}");
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Plugin.Log.Warn("Failed to open live chat map link:");
                Plugin.Log.Warn(ex);
                StatusChanged?.Invoke($"Map link failed: {MapLinkFailureMessage(ex)}");
            }
        }

        private static string MapLinkFailureMessage(Exception ex) {
            if (ex is IOException || ex is UnauthorizedAccessException) {
                return "Could not install the downloaded map.";
            }

            return string.IsNullOrWhiteSpace(ex?.Message) ? "Unknown error." : ex.Message;
        }

        private Task<LiveSongCommand> SongFromTarget(LiveChatLinkTarget target, CancellationToken cancellationToken) {
            switch (target.Kind) {
                case LiveChatLinkKind.BeatSaverId:
                    return SongFromBeatSaverId(target.Value, cancellationToken);
                case LiveChatLinkKind.ScoreSaberMapId:
                case LiveChatLinkKind.ScoreSaberLeaderboardId:
                    return SongFromScoreSaberMap(target, cancellationToken);
                default:
                    return Task.FromResult(new LiveSongCommand { Hash = target.Value });
            }
        }

        private async Task<LiveSongCommand> SongFromBeatSaverId(string id, CancellationToken cancellationToken) {
            BeatSaverMap map = await _beatSaver.GetMapById(id, cancellationToken);
            BeatSaverVersion version = map?.Versions?.FirstOrDefault();
            BeatSaverDifficulty diff = version?.Diffs?.FirstOrDefault();
            return new LiveSongCommand {
                Hash = version?.Hash ?? string.Empty,
                Difficulty = diff?.Difficulty ?? string.Empty,
                Characteristic = diff?.Characteristic ?? string.Empty
            };
        }

        private async Task<LiveSongCommand> SongFromScoreSaberMap(LiveChatLinkTarget target, CancellationToken cancellationToken) {
            if (!int.TryParse(target.Value, out int mapId)) {
                return null;
            }

            MapDetailsResponse map = await _apiClient.GetMapById(mapId, cancellationToken);
            MapDetailsResponseLeaderboardsItem leaderboard = FindLeaderboard(map, target.SecondaryValue);
            if (!string.IsNullOrWhiteSpace(map?.Hash)) {
                return new LiveSongCommand {
                    Hash = map.Hash,
                    Difficulty = DifficultyName(leaderboard),
                    Characteristic = CharacteristicName(leaderboard)
                };
            }

            return string.IsNullOrWhiteSpace(map?.Bsid)
                ? null
                : await SongFromBeatSaverId(map.Bsid, cancellationToken);
        }

        internal Task<OwnedPresentationResult> PrepareOwnedPresentation(LiveChatEntry[] entries, CultureInfo culture, TimeZoneInfo timeZone) {
            var owned = new OwnedPresentation(entries, new Dictionary<string, string>(_playerNames),
                new Dictionary<string, string>(_mapNames), CultureInfo.ReadOnly((CultureInfo)culture.Clone()), timeZone);
            return ReplayStorageService.QueueOwnedPreparation(owned.Prepare);
        }

        internal void AdmitResolution(ResolutionRequest request) {
            if (request.IsMap) {
                QueueMapResolution(request.Key);
            } else {
                QueuePlayerResolution(request.Key);
            }
        }

        internal sealed class ResolutionRequest {
            internal ResolutionRequest(string key, bool isMap) {
                Key = key;
                IsMap = isMap;
            }

            internal string Key { get; }
            internal bool IsMap { get; }
        }

        internal sealed class OwnedPresentationResult {
            internal OwnedPresentationResult(LiveChatFloatingViewController.LiveChatFloatingRow[] rows, ResolutionRequest[] resolutions, Exception error = null) {
                Rows = rows;
                Resolutions = resolutions;
                Error = error;
            }

            internal LiveChatFloatingViewController.LiveChatFloatingRow[] Rows { get; }
            internal ResolutionRequest[] Resolutions { get; }
            internal Exception Error { get; }
        }

        private sealed class OwnedPresentation {
            private readonly LiveChatEntry[] _entries;
            private readonly Dictionary<string, string> _players;
            private readonly Dictionary<string, string> _maps;
            private readonly CultureInfo _culture;
            private readonly TimeZoneInfo _timeZone;
            private readonly List<ResolutionRequest> _resolutions = new List<ResolutionRequest>();

            internal OwnedPresentation(LiveChatEntry[] entries, Dictionary<string, string> players,
                Dictionary<string, string> maps, CultureInfo culture, TimeZoneInfo timeZone) {
                _entries = entries;
                _players = players;
                _maps = maps;
                _culture = culture;
                _timeZone = timeZone;
            }

            internal OwnedPresentationResult Prepare() {
                var rows = new LiveChatFloatingViewController.LiveChatFloatingRow[_entries.Length];
                try {
                    for (int i = 0; i < _entries.Length; i++) {
                        LiveChatEntry entry = _entries[i];
                        LiveChatLinkTarget target = FindFirstLink(entry.Text);
                        string sender = DisplaySenderName(entry, PlayerName);
                        string text = DisplayText(entry, PlayerName, MapName);
                        string time = entry.CreatedAtUnixMs <= 0 ? "--:--"
                            : TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtUnixMs), _timeZone).ToString("HH:mm", _culture);
                        rows[i] = LiveChatFloatingViewController.PrepareOwnedRow(entry, target, sender, text, time);
                    }
                } catch (Exception error) {
                    return new OwnedPresentationResult(null, _resolutions.ToArray(), error);
                }

                return new OwnedPresentationResult(rows, _resolutions.ToArray());
            }

            private string PlayerName(string key) => ResolveName(_players, key, false);
            private string MapName(string key) => ResolveName(_maps, key, true);

            private string ResolveName(Dictionary<string, string> names, string key, bool isMap) {
                if (string.IsNullOrWhiteSpace(key)) {
                    return string.Empty;
                }

                if (names.TryGetValue(key, out string name)) {
                    return name;
                }

                _resolutions.Add(new ResolutionRequest(key, isMap));
                return string.Empty;
            }
        }

        private string ResolvedPlayerName(string playerId) {
            if (string.IsNullOrWhiteSpace(playerId)) {
                return string.Empty;
            }

            if (_playerNames.TryGetValue(playerId, out string name)) {
                return name;
            }

            QueuePlayerResolution(playerId);
            return string.Empty;
        }

        private string ResolvedMapName(string hash) {
            if (string.IsNullOrWhiteSpace(hash)) {
                return string.Empty;
            }

            if (_mapNames.TryGetValue(hash, out string name)) {
                return name;
            }

            QueueMapResolution(hash);
            return string.Empty;
        }

        private void QueuePlayerResolution(string playerId) {
            if (_pendingPlayerNames.Contains(playerId) || _playerNames.ContainsKey(playerId)) {
                return;
            }

            _pendingPlayerNames.Add(playerId);
            _ = ResolvePlayerName(playerId);
        }

        private async Task ResolvePlayerName(string playerId) {
            string name = string.Empty;
            try {
                PlayerProfile player = await _apiClient.GetPlayerProfile(playerId, false, null, CancellationToken.None);
                name = FirstNonEmpty(CleanDisplayName(player?.Name));
            } catch (Exception ex) {
                Plugin.Log.Warn($"Failed to resolve live chat player {playerId}: {ex.Message}");
            }

            await UnityMainThreadTaskScheduler.Factory.StartNew(() => {
                _pendingPlayerNames.Remove(playerId);
                _playerNames[playerId] = name;
                TextCacheVersion++;
                ResolvedTextChanged?.Invoke();
            });
        }

        private void QueueMapResolution(string hash) {
            if (_pendingMapNames.Contains(hash) || _mapNames.ContainsKey(hash)) {
                return;
            }

            _pendingMapNames.Add(hash);
            _ = ResolveMapName(hash);
        }

        private async Task ResolveMapName(string hash) {
            string name = string.Empty;
            try {
                MapDetailsResponse map = await _apiClient.GetMapByHash(hash, CancellationToken.None);
                name = FormatMapName(map);
            } catch (Exception ex) {
                Plugin.Log.Warn($"Failed to resolve live chat map {hash}: {ex.Message}");
            }

            await UnityMainThreadTaskScheduler.Factory.StartNew(() => {
                _pendingMapNames.Remove(hash);
                _mapNames[hash] = name;
                TextCacheVersion++;
                ResolvedTextChanged?.Invoke();
            });
        }

        private static string FormatMapName(MapDetailsResponse map) {
            string name = FirstNonEmpty(map?.SongName, "map");
            return string.IsNullOrWhiteSpace(map?.SongAuthorName) ? name : $"{name} by {map.SongAuthorName}";
        }

        private static string NormalizeLogSuffix(string suffix) {
            suffix = string.Join(" ", (suffix ?? string.Empty).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrEmpty(suffix) ? string.Empty : $" {suffix}";
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (string value in values) {
                if (!string.IsNullOrWhiteSpace(value)) {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string CleanDisplayName(string value) {
            string name = StripDisplayMarkup(value);
            return string.Join(" ", (name ?? string.Empty).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string StripDisplayMarkup(string value) {
            return string.IsNullOrEmpty(value) ? string.Empty : DisplayMarkupTagPattern.Replace(value, string.Empty);
        }

        private static bool TryCreateTarget(Match match, string token, out LiveChatLinkTarget target) {
            target = null;

            if (match.Groups.Count > 2 && match.Groups[2].Success) {
                string id = match.Groups[2].Value;
                target = new LiveChatLinkTarget {
                    Kind = LiveChatLinkKind.BeatSaverId,
                    Value = id,
                    Url = $"https://beatsaver.com/maps/{id}"
                };
                return true;
            }

            if (!Uri.TryCreate(token, UriKind.Absolute, out Uri uri)) {
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            if (host.EndsWith("beatsaver.com", StringComparison.Ordinal)) {
                return TryResolveBeatSaverUri(uri, out target);
            }

            if (host.EndsWith("scoresaber.com", StringComparison.Ordinal)) {
                if (TryResolveScoreSaberUri(uri, out target)) {
                    return true;
                }

                Match hashMatch = HashPattern.Match(uri.ToString());
                if (hashMatch.Success) {
                    target = new LiveChatLinkTarget {
                        Kind = LiveChatLinkKind.BeatSaverHash,
                        Value = hashMatch.Value,
                        Url = token
                    };
                    return true;
                }

                target = new LiveChatLinkTarget {
                    Kind = LiveChatLinkKind.ExternalUrl,
                    Value = token,
                    Url = token
                };
                return true;
            }

            return false;
        }

        private static bool TryResolveScoreSaberUri(Uri uri, out LiveChatLinkTarget target) {
            target = null;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            for (int i = 0; i < parts.Length; i++) {
                if (!parts[i].Equals("map", StringComparison.OrdinalIgnoreCase) || i + 1 >= parts.Length || !IsNumericId(parts[i + 1])) {
                    continue;
                }

                for (int j = i + 2; j + 1 < parts.Length; j++) {
                    if (parts[j].Equals("difficulty", StringComparison.OrdinalIgnoreCase) && IsNumericId(parts[j + 1])) {
                        target = new LiveChatLinkTarget {
                            Kind = LiveChatLinkKind.ScoreSaberLeaderboardId,
                            Value = parts[i + 1],
                            SecondaryValue = parts[j + 1],
                            Url = uri.ToString()
                        };
                        return true;
                    }
                }

                target = new LiveChatLinkTarget {
                    Kind = LiveChatLinkKind.ScoreSaberMapId,
                    Value = parts[i + 1],
                    Url = uri.ToString()
                };
                return true;
            }

            return false;
        }

        private static bool TryResolveBeatSaverUri(Uri uri, out LiveChatLinkTarget target) {
            target = null;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            for (int i = 0; i < parts.Length; i++) {
                string part = parts[i].ToLowerInvariant();
                if ((part == "maps" || part == "map") && i + 1 < parts.Length) {
                    if (parts[i + 1].Equals("hash", StringComparison.OrdinalIgnoreCase) && i + 2 < parts.Length) {
                        target = new LiveChatLinkTarget {
                            Kind = LiveChatLinkKind.BeatSaverHash,
                            Value = parts[i + 2],
                            Url = uri.ToString()
                        };
                        return true;
                    }

                    target = new LiveChatLinkTarget {
                        Kind = LiveChatLinkKind.BeatSaverId,
                        Value = parts[i + 1],
                        Url = uri.ToString()
                    };
                    return true;
                }
            }

            return false;
        }

        private static bool IsNumericId(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            for (int i = 0; i < value.Length; i++) {
                if (!char.IsDigit(value[i])) {
                    return false;
                }
            }

            return true;
        }

        private static MapDetailsResponseLeaderboardsItem FindLeaderboard(MapDetailsResponse map, string leaderboardIdText) {
            if (map?.Leaderboards == null || !int.TryParse(leaderboardIdText, out int leaderboardId)) {
                return null;
            }

            return map.Leaderboards.FirstOrDefault(leaderboard => Math.Abs(leaderboard.Id - leaderboardId) < 0.5d);
        }

        private static string DifficultyName(MapDetailsResponseLeaderboardsItem leaderboard) {
            string rawDifficulty = RawDifficultyPart(leaderboard?.RawDifficulty, 0);
            if (!string.IsNullOrEmpty(rawDifficulty)) {
                return rawDifficulty;
            }

            switch ((int)(leaderboard?.Difficulty ?? 0)) {
                case 1:
                    return "Easy";
                case 3:
                    return "Normal";
                case 5:
                    return "Hard";
                case 7:
                    return "Expert";
                case 9:
                    return "ExpertPlus";
                default:
                    return string.Empty;
            }
        }

        private static string CharacteristicName(MapDetailsResponseLeaderboardsItem leaderboard) {
            string gameMode = leaderboard?.GameMode;
            if (string.IsNullOrWhiteSpace(gameMode)) {
                gameMode = RawDifficultyPart(leaderboard?.RawDifficulty, 1);
            }

            if (string.IsNullOrWhiteSpace(gameMode)) {
                return string.Empty;
            }

            return gameMode.StartsWith("Solo", StringComparison.OrdinalIgnoreCase)
                ? gameMode.Substring(4)
                : gameMode;
        }

        private static string RawDifficultyPart(string rawDifficulty, int index) {
            if (string.IsNullOrWhiteSpace(rawDifficulty)) {
                return string.Empty;
            }

            string[] parts = rawDifficulty.Trim('_').Split('_');
            return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
        }

        private static string TrimToken(string token) => token.TrimEnd('.', ',', ';', ':', ')', ']', '}');
    }
}
