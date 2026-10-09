using ScoreSaber.Features.Replays;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class ApiRequestContentPreparation {
        internal static StringContent Create(string text) {
            if (!CanPrepare(text)) {
                return new StringContent(text);
            }

            Task<StringContent> task = null;
            try {
                TryQueue(text, out task);
            } catch { }
            if (task == null) {
                return new StringContent(text);
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            return task.GetAwaiter().GetResult();
        }

        private static bool CanPrepare(string text) {
            if (text == null || text.Length < 512 * 1024 || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }
            try {
                Encoding encoding = Encoding.UTF8;
                return encoding.GetType() == typeof(UTF8Encoding) && encoding.IsReadOnly
                    && encoding.EncoderFallback?.GetType() == typeof(EncoderReplacementFallback)
                    && encoding.DecoderFallback?.GetType() == typeof(DecoderReplacementFallback);
            } catch {
                return false;
            }
        }

        private static bool TryQueue(string text, out Task<StringContent> task) {
            Func<StringContent> prepare = () => new StringContent(text);
            return ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)
                || ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task);
        }
    }
}
