using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonCompositeFormatAppendPreparation {
        internal static readonly MethodInfo Target = Initialize();
        private static bool Rewritten;
        internal static bool Supported => Target != null;

        internal static bool IsAccepted() {
            if (!Supported) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            if (info == null) { return true; }
            return Rewritten && info.Prefixes.Count == 0 && info.Postfixes.Count == 0
                && info.Finalizers.Count == 0 && info.Transpilers.Count == 1
                && info.Transpilers[0].PatchMethod == ApiJsonCompositeFormatAppendPatch.TranspilerMethod;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            MethodInfo append = AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(string) });
            MethodInfo wrapper = AccessTools.Method(typeof(ApiJsonDateBuilderOutputPreparation), "AppendForFormatter");
            if (!Supported || code.Count != 498 || append == null || wrapper == null
                || code[483].opcode != OpCodes.Call || !Equals(code[483].operand, append)) { return code; }
            code[483].operand = wrapper;
            Rewritten = true;
            return code;
        }

        private static MethodInfo Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) { return null; }
                Type parameters = typeof(string).Assembly.GetType("System.ParamsArray");
                MethodInfo target = AccessTools.Method(typeof(StringBuilder), "AppendFormatHelper", new[] { typeof(IFormatProvider), typeof(string), parameters });
                byte[] bytes = target?.GetMethodBody()?.GetILAsByteArray();
                if (bytes == null || bytes.Length != 988) { return null; }
                using (SHA256 hash = SHA256.Create()) {
                    return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant()
                        == "570368ca8de8c65837b1e5470dcbd6dd53d8bcaccbcf46f074a7ac8e70b2cb34" ? target : null;
                }
            } catch { return null; }
        }

        private static bool BorrowRewritten;

        internal static bool HasBorrowAdapter() => Rewritten && BorrowRewritten && IsAccepted();

        internal static IEnumerable<CodeInstruction> RewriteBorrow(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(Rewrite(instructions));
            BorrowRewritten = false;
            MethodInfo selected = AccessTools.PropertyGetter(typeof(ApiJsonFormattingPreparation), "IsSelected");
            MethodInfo remaining = AccessTools.Method(typeof(ApiJsonFormatterBorrowPreparation), "GetRemaining");
            MethodInfo format = AccessTools.Method(typeof(ApiJsonFormatterBorrowPreparation), "TryFormat");
            MethodInfo builtin = AccessTools.Method(typeof(ApiJsonFormatterBorrowPreparation), "IsBuiltin");
            if (!Rewritten || generator == null || !ApiJsonFormatterBorrowPreparation.Supported
                || selected == null || remaining == null || format == null || builtin == null || code.Count != 498
                || code[409].opcode != OpCodes.Ldloc_S || code[410].opcode != OpCodes.Ldarg_0
                || code[411].opcode != OpCodes.Call || !Equals(code[411].operand, ApiJsonFormatterBorrowPreparation.GetterTarget)
                || code[415].opcode != OpCodes.Callvirt || !Equals(code[415].operand, ApiJsonFormatterBorrowPreparation.FormatterTarget)
                || code[411].blocks.Count != 0 || code[415].blocks.Count != 0) { return code; }
            LocalBuilder state = generator.DeclareLocal(typeof(ApiJsonDateBuilderOutputPreparation.BorrowState));
            Label originalFormat = generator.DefineLabel();
            Label formatted = generator.DefineLabel();
            var formatPrefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Call, selected),
                new CodeInstruction(OpCodes.Brfalse, originalFormat),
                new CodeInstruction(code[409].opcode, code[409].operand),
                new CodeInstruction(OpCodes.Call, builtin),
                new CodeInstruction(OpCodes.Brfalse, originalFormat),
                new CodeInstruction(OpCodes.Ldloca, state),
                new CodeInstruction(OpCodes.Call, format),
                new CodeInstruction(OpCodes.Br, formatted)
            };
            formatPrefix[0].labels.AddRange(code[415].labels);
            code[415].labels.Clear();
            code[415].labels.Add(originalFormat);
            code[416].labels.Add(formatted);
            code.InsertRange(415, formatPrefix);
            Label originalRemaining = generator.DefineLabel();
            Label obtained = generator.DefineLabel();
            var remainingPrefix = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldloca, state),
                new CodeInstruction(OpCodes.Initobj, typeof(ApiJsonDateBuilderOutputPreparation.BorrowState)),
                new CodeInstruction(OpCodes.Call, selected),
                new CodeInstruction(OpCodes.Brfalse, originalRemaining),
                new CodeInstruction(code[409].opcode, code[409].operand),
                new CodeInstruction(OpCodes.Call, builtin),
                new CodeInstruction(OpCodes.Brfalse, originalRemaining),
                new CodeInstruction(code[409].opcode, code[409].operand),
                new CodeInstruction(OpCodes.Ldloca, state),
                new CodeInstruction(OpCodes.Call, remaining),
                new CodeInstruction(OpCodes.Br, obtained)
            };
            remainingPrefix[0].labels.AddRange(code[411].labels);
            code[411].labels.Clear();
            code[411].labels.Add(originalRemaining);
            code[412].labels.Add(obtained);
            code.InsertRange(411, remainingPrefix);
            BorrowRewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonCompositeFormatAppendPatch {
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonCompositeFormatAppendPatch), "Transpiler");
        private static bool Prepare() => ApiJsonCompositeFormatAppendPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonCompositeFormatAppendPreparation.Target;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) => ApiJsonCompositeFormatAppendPreparation.RewriteBorrow(instructions, generator);
    }
}
