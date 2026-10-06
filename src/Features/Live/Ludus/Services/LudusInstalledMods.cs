using IPA.Loader;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScoreSaber.Features.Live.Ludus.Services {
    internal static class LudusInstalledMods {
        internal static List<LiveMod> List() {
            try {
                string[] ids = PluginManager.EnabledPlugins.Select(plugin => plugin.Id).ToArray();
                StringComparer comparer = StringComparer.CurrentCulture;
                if (ids.Length < 2 || Thread.CurrentThread.IsThreadPoolThread) {
                    return PrepareOwned(ids, comparer);
                }

                Task<List<LiveMod>> preparation;
                if (ExecutionContext.IsFlowSuppressed()) {
                    preparation = StartPreparation(ids, comparer);
                } else {
                    using (ExecutionContext.SuppressFlow()) {
                        preparation = StartPreparation(ids, comparer);
                    }
                }

                return preparation.GetAwaiter().GetResult();
            } catch (Exception ex) {
                Plugin.Log.Warn($"Failed to collect installed mods for Ludus: {ex.Message}");
                return new List<LiveMod>();
            }
        }

        private static Task<List<LiveMod>> StartPreparation(string[] ids, StringComparer comparer) {
            return Task.Factory.StartNew(
                () => PrepareOwned(ids, comparer),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private static List<LiveMod> PrepareOwned(string[] ids, StringComparer comparer) {
            return ids
                .Select(Normalize)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .OrderBy(id => id, comparer)
                .Select(id => new LiveMod { Id = id })
                .ToList();
        }

        private static string Normalize(string value) {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }
    }
}
