using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using ScoreSaber.Features.Replays;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonOwnedOutputPreparation {
        internal static MethodInfo Producer;
        internal static MethodInfo WriteString;
        internal static MethodInfo WriteArray;
        internal static MethodInfo Materialize;
        internal static MethodInfo ExposeBuilder;
        private static ConstructorInfo WriterConstructor;
        private static FieldInfo BuilderField;
        private static FieldInfo OpenField;
        private static FieldInfo MaximumField;
        private static MethodBase[] Guards;
        private static readonly bool Ready = Initialize();
        private static bool Rewritten;
        internal static bool Supported => Ready;
        internal static bool CanUseOriginalBuilder() => IsUnchanged();

        [ThreadStatic]
        private static Scope _scope;

        internal sealed class Scope {
            internal readonly Scope Previous;
            internal StringWriter Writer;
            internal StringBuilder Builder;
            internal bool Retired;

            internal Scope(Scope previous) => Previous = previous;
        }

        internal static Scope Enter() {
            Scope previous = _scope;
            _scope = null;
            try {
                bool admitted = ApiJsonEscapePreparation.IsActive && !Thread.CurrentThread.IsThreadPoolThread && IsUnchanged();
                if (admitted || previous != null) {
                    _scope = new Scope(previous) { Retired = !admitted };
                }
            } catch {
                for (Scope scope = previous; scope != null; scope = scope.Previous) { scope.Retired = true; }
            }
            return previous;
        }

        internal static void Exit(Scope previous) {
            Scope current = _scope;
            if (current != null) { current.Retired = true; }
            _scope = previous;
        }

        private static void Record(StringWriter writer) {
            try {
                Scope scope = _scope;
                if (scope == null || scope.Retired || scope.Writer != null || writer == null
                    || writer.GetType() != typeof(StringWriter) || !IsUnchanged()) { return; }
                var builder = BuilderField.GetValue(writer) as StringBuilder;
                if (builder == null || builder.GetType() != typeof(StringBuilder) || builder.Length != 0) { return; }
                scope.Writer = writer;
                scope.Builder = builder;
            } catch { }
        }

        internal static void Revoke(StringWriter writer) {
            for (Scope scope = _scope; scope != null; scope = scope.Previous) {
                if (ReferenceEquals(scope.Writer, writer)) { scope.Retired = true; }
            }
        }

        private static bool CanUse(StringWriter writer, int count, bool writing) {
            Scope scope = _scope;
            if (!ApiJsonEscapePreparation.IsActive || Thread.CurrentThread.IsThreadPoolThread || !Supported
                || scope == null || scope.Retired || !ReferenceEquals(scope.Writer, writer) || writer == null
                || count < 524288 || count > int.MaxValue / 2 || !IsUnchanged()
                || !ReferenceEquals(BuilderField.GetValue(writer), scope.Builder)) { return false; }
            if (writing) {
                if (!(bool)OpenField.GetValue(writer)) { return false; }
                int maximum = (int)MaximumField.GetValue(scope.Builder);
                if (scope.Builder.Length > maximum - count) { return false; }
            }
            return true;
        }

        internal static bool TryWriteString(StringWriter writer, string text) {
            try { if (text == null || !CanUse(writer, text.Length, true)) { return false; } }
            catch { return false; }
            Task<bool> task = null;
            try { QueueString(writer, text, out task); }
            catch { }
            if (task == null) { return false; }
            Wait(task);
            task.GetAwaiter().GetResult();
            return true;
        }

        internal static bool TryWriteArray(TextWriter writer, char[] buffer, int start, int count, bool privateInput) {
            StringWriter owned = writer as StringWriter;
            try {
                if (!privateInput || owned == null || buffer == null || start < 0 || count > buffer.Length - start
                    || !CanUse(owned, count, true)) { return false; }
            } catch { return false; }
            Task<bool> task = null;
            try { QueueArray(owned, buffer, start, count, out task); }
            catch { }
            if (task == null) { return false; }
            Wait(task);
            task.GetAwaiter().GetResult();
            return true;
        }

        internal static bool TryMaterialize(StringWriter writer, out string result) {
            result = null;
            try {
                Scope scope = _scope;
                if (scope == null || scope.Builder == null || !CanUse(writer, scope.Builder.Length, false)) { return false; }
            } catch { return false; }
            Task<string> task = null;
            try { QueueMaterialize(writer, out task); }
            catch { }
            if (task == null) { return false; }
            Wait(task);
            result = task.GetAwaiter().GetResult();
            return true;
        }

        internal static bool TryWriteIndent(TextWriter writer, char[] pattern, int newlineLength, int indentCount) {
            StringWriter owned = writer as StringWriter;
            try {
                long total = (long)newlineLength + indentCount;
                if (owned == null || pattern == null || newlineLength < 0 || indentCount < 0
                    || pattern.Length > 4096 || pattern.Length != (long)newlineLength + 12
                    || total > int.MaxValue / 2 || !CanUse(owned, (int)total, true)
                    || !ApiJsonOutputIndentPatch.IsUnchanged()) { return false; }
            } catch { return false; }
            Task<bool> task = null;
            try {
                var ownedPattern = new char[pattern.Length];
                Array.Copy(pattern, ownedPattern, ownedPattern.Length);
                QueueIndent(owned, ownedPattern, newlineLength, indentCount, out task);
            } catch { }
            if (task == null) { return false; }
            Wait(task);
            task.GetAwaiter().GetResult();
            return true;
        }

        private static void QueueIndent(StringWriter writer, char[] pattern, int newlineLength, int indentCount,
            out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => WriteIndent(writer, pattern, newlineLength, indentCount), out task);
        }

        private static bool WriteIndent(StringWriter writer, char[] pattern, int newlineLength, int indentCount) {
            writer.Write(pattern, 0, newlineLength + Math.Min(indentCount, 12));
            while ((indentCount -= 12) > 0) {
                writer.Write(pattern, newlineLength, Math.Min(indentCount, 12));
            }
            return true;
        }

        private static void QueueString(StringWriter writer, string text, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => { writer.Write(text); return true; }, out task);
        }

        private static void QueueArray(StringWriter writer, char[] buffer, int start, int count, out Task<bool> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => { writer.Write(buffer, start, count); return true; }, out task);
        }

        private static void QueueMaterialize(StringWriter writer, out Task<string> task) {
            ReplayStorageService.TryQueueOwnedPreparationWhenIdle(() => writer.ToString(), out task);
        }

        private static void Wait(Task task) {
            while (!task.IsCompleted) {
                try { task.Wait(); }
                catch (ThreadInterruptedException) { }
                catch (AggregateException) { }
            }
        }

        private static bool IsUnchanged() {
            if (!Ready || !Rewritten || Guards == null) { return false; }
            HarmonyLib.Patches producer = Harmony.GetPatchInfo(Producer);
            if (producer == null || producer.Prefixes.Count != 1 || producer.Prefixes[0].PatchMethod != ApiJsonOutputProducerPatch.PrefixMethod
                || producer.Postfixes.Count != 0 || producer.Finalizers.Count != 1 || producer.Finalizers[0].PatchMethod != ApiJsonOutputProducerPatch.FinalizerMethod
                || producer.Transpilers.Count != 1 || producer.Transpilers[0].PatchMethod != ApiJsonOutputProducerPatch.TranspilerMethod
                || !OnlyPrefix(WriteString, ApiJsonOutputStringPatch.PrefixMethod)
                || !OnlyPrefix(Materialize, ApiJsonOutputMaterializePatch.PrefixMethod)
                || !OnlyPrefix(ExposeBuilder, ApiJsonOutputBuilderEscapePatch.PrefixMethod)) { return false; }
            foreach (MethodBase method in Guards) {
                if (method == ApiJsonSelectedStringAllocationPreparation.Target) {
                    if (!ApiJsonSelectedStringAllocationPreparation.IsAccepted()) { return false; }
                } else if (Harmony.GetPatchInfo(method) != null) { return false; }
            }
            return true;
        }

        private static bool OnlyPrefix(MethodBase method, MethodInfo prefix) {
            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == prefix
                && info.Postfixes.Count == 0 && info.Finalizers.Count == 0 && info.Transpilers.Count == 0;
        }

        private static bool Initialize() {
            try {
                if (typeof(string).Module.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")
                    || typeof(JsonConvert).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) { return false; }
                var methods = new Dictionary<string, MethodBase>();
                methods["Newtonsoft.Json.JsonConvert::SerializeObjectInternal(System.Object,System.Type,Newtonsoft.Json.JsonSerializer)"] = Find("Newtonsoft.Json.JsonConvert", "SerializeObjectInternal", 68, "1a514dcd985040ffa386ab9cd752eb163fb4fd7f0e77c16d81feff2ca1aa5f20", "System.Object", "System.Type", "Newtonsoft.Json.JsonSerializer");
                methods["System.IO.StringWriter::Write(System.Char[],System.Int32,System.Int32)"] = Find("System.IO.StringWriter", "Write", 127, "e21f4a2eb906d485d65b29948f39ee7334a5dd8503581251efbc8611261759e3", "System.Char[]", "System.Int32", "System.Int32");
                methods["System.IO.StringWriter::Write(System.String)"] = Find("System.IO.StringWriter", "Write", 30, "f2e9bf7743475caa4e2ff722ef70039ddf03e3e95840b5fbdfbf25ef17d1dc4f", "System.String");
                methods["Newtonsoft.Json.JsonTextWriter::.ctor(System.IO.TextWriter)"] = Find("Newtonsoft.Json.JsonTextWriter", ".ctor", 91, "e102f70ebbf3bb77f749d764d2555b7f27ccd2652b73681660a81fb82b1507f3", "System.IO.TextWriter");
                methods["System.IO.StringWriter::.ctor(System.Text.StringBuilder,System.IFormatProvider)"] = Find("System.IO.StringWriter", ".ctor", 46, "4ee1cf37fcd5fb22cca3f3565d2736d657be6a36e75aa6a5c1f65589b9306413", "System.Text.StringBuilder", "System.IFormatProvider");
                methods["System.IO.StringWriter::GetStringBuilder()"] = Find("System.IO.StringWriter", "GetStringBuilder", 7, "d9c8c9801219ca4b6c9cbe27015ca8792f6f6989fdab0060b86f834172fd6d86");
                methods["System.IO.StringWriter::ToString()"] = Find("System.IO.StringWriter", "ToString", 12, "3fb06d96248885caa9f1785e2f89067d5625422823d3e7c7c0ae1ad40110f8d2");
                methods["System.IO.TextWriter::.ctor(System.IFormatProvider)"] = Find("System.IO.TextWriter", ".ctor", 36, "c762b19cafefac1036e2fc727567ebea654ac2a42b79cc416786b94243c78a54", "System.IFormatProvider");
                methods["Newtonsoft.Json.JsonTextWriter::UpdateCharEscapeFlags()"] = Find("Newtonsoft.Json.JsonTextWriter", "UpdateCharEscapeFlags", 24, "f5350169d857fb681604a1f72cb05acf8f7c73201f0d726d7ff9eaa863e1c21f");
                methods["System.Text.StringBuilder::.ctor(System.Int32)"] = Find("System.Text.StringBuilder", ".ctor", 13, "2be434af676b7eeab0d8cb97fb86cb287cac69d146891a6981ef45f870b8afa0", "System.Int32");
                methods["System.Text.StringBuilder::ToString()"] = Find("System.Text.StringBuilder", "ToString", 163, "d296a535d7bd32e890da35d49e795a8e64fb490cb04710a8890be0851ac567ff");
                methods["System.Text.StringBuilder::Append(System.Char[],System.Int32,System.Int32)"] = Find("System.Text.StringBuilder", "Append", 113, "edb9ffcd38c943df2af7bbf065384354ca67539bf8f3756cc848920336c4be3c", "System.Char[]", "System.Int32", "System.Int32");
                methods["System.Text.StringBuilder::Append(System.String)"] = Find("System.Text.StringBuilder", "Append", 140, "7a6af259f057c46f6df1b7c03159f9b1085a7f515a0dbf718382196bad5caf86", "System.String");
                methods["System.Text.StringBuilder::.ctor(System.Int32,System.Int32)"] = Find("System.Text.StringBuilder", ".ctor", 109, "76ec916465f69895c5a6c12518cf0bbfa53047cee1cbfe12eb4f720fb605e6ed", "System.Int32", "System.Int32");
                methods["System.Text.StringBuilder::AppendHelper(System.String)"] = Find("System.Text.StringBuilder", "AppendHelper", 33, "e6fdef9db4775e53605e9254e400fbef977b4c54ab99dc2521ce3aefcffa4f8a", "System.String");
                methods["System.Text.StringBuilder::Append(System.Char*,System.Int32)"] = Find("System.Text.StringBuilder", "Append", 198, "422278f7352debcae9e8e54949c40d715ae2d2a2bf430f0851743b85c54a8820", "System.Char*", "System.Int32");
                methods["System.Text.StringBuilder::ExpandByABlock(System.Int32)"] = Find("System.Text.StringBuilder", "ExpandByABlock", 141, "047a599c7349b2b3593c3eb273f23d88b929e5046b7c3e07f00663051ced187a", "System.Int32");
                methods["System.Text.StringBuilder::get_Length()"] = Find("System.Text.StringBuilder", "get_Length", 14, "6ada7d0f55e3b8ba0ee4c69e5986e388a046e4de7979c68599f354853f201ad8");
                methods["System.Text.StringBuilder::ThreadSafeCopy(System.Char*,System.Char[],System.Int32,System.Int32)"] = Find("System.Text.StringBuilder", "ThreadSafeCopy", 56, "188a7e46ada4381c51e4792b689bf4356e5d64aadb88ad267211e83b11f4b0d7", "System.Char*", "System.Char[]", "System.Int32", "System.Int32");
                methods["System.Text.StringBuilder::.ctor(System.Text.StringBuilder)"] = Find("System.Text.StringBuilder", ".ctor", 67, "890f49dd21111c4d012c8a5137707ea7eb954fe8b6e7a569bc1ba82ed06e49ed", "System.Text.StringBuilder");
                foreach (MethodBase method in methods.Values) { if (method == null) { return false; } }
                Producer = (MethodInfo)methods["Newtonsoft.Json.JsonConvert::SerializeObjectInternal(System.Object,System.Type,Newtonsoft.Json.JsonSerializer)"];
                WriteString = (MethodInfo)methods["System.IO.StringWriter::Write(System.String)"];
                WriteArray = (MethodInfo)methods["System.IO.StringWriter::Write(System.Char[],System.Int32,System.Int32)"];
                Materialize = (MethodInfo)methods["System.IO.StringWriter::ToString()"];
                ExposeBuilder = (MethodInfo)methods["System.IO.StringWriter::GetStringBuilder()"];
                WriterConstructor = (ConstructorInfo)methods["System.IO.StringWriter::.ctor(System.Text.StringBuilder,System.IFormatProvider)"];
                BuilderField = AccessTools.Field(typeof(StringWriter), "_sb");
                OpenField = AccessTools.Field(typeof(StringWriter), "_isOpen");
                MaximumField = AccessTools.Field(typeof(StringBuilder), "m_MaxCapacity");
                if (BuilderField?.FieldType != typeof(StringBuilder) || OpenField?.FieldType != typeof(bool)
                    || MaximumField?.FieldType != typeof(int)) { return false; }
                var guards = new List<MethodBase>();
                foreach (MethodBase method in methods.Values) {
                    if (method != Producer && method != WriteString && method != Materialize && method != ExposeBuilder) { guards.Add(method); }
                }
                AddCopyGuards(guards);
                guards.Add(AccessTools.Constructor(typeof(JsonWriter), Type.EmptyTypes));
                guards.Add(AccessTools.Constructor(typeof(object), Type.EmptyTypes));
                guards.Add(AccessTools.Constructor(typeof(MarshalByRefObject), Type.EmptyTypes));
                guards.Add(AccessTools.PropertyGetter(typeof(JsonWriter), "StringEscapeHandling"));
                guards.Add(AccessTools.Method(typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Utilities.JavaScriptUtils"),
                    "GetCharEscapeFlags", new[] { typeof(StringEscapeHandling), typeof(char) }));
                foreach (Type owner in new[] { typeof(ApiJsonOwnedOutputPreparation), typeof(ApiJsonWriterCopyPreparation),
                    typeof(ApiJsonOutputProducerPatch), typeof(ApiJsonOutputStringPatch), typeof(ApiJsonOutputMaterializePatch), typeof(ApiJsonOutputBuilderEscapePatch), typeof(ApiJsonOutputIndentPatch),
                    typeof(ApiJsonSelectedStringAllocationPreparation), typeof(ApiJsonSelectedStringAllocationPatch) }) {
                    guards.AddRange(owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                    foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                        guards.AddRange(nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                        guards.AddRange(nested.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    }
                }
                guards.Add(AccessTools.Method(typeof(ApiJsonEscapePreparation), "Serialize"));
                guards.Add(AccessTools.PropertyGetter(typeof(ApiJsonEscapePreparation), "IsActive"));
                foreach (MethodInfo method in typeof(ReplayStorageService).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
                    if (method.Name == "TryQueueOwnedPreparationWhenIdle" && method.IsGenericMethodDefinition) {
                        guards.Add(method); guards.Add(method.MakeGenericMethod(typeof(bool))); guards.Add(method.MakeGenericMethod(typeof(string)));
                    }
                }
                foreach (MethodBase method in guards) { if (method == null) { return false; } }
                Guards = guards.ToArray();
                return true;
            } catch { return false; }
        }

        internal static void AddCopyGuards(List<MethodBase> guards) {
            Type chars = typeof(char).MakePointerType();
            Type bytes = typeof(byte).MakePointerType();
            guards.Add(AccessTools.Method(typeof(Math), "Min", new[] { typeof(int), typeof(int) }));
            guards.Add(AccessTools.Method(typeof(Math), "Max", new[] { typeof(int), typeof(int) }));
            guards.Add(AccessTools.Method(typeof(Array), "Copy", new[] { typeof(Array), typeof(Array), typeof(int) }));
            guards.Add(AccessTools.Method(typeof(string), "wstrcpy", new[] { chars, chars, typeof(int) }));
            guards.Add(AccessTools.Method(typeof(string), "FastAllocateString", new[] { typeof(int) }));
            guards.Add(AccessTools.PropertyGetter(typeof(string), "Length"));
            guards.Add(AccessTools.PropertyGetter(typeof(string), "Chars"));
            guards.Add(AccessTools.PropertyGetter(typeof(System.Runtime.CompilerServices.RuntimeHelpers), "OffsetToStringData"));
            guards.Add(AccessTools.Method(typeof(Buffer), "Memmove", new[] { bytes, bytes, typeof(uint) }));
            guards.Add(AccessTools.Method(typeof(Buffer), "Memcpy", new[] { bytes, bytes, typeof(int) }));
            guards.Add(AccessTools.Method(typeof(Buffer), "InternalMemcpy", new[] { bytes, bytes, typeof(int) }));
            guards.Add(AccessTools.Method(typeof(string).Assembly.GetType("System.Runtime.RuntimeImports"), "Memmove", new[] { bytes, bytes, typeof(uint) }));
            guards.Add(AccessTools.Method(typeof(Interlocked), "Read", new[] { typeof(long).MakeByRefType() }));
            foreach (string name in new[] { "memcpy1", "memcpy2", "memcpy4" }) { guards.Add(AccessTools.Method(typeof(Buffer), name, new[] { bytes, bytes, typeof(int) })); }
        }

        private static Type ResolveType(string name) {
            if (name.EndsWith("[]", StringComparison.Ordinal)) { return ResolveType(name.Substring(0, name.Length - 2)).MakeArrayType(); }
            if (name.EndsWith("*", StringComparison.Ordinal)) { return ResolveType(name.Substring(0, name.Length - 1)).MakePointerType(); }
            return typeof(string).Assembly.GetType(name) ?? typeof(JsonConvert).Assembly.GetType(name);
        }

        private static MethodBase Find(string owner, string name, int size, string expected, params string[] parameters) {
            var types = new Type[parameters.Length];
            for (int at = 0; at < types.Length; at++) { types[at] = ResolveType(parameters[at]); }
            MethodBase method = name == ".ctor" ? (MethodBase)AccessTools.Constructor(ResolveType(owner), types) : AccessTools.Method(ResolveType(owner), name, types);
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != size) { return null; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected ? method : null;
            }
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Supported || code.Count != 26 || code[3].opcode != OpCodes.Newobj || !Equals(code[3].operand, WriterConstructor)
                || code[4].opcode != OpCodes.Stloc_0) { return code; }
            code.InsertRange(4, new[] { new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonOwnedOutputPreparation), "Record")) });
            Rewritten = true;
            return code;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonOutputProducerPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonOutputProducerPatch), "Prefix");
        internal static readonly MethodInfo FinalizerMethod = AccessTools.Method(typeof(ApiJsonOutputProducerPatch), "Finalizer");
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonOutputProducerPatch), "Transpiler");
        private static bool Prepare() => ApiJsonOwnedOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonOwnedOutputPreparation.Producer;
        private static void Prefix(out ApiJsonOwnedOutputPreparation.Scope __state) => __state = ApiJsonOwnedOutputPreparation.Enter();
        private static Exception Finalizer(Exception __exception, ApiJsonOwnedOutputPreparation.Scope __state) {
            ApiJsonOwnedOutputPreparation.Exit(__state); return __exception;
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ApiJsonOwnedOutputPreparation.Rewrite(instructions);
    }

    [HarmonyPatch]
    internal static class ApiJsonOutputStringPatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonOutputStringPatch), "Prefix");
        private static bool Prepare() => ApiJsonOwnedOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonOwnedOutputPreparation.WriteString;
        private static bool Prefix(StringWriter __instance, string __0) => !ApiJsonOwnedOutputPreparation.TryWriteString(__instance, __0);
    }

    [HarmonyPatch]
    internal static class ApiJsonOutputMaterializePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonOutputMaterializePatch), "Prefix");
        private static bool Prepare() => ApiJsonOwnedOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonOwnedOutputPreparation.Materialize;
        private static bool Prefix(StringWriter __instance, ref string __result) {
            if (!ApiJsonOwnedOutputPreparation.TryMaterialize(__instance, out string result)) { return true; }
            __result = result; return false;
        }
    }

    [HarmonyPatch]
    internal static class ApiJsonOutputBuilderEscapePatch {
        internal static readonly MethodInfo PrefixMethod = AccessTools.Method(typeof(ApiJsonOutputBuilderEscapePatch), "Prefix");
        private static bool Prepare() => ApiJsonOwnedOutputPreparation.Supported;
        private static MethodBase TargetMethod() => ApiJsonOwnedOutputPreparation.ExposeBuilder;
        private static void Prefix(StringWriter __instance) => ApiJsonOwnedOutputPreparation.Revoke(__instance);
    }

    [HarmonyPatch]
    internal static class ApiJsonOutputIndentPatch {
        private static MethodInfo Target;
        private static MethodInfo SetPattern;
        private static MethodInfo Top;
        private static FieldInfo Writer;
        private static FieldInfo Pattern;
        internal static readonly MethodInfo TranspilerMethod = AccessTools.Method(typeof(ApiJsonOutputIndentPatch), "Transpiler");
        private static readonly bool Ready = Initialize();
        private static bool Rewritten;

        private static bool Prepare() => Ready && ApiJsonOwnedOutputPreparation.Supported;
        private static MethodBase TargetMethod() => Target;

        internal static bool IsUnchanged() {
            if (!Ready || !Rewritten) { return false; }
            HarmonyLib.Patches info = Harmony.GetPatchInfo(Target);
            return info != null && info.Prefixes.Count == 0 && info.Postfixes.Count == 0 && info.Finalizers.Count == 0
                && info.Transpilers.Count == 1 && info.Transpilers[0].PatchMethod == TranspilerMethod
                && Harmony.GetPatchInfo(SetPattern) == null && Harmony.GetPatchInfo(Top) == null;
        }

        private static bool Initialize() {
            try {
                if (typeof(JsonConvert).Module.ModuleVersionId != new Guid("8b49fe53-8c3d-48f9-b341-caeedeff32a2")) { return false; }
                Target = AccessTools.Method(typeof(JsonTextWriter), "WriteIndent", Type.EmptyTypes);
                SetPattern = AccessTools.Method(typeof(JsonTextWriter), "SetIndentChars", Type.EmptyTypes);
                Top = AccessTools.PropertyGetter(typeof(JsonWriter), "Top");
                Writer = AccessTools.Field(typeof(JsonTextWriter), "_writer");
                Pattern = AccessTools.Field(typeof(JsonTextWriter), "_indentChars");
                return Matches(Target, 87, "950d1a974a91a320762977712b50ddfddce8b746852291efcac18302a08febce")
                    && Matches(SetPattern, 116, "455a5c7be0d4f6dfceb358f4b71e2937358b19bd13ffbeab6c620738422cf8e8")
                    && Matches(Top, 33, "491f88079a0c57adc9d80dc47b9e397c83d423662e4eae0692c5c64eb78bfcab")
                    && Writer?.FieldType == typeof(TextWriter) && Pattern?.FieldType == typeof(char[]);
            } catch { return false; }
        }

        private static bool Matches(MethodInfo method, int length, string expected) {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null || bytes.Length != length) { return false; }
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == expected;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var code = new List<CodeInstruction>(instructions);
            Rewritten = false;
            if (!Ready || code.Count != 38 || code[7].opcode != OpCodes.Call || !Equals(code[7].operand, SetPattern)
                || code[8].opcode != OpCodes.Stloc_1 || code[9].opcode != OpCodes.Ldarg_0) { return code; }
            foreach (CodeInstruction instruction in code) { if (instruction.blocks.Count != 0) { return code; } }
            Label fallback = generator.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[9].labels);
            code[9].labels.Clear();
            code[9].labels.Add(fallback);
            code.InsertRange(9, new[] {
                first, new CodeInstruction(OpCodes.Ldfld, Writer), new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Pattern), new CodeInstruction(OpCodes.Ldloc_1),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ApiJsonOwnedOutputPreparation), "TryWriteIndent")),
                new CodeInstruction(OpCodes.Brfalse, fallback), new CodeInstruction(OpCodes.Ret)
            });
            Rewritten = true;
            return code;
        }
    }
}
