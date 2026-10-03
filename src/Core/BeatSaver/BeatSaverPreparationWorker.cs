using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.BeatSaver {
    internal static class BeatSaverPreparationWorker {
        private static readonly object QueueLock = new object();
        private static Task _tail = Task.CompletedTask;
        private static readonly object DestinationLock = new object();
        private static readonly Dictionary<string, DestinationGate> Destinations =
            new Dictionary<string, DestinationGate>(StringComparer.OrdinalIgnoreCase);

        internal static Task<Result<BeatSaverMap>> Decode(byte[] bytes, CancellationToken cancellationToken) {
            return Enqueue(new JsonRequest(bytes, cancellationToken).Run);
        }

        internal static Task<Result<bool>> File(BeatSaverService.FileRequest request) {
            return Enqueue(request.Run);
        }

        internal static async Task<IDisposable> EnterDestination(string path) {
            DestinationGate gate;
            lock (DestinationLock) {
                if (!Destinations.TryGetValue(path, out gate)) {
                    gate = new DestinationGate();
                    Destinations.Add(path, gate);
                }

                gate.Users++;
            }

            try {
                await gate.Semaphore.WaitAsync();
                return new DestinationLease(path, gate);
            } catch {
                ReleaseDestination(path, gate, false);
                throw;
            }
        }

        private static Task<Result<T>> Enqueue<T>(Func<Result<T>> prepare) {
            lock (QueueLock) {
                Task<Result<T>> task = _tail.ContinueWith(
                    (_, state) => ((Func<Result<T>>)state)(), prepare,
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                _tail = task;
                _ = task.ContinueWith(completed => {
                    lock (QueueLock) {
                        if (ReferenceEquals(_tail, completed)) {
                            _tail = Task.CompletedTask;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return task;
            }
        }

        private static void ReleaseDestination(string path, DestinationGate gate, bool acquired) {
            if (acquired) {
                gate.Semaphore.Release();
            }

            lock (DestinationLock) {
                gate.Users--;
                if (gate.Users == 0) {
                    Destinations.Remove(path);
                    gate.Semaphore.Dispose();
                }
            }
        }

        internal sealed class Result<T> {
            internal T Value { get; }
            internal Exception Error { get; }
            internal string[] Warnings { get; }

            internal Result(T value, Exception error = null, string[] warnings = null) {
                Value = value;
                Error = error;
                Warnings = warnings ?? Array.Empty<string>();
            }
        }

        private sealed class JsonRequest {
            private byte[] _bytes;
            private readonly CancellationToken _cancellationToken;

            internal JsonRequest(byte[] bytes, CancellationToken cancellationToken) {
                _bytes = bytes;
                _cancellationToken = cancellationToken;
            }

            internal Result<BeatSaverMap> Run() {
                try {
                    _cancellationToken.ThrowIfCancellationRequested();
                    JsonSerializer serializer = JsonSerializer.Create();
                    serializer.CheckAdditionalContent = true;
                    string json = Encoding.UTF8.GetString(_bytes);
                    using (var text = new StringReader(json))
                    using (var reader = new JsonTextReader(text)) {
                        BeatSaverMap map = serializer.Deserialize<BeatSaverMap>(reader);
                        _cancellationToken.ThrowIfCancellationRequested();
                        return new Result<BeatSaverMap>(map);
                    }
                } catch (Exception error) {
                    return new Result<BeatSaverMap>(null, error);
                } finally {
                    _bytes = null;
                }
            }
        }

        private sealed class DestinationGate {
            internal readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            internal int Users;
        }

        private sealed class DestinationLease : IDisposable {
            private readonly string _path;
            private DestinationGate _gate;

            internal DestinationLease(string path, DestinationGate gate) {
                _path = path;
                _gate = gate;
            }

            public void Dispose() {
                DestinationGate gate = Interlocked.Exchange(ref _gate, null);
                if (gate != null) {
                    ReleaseDestination(_path, gate, true);
                }
            }
        }
    }
}
