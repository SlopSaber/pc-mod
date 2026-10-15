extern alias ScoreSaberCore;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using CoreSpan = ScoreSaberCore::System.Span<char>;
using CoreReadOnlySpan = ScoreSaberCore::System.ReadOnlySpan<char>;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonFormatterBorrowPreparation {
        internal delegate CoreSpan RemainingReader(StringBuilder builder);
        internal delegate bool NumericFormatter(object value, CoreSpan destination, out int written, CoreReadOnlySpan format, IFormatProvider provider);
        internal static MethodInfo GetterTarget;
        internal static MethodInfo FormatterTarget;
        private static NumericFormatter Formatter;
        private static Func<StringBuilder, int> Length;
        private static MethodBase[] Guards;
        private static readonly RemainingReader Remaining = Initialize();
        internal static bool Supported => Remaining != null && Formatter != null && Length != null && Guards != null;

        internal static CoreSpan GetRemaining(StringBuilder builder, object value, out ApiJsonDateBuilderOutputPreparation.BorrowState state) {
            state = default;
            try { state = ApiJsonDateBuilderOutputPreparation.CaptureFormatterBorrow(builder, value); }
            catch { }
            return Remaining(builder);
        }

        internal static bool TryFormat(object value, CoreSpan destination, out int written, CoreReadOnlySpan format,
            IFormatProvider provider, ref ApiJsonDateBuilderOutputPreparation.BorrowState state) {
            bool result = Formatter(value, destination, out written, format, provider);
            try { ApiJsonDateBuilderOutputPreparation.CompleteFormatterBorrow(state); }
            catch { }
            return result;
        }

        internal static int ReadLength(StringBuilder builder) => Length(builder);

        internal static bool IsBuiltin(object value) => value is int || value is double;

        internal static bool IsUnchanged() {
            if (!Supported) { return false; }
            foreach (MethodBase method in Guards) {
                if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static RemainingReader Initialize() {
            try {
                Assembly core = typeof(string).Assembly;
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")
                    || typeof(CoreSpan).Assembly != core || typeof(CoreReadOnlySpan).Assembly != core) { return null; }
                Type number = core.GetType("System.Number");
                Type builder = core.GetType("System.Text.ValueStringBuilder");
                Type formattable = core.GetType("System.ISpanFormattable");
                Type marshal = core.GetType("System.Runtime.InteropServices.MemoryMarshal");
                Type byReference = core.GetType("System.ByReference`1")?.MakeGenericType(typeof(char));
                Type unsafeType = core.GetType("System.Runtime.CompilerServices.Unsafe");
                if (number == null || builder == null || formattable == null || marshal == null || byReference == null || unsafeType == null) { return null; }
                Type integerReference = typeof(int).MakeByRefType();
                GetterTarget = AccessTools.PropertyGetter(typeof(StringBuilder), "RemainingCurrentChunk");
                FormatterTarget = AccessTools.Method(formattable, "TryFormat", new[] { typeof(CoreSpan), integerReference, typeof(CoreReadOnlySpan), typeof(IFormatProvider) });
                FieldInfo chunkLength = AccessTools.Field(typeof(StringBuilder), "m_ChunkLength");
                FieldInfo chunkOffset = AccessTools.Field(typeof(StringBuilder), "m_ChunkOffset");
                if (GetterTarget == null || GetterTarget.ReturnType != typeof(CoreSpan)
                    || FormatterTarget == null || FormatterTarget.ReturnType != typeof(bool)
                    || chunkLength == null || chunkOffset == null || chunkLength.IsStatic || chunkOffset.IsStatic
                    || chunkLength.FieldType != typeof(int) || chunkOffset.FieldType != typeof(int)) { return null; }
                var guards = new List<MethodBase> {
                    FormatterTarget,
                    AccessTools.Method(typeof(int), "TryFormat", new[] { typeof(CoreSpan), integerReference, typeof(CoreReadOnlySpan), typeof(IFormatProvider) }),
                    AccessTools.Method(typeof(double), "TryFormat", new[] { typeof(CoreSpan), integerReference, typeof(CoreReadOnlySpan), typeof(IFormatProvider) }),
                    AccessTools.Method(number, "TryFormatInt32"), AccessTools.Method(number, "TryFormatDouble"),
                    AccessTools.Method(number, "TryUInt32ToDecStr"), AccessTools.Method(number, "TryNegativeInt32ToDecStr"),
                    AccessTools.Method(number, "TryInt32ToHexStr"), AccessTools.Method(number, "UInt32ToDecChars"),
                    AccessTools.Method(number, "Int32ToHexChars"),
                    AccessTools.Method(number, "TryCopyTo", new[] { typeof(string), typeof(CoreSpan), integerReference }),
                    AccessTools.Method(builder, "TryCopyTo", new[] { typeof(CoreSpan), integerReference }),
                    AccessTools.Method(typeof(CoreSpan), "TryCopyTo", new[] { typeof(CoreSpan) }),
                    AccessTools.PropertyGetter(typeof(CoreSpan), "Length"),
                    AccessTools.Method(typeof(CoreReadOnlySpan), "TryCopyTo", new[] { typeof(CoreSpan) }),
                    AccessTools.PropertyGetter(byReference, "Value")
                };
                foreach (MethodInfo method in marshal.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "GetReference" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1) {
                        guards.Add(method.MakeGenericMethod(typeof(char)));
                    }
                }
                foreach (MethodInfo method in typeof(Buffer).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    guards.Add(method);
                    if (method.Name == "Memmove" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1) {
                        guards.Add(method.MakeGenericMethod(typeof(char)));
                    }
                }
                foreach (MethodInfo method in unsafeType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (!method.IsGenericMethodDefinition) { continue; }
                    int count = method.GetGenericArguments().Length;
                    if (method.Name == "As" && count == 2) { guards.Add(method.MakeGenericMethod(typeof(char), typeof(byte))); }
                    if ((method.Name == "AsRef" || method.Name == "Add") && count == 1) { guards.Add(method.MakeGenericMethod(typeof(char))); }
                }
                foreach (MethodBase method in guards) { if (method == null) { return null; } }
                Guards = guards.ToArray();
                var remaining = new DynamicMethod("ReadFormatterChunk", typeof(CoreSpan), new[] { typeof(StringBuilder) }, typeof(ApiJsonFormatterBorrowPreparation), true);
                ILGenerator il = remaining.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, GetterTarget); il.Emit(OpCodes.Ret);
                var formatter = new DynamicMethod("FormatBorrowedNumber", typeof(bool), new[] { typeof(object), typeof(CoreSpan), integerReference, typeof(CoreReadOnlySpan), typeof(IFormatProvider) }, typeof(ApiJsonFormatterBorrowPreparation), true);
                ILGenerator format = formatter.GetILGenerator();
                format.Emit(OpCodes.Ldarg_0); format.Emit(OpCodes.Castclass, formattable);
                format.Emit(OpCodes.Ldarg_1); format.Emit(OpCodes.Ldarg_2); format.Emit(OpCodes.Ldarg_3); format.Emit(OpCodes.Ldarg_S, (byte)4);
                format.Emit(OpCodes.Callvirt, FormatterTarget); format.Emit(OpCodes.Ret);
                var length = new DynamicMethod("ReadFormatterLength", typeof(int), new[] { typeof(StringBuilder) }, typeof(ApiJsonFormatterBorrowPreparation), true);
                ILGenerator read = length.GetILGenerator();
                read.Emit(OpCodes.Ldarg_0); read.Emit(OpCodes.Ldfld, chunkLength);
                read.Emit(OpCodes.Ldarg_0); read.Emit(OpCodes.Ldfld, chunkOffset); read.Emit(OpCodes.Add); read.Emit(OpCodes.Ret);
                Formatter = (NumericFormatter)formatter.CreateDelegate(typeof(NumericFormatter));
                Length = (Func<StringBuilder, int>)length.CreateDelegate(typeof(Func<StringBuilder, int>));
                return (RemainingReader)remaining.CreateDelegate(typeof(RemainingReader));
            } catch { return null; }
        }
    }
}
