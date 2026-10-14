using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailNamePreparation {
        private static readonly MethodBase[] Guards = Initialize();

        internal static string Concat(string name, string suffix) {
            bool admitted = false;
            try {
                admitted = name != null && name.Length >= 524288 && !string.IsNullOrEmpty(suffix)
                    && (long)name.Length + suffix.Length <= int.MaxValue / 2
                    && !Thread.CurrentThread.IsThreadPoolThread && IsUnchanged();
            } catch { }
            if (!admitted) { return string.Concat(name, suffix); }

            Task<string> task = null;
            try { Queue(name, suffix, out task); } catch { }
            if (task == null) { return string.Concat(name, suffix); }
            return task.GetAwaiter().GetResult();
        }

        private static void Queue(string name, string suffix, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => string.Concat(name, suffix), out task);
        }

        private static bool IsUnchanged() {
            if (Guards == null) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static MethodBase[] Initialize() {
            try {
                var guards = new List<MethodBase>();
                bool supported = false;
                AddRuntimeGuards(guards, ref supported);
                if (!supported) { return null; }
                guards.Add(AccessTools.Method(typeof(ReplayStorageService), "TryQueueOwnedPreparationWhenIdle"));
                AddDeclaredGuards(guards, typeof(ScoreDetailNamePreparation));
                foreach (Type type in typeof(ScoreDetailNamePreparation).GetNestedTypes(BindingFlags.NonPublic)) {
                    AddDeclaredGuards(guards, type);
                }
                if (guards.Exists(method => method == null)) { return null; }
                return guards.ToArray();
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            guards.AddRange(type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            guards.AddRange(type.GetConstructors(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }

        static partial void AddRuntimeGuards(List<MethodBase> guards, ref bool supported);
    }
}
