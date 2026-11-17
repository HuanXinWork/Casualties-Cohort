using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Together {

    public static class MyLiteNetLibExtensions {

        public static void ResetKeepMsgId(this NetDataWriter w2) {
            w2.SetPosition(sizeof(NetmsgId));
        }

        public static void CompressWriter(this NetDataWriter w2, int start_to_ignore = sizeof(NetmsgId)) {
            if (FixCompressWriter.Apply(w2, start_to_ignore)) {
                return;
            }

            byte[] og = CopyData(w2, start_to_ignore);
            byte[] compressed = Util.Compress(og);
            w2.SetPosition(start_to_ignore); // dont compress the first two bytes cuz its the msg id
            w2.PutBytesWithLength(compressed);
        }

        public static NetDataReader DecompressReader(this NetDataReader r2) {
            // 优先在 r2.RawData 切片上直接原位 Gzip 解压并推进游标，省去 GetBytesWithLength() 中间数组分配；失败时复原游标回退
            if (r2 != null && r2.RawData != null && r2.AvailableBytes >= sizeof(ushort)) {
                int savedPos = r2.Position;
                ushort len = r2.GetUShort();
                if (len <= r2.AvailableBytes && FixCompressWriter.TryDecompressSlice(r2.RawData, r2.Position, len, out byte[] pooled)) {
                    r2.SkipBytes(len);
                    return new NetDataReader(pooled);
                }
                r2.SetPosition(savedPos);
            }
            byte[] og = Util.Decompress(r2.GetBytesWithLength());
            return new NetDataReader(og);
        }

        public static byte[] CopyData(this NetDataWriter w2, int start_index) {
            return CopyData(w2, start_index, w2.Length - start_index);
        }
        public static byte[] CopyData(this NetDataWriter w2, int start_index, int length) {
            byte[] resultData = new byte[length];
            Buffer.BlockCopy(w2.Data, start_index, resultData, 0, length);
            return resultData;
        }

        public static void Put<T>(this NetDataWriter writer, T value) where T : unmanaged, INetSerializeByMemcpy {
            writer.PutUnmanaged(value);
        }
        public static void Get<T>(this NetDataReader reader, out T result) where T : unmanaged, INetSerializeByMemcpy {
            result = reader.GetUnmanaged<T>();
        }
        public static void Get(this NetDataReader reader, out byte[] result) {
            result = reader.GetBytesWithLength();
        }

        public static void Put(this NetDataWriter writer, float[] value) {
            writer.PutArray(value);
        }
        public static void Get(this NetDataReader reader, out float[] result) {
            result = reader.GetFloatArray();
        }

        public static void Put(this NetDataWriter writer, Vector2[] value) {
            writer.Put(value.Length);
            for (int i = 0; i < value.Length; i++) {
                writer.Put(value[i].x);
                writer.Put(value[i].y);
            }
        }
        public static void Get(this NetDataReader reader, out Vector2[] result) {
            int length = reader.GetInt();
            result = new Vector2[length];

            for (int i = 0; i < length; i++) {
                float x = reader.GetFloat();
                float y = reader.GetFloat();
                result[i] = new Vector2(x, y);
            }
        }

        public static void Put(this NetDataWriter writer, Vector2 value) {
            writer.Put(value.x);
            writer.Put(value.y);
        }
        public static void Get(this NetDataReader reader, out Vector2 result) {
            result = new Vector2(reader.GetFloat(), reader.GetFloat());
        }
        public static Vector2 GetVector2(this NetDataReader reader) {
            Vector2 v;
            v.x = reader.GetFloat();
            v.y = reader.GetFloat();
            return v;
        }

        public static void Put(this NetDataWriter writer, Vector2Int value) {
            writer.Put(value.x);
            writer.Put(value.y);
        }
        public static void Get(this NetDataReader reader, out Vector2Int result) {
            result = new Vector2Int(reader.GetInt(), reader.GetInt());
        }

        public static void Put(this NetDataWriter writer, Color value) {
            writer.Put(value.r);
            writer.Put(value.g);
            writer.Put(value.b);
            writer.Put(value.a);
        }
        public static void Get(this NetDataReader reader, out Color result) {
            result = new Color(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        public static void Put(this NetDataWriter writer, knetid value) {
            writer.Put(value.id);
        }
        public static void Get(this NetDataReader reader, out knetid result) {
            result = reader.GetUShort();
        }

        public static void Put(this NetDataWriter writer, string value, bool oneByteChars) {
            if (oneByteChars) {
                writer.PutBytesWithLength(value.ToByteArray());
            } else {
                writer.Put(value);
            }
        }
        public static void Get(this NetDataReader reader, out string result, bool oneByteChars) {
            if (oneByteChars) {
                result = reader.GetBytesWithLength().ToStringFromCharBytes();
            } else {
                result = reader.GetString();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GetUnmanaged<T>(this NetDataReader reader, out T result) where T : unmanaged {
            result = reader.GetUnmanaged<T>();
        }

        public static void Put(this NetDataWriter writer, Dictionary<string, string> value) {
            writer.Put((ushort)value.Count);
            writer.PutArray(value.Keys.ToArray());
            writer.PutArray(value.Values.ToArray());
        }
        public static void Get(this NetDataReader reader, out Dictionary<string, string> result) {
            reader.Get(out ushort Count);
            string[] Keys = reader.GetStringArray();
            string[] Values = reader.GetStringArray();
            result = new();
            for (int i = 0; i < Count; i++) {
                result[Keys[i]] = Values[i];
            }
        }

        #region CARELESS_SERIALIZERS

        public static void PutAny(this NetDataWriter writer, object value) {
            Type type = value.GetType();

            if (type == typeof(bool)) {
                writer.Put((bool)value);
            } else if (type == typeof(byte)) {
                writer.Put((byte)value);
            } else if (type == typeof(sbyte)) {
                writer.Put((sbyte)value);
            } else if (type == typeof(short)) {
                writer.Put((short)value);
            } else if (type == typeof(ushort)) {
                writer.Put((ushort)value);
            } else if (type == typeof(int)) {
                writer.Put((int)value);
            } else if (type == typeof(uint)) {
                writer.Put((uint)value);
            } else if (type == typeof(long)) {
                writer.Put((long)value);
            } else if (type == typeof(ulong)) {
                writer.Put((ulong)value);
            } else if (type == typeof(float)) {
                writer.Put((float)value);
            } else if (type == typeof(double)) {
                writer.Put((double)value);
            } else if (type == typeof(char)) {
                writer.Put((char)value);
            } else if (type == typeof(string)) {
                writer.Put((string)value);
            } else if (type == typeof(byte[])) {
                writer.Put((byte[])value);
            } else if (type == typeof(Guid)) {
                writer.Put((Guid)value);
            } else {
                throw new NotSupportedException($"Type {type} is not supported.");
            }
        }

        public static bool GetAny<T>(this NetDataReader reader, out T value) {
            Type type = typeof(T);
            bool b = GetAny(reader, out object result, type);
            value = (T)result;
            return b;
        }

        public static bool GetAny(this NetDataReader reader, out object value, Type type) {
            object result;

            try {
                if (type == typeof(bool)) {
                    result = reader.GetBool();
                } else if (type == typeof(byte)) {
                    result = reader.GetByte();
                } else if (type == typeof(sbyte)) {
                    result = reader.GetSByte();
                } else if (type == typeof(short)) {
                    result = reader.GetShort();
                } else if (type == typeof(ushort)) {
                    result = reader.GetUShort();
                } else if (type == typeof(int)) {
                    result = reader.GetInt();
                } else if (type == typeof(uint)) {
                    result = reader.GetUInt();
                } else if (type == typeof(long)) {
                    result = reader.GetLong();
                } else if (type == typeof(ulong)) {
                    result = reader.GetULong();
                } else if (type == typeof(float)) {
                    result = reader.GetFloat();
                } else if (type == typeof(double)) {
                    result = reader.GetDouble();
                } else if (type == typeof(char)) {
                    result = reader.GetChar();
                } else if (type == typeof(string)) {
                    result = reader.GetString();
                } else if (type == typeof(byte[])) {
                    result = reader.GetBytesWithLength();
                } else if (type == typeof(Guid)) {
                    result = reader.GetGuid();
                } else {
                    throw new NotSupportedException($"Type {type} is not supported.");
                }

                value = result;
                return true;
            } catch (Exception) {
                // 网络读取器剩余字节不足或载荷截断时按 Try 模式安全返回 false
                value = default!;
                return false;
            }
        }

        public enum NetValueType : byte {
            Null = 0,
            Bool,
            Byte,
            SByte,
            Short,
            UShort,
            Int,
            UInt,
            Long,
            ULong,
            Float,
            Double,
            Char,
            String,
            Guid,
            ByteArray
        }

        public static void PutObject(this NetDataWriter writer, object value) {
            if (value == null) {
                writer.Put((byte)NetValueType.Null);
                return;
            }

            switch (value) {
                case bool v:
                    writer.Put((byte)NetValueType.Bool);
                    writer.Put(v);
                    break;
                case byte v:
                    writer.Put((byte)NetValueType.Byte);
                    writer.Put(v);
                    break;
                case sbyte v:
                    writer.Put((byte)NetValueType.SByte);
                    writer.Put(v);
                    break;
                case short v:
                    writer.Put((byte)NetValueType.Short);
                    writer.Put(v);
                    break;
                case ushort v:
                    writer.Put((byte)NetValueType.UShort);
                    writer.Put(v);
                    break;
                case int v:
                    writer.Put((byte)NetValueType.Int);
                    writer.Put(v);
                    break;
                case uint v:
                    writer.Put((byte)NetValueType.UInt);
                    writer.Put(v);
                    break;
                case long v:
                    writer.Put((byte)NetValueType.Long);
                    writer.Put(v);
                    break;
                case ulong v:
                    writer.Put((byte)NetValueType.ULong);
                    writer.Put(v);
                    break;
                case float v:
                    writer.Put((byte)NetValueType.Float);
                    writer.Put(v);
                    break;
                case double v:
                    writer.Put((byte)NetValueType.Double);
                    writer.Put(v);
                    break;
                case char v:
                    writer.Put((byte)NetValueType.Char);
                    writer.Put(v);
                    break;
                case string v:
                    writer.Put((byte)NetValueType.String);
                    writer.Put(v);
                    break;
                case Guid v:
                    writer.Put((byte)NetValueType.Guid);
                    writer.Put(v);
                    break;
                case byte[] v:
                    writer.Put((byte)NetValueType.ByteArray);
                    writer.PutBytesWithLength(v);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported object type: {value.GetType().FullName}");
            }
        }

        public static object GetObject(this NetDataReader reader) {
            NetValueType type = (NetValueType)reader.GetByte();

            return type switch {
                NetValueType.Null => null,
                NetValueType.Bool => reader.GetBool(),
                NetValueType.Byte => reader.GetByte(),
                NetValueType.SByte => reader.GetSByte(),
                NetValueType.Short => reader.GetShort(),
                NetValueType.UShort => reader.GetUShort(),
                NetValueType.Int => reader.GetInt(),
                NetValueType.UInt => reader.GetUInt(),
                NetValueType.Long => reader.GetLong(),
                NetValueType.ULong => reader.GetULong(),
                NetValueType.Float => reader.GetFloat(),
                NetValueType.Double => reader.GetDouble(),
                NetValueType.Char => reader.GetChar(),
                NetValueType.String => reader.GetString(),
                NetValueType.Guid => reader.GetGuid(),
                NetValueType.ByteArray => reader.GetBytesWithLength(),
                _ => throw new InvalidOperationException($"Unknown NetValueType: {type}")
            };
        }

        #endregion
    }
}
