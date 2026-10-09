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
            private readonly int _ownerThread = Thread.CurrentThread.ManagedThreadId;
            private JsonTextReader _jsonReader;

            internal OwnedBlockReader(StreamReader reader) {
                _reader = reader;
            }

            internal void Attach(JsonTextReader reader) {
                _jsonReader = reader;
            }

            public override int Read(char[] buffer, int index, int count) {
                if (!CanLease(buffer, index, count) || !TryReadOwned(_reader, buffer, index, count, out int read)) {
                    return _reader.Read(buffer, index, count);
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
                        && _reader.BaseStream.Length - _reader.BaseStream.Position >= 1024 * 1024;
                } catch {
                    return false;
                }
            }

            public override int Read() => _reader.Read();
            public override int Peek() => _reader.Peek();
            public override Task<int> ReadAsync(char[] buffer, int index, int count) => _reader.ReadAsync(buffer, index, count);

            protected override void Dispose(bool disposing) {
                if (disposing) {
                    _reader.Dispose();
                }
                base.Dispose(disposing);
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
