using Newtonsoft.Json;
using ScoreSaber.Features.Replays;
using SiraUtil.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class ApiResponseReadPreparation {
        internal static Task<IHttpResponse> Send(IHttpService service, HTTPMethod method, string url, int timeout,
            string body, IDictionary<string, string> headers, CancellationToken cancellationToken, out bool nativeResponse) {
            IHttpService nativeService = FindNativeService(service);
            nativeResponse = nativeService != null;
            return (nativeService ?? service).SendAsync(method, url, timeout, body, headers, null, cancellationToken);
        }

        private static IHttpService FindNativeService(IHttpService service) {
            try {
                Assembly assembly = typeof(IHttpService).Assembly;
                Type nativeType = assembly.GetType("SiraUtil.Web.Implementations.UWRHttpService", false);
                if (nativeType != null && service?.GetType() == nativeType) {
                    return service;
                }
                Type wrapperType = assembly.GetType("SiraUtil.Web.Zenject.ContainerizedHttpService", false);
                if (wrapperType == null || service?.GetType() != wrapperType) {
                    return null;
                }
                FieldInfo childField = wrapperType.GetField("_childService", BindingFlags.Instance | BindingFlags.NonPublic);
                IHttpService child = childField?.GetValue(service) as IHttpService;
                return nativeType != null && child?.GetType() == nativeType ? child : null;
            } catch {
                return null;
            }
        }

        internal static HttpContent CreateContent(byte[] bytes, IHttpResponse response, bool nativeResponse) {
            bool owned = false;
            try {
                Type responseType = typeof(IHttpResponse).Assembly.GetType("SiraUtil.Web.Implementations.UnityWebRequestHttpResponse", false);
                owned = nativeResponse && bytes != null && bytes.Length >= 1024 * 1024
                    && responseType != null && response?.GetType() == responseType;
            } catch { }
            return owned ? new OwnedContent(bytes) : new ByteArrayContent(bytes);
        }

        internal static TextReader WrapReader(StreamReader reader, HttpContent content) {
            try {
                if (content is OwnedContent && reader.GetType() == typeof(StreamReader)
                    && reader.BaseStream?.GetType() == typeof(MemoryStream)) {
                    return new OwnedBlockReader(reader);
                }
            } catch { }
            return reader;
        }

        internal static void AttachReader(TextReader reader, JsonTextReader jsonReader) {
            if (reader is OwnedBlockReader owned) {
                owned.Attach(jsonReader);
            }
        }

        private sealed class OwnedContent : ByteArrayContent {
            internal OwnedContent(byte[] bytes) : base(bytes) { }
        }

        private sealed class OwnedBlockReader : TextReader {
            private readonly StreamReader _reader;
            private readonly object _gate = new object();
            private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
            private JsonTextReader _jsonReader;
            private StreamReader _activeReader;
            private StreamReaderBatchPreparation.Batch _batch;
            private int _batchPosition;

            internal OwnedBlockReader(StreamReader reader) {
                _reader = reader;
                _activeReader = reader;
            }

            internal void Attach(JsonTextReader reader) {
                _jsonReader = reader;
            }

            public override int Read(char[] buffer, int index, int count) {
                lock (_gate) {
                    if (!ValidRange(buffer, index, count)) {
                        return _activeReader.Read(buffer, index, count);
                    }
                    _activeReader.Read(buffer, index, 0);
                    int copied = 0;
                    while (copied < count) {
                        if (_batch != null) {
                            copied += CopyBatch(buffer, index + copied, count - copied, out bool blocked);
                            if (blocked) {
                                return copied;
                            }
                            continue;
                        }
                        if (TryPrepareBatch(buffer, index + copied, count - copied, out int preparedCopy, out bool preparedBlocked)) {
                            copied += preparedCopy;
                            if (preparedBlocked) {
                                return copied;
                            }
                            continue;
                        }
                        int remaining = count - copied;
                        if (!CanLease(buffer, index + copied, remaining)
                            || !TryReadOwned(_activeReader, buffer, index + copied, remaining, out int read)) {
                            read = _activeReader.Read(buffer, index + copied, remaining);
                        }
                        return copied + read;
                    }
                    return copied;
                }
            }

            private static bool ValidRange(char[] buffer, int index, int count) =>
                buffer != null && index >= 0 && count >= 0 && count <= buffer.Length - index;

            private bool TryPrepareBatch(char[] buffer, int index, int count, out int copied, out bool blocked) {
                copied = 0;
                blocked = false;
                if (Thread.CurrentThread.IsThreadPoolThread || Thread.CurrentThread.ManagedThreadId != _ownerThread
                    || _jsonReader == null || _jsonReader.ArrayPool != null) {
                    return false;
                }
                try {
                    if (_activeReader.BaseStream.Length - _activeReader.BaseStream.Position < 256 * 1024) {
                        return false;
                    }
                } catch {
                    return false;
                }
                char[] destination = CanCopyInitialBatch(buffer, index, count) ? buffer : null;
                if (!StreamReaderBatchPreparation.TryPrepare(_activeReader, destination, index, count, out var batch)) {
                    return false;
                }
                StreamReader previous = _activeReader;
                _activeReader = batch.Reader;
                _batch = batch;
                copied = batch.InitialCopyCount;
                _batchPosition = copied;
                blocked = copied == batch.Count && batch.Blocked;
                if (copied == batch.Count) {
                    _batch = null;
                }
                if (!ReferenceEquals(previous, _reader)) {
                    StreamReaderBatchPreparation.Discard(previous);
                }
                return true;
            }

            private bool CanCopyInitialBatch(char[] buffer, int index, int count) {
                if (count < 128 * 1024 || !ValidRange(buffer, index, count)) {
                    return false;
                }
                try {
                    return _jsonReader != null && _jsonReader.GetType() == typeof(JsonTextReader)
                        && typeof(JsonTextReader).Module.ModuleVersionId == new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")
                        && ReferenceEquals(typeof(JsonTextReader).GetField("_chars", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.GetValue(_jsonReader), buffer);
                } catch {
                    return false;
                }
            }

            private int CopyBatch(char[] buffer, int index, int count, out bool blocked) {
                int read = Math.Min(count, _batch.Count - _batchPosition);
                Array.Copy(_batch.Characters, _batchPosition, buffer, index, read);
                _batchPosition += read;
                blocked = _batchPosition == _batch.Count && _batch.Blocked;
                if (_batchPosition == _batch.Count) {
                    _batch = null;
                }
                return read;
            }

            private bool CanLease(char[] buffer, int index, int count) {
                if (count < 512 * 1024 || buffer == null || index < 0 || count > buffer.Length - index
                    || Thread.CurrentThread.IsThreadPoolThread || Thread.CurrentThread.ManagedThreadId != _ownerThread) {
                    return false;
                }
                try {
                    return _jsonReader != null && _jsonReader.ArrayPool == null
                        && _activeReader.BaseStream.Length - _activeReader.BaseStream.Position >= 1024 * 1024;
                } catch {
                    return false;
                }
            }

            public override int Read() {
                lock (_gate) {
                    if (_batch != null && _batchPosition == _batch.Count) {
                        _batch = null;
                    }
                    if (_batch == null) {
                        return _activeReader.Read();
                    }
                    int value = _batchPosition < _batch.Count ? _batch.Characters[_batchPosition++] : -1;
                    if (_batchPosition == _batch.Count) {
                        _batch = null;
                    }
                    return value;
                }
            }

            public override int Peek() {
                lock (_gate) {
                    return _batch != null && _batchPosition < _batch.Count
                        ? _batch.Characters[_batchPosition] : _activeReader.Peek();
                }
            }

            public override Task<int> ReadAsync(char[] buffer, int index, int count) {
                lock (_gate) {
                    if (_batch == null || !ValidRange(buffer, index, count) || _activeReader.BaseStream == null
                        || StreamReaderBatchPreparation.IsAsyncBusy(_activeReader)) {
                        return _activeReader.ReadAsync(buffer, index, count);
                    }
                    return ReadCachedAsync(buffer, index, count);
                }
            }

            private async Task<int> ReadCachedAsync(char[] buffer, int index, int count) {
                int copied = CopyBatch(buffer, index, count, out bool blocked);
                if (blocked || copied == count) {
                    return copied;
                }
                int read = await _activeReader.ReadAsync(buffer, index + copied, count - copied).ConfigureAwait(false);
                return copied + read;
            }

            protected override void Dispose(bool disposing) {
                lock (_gate) {
                    if (disposing) {
                        _batch = null;
                        if (!ReferenceEquals(_activeReader, _reader)) {
                            StreamReaderBatchPreparation.Discard(_activeReader);
                        }
                        _reader.Dispose();
                    }
                    base.Dispose(disposing);
                }
            }
        }

        private static bool TryReadOwned(StreamReader reader, char[] buffer, int index, int count, out int read) {
            read = 0;
            Task<int> task = null;
            try {
                if (!TryQueueRead(reader, buffer, index, count, out task)) {
                    return false;
                }
            } catch {
                if (task == null) {
                    return false;
                }
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            read = task.GetAwaiter().GetResult();
            return true;
        }

        private static bool TryQueueRead(StreamReader reader, char[] buffer, int index, int count, out Task<int> task) {
            Func<int> prepare = () => reader.Read(buffer, index, count);
            return ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)
                || ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task);
        }
    }
}
