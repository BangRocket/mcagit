using System.Globalization;
using System.Text;
using fNbt;

namespace McaDiff.Diff;

/// <summary>Human-readable, type-suffixed string forms of NBT tags (SNBT-ish).</summary>
public static class ValueRepr
{
    private const int MaxStringLen = 120;

    /// <summary>Compact one-line repr of any tag (scalars, arrays, containers).</summary>
    public static string Describe(NbtTag tag) => tag.TagType switch
    {
        NbtTagType.Compound => $"compound{{{((NbtCompound)tag).Count} keys}}",
        NbtTagType.List => $"list[{((NbtList)tag).Count}]",
        NbtTagType.ByteArray or NbtTagType.IntArray or NbtTagType.LongArray => ArraySummary(tag),
        _ => Scalar(tag),
    };

    /// <summary>Repr for scalar leaf tags, with Minecraft/SNBT type suffixes.</summary>
    public static string Scalar(NbtTag tag) => tag.TagType switch
    {
        NbtTagType.Byte => $"{(sbyte)tag.ByteValue}b", // signed, like every Minecraft/SNBT tool (0xC8 → -56b)
        NbtTagType.Short => $"{tag.ShortValue}s",
        NbtTagType.Int => tag.IntValue.ToString(CultureInfo.InvariantCulture),
        NbtTagType.Long => $"{tag.LongValue.ToString(CultureInfo.InvariantCulture)}L",
        NbtTagType.Float => $"{tag.FloatValue.ToString("R", CultureInfo.InvariantCulture)}f",
        NbtTagType.Double => $"{tag.DoubleValue.ToString("R", CultureInfo.InvariantCulture)}d",
        NbtTagType.String => Quote(tag.StringValue),
        _ => tag.TagType.ToString(),
    };

    /// <summary>Type + length summary for array tags, e.g. <c>long[37]</c>.</summary>
    public static string ArraySummary(NbtTag tag) => tag.TagType switch
    {
        NbtTagType.ByteArray => $"byte[{tag.ByteArrayValue.Length}]",
        NbtTagType.IntArray => $"int[{tag.IntArrayValue.Length}]",
        NbtTagType.LongArray => $"long[{tag.LongArrayValue.Length}]",
        _ => tag.TagType.ToString(),
    };

    public static bool ScalarEquals(NbtTag a, NbtTag b) => a.TagType switch
    {
        NbtTagType.Byte => a.ByteValue == b.ByteValue,
        NbtTagType.Short => a.ShortValue == b.ShortValue,
        NbtTagType.Int => a.IntValue == b.IntValue,
        NbtTagType.Long => a.LongValue == b.LongValue,
        NbtTagType.Float => a.FloatValue.Equals(b.FloatValue),   // .Equals so NaN==NaN
        NbtTagType.Double => a.DoubleValue.Equals(b.DoubleValue),
        NbtTagType.String => string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal),
        _ => false,
    };

    private static string Quote(string? s)
    {
        s ??= "";
        bool truncated = s.Length > MaxStringLen;
        if (truncated) s = s[..MaxStringLen];
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        if (truncated) sb.Append("…");
        return sb.ToString();
    }
}
