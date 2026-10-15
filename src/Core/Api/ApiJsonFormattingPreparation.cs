using System;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonFormattingPreparation {
        [ThreadStatic] private static bool _selected;

        internal static bool IsActive => ApiJsonNumericPreparation.IsActive || _selected;

        internal static bool EnterSelected() {
            bool previous = _selected;
            _selected = true;
            return previous;
        }

        internal static void ExitSelected(bool previous) {
            _selected = previous;
        }

        internal static bool IsSelected => _selected;
    }
}
