using ProtoBuf.Meta;
using ScoreSaber.Live.V1;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ScoreSaber.Features.Live.Protocol {
    internal static class OwnedRoomSnapshotPolicy {
        internal static bool CanCaptureModel() {
            try {
                return Metadata.Supported;
            } catch {
                return false;
            }
        }

        internal static bool IsPrivateModel(RuntimeTypeModel model) {
            if (model == null || !CanCaptureModel()) return false;
            try {
                int required = 0;
                foreach (MetaType meta in model.GetTypes()) {
                    Type type = meta.Type;
                    bool protocol = type.Module == typeof(LudusEnvelope).Module
                        && type.Namespace == typeof(LudusEnvelope).Namespace;
                    bool list = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
                    if (!protocol && !list) return false;
                    int flags = Convert.ToInt32(Metadata.Flags.GetValue(meta));
                    if ((flags & 4) == 0 || (!type.IsValueType && (flags & 16) != 0)
                        || Metadata.Factory.GetValue(meta) != null || Metadata.Surrogate.GetValue(meta) != null
                        || meta.BaseType != null || meta.HasSubtypes || meta.SerializerType != null
                        || (meta.ConstructType != null && meta.ConstructType != type)) return false;
                    object callbacks = Metadata.Callbacks.GetValue(meta);
                    if (callbacks != null) {
                        if (callbacks.GetType() != typeof(CallbackSet)) return false;
                        foreach (FieldInfo callback in Metadata.CallbackMethods) {
                            if (callback.GetValue(callbacks) != null) return false;
                        }
                    }
                    ValueMember[] members = meta.GetFields();
                    if ((list || type.IsEnum) && members.Length != 0) return false;
                    foreach (ValueMember member in members) {
                        if (member.GetType() != typeof(ValueMember) || member.BackingMember != null
                            || Metadata.MemberSerializer.GetValue(member) == null
                            || Metadata.GetSpecified.GetValue(member) != null
                            || Metadata.SetSpecified.GetValue(member) != null) return false;
                        PropertyInfo property = member.Member as PropertyInfo;
                        if (property == null || property.GetType() != Metadata.PropertyType
                            || property.DeclaringType != type || property.GetIndexParameters().Length != 0) return false;
                        MethodInfo getter = property.GetGetMethod();
                        MethodInfo setter = property.GetSetMethod();
                        if (getter == null || setter == null || getter.IsStatic || setter.IsStatic
                            || getter.IsVirtual || setter.IsVirtual
                            || !getter.IsDefined(typeof(CompilerGeneratedAttribute), false)
                            || !setter.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
                        object value = member.DefaultValue;
                        if (value != null && !(value is string) && !value.GetType().IsPrimitive
                            && !value.GetType().IsEnum) return false;
                    }
                    if (type == typeof(LudusEnvelope)) required |= 1;
                    else if (type == typeof(RoomSnapshot)) required |= 2;
                    else if (type == typeof(LiveMatchRoomState)) required |= 4;
                    else if (type == typeof(LiveRoomPlayerState)) required |= 8;
                }
                return required == 15;
            } catch {
                return false;
            }
        }

        private static class Metadata {
            internal static readonly bool Supported;
            internal static readonly FieldInfo Flags, Factory, Surrogate, Callbacks;
            internal static readonly FieldInfo MemberSerializer, GetSpecified, SetSpecified;
            internal static readonly FieldInfo[] CallbackMethods;
            internal static readonly Type PropertyType;

            static Metadata() {
                if (typeof(MetaType).Module.ModuleVersionId != new Guid("68b79965-b8f1-48d8-95fc-a6d169e9a789")) return;
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                Flags = typeof(MetaType).GetField("flags", fields);
                Factory = typeof(MetaType).GetField("factory", fields);
                Surrogate = typeof(MetaType).GetField("surrogateType", fields);
                Callbacks = typeof(MetaType).GetField("callbacks", fields);
                MemberSerializer = typeof(ValueMember).GetField("serializer", fields);
                GetSpecified = typeof(ValueMember).GetField("getSpecified", fields);
                SetSpecified = typeof(ValueMember).GetField("setSpecified", fields);
                CallbackMethods = new[] {
                    typeof(CallbackSet).GetField("beforeSerialize", fields),
                    typeof(CallbackSet).GetField("afterSerialize", fields),
                    typeof(CallbackSet).GetField("beforeDeserialize", fields),
                    typeof(CallbackSet).GetField("afterDeserialize", fields)
                };
                PropertyType = typeof(LiveMatchRoomState).GetProperty(nameof(LiveMatchRoomState.PlayerStates)).GetType();
                Supported = Flags != null && Factory != null && Surrogate != null && Callbacks != null
                    && MemberSerializer != null && GetSpecified != null && SetSpecified != null;
                foreach (FieldInfo callback in CallbackMethods) Supported &= callback != null;
            }
        }
    }
}
