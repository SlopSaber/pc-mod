using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using ScoreSaber.Core.Api;

namespace ScoreSaber.Features.Leaderboards.UI.ScoreDetails {
    internal static partial class ScoreDetailNamePreparation {
        static partial void AddRuntimeGuards(List<MethodBase> guards, ref bool supported) {
            if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return; }
            MethodInfo concat = AccessTools.Method(typeof(string), "Concat", new[] { typeof(string), typeof(string) });
            MethodInfo empty = AccessTools.Method(typeof(string), "IsNullOrEmpty", new[] { typeof(string) });
            MethodInfo fill = AccessTools.Method(typeof(string), "FillStringChecked", new[] { typeof(string), typeof(int), typeof(string) });
            if (!Matches(concat, 71, "d191d62d3d52b2e4524fd489bd1265fe589334cfcc644a78007dc240080f7752")
                || !Matches(empty, 16, "dadf43597eaa2cded2f651d3e2cc9598b8fe0eb74770fdb8f66c78aabf6d3619")
                || !Matches(fill, 65, "e865ddfcb998a9c9ea911aa781d763799b146d558cc68009635508ed8a38e246")) { return; }
            MethodInfo concat3 = AccessTools.Method(typeof(string), "Concat", new[] { typeof(string), typeof(string), typeof(string) });
            MethodInfo concat4 = AccessTools.Method(typeof(string), "Concat", new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
            if (!Matches(concat3, 115, "3a0a221449ed420d495a6a8ff8c82a38fd014376eb731525e4b3e65461ce0794")
                || !Matches(concat4, 169, "d57d928cf5b4de26babfba295aad55a6a61cc26edd9e66ae9c5c0dd62b66f61a")) { return; }
            guards.Add(concat3);
            guards.Add(concat4);
            guards.Add(concat);
            guards.Add(empty);
            guards.Add(fill);
            guards.Add(AccessTools.Method(typeof(ApiJsonOwnedOutputPreparation), "AddCopyGuards"));
            ApiJsonOwnedOutputPreparation.AddCopyGuards(guards);
            supported = true;
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
