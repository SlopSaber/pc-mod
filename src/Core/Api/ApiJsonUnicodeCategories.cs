using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ScoreSaber.Core.Api {
    internal static class ApiJsonUnicodeCategories {
        private static readonly Tables Data = Load();

        internal static bool Supported => Data != null;

        internal static bool IsLetterOrDigit(char value) {
            Tables data = Data;
            if (data == null) {
                return false;
            }
            int category = value <= 255 ? data.Latin1[value] : data.Category(value);
            return category <= 4 || category == 8;
        }

        internal static bool IsWhiteSpace(char value) {
            if (value <= 255) {
                return value == ' ' || (value >= '\t' && value <= '\r')
                    || value == '\u0085' || value == '\u00a0';
            }
            Tables data = Data;
            if (data == null) {
                return false;
            }
            int category = data.Category(value);
            return category >= 11 && category <= 13;
        }

        private static Tables Load() {
            try {
                Module core = typeof(char).Module;
                if (core.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790")) {
                    return null;
                }
                Type details = core.Assembly.GetType("<PrivateImplementationDetails>");
                byte[] level1 = ReadTable(details, 2176,
                    "B55F94CD2F415D0279D7A1AF2265C4D9A90CE47F8C900D5D09AD088796210838");
                byte[] level2 = ReadTable(details, 5952,
                    "9086502742CE7F0595B57A4E5B32901FF4CF97959B92F7E91A435E4765AC1115");
                byte[] level3 = ReadTable(details, 10800,
                    "56073E3CC3FC817690CC306D0DB7EA63EBCB0801359567CA44CA3D3B9BF63854");
                byte[] values = ReadTable(details, 172,
                    "D6691EE5A533DE7E0859066942261B24D0C836D7EE016D2251377BFEE40FEA15");
                FieldInfo latinField = typeof(char).GetField("s_categoryForLatin1",
                    BindingFlags.Static | BindingFlags.NonPublic);
                byte[] latinSource = latinField?.GetValue(null) as byte[];
                if (latinSource == null || latinSource.Length != 256) {
                    return null;
                }
                var latin1 = (byte[])latinSource.Clone();
                if (!Matches(latin1, "801494072CDD59E61F9AA9345A80D045378705DFDCE94902C22EAEAE049BE780")) {
                    return null;
                }
                return new Tables(level1, level2, level3, values, latin1);
            } catch {
                return null;
            }
        }

        private static byte[] ReadTable(Type details, int length, string hash) {
            FieldInfo field = details?.GetField(hash, BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null || !field.IsInitOnly || (field.Attributes & FieldAttributes.HasFieldRVA) == 0) {
                throw new InvalidOperationException();
            }
            var bytes = new byte[length];
            RuntimeHelpers.InitializeArray(bytes, field.FieldHandle);
            if (!Matches(bytes, hash)) {
                throw new InvalidOperationException();
            }
            return bytes;
        }

        private static bool Matches(byte[] bytes, string expected) {
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "") == expected;
            }
        }

        private sealed class Tables {
            private readonly byte[] _level1;
            private readonly byte[] _level2;
            private readonly byte[] _level3;
            private readonly byte[] _values;
            internal readonly byte[] Latin1;

            internal Tables(byte[] level1, byte[] level2, byte[] level3, byte[] values, byte[] latin1) {
                _level1 = level1;
                _level2 = level2;
                _level3 = level3;
                _values = values;
                Latin1 = latin1;
            }

            internal int Category(char value) {
                int at = (_level1[value >> 9] << 6) + ((value >> 3) & 62);
                int word = _level2[at] | (_level2[at + 1] << 8);
                int entry = _level3[(word << 4) + (value & 15)];
                return _values[entry * 2];
            }
        }
    }
}
