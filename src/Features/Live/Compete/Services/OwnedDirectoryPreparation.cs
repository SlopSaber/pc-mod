using ScoreSaber.Core.Api.Generated;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Replays;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Services {
    internal static class OwnedDirectoryPreparation {
        internal sealed class ListCapture<T> where T : class {
            private readonly List<T> _source;
            private readonly T[] _references;
            private readonly Func<T, T, bool> _matches;
            internal T[] Owned { get; }

            internal ListCapture(List<T> source, T[] references, T[] owned, Func<T, T, bool> matches) {
                _source = source;
                _references = references;
                Owned = owned;
                _matches = matches;
            }

            internal bool Matches(List<T> source) {
                if (!ReferenceEquals(source, _source) || source.Count != Owned.Length) {
                    return false;
                }
                for (int i = 0; i < Owned.Length; i++) {
                    if (!ReferenceEquals(source[i], _references[i]) || !_matches(source[i], Owned[i])) {
                        return false;
                    }
                }
                return true;
            }
        }

        internal sealed class CultureCapture {
            private readonly CultureInfo _source;
            internal CultureInfo Owned { get; }
            internal CultureCapture(CultureInfo source, CultureInfo owned) {
                _source = source;
                Owned = owned;
            }
            internal bool Matches() => ReferenceEquals(_source, CultureInfo.CurrentCulture);
        }

        internal sealed class Roster {
            internal CompeteTeam[] Teams { get; }
            internal CompetePlayer[] Players { get; }
            internal Roster(CompeteTeam[] teams, CompetePlayer[] players) {
                Teams = teams;
                Players = players;
            }
        }

        internal sealed class RoomSummary {
            internal string MatchId { get; }
            internal string TournamentId { get; }
            internal string Name { get; }
            internal string InviteCode { get; }
            internal string State { get; }
            internal CompetePlayerListMode Mode { get; }
            internal int PlayerCount { get; }
            internal RoomSummary(LivePlayerRoomSummary room) {
                MatchId = room.MatchId ?? string.Empty;
                TournamentId = room.TournamentId ?? string.Empty;
                Name = string.IsNullOrEmpty(room.MatchId) ? "Room" : room.MatchId;
                InviteCode = room.InviteCode ?? string.Empty;
                State = CompeteDirectoryService.PrepareRoomState(room.State.ToString());
                Mode = room.RosterMode == LivePlayerRoomSummaryRosterMode.TEAM ? CompetePlayerListMode.Teams : CompetePlayerListMode.Regular;
                PlayerCount = (int)Math.Round(room.PlayerCount);
            }
        }

        internal static bool TryCaptureCulture(out CultureCapture capture) {
            CultureInfo culture = CultureInfo.CurrentCulture;
            capture = null;
            if (culture.GetType() != typeof(CultureInfo) || !culture.IsReadOnly ||
                culture.NumberFormat.GetType() != typeof(NumberFormatInfo) ||
                culture.DateTimeFormat.GetType() != typeof(DateTimeFormatInfo) ||
                culture.DateTimeFormat.Calendar.GetType().Assembly != typeof(Calendar).Assembly) {
                return false;
            }
            capture = new CultureCapture(culture, CultureInfo.ReadOnly((CultureInfo)culture.Clone()));
            return true;
        }

        private static bool TryCapture<T>(List<T> source, Func<T, T> copy, Func<T, T, bool> matches, out ListCapture<T> capture) where T : class {
            capture = null;
            if (source == null || source.GetType() != typeof(List<T>)) {
                return false;
            }
            T[] references = source.ToArray();
            T[] owned = new T[references.Length];
            for (int i = 0; i < references.Length; i++) {
                T value = references[i];
                if (value == null || value.GetType() != typeof(T) || (owned[i] = copy(value)) == null) {
                    return false;
                }
            }
            capture = new ListCapture<T>(source, references, owned, matches);
            return true;
        }

        internal static bool TryCaptureTournaments(List<LivePlayerTournamentSummary> source, out ListCapture<LivePlayerTournamentSummary> capture) {
            return TryCapture(source, value => new LivePlayerTournamentSummary {
                TournamentId = value.TournamentId, Name = value.Name, RoomSummary = value.RoomSummary
            }, (value, copy) => value.TournamentId == copy.TournamentId && value.Name == copy.Name && value.RoomSummary == copy.RoomSummary, out capture);
        }

        internal static bool TryCaptureRooms(List<LivePlayerRoomSummary> source, out ListCapture<LivePlayerRoomSummary> capture) {
            return TryCapture(source, value => new LivePlayerRoomSummary {
                MatchId = value.MatchId, TournamentId = value.TournamentId, InviteCode = value.InviteCode,
                State = value.State, RosterMode = value.RosterMode, PlayerCount = value.PlayerCount
            }, (value, copy) => value.MatchId == copy.MatchId && value.TournamentId == copy.TournamentId &&
                value.InviteCode == copy.InviteCode && value.State == copy.State && value.RosterMode == copy.RosterMode &&
                value.PlayerCount.Equals(copy.PlayerCount), out capture);
        }

        internal static bool TryCaptureMembers(List<LivePlayerRoomDetailsMembersItem> source, out ListCapture<LivePlayerRoomDetailsMembersItem> capture) {
            return TryCapture(source, CopyMember, MemberMatches, out capture);
        }

        private static LivePlayerRoomDetailsMembersItem CopyMember(LivePlayerRoomDetailsMembersItem value) {
            if (value.Player != null && value.Player.GetType() != typeof(LivePlayerRoomDetailsMembersItemPlayer)) {
                return null;
            }
            return new LivePlayerRoomDetailsMembersItem {
                PlayerId = value.PlayerId, TeamId = value.TeamId, TeamName = value.TeamName,
                Connected = value.Connected, IsBot = value.IsBot, Role = value.Role,
                PlayState = value.PlayState, DownloadState = value.DownloadState,
                Player = value.Player == null ? null : new LivePlayerRoomDetailsMembersItemPlayer {
                    Id = value.Player.Id, Name = value.Player.Name, Avatar = value.Player.Avatar
                }
            };
        }

        private static bool MemberMatches(LivePlayerRoomDetailsMembersItem value, LivePlayerRoomDetailsMembersItem copy) {
            if (value.PlayerId != copy.PlayerId || !value.TeamId.Equals(copy.TeamId) || value.TeamName != copy.TeamName ||
                value.Connected != copy.Connected || value.IsBot != copy.IsBot || value.Role != copy.Role ||
                value.PlayState != copy.PlayState || value.DownloadState != copy.DownloadState) {
                return false;
            }
            if (value.Player == null || copy.Player == null) {
                return value.Player == null && copy.Player == null;
            }
            return value.Player.GetType() == typeof(LivePlayerRoomDetailsMembersItemPlayer) &&
                value.Player.Id == copy.Player.Id && value.Player.Name == copy.Player.Name && value.Player.Avatar == copy.Player.Avatar;
        }

        internal static LivePlayerRoomDetailsSelectedSong CopySong(LivePlayerRoomDetailsSelectedSong value) {
            if (value == null || value.GetType() != typeof(LivePlayerRoomDetailsSelectedSong)) {
                return null;
            }
            return new LivePlayerRoomDetailsSelectedSong {
                MapHash = value.MapHash, Difficulty = value.Difficulty, Characteristic = value.Characteristic,
                LeaderboardId = value.LeaderboardId, SongName = value.SongName, SongSubName = value.SongSubName,
                SongAuthorName = value.SongAuthorName, LevelAuthorName = value.LevelAuthorName,
                Bpm = value.Bpm, Nps = value.Nps, DurationSeconds = value.DurationSeconds,
                CoverUrl = value.CoverUrl, DownloadUrl = value.DownloadUrl
            };
        }

        internal static bool SongMatches(LivePlayerRoomDetailsSelectedSong value, LivePlayerRoomDetailsSelectedSong copy) {
            return value != null && value.GetType() == typeof(LivePlayerRoomDetailsSelectedSong) &&
                value.MapHash == copy.MapHash && value.Difficulty == copy.Difficulty && value.Characteristic == copy.Characteristic &&
                value.LeaderboardId.Equals(copy.LeaderboardId) && value.SongName == copy.SongName && value.SongSubName == copy.SongSubName &&
                value.SongAuthorName == copy.SongAuthorName && value.LevelAuthorName == copy.LevelAuthorName &&
                value.Bpm.Equals(copy.Bpm) && value.Nps.Equals(copy.Nps) && value.DurationSeconds.Equals(copy.DurationSeconds) &&
                value.CoverUrl == copy.CoverUrl && value.DownloadUrl == copy.DownloadUrl;
        }

        internal static bool TryCaptureLeaderboards(List<MapDetailsResponseLeaderboardsItem> source, out ListCapture<MapDetailsResponseLeaderboardsItem> capture) {
            return TryCapture(source, value => {
                if (value.Realm != null && value.Realm.GetType() != typeof(MapDetailsResponseLeaderboardsItemRealm)) {
                    return null;
                }
                return new MapDetailsResponseLeaderboardsItem {
                    Id = value.Id, Difficulty = value.Difficulty, GameMode = value.GameMode, RawDifficulty = value.RawDifficulty,
                    Realm = value.Realm == null ? null : new MapDetailsResponseLeaderboardsItemRealm { Stars = value.Realm.Stars }
                };
            }, (value, copy) => value.Id.Equals(copy.Id) && value.Difficulty.Equals(copy.Difficulty) &&
                value.GameMode == copy.GameMode && value.RawDifficulty == copy.RawDifficulty &&
                (value.Realm == null || copy.Realm == null ? value.Realm == null && copy.Realm == null :
                    value.Realm.GetType() == typeof(MapDetailsResponseLeaderboardsItemRealm) && value.Realm.Stars.Equals(copy.Realm.Stars)), out capture);
        }

        internal static Task<CompeteTournament[]> QueueTournaments(LivePlayerTournamentSummary[] owned) {
            return ReplayStorageService.QueueOwnedPreparation(() => owned.Select(value => new CompeteTournament(
                value.TournamentId ?? string.Empty, value.Name ?? value.TournamentId ?? "Tournament", value.RoomSummary ?? string.Empty)).ToArray());
        }

        internal static Task<RoomSummary[]> QueueRooms(LivePlayerRoomSummary[] owned) {
            return ReplayStorageService.QueueOwnedPreparation(() => owned.Select(value => new RoomSummary(value)).ToArray());
        }

        internal static Task<Roster> QueueRoster(LivePlayerRoomDetailsMembersItem[] owned, bool hasLocalPlayer, string localPlayerId, CultureInfo culture) {
            return ReplayStorageService.QueueOwnedPreparation(() => CompeteDirectoryService.PrepareRoster(owned, hasLocalPlayer, localPlayerId, culture));
        }

        internal static Task<string> QueueStars(MapDetailsResponseLeaderboardsItem[] owned, LivePlayerRoomDetailsSelectedSong song) {
            return ReplayStorageService.QueueOwnedPreparation(() => CompeteDirectoryService.PrepareStars(owned, song));
        }

        internal static Task<string[]> QueueSong(LivePlayerRoomDetailsSelectedSong owned, CultureInfo culture) {
            return ReplayStorageService.QueueOwnedPreparation(() => CompeteDirectoryService.PrepareSongText(owned, culture));
        }
    }
}
