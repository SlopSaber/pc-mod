using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using ScoreSaber.Core.Api;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailFormatPreparation {
        private static readonly MethodBase[] Guards = Initialize();

        static partial void Enter(ref object previous, ref bool entered) {
            if (Guards == null || !IsUnchanged() || !ApiJsonDateBuilderOutputPreparation.CanUseFormattingOutput()) { return; }
            previous = ApiJsonDateBuilderOutputPreparation.Enter();
            entered = true;
        }

        static partial void Exit(object previous, bool entered) {
            if (entered) { ApiJsonDateBuilderOutputPreparation.Exit((ApiJsonDateBuilderOutputPreparation.Frame)previous); }
        }

        private static bool IsUnchanged() {
            foreach (MethodBase method in Guards) {
                if (method == ApiJsonCompositeFormatAppendPreparation.Target) {
                    if (!ApiJsonCompositeFormatAppendPreparation.IsAccepted()) { return false; }
                } else if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static MethodBase[] Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type parameters = typeof(string).Assembly.GetType("System.ParamsArray");
                MethodInfo format = AccessTools.Method(typeof(string), "Format", new[] { typeof(string), typeof(object), typeof(object) });
                MethodInfo helper = AccessTools.Method(typeof(string), "FormatHelper", new[] { typeof(IFormatProvider), typeof(string), parameters });
                MethodInfo append = AccessTools.Method(typeof(StringBuilder), "AppendFormatHelper", new[] { typeof(IFormatProvider), typeof(string), parameters });
                if (!Matches(format, 15, "1a6a760cc8706bc762033e2fb379b87830d7b6114fa23355147a6fd83d12bb12")
                    || !Matches(helper, 49, "6ae652dd15ea640b73ce5577ca8a053c3c0111a3e3fa085a872a0387d9802890")
                    || !Matches(append, 988, "570368ca8de8c65837b1e5470dcbd6dd53d8bcaccbcf46f074a7ac8e70b2cb34")) { return null; }
                var guards = new List<MethodBase> { format, helper, append };
                AddDeclaredGuards(guards, parameters);
                AddDeclaredGuards(guards, typeof(ApiJsonCompositeFormatAppendPreparation));
                AddDeclaredGuards(guards, typeof(ApiJsonCompositeFormatAppendPatch));
                AddDeclaredGuards(guards, typeof(double));
                AddDeclaredGuards(guards, typeof(string).Assembly.GetType("System.Number"));
                AddDeclaredGuards(guards, typeof(ScoreDetailFormatPreparation));
                foreach (Type type in typeof(ScoreDetailFormatPreparation).GetNestedTypes(BindingFlags.NonPublic)) { AddDeclaredGuards(guards, type); }
                if (guards.Exists(method => method == null)) { return null; }
                return guards.ToArray();
            } catch { return null; }
        }

        private static void AddDeclaredGuards(List<MethodBase> guards, Type type) {
            guards.AddRange(type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            guards.AddRange(type.GetConstructors(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }

        private static bool Matches(MethodBase method, int size, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != size) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }
    }
}
