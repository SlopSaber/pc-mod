using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace ScoreSaber.Features.Replays {
    internal static class ReplayLaunchGuard {
        private static readonly Harmony Harmony = new Harmony("com.umbranox.BeatSaber.ScoreSaber.ReplayLaunchGuard");
        private static bool _installed;

        private sealed class Launch {
            internal readonly Func<bool> IsCurrent;
            private readonly Action _install;
            private readonly Func<Action> _enter;
            private readonly Func<bool> _ready;
            private bool _helperAdmitted;
            private bool _pushAdmitted;
            private bool _installed;
            private int _helperDepth;

            internal Launch(Func<bool> isCurrent, Action install, Func<Action> enter, Func<bool> ready) {
                IsCurrent = isCurrent;
                _install = install;
                _enter = enter;
                _ready = ready;
            }

            internal void Install() {
                if (!IsCurrent() || _installed) return;
                _installed = true;
                _install();
            }

            internal bool TryEnter(bool push, out IDisposable lease) {
                lease = null;
                if (!IsCurrent() || !_ready()) return false;
                if (push) {
                    if (!_helperAdmitted || _pushAdmitted) return false;
                    _pushAdmitted = true;
                } else {
                    if (_helperAdmitted && (_helperDepth == 0 || _pushAdmitted)) return false;
                    _helperAdmitted = true;
                    _helperDepth++;
                }
                var release = _enter();
                lease = new Lease(exception => {
                    try {
                        release();
                    } finally {
                        if (!push) {
                            _helperDepth--;
                            if (_helperDepth == 0 && exception != null && !_pushAdmitted) _helperAdmitted = false;
                        }
                    }
                });
                return true;
            }

            internal bool HasPushAdmission => _pushAdmitted;
        }

        private sealed class Lease : IDisposable {
            private Action<Exception> _release;

            internal Lease(Action<Exception> release) {
                _release = release;
            }

            internal void Finish(Exception exception) {
                var release = _release;
                _release = null;
                release?.Invoke(exception);
            }

            public void Dispose() => Finish(null);
        }

        internal static Action CreateCallback(Func<bool> isCurrent, Action install, Func<Action> enter, Func<bool> ready) =>
            new Launch(isCurrent, install, enter, ready).Install;

        internal static bool HasPushAdmission(Action callback) =>
            callback.Target is Launch launch && callback.GetInvocationList().Length == 1 && launch.HasPushAdmission;

        internal static void EnsureInstalled() {
            if (_installed) return;
            var prefix = new HarmonyMethod(typeof(ReplayLaunchGuard), nameof(BeforeStart));
            var finalizer = new HarmonyMethod(typeof(ReplayLaunchGuard), nameof(FinishStart));
            foreach (var method in typeof(MenuTransitionsHelper).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == nameof(MenuTransitionsHelper.StartStandardLevel))) {
                Harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            foreach (var method in typeof(GameScenesManager).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == nameof(GameScenesManager.PushScenes))) {
                Harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            _installed = true;
        }

        private static bool BeforeStart(object[] __args, MethodBase __originalMethod, out IDisposable __state) {
            __state = null;
            foreach (var argument in __args) {
                if (argument is Action callback && callback.Target is Launch launch && callback.GetInvocationList().Length == 1) {
                    return launch.TryEnter(__originalMethod.DeclaringType == typeof(GameScenesManager), out __state);
                }
            }
            return true;
        }

        private static Exception FinishStart(Exception __exception, IDisposable __state) {
            if (__state is Lease lease) lease.Finish(__exception);
            return __exception;
        }
    }
}
