using ScoreSaber.Core;
using ScoreSaber.Features.Live.Protocol;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Ludus.Services {
    internal sealed class LudusSessionTransport {
        internal sealed class Connection {
            internal readonly ClientWebSocket Socket = new ClientWebSocket();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Task ConnectTask = Task.CompletedTask;
            internal Task ReceiveTask = Task.CompletedTask;
            internal bool ConnectStarted;
            internal bool ReceiveStarted;
            private int _retired;

            internal bool IsRetired => Volatile.Read(ref _retired) != 0;
            internal void Retire() => Interlocked.Exchange(ref _retired, 1);
        }

        private readonly LudusMainThreadQueue _mainThread;
        private readonly object _sendTaskLock = new object();
        private Connection _connection;
        private int _generation;
        private Task _sendTask = Task.CompletedTask;
        private Task _retirementTask = Task.CompletedTask;

        internal LudusSessionTransport(LudusMainThreadQueue mainThread) {
            _mainThread = mainThread;
        }

        internal event Action<DecodedLudusEnvelope> MessageReceived;
        internal event Action<string> ReceiveFailed;
        internal event Action<string> SendFailed;
        internal event Action<string> ReconnectRequested;
        internal event Action Disconnected;

        internal bool IsOpen => _connection != null && !_connection.IsRetired && _connection.Socket.State == WebSocketState.Open;
        internal CancellationToken Token => _connection?.Cancellation.Token ?? CancellationToken.None;

        internal Connection Prepare() {
            int generation = RetireCurrentConnection();
            lock (_sendTaskLock) {
                if (generation != _generation) {
                    throw new OperationCanceledException("Ludus connection preparation was replaced.");
                }
                _connection = new Connection();
                _generation++;
                return _connection;
            }
        }

        internal bool IsCurrent(Connection connection) {
            lock (_sendTaskLock) {
                return connection != null && !connection.IsRetired && ReferenceEquals(_connection, connection);
            }
        }

        internal Task ConnectAsync(Connection connection, Uri uri) {
            lock (_sendTaskLock) {
                if (!IsCurrent(connection)) {
                    return Task.FromCanceled(new CancellationToken(true));
                }
                if (!connection.ConnectStarted) {
                    connection.ConnectStarted = true;
                    connection.ConnectTask = Task.Run(() => connection.Socket.ConnectAsync(uri, connection.Cancellation.Token));
                }
                return connection.ConnectTask;
            }
        }

        internal void StartReceiveLoop(Connection connection) {
            lock (_sendTaskLock) {
                if (!IsCurrent(connection) || connection.ReceiveStarted) {
                    return;
                }
                connection.ReceiveStarted = true;
                connection.ReceiveTask = Task.Run(() => ReceiveLoop(connection));
                connection.ReceiveTask.RunTask();
            }
        }

        internal bool SendDeferred(Func<byte[]> bytesFactory) {
            if (bytesFactory == null) {
                return false;
            }

            lock (_sendTaskLock) {
                Connection connection = _connection;
                if (connection == null || connection.IsRetired) {
                    return false;
                }
                Task previousSend = _sendTask;
                Task sendTask = previousSend.ContinueWith(
                    completed => SendAfter(connection, bytesFactory),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
                _sendTask = sendTask;
                sendTask.ContinueWith(completed => {
                    lock (_sendTaskLock) {
                        if (ReferenceEquals(_sendTask, completed)) {
                            _sendTask = Task.CompletedTask;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                sendTask.RunTask();
                return true;
            }
        }

        internal void DisposeSocket() => RetireCurrentConnection();

        private int RetireCurrentConnection() {
            Connection connection;
            int generation;
            var cancellationComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sendTaskLock) {
                generation = ++_generation;
                connection = _connection;
                if (connection == null) {
                    return generation;
                }
                _connection = null;
                connection.Retire();
                Task previousRetirement = _retirementTask;
                Task sends = _sendTask;
                Task retirementTask = Task.Run(async () => {
                    await cancellationComplete.Task.ConfigureAwait(false);
                    await RetireConnection(connection, sends, previousRetirement).ConfigureAwait(false);
                });
                _retirementTask = retirementTask;
                retirementTask.ContinueWith(completed => {
                    lock (_sendTaskLock) {
                        if (ReferenceEquals(_retirementTask, completed)) {
                            _retirementTask = Task.CompletedTask;
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                retirementTask.RunTask();
            }

            try {
                connection.Cancellation.Cancel();
            } catch (Exception ex) {
                Plugin.Log.Warn($"Failed to cancel ludus socket: {ex.Message}");
            } finally {
                cancellationComplete.TrySetResult(true);
            }
            return generation;
        }

        private async Task RetireConnection(Connection connection, Task sends, Task previousRetirement) {
            try {
                await Task.WhenAll(connection.ConnectTask, connection.ReceiveTask, sends).ConfigureAwait(false);
            } catch {
            }

            try {
                connection.Socket.Dispose();
            } catch (Exception ex) {
                string message = ex.Message;
                _mainThread.Enqueue(() => Plugin.Log.Warn($"Failed to close ludus socket: {message}"));
            } finally {
                connection.Cancellation.Dispose();
            }

            try {
                await previousRetirement.ConfigureAwait(false);
            } catch {
            }
        }

        private async Task ReceiveLoop(Connection connection) {
            byte[] buffer = new byte[64 * 1024];
            var message = new List<byte>();

            try {
                while (!connection.IsRetired && connection.Socket.State == WebSocketState.Open) {
                    WebSocketReceiveResult result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Cancellation.Token).ConfigureAwait(false);
                    if (connection.IsRetired || result.MessageType == WebSocketMessageType.Close) {
                        break;
                    }

                    for (int i = 0; i < result.Count; i++) {
                        message.Add(buffer[i]);
                    }

                    if (result.EndOfMessage) {
                        byte[] bytes = message.ToArray();
                        message.Clear();
                        string parseError;
                        DecodedLudusEnvelope envelope = LudusProto.Decode(bytes, out parseError);
                        if (envelope?.Type == LudusEnvelopeType.RoomSnapshot) {
                            envelope.PreparedRooms = OwnedRoomSnapshotPreparation.Prepare(envelope.Rooms);
                        }
                        if (envelope?.Type == LudusEnvelopeType.ChatSnapshot) {
                            envelope.PreparedChatKeys = OwnedChatKeyPreparation.Prepare(envelope.ChatSnapshot);
                        }
                        EnqueueCurrent(connection, () => {
                            if (parseError != null) {
                                Plugin.Log.Warn($"Failed to parse ludus protobuf frame: {parseError}");
                            }
                            MessageReceived?.Invoke(envelope);
                        });
                    }
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                string messageText = ex.Message;
                EnqueueCurrent(connection, () => ReceiveFailed?.Invoke(messageText));
            }

            EnqueueCurrent(connection, () => Disconnected?.Invoke());
        }

        private async Task SendAfter(Connection connection, Func<byte[]> bytesFactory) {
            if (connection.IsRetired) {
                return;
            }
            if (connection.Socket.State != WebSocketState.Open) {
                EnqueueCurrent(connection, () => ReconnectRequested?.Invoke("socket is not open"));
                return;
            }

            byte[] bytes;
            try {
                bytes = bytesFactory();
            } catch (Exception ex) {
                string message = ex.Message;
                EnqueueCurrent(connection, () => SendFailed?.Invoke(message));
                return;
            }
            if (connection.IsRetired || bytes == null || bytes.Length == 0) {
                return;
            }

            try {
                // ClientWebSocket permits only one physical send at a time.
                await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, connection.Cancellation.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } catch (ObjectDisposedException) {
            } catch (Exception ex) {
                string message = ex.Message;
                EnqueueCurrent(connection, () => {
                    SendFailed?.Invoke(message);
                    if (IsCurrent(connection)) {
                        ReconnectRequested?.Invoke(message);
                    }
                });
            }
        }

        private void EnqueueCurrent(Connection connection, Action action) {
            _mainThread.Enqueue(() => {
                if (IsCurrent(connection)) {
                    action();
                }
            });
        }
    }
}
