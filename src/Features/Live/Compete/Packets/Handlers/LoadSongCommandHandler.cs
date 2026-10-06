using ScoreSaber.Core;
using ScoreSaber.Features.Live.Compete.Domain;
using ScoreSaber.Features.Live.Compete.Packets;
using ScoreSaber.Live.V1;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Compete.Packets.Handlers {
    internal sealed class LoadSongCommandHandler : ILudusServerCommandHandler {
        public LudusCommandType Type => LudusCommandType.LudusCommandTypeLoadSong;

        public void Handle(ILudusServerCommandSession session, ServerCommand command) {
            LoadSong(session, command).RunTask();
        }

        internal static async Task LoadSong(ILudusServerCommandSession session, ServerCommand command) {
            await EnsureSongReady(session, command?.Song);
        }

        internal static Task<bool> EnsureSongReady(ILudusServerCommandSession session, LiveSongCommand song) {
            return session is CompeteLudusCommandSession ownedSession && ownedSession.CanPrepareOwnedSongDetails &&
                song != null && song.GetType() == typeof(LiveSongCommand)
                ? EnsureOwnedSongReady(ownedSession, song, ownedSession.BeginSongRequest())
                : EnsureSongReadyOnCaller(session, song);
        }

        private static async Task<bool> EnsureSongReadyOnCaller(ILudusServerCommandSession session, LiveSongCommand song) {
            if (session.TournamentRoom == null || song == null) {
                return false;
            }

            if (session.TournamentRoom.Song?.BeatmapLevel != null && MatchesSong(session.TournamentRoom.Song, song)) {
                return true;
            }

            CancellationToken cancellationToken = session.ConnectionCancellationToken;
            CompeteSongSelection installed = await session.SongService.ResolveInstalled(song, cancellationToken);
            if (installed != null) {
                if (session.TournamentRoom == null) {
                    return false;
                }

                session.TournamentRoom = session.TournamentRoom.WithSong(installed);
                session.NotifyRoomUpdated(session.TournamentRoom);
                session.SendDownloadState(LudusDownloadState.LudusDownloadStateDownloaded);
                return true;
            }

            CompeteSongSelection preview = await session.SongService.CreatePreview(song, cancellationToken);
            if (session.TournamentRoom == null) {
                return false;
            }

            session.TournamentRoom = session.TournamentRoom.WithSongStatus(preview ?? session.TournamentRoom.Song, "Downloading map...");
            session.NotifyRoomUpdated(session.TournamentRoom);
            session.SendDownloadState(LudusDownloadState.LudusDownloadStateDownloading);

            try {
                CompeteSongSelection resolved = await session.SongService.ResolveOrDownload(song, cancellationToken);
                if (resolved == null || resolved.BeatmapLevel == null) {
                    throw new InvalidOperationException("SongCore could not resolve the downloaded song");
                }

                if (session.TournamentRoom == null) {
                    return false;
                }

                session.TournamentRoom = session.TournamentRoom.WithSong(resolved);
                session.NotifyRoomUpdated(session.TournamentRoom);
                session.SendDownloadState(LudusDownloadState.LudusDownloadStateDownloaded);
                return true;
            } catch (Exception ex) {
                CompeteSongSelection installedAfterRefresh = await ResolveInstalledAfterDownloadFailure(session, song, cancellationToken);
                if (installedAfterRefresh != null) {
                    if (session.TournamentRoom == null) {
                        return false;
                    }

                    session.TournamentRoom = session.TournamentRoom.WithSong(installedAfterRefresh);
                    session.NotifyRoomUpdated(session.TournamentRoom);
                    session.SendDownloadState(LudusDownloadState.LudusDownloadStateDownloaded);
                    return true;
                }

                Plugin.Log.Warn($"Failed to load live room song: {ex.Message}");
                if (session.TournamentRoom == null) {
                    return false;
                }

                session.TournamentRoom = session.TournamentRoom.WithSongStatus(preview ?? session.TournamentRoom.Song, "Map download failed.");
                session.NotifyRoomUpdated(session.TournamentRoom);
                session.SendDownloadState(LudusDownloadState.LudusDownloadStateError, ex.Message);
                return false;
            }
        }

        private static Task<T> OnOwner<T>(Func<T> action) {
            return IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(action);
        }

        private static async Task<bool> EnsureOwnedSongReady(CompeteLudusCommandSession session, LiveSongCommand song, long version) {
            SongLoadRequest request = await OnOwner(() => session.TournamentRoom == null
                ? null : new SongLoadRequest(session, song, version));
            if (request == null || !await OnOwner(request.IsCurrent)) {
                return false;
            }
            if (await OnOwner(() => request.IsCurrent() && session.TournamentRoom.Song?.BeatmapLevel != null &&
                MatchesSong(session.TournamentRoom.Song, song))) {
                return true;
            }

            CompeteSongSelection installed = await OnOwner(() => request.IsCurrent() ? session.SongService.ResolveInstalled(song, request.Token, request.IsCurrent) : Task.FromResult<CompeteSongSelection>(null)).Unwrap();
            if (installed != null) {
                return await OnOwner(() => request.Publish(installed, LudusDownloadState.LudusDownloadStateDownloaded));
            }
            if (!await OnOwner(request.IsCurrent)) {
                return false;
            }
            CompeteSongSelection preview = await OnOwner(() => request.IsCurrent() ? session.SongService.CreatePreview(song, request.Token, request.IsCurrent) : Task.FromResult<CompeteSongSelection>(null)).Unwrap();
            if (!await OnOwner(() => request.Publish(preview, LudusDownloadState.LudusDownloadStateDownloading, "Downloading map..."))) {
                return false;
            }

            try {
                CompeteSongSelection resolved = await OnOwner(() => request.IsCurrent() ? session.SongService.ResolveOrDownload(song, request.Token, request.IsCurrent) : Task.FromResult<CompeteSongSelection>(null)).Unwrap();
                return await OnOwner(() => {
                    if (!request.IsCurrent()) {
                        return false;
                    }
                    if (resolved == null || resolved.BeatmapLevel == null) {
                        throw new InvalidOperationException("SongCore could not resolve the downloaded song");
                    }
                    return request.Publish(resolved, LudusDownloadState.LudusDownloadStateDownloaded);
                });
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                if (!await OnOwner(request.IsCurrent)) {
                    return false;
                }
                CompeteSongSelection installedAfterRefresh = await ResolveOwnedAfterDownloadFailure(session, song, request);
                if (installedAfterRefresh != null) {
                    return await OnOwner(() => request.Publish(installedAfterRefresh, LudusDownloadState.LudusDownloadStateDownloaded));
                }
                return await OnOwner(() => {
                    if (!request.IsCurrent()) {
                        return false;
                    }
                    Plugin.Log.Warn($"Failed to load live room song: {ex.Message}");
                    return request.Publish(preview, LudusDownloadState.LudusDownloadStateError, "Map download failed.", ex.Message);
                });
            }
        }

        private static async Task<CompeteSongSelection> ResolveOwnedAfterDownloadFailure(
            CompeteLudusCommandSession session, LiveSongCommand song, SongLoadRequest request) {
            try {
                return await OnOwner(() => request.IsCurrent()
                    ? session.SongService.ResolveInstalledAfterRefresh(song, request.Token, request.IsCurrent)
                    : Task.FromResult<CompeteSongSelection>(null)).Unwrap();
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                await OnOwner(() => {
                    if (request.IsCurrent()) {
                        Plugin.Log.Warn($"Failed to refresh local live room songs: {ex.Message}");
                    }
                    return true;
                });
                return null;
            }
        }

        private sealed class SongLoadRequest {
            private readonly CompeteLudusCommandSession _session;
            private readonly LiveSongCommand _command;
            private readonly long _version;
            private readonly string _roomId;
            private readonly string _tournamentId;
            private readonly string _hash;
            private readonly string _difficulty;
            private readonly string _characteristic;
            private long _roomGeneration;
            private CompeteSongSelection _song;
            internal CancellationToken Token { get; }

            internal SongLoadRequest(CompeteLudusCommandSession session, LiveSongCommand command, long version) {
                _session = session;
                _command = command;
                _version = version;
                _roomGeneration = session.SongRoomGeneration;
                CompeteRoom room = session.TournamentRoom;
                _roomId = room.Id;
                _tournamentId = room.TournamentId;
                _song = room.Song;
                _hash = command.Hash;
                _difficulty = command.Difficulty;
                _characteristic = command.Characteristic;
                Token = session.ConnectionCancellationToken;
            }

            internal bool IsCurrent() {
                if (!_session.IsSongRequestCurrent(_version) || _session.SongRoomGeneration != _roomGeneration ||
                    Token.IsCancellationRequested || Token != _session.ConnectionCancellationToken ||
                    _command.Hash != _hash || _command.Difficulty != _difficulty || _command.Characteristic != _characteristic) {
                    return false;
                }
                CompeteRoom room = _session.TournamentRoom;
                return room != null && room.Id == _roomId && room.TournamentId == _tournamentId && ReferenceEquals(room.Song, _song);
            }

            internal bool Publish(CompeteSongSelection selection, LudusDownloadState state, string status = null, string errorMessage = "") {
                if (!IsCurrent()) {
                    return false;
                }
                CompeteRoom room = _session.TournamentRoom;
                CompeteRoom next = status == null ? room.WithSong(selection) : room.WithSongStatus(selection ?? room.Song, status);
                _session.TournamentRoom = next;
                _roomGeneration = _session.SongRoomGeneration;
                _song = next.Song;
                if (!IsCurrent()) {
                    return false;
                }
                _session.NotifyRoomUpdated(next);
                if (!IsCurrent()) {
                    return false;
                }
                _session.SendDownloadState(state, errorMessage);
                return IsCurrent();
            }
        }

        internal static LiveSongCommand SongCommandFromSelection(CompeteSongSelection song) {
            if (song == null || string.IsNullOrEmpty(song.MapHash)) {
                return null;
            }

            return new LiveSongCommand {
                Hash = song.MapHash,
                Difficulty = song.Difficulty,
                Characteristic = song.Characteristic
            };
        }

        private static async Task<CompeteSongSelection> ResolveInstalledAfterDownloadFailure(ILudusServerCommandSession session, LiveSongCommand song, CancellationToken cancellationToken) {
            try {
                return await session.SongService.ResolveInstalledAfterRefresh(song, cancellationToken);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception refreshEx) {
                Plugin.Log.Warn($"Failed to refresh local live room songs: {refreshEx.Message}");
                return null;
            }
        }

        private static bool MatchesSong(CompeteSongSelection selection, LiveSongCommand song) {
            if (selection == null || song == null) {
                return false;
            }

            return string.Equals(selection.MapHash, song.Hash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
