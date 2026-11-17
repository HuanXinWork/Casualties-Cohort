using CasualtiesTogetherUtils;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Together;

public struct SByteScalarVec2 : INetSerializeByMemcpy, IEquatable<SByteScalarVec2> {
    public sbyte x;
    public sbyte y;

    public SByteScalarVec2(sbyte x, sbyte y) {
        this.x = x; this.y = y;
    }

    public static implicit operator Vector2(SByteScalarVec2 compressed) {
        return new Vector2(
            ((float)compressed.x) / ((float)sbyte.MaxValue),
            ((float)compressed.y) / ((float)sbyte.MaxValue)
        );
    }

    public static explicit operator SByteScalarVec2(Vector2 vec) {
        var cx = (sbyte)Math.Round(KM.Clamp11(vec.x) * sbyte.MaxValue);
        var cy = (sbyte)Math.Round(KM.Clamp11(vec.y) * sbyte.MaxValue);
        return new SByteScalarVec2 { x = cx, y = cy };
    }

    public static bool operator ==(SByteScalarVec2 a, SByteScalarVec2 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(SByteScalarVec2 a, SByteScalarVec2 b) {
        return a.x != b.x || a.y != b.y;
    }
    public bool Equals(SByteScalarVec2 other) => x == other.x && y == other.y;
    public override bool Equals(object other) {
        return other is SByteScalarVec2 v && x == v.x && y == v.y;
    }
    public override int GetHashCode() {
        return (((byte)x << 8) | (byte)y);
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }
}


public struct ShortScalarVec2 : INetSerializeByMemcpy, IEquatable<ShortScalarVec2> {
    public short x;
    public short y;

    public ShortScalarVec2(short x, short y) {
        this.x = x; this.y = y;
    }

    public static implicit operator Vector2(ShortScalarVec2 compressed) {
        return new Vector2(
            ((float)compressed.x) / ((float)short.MaxValue),
            ((float)compressed.y) / ((float)short.MaxValue)
        );
    }

    public static explicit operator ShortScalarVec2(Vector2 vec) {
        var cx = (short)Math.Round(KM.Clamp11(vec.x) * short.MaxValue);
        var cy = (short)Math.Round(KM.Clamp11(vec.y) * short.MaxValue);
        return new ShortScalarVec2 { x = cx, y = cy };
    }

    public static bool operator ==(ShortScalarVec2 a, ShortScalarVec2 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(ShortScalarVec2 a, ShortScalarVec2 b) {
        return a.x != b.x || a.y != b.y;
    }
    public bool Equals(ShortScalarVec2 other) => x == other.x && y == other.y;
    public override bool Equals(object other) {
        return other is ShortScalarVec2 v && x == v.x && y == v.y;
    }
    public override int GetHashCode() {
        return (((ushort)x << 16) | (ushort)y);
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }
}


public struct Vector2_4byte_512 : INetSerializeByMemcpy, IEquatable<Vector2_4byte_512> {

    public short x;
    public short y;


    const float MAPSIZE = 512f;
    const float SCALE = (float)short.MaxValue / MAPSIZE;
    const float INVSCALE = 1f / SCALE;
    public const float PRECISION = 1f / SCALE;
    public const float PRECISION3 = 3f * PRECISION;
    public const float PRECISION3_sqr = PRECISION3 * PRECISION3;


    public Vector2_4byte_512(short x, short y) {
        this.x = x; this.y = y;
    }
    public Vector2_4byte_512(int x, int y) {
        this.x = (short)(((float)x) * SCALE);
        this.y = (short)(((float)y) * SCALE);
    }
    public Vector2_4byte_512(float x, float y) {
        this.x = (short)(((float)x) * SCALE);
        this.y = (short)(((float)y) * SCALE);
    }
    public Vector2_4byte_512() { }
    public static Vector2_4byte_512 Unclamped(Vector2 vec) {
        return new Vector2_4byte_512(
            (short)((vec.x) * SCALE),
            (short)((vec.y) * SCALE)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2_4byte_512 Lerp(Vector2_4byte_512 a, Vector2_4byte_512 b, float t) {
        t = Mathf.Clamp01(t);
        return new Vector2_4byte_512((short)(a.x + (b.x - a.x) * t), (short)(a.y + (b.y - a.y) * t));
    }

    public static implicit operator Vector3(Vector2_4byte_512 compressed) {
        return new Vector3(
            ((float)compressed.x) * INVSCALE,
            ((float)compressed.y) * INVSCALE
        );
    }

    public static implicit operator Vector2(Vector2_4byte_512 compressed) {
        return new Vector2(
            ((float)compressed.x) * INVSCALE,
            ((float)compressed.y) * INVSCALE
        );
    }

    public static implicit operator Vector2_4byte_512(Vector2 vec) {
        var cx = (short)Math.Round(Mathf.Clamp(vec.x, -MAPSIZE, MAPSIZE) * SCALE);
        var cy = (short)Math.Round(Mathf.Clamp(vec.y, -MAPSIZE, MAPSIZE) * SCALE);
        return new Vector2_4byte_512 { x = cx, y = cy };
    }
    public static implicit operator Vector2_4byte_512(Vector3 vec) {
        var cx = (short)Math.Round(Mathf.Clamp(vec.x, -MAPSIZE, MAPSIZE) * SCALE);
        var cy = (short)Math.Round(Mathf.Clamp(vec.y, -MAPSIZE, MAPSIZE) * SCALE);
        return new Vector2_4byte_512 { x = cx, y = cy };
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }


    public static bool operator ==(Vector2_4byte_512 a, Vector2_4byte_512 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(Vector2_4byte_512 a, Vector2_4byte_512 b) {
        return a.x != b.x || a.y != b.y;
    }

    public bool Equals(Vector2_4byte_512 other) => x == other.x && y == other.y;

    public override bool Equals(object other) {
        if (!(other is Vector2_4byte_512)) {
            return false;
        }
        return this == (Vector2_4byte_512)other;
    }
    public override int GetHashCode() {
        return (((ushort)x << 16) | (ushort)y);
    }

}


public struct Vector2_4byte_200 : INetSerializeByMemcpy, IEquatable<Vector2_4byte_200> {

    public short x;
    public short y;


    const float MAPSIZE = 200f;
    const float SCALE = (float)short.MaxValue / MAPSIZE;
    const float INVSCALE = 1f / SCALE;
    public const float PRECISION = 1f / SCALE;
    public const float PRECISION2 = 2f * PRECISION;


    public Vector2_4byte_200(short x, short y) {
        this.x = x; this.y = y;
    }
    public Vector2_4byte_200(int x, int y) {
        this.x = (short)(((float)x) * SCALE);
        this.y = (short)(((float)y) * SCALE);
    }
    public Vector2_4byte_200(float x, float y) {
        this.x = (short)(((float)x) * SCALE);
        this.y = (short)(((float)y) * SCALE);
    }
    public Vector2_4byte_200() { }
    public static Vector2_4byte_200 Unclamped(Vector2 vec) {
        return new Vector2_4byte_200(
            (short)((vec.x) * SCALE),
            (short)((vec.y) * SCALE)
        );
    }


    public static implicit operator Vector2(Vector2_4byte_200 compressed) {
        return new Vector2(
            ((float)compressed.x) * INVSCALE,
            ((float)compressed.y) * INVSCALE
        );
    }

    public static implicit operator Vector2_4byte_200(Vector2 vec) {
        var cx = (short)Math.Round(Mathf.Clamp(vec.x, -MAPSIZE, MAPSIZE) * SCALE);
        var cy = (short)Math.Round(Mathf.Clamp(vec.y, -MAPSIZE, MAPSIZE) * SCALE);
        return new Vector2_4byte_200 { x = cx, y = cy };
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }



    public static bool operator ==(Vector2_4byte_200 a, Vector2_4byte_200 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(Vector2_4byte_200 a, Vector2_4byte_200 b) {
        return a.x != b.x || a.y != b.y;
    }

    public bool Equals(Vector2_4byte_200 other) => x == other.x && y == other.y;

    public override bool Equals(object other) {
        if (!(other is Vector2_4byte_200)) {
            return false;
        }
        return this == (Vector2_4byte_200)other;
    }
    public override int GetHashCode() {
        return (((ushort)x << 16) | (ushort)y);
    }
}

public struct Color24 : INetSerializeByMemcpy, IEquatable<Color24> {
    public byte r;
    public byte g;
    public byte b;

    public byte this[int index] {
        get {
            return index switch {
                0 => r,
                1 => g,
                2 => b,
                _ => throw new IndexOutOfRangeException("Invalid Color24 index(" + index + ")!"),
            };
        }
        set {
            switch (index) {
                case 0:
                r = value;
                break;
                case 1:
                g = value;
                break;
                case 2:
                b = value;
                break;
                default:
                throw new IndexOutOfRangeException("Invalid Color24 index(" + index + ")!");
            }
        }
    }

    public Color24(string hex) {
        if (TryParseHex(hex, out var c)) {
            this.r = c.r;
            this.g = c.g;
            this.b = c.b;
        } else {
            throw new ArgumentException("String must be a hex color (ex. #FF0000 )");
        }
    }

    public Color24(byte r, byte g, byte b) {
        this.r = r;
        this.g = g;
        this.b = b;
    }

    public static implicit operator Color24(Color32 c) {
        return new Color24(c.r, c.g, c.b);
    }
    public static implicit operator Color24(Color c) {
        return new Color24((byte)Math.Round(Mathf.Clamp01(c.r) * 255f), (byte)Math.Round(Mathf.Clamp01(c.g) * 255f), (byte)Math.Round(Mathf.Clamp01(c.b) * 255f));
    }
    public static implicit operator Color(Color24 c) {
        return new Color((float)c.r / 255f, (float)c.g / 255f, (float)c.b / 255f);
    }
    public static implicit operator Color32(Color24 c) {
        return new Color32(c.r, c.g, c.b, byte.MaxValue);
    }
    public Color ToColorWithAlpha(float a) {
        return new Color((float)this.r / 255f, (float)this.g / 255f, (float)this.b / 255f, a);
    }
    public Color ToColorWithAlpha(byte a) {
        return new Color((float)this.r / 255f, (float)this.g / 255f, (float)this.b / 255f, (float)a / 255f);
    }


    public static bool operator ==(Color24 lhs, Color24 rhs) {
        return
            lhs.r == rhs.r &&
            lhs.g == rhs.g &&
            lhs.b == rhs.b;
    }
    public static bool operator !=(Color24 lhs, Color24 rhs) {
        return !(lhs == rhs);
    }
    public bool Equals(Color24 other) => r == other.r && g == other.g && b == other.b;
    public override bool Equals(object other) {
        if (!(other is Color24)) {
            return false;
        }
        return this == (Color24)other;
    }


    public int Sum() {
        return (r + g + b);
    }

    public int Hash() {
        return (r << 16) | (g << 8) | b;
    }
    public override int GetHashCode() {
        return Hash();
    }
    public string ToHex(bool with_hashtag = true) {
        string str = string.Format(CultureInfo.InvariantCulture.NumberFormat, "{0:X2}{1:X2}{2:X2}", r, g, b);

        if (with_hashtag) {
            str = "#" + str;
        }
        return str;
    }
    public string ToHexString() {
        return string.Format(CultureInfo.InvariantCulture.NumberFormat, "{0:X2}{1:X2}{2:X2}", r, g, b);
    }

    public static Color24 FromHex(string hex) {
        if (!hex.StartsWith("#")) {
            hex = "#" + hex;
        }
        if (ColorUtility.TryParseHtmlString(hex, out var c)) {
            return c;
        }
        throw new Exception($"'{hex}' is not a valid color hex.");
    }
    public static bool TryParseHex(string hex, out Color24 color) {
        if (!hex.StartsWith("#")) {
            hex = "#" + hex;
        }
        if (ColorUtility.TryParseHtmlString(hex, out var c)) {
            color = c;
            return true;
        }
        color = default(Color24);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() {
        return $"RGB({r.ToString()}, {g.ToString()}, {b.ToString()})";
    }

    public static Color24 white {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            return new Color24(byte.MaxValue, byte.MaxValue, byte.MaxValue);
        }
    }

    public static Color24 black {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get {
            return new Color24(0, 0, 0);
        }
    }

}

public struct Vector2Int16 : INetSerializeByMemcpy, IEquatable<Vector2Int16> {
    public short x;
    public short y;


    public Vector2Int16(short x, short y) {
        this.x = x; this.y = y;
    }

    public static implicit operator Vector2(Vector2Int16 v2i16) {
        return new Vector2(v2i16.x, v2i16.y);
    }

    public static implicit operator Vector2Int(Vector2Int16 v2) {
        return new Vector2Int { x = v2.x, y = v2.y };
    }
    public static implicit operator Vector2Int16(Vector2Int v2) {
        return new Vector2Int16 { x = (short)(v2.x), y = (short)(v2.y) };
    }
    public static explicit operator Vector2Int16(Vector2 v2) {
        return new Vector2Int16 { x = (short)Math.Round(v2.x), y = (short)Math.Round(v2.y) };
    }
    public static explicit operator Vector2Int16(Vector3 v3) {
        return new Vector2Int16 { x = (short)Math.Round(v3.x), y = (short)Math.Round(v3.y) };
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2Int operator /(Vector2Int16 a, int b) {
        return new Vector2Int(a.x / b, a.y / b);
    }

    public static bool operator ==(Vector2Int16 a, Vector2Int16 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(Vector2Int16 a, Vector2Int16 b) {
        return a.x != b.x || a.y != b.y;
    }

    public bool Equals(Vector2Int16 other) => x == other.x && y == other.y;

    public override bool Equals(object other) {
        if (!(other is Vector2Int16)) {
            return false;
        }
        return this == (Vector2Int16)other;
    }
    public override int GetHashCode() {
        return (((ushort)x << 16) | (ushort)y);
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }
}

public struct Vector2UInt8 : INetSerializeByMemcpy, IEquatable<Vector2UInt8> {
    public byte x;
    public byte y;

    public static implicit operator Vector2(Vector2UInt8 v2ui8) {
        return new Vector2(v2ui8.x, v2ui8.y);
    }

    public Vector2UInt8(byte x, byte y) {
        this.x = x; this.y = y;
    }
    public Vector2UInt8(int x, int y) {
        this.x = (byte)x; this.y = (byte)y;
    }
    public static explicit operator Vector2UInt8(Vector2Int v2) {
        return new Vector2UInt8 { x = (byte)(v2.x), y = (byte)(v2.y) };
    }
    public static explicit operator Vector2UInt8(Vector2 v2) {
        return new Vector2UInt8 { x = (byte)(v2.x), y = (byte)(v2.y) };
    }
    public static explicit operator Vector2UInt8(Vector3 v3) {
        return new Vector2UInt8 { x = (byte)(v3.x), y = (byte)(v3.y) };
    }


    public static bool operator ==(Vector2UInt8 a, Vector2UInt8 b) {
        return a.x == b.x && a.y == b.y;
    }
    public static bool operator !=(Vector2UInt8 a, Vector2UInt8 b) {
        return a.x != b.x || a.y != b.y;
    }

    public bool Equals(Vector2UInt8 other) => x == other.x && y == other.y;

    public override bool Equals(object other) {
        if (!(other is Vector2UInt8)) {
            return false;
        }
        return this == (Vector2UInt8)other;
    }
    public override int GetHashCode() {
        return (((ushort)x << 16) | (ushort)y);
    }

    public override string ToString() {
        return ((Vector2)this).ToString();
    }
}
