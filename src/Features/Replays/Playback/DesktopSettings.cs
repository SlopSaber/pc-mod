#if BSIPA_MODS
using System;
using System.Reflection;
using UnityEngine;
using Zenject;

namespace ScoreSaber.Features.Replays.Playback {
    internal sealed class DesktopSettings : ITickable {
        private static readonly Type FlightType = typeof(IPA.Utilities.UnityGame).Assembly.GetType("IPA.Desktop.DesktopFlight");
        private static readonly PropertyInfo Instance = FlightType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic);
        private event Action<DesktopSettings> Changed;
        private bool _lastEnabled;
        internal static Behaviour Flight => Instance?.GetValue(null) as Behaviour;
        public bool Enabled => Flight != null;

        public void AddChangedListener(Action<DesktopSettings> listener) {
            Changed += listener;
            listener(this);
        }

        public void RemoveChangedListener(Action<DesktopSettings> listener) => Changed -= listener;

        public void Tick() {
            bool enabled = Enabled;
            if (enabled == _lastEnabled) return;
            _lastEnabled = enabled;
            Changed?.Invoke(this);
        }
    }
}
#endif
