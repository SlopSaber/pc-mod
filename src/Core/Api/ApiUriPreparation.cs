using ScoreSaber.Features.Replays;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Core.Api {
    internal static class ApiUriPreparation {
        internal static string Escape(string text) {
            if (!CanPrepare(text)) {
                return Uri.EscapeDataString(text);
            }

            Task<string> task = null;
            try {
                TryQueue(text, out task);
            } catch { }
            if (task == null) {
                return Uri.EscapeDataString(text);
            }
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
            return task.GetAwaiter().GetResult();
        }

        private static bool CanPrepare(string text) {
            if (text == null || text.Length < 32 * 1024 || text.Length >= 65520
                || Thread.CurrentThread.IsThreadPoolThread) {
                return false;
            }
            try {
                Encoding encoding = Encoding.UTF8;
                if (encoding.GetType() != typeof(UTF8Encoding) || !encoding.IsReadOnly
                    || encoding.EncoderFallback?.GetType() != typeof(EncoderReplacementFallback)
                    || encoding.DecoderFallback?.GetType() != typeof(DecoderReplacementFallback)) {
                    return false;
                }
                Uri.EscapeDataString("%");
                return true;
            } catch {
                return false;
            }
        }

        private static bool TryQueue(string text, out Task<string> task) {
            Func<string> prepare = () => Uri.EscapeDataString(text);
            return ReplayStorageService.TryQueueOwnedPreparationWhenIdle(prepare, out task)
                || ReplayStorageService.TryStartIndependentOwnedPreparation(prepare, out task);
        }
    }
}
