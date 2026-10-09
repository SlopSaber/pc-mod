using ScoreSaber.Features.Replays;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class StreamReaderBatchPreparation {
        private const int BatchSize = 256 * 1024;
        private static readonly Guid SupportedModule = new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790");

        internal sealed class Batch {
            internal readonly StreamReader Reader;
            internal readonly char[] Characters;
            internal readonly int Count;
            internal readonly bool Blocked;

            internal Batch(StreamReader reader, char[] characters, int count, bool blocked) {
                Reader = reader;
                Characters = characters;
                Count = count;
                Blocked = blocked;
            }
        }

        internal static bool TryPrepare(StreamReader reader, out Batch batch) {
            batch = null;
            StreamReader candidate = null;
            Task<Batch> task = null;
            try {
                if (!TryClone(reader, out candidate)) {
                    return false;
                }
                TryQueue(candidate, out task);
            } catch { }
            if (task == null) {
                Discard(candidate);
                return false;
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            try {
                batch = task.GetAwaiter().GetResult();
                return true;
            } catch {
                Discard(candidate);
                return false;
            }
        }

        internal static bool IsAsyncBusy(StreamReader reader) {
            Task task = Schema.AsyncTask.GetValue(reader) as Task;
            return task != null && !task.IsCompleted;
        }

        internal static void Discard(StreamReader reader) {
            if (reader == null) {
                return;
            }
            try { reader.Dispose(); }
            catch { }
        }

        private static bool TryClone(StreamReader reader, out StreamReader candidate) {
            candidate = null;
            if (typeof(StreamReader).Module.ModuleVersionId != SupportedModule
                || reader.GetType() != typeof(StreamReader)
                || reader.BaseStream?.GetType() != typeof(MemoryStream)) {
                return false;
            }
            MemoryStream stream = (MemoryStream)reader.BaseStream;
            if (stream.Length - stream.Position < BatchSize) {
                return false;
            }
            Encoding encoding = reader.CurrentEncoding;
            Decoder decoder = Schema.Decoder.GetValue(reader) as Decoder;
            Task lastRead = Schema.LastReadTask.GetValue(stream) as Task;
            if (IsAsyncBusy(reader)
                || Schema.Identity.GetValue(reader) != null || Schema.Identity.GetValue(stream) != null
                || Schema.StreamTask.GetValue(stream) != null || Schema.StreamSemaphore.GetValue(stream) != null
                || (lastRead != null && lastRead.Status != TaskStatus.RanToCompletion)
                || !ReferenceEquals(encoding, Encoding.UTF8) || encoding.GetType() != typeof(UTF8Encoding)
                || !encoding.IsReadOnly || encoding.EncoderFallback?.GetType() != typeof(EncoderReplacementFallback)
                || encoding.DecoderFallback?.GetType() != typeof(DecoderReplacementFallback)
                || decoder?.GetType() != Schema.DecoderType || Schema.FallbackBuffer.GetValue(decoder) != null
                || !ReferenceEquals(Schema.DecoderEncoding.GetValue(decoder), encoding)
                || !ReferenceEquals(Schema.Fallback.GetValue(decoder), encoding.DecoderFallback)) {
                return false;
            }

            MemoryStream streamCopy = (MemoryStream)Schema.MemberwiseClone.Invoke(stream, null);
            Decoder decoderCopy = (Decoder)Schema.MemberwiseClone.Invoke(decoder, null);
            byte[] bytes = (byte[])((byte[])Schema.ByteBuffer.GetValue(reader)).Clone();
            char[] characters = (char[])((char[])Schema.CharBuffer.GetValue(reader)).Clone();
            StreamReader copy = (StreamReader)Schema.MemberwiseClone.Invoke(reader, null);
            Schema.Stream.SetValue(copy, streamCopy);
            candidate = copy;
            Schema.Decoder.SetValue(copy, decoderCopy);
            Schema.ByteBuffer.SetValue(copy, bytes);
            Schema.CharBuffer.SetValue(copy, characters);
            Schema.AsyncTask.SetValue(copy, null);
            return true;
        }

        private static bool TryQueue(StreamReader reader, out Task<Batch> task) {
            Func<Batch> prepare = () => Prepare(reader);
            return ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)
                || ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task);
        }

        private static Batch Prepare(StreamReader reader) {
            char[] current = (char[])Schema.CharBuffer.GetValue(reader);
            char[] characters = new char[BatchSize + current.Length];
            int count = reader.Read(characters, 0, BatchSize);
            bool blocked = (bool)Schema.Blocked.GetValue(reader);
            if (blocked) {
                int remaining = (int)Schema.CharLength.GetValue(reader) - (int)Schema.CharPosition.GetValue(reader);
                if (remaining > 0) {
                    count += reader.Read(characters, count, remaining);
                }
            }
            return new Batch(reader, characters, count, blocked);
        }

        private static class Schema {
            private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            internal static readonly MethodInfo MemberwiseClone = typeof(object).GetMethod("MemberwiseClone", InstanceFields);
            internal static readonly FieldInfo Stream = Field(typeof(StreamReader), "_stream");
            internal static readonly FieldInfo Decoder = Field(typeof(StreamReader), "_decoder");
            internal static readonly FieldInfo ByteBuffer = Field(typeof(StreamReader), "_byteBuffer");
            internal static readonly FieldInfo CharBuffer = Field(typeof(StreamReader), "_charBuffer");
            internal static readonly FieldInfo CharPosition = Field(typeof(StreamReader), "_charPos");
            internal static readonly FieldInfo CharLength = Field(typeof(StreamReader), "_charLen");
            internal static readonly FieldInfo Blocked = Field(typeof(StreamReader), "_isBlocked");
            internal static readonly FieldInfo AsyncTask = Field(typeof(StreamReader), "_asyncReadTask");
            internal static readonly FieldInfo LastReadTask = Field(typeof(MemoryStream), "_lastReadTask");
            internal static readonly FieldInfo StreamTask = Field(typeof(System.IO.Stream), "_activeReadWriteTask");
            internal static readonly FieldInfo StreamSemaphore = Field(typeof(System.IO.Stream), "_asyncActiveSemaphore");
            internal static readonly FieldInfo Identity = Field(typeof(MarshalByRefObject), "_identity");
            internal static readonly FieldInfo Fallback = Field(typeof(System.Text.Decoder), "_fallback");
            internal static readonly FieldInfo FallbackBuffer = Field(typeof(System.Text.Decoder), "_fallbackBuffer");
            internal static readonly Type DecoderType = typeof(UTF8Encoding).GetNestedType("UTF8Decoder", BindingFlags.NonPublic);
            internal static readonly FieldInfo DecoderEncoding = Field(DecoderType.BaseType, "_encoding");

            private static FieldInfo Field(Type type, string name) {
                return type.GetField(name, InstanceFields) ?? throw new MissingFieldException(type.FullName, name);
            }
        }
    }
}
