using System.Globalization;
using System.Text.Json.Nodes;
using fNbt;

namespace McaDiff.Nbt;

/// <summary>
/// Lossless, human-inspectable JSON encoding of NBT values for patch files.
/// Each value is a single-key, type-tagged object:
/// <c>{"int":5}</c>, <c>{"string":"…"}</c>, <c>{"double":3}</c>, <c>{"byte":1}</c>,
/// <c>{"bytes":[…]}</c>, <c>{"ints":[…]}</c>, <c>{"list":{"type":"…","items":[…]}}</c>,
/// <c>{"compound":{"k":<val>,…}}</c>. <c>long</c> and <c>longArray</c> elements are
/// string-encoded because JSON numbers lose precision past 2^53.
/// </summary>
public static class NbtJson
{
    public static JsonObject ToJson(NbtTag tag)
    {
        switch (tag.TagType)
        {
            case NbtTagType.Byte: return One("byte", tag.ByteValue);
            case NbtTagType.Short: return One("short", tag.ShortValue);
            case NbtTagType.Int: return One("int", tag.IntValue);
            case NbtTagType.Long: return One("long", tag.LongValue.ToString(CultureInfo.InvariantCulture));
            // Float/double are string-encoded (like long) so NaN/±Infinity — which occur in
            // corrupted/weird Minecraft data — round-trip exactly. Plain JSON numbers can't carry
            // them (System.Text.Json throws), so a single NaN would otherwise break patch extraction.
            case NbtTagType.Float: return One("float", tag.FloatValue.ToString("R", CultureInfo.InvariantCulture));
            case NbtTagType.Double: return One("double", tag.DoubleValue.ToString("R", CultureInfo.InvariantCulture));
            case NbtTagType.String: return One("string", tag.StringValue);
            case NbtTagType.ByteArray:
            {
                var arr = new JsonArray();
                foreach (byte v in tag.ByteArrayValue) arr.Add(v);
                return new JsonObject { ["bytes"] = arr };
            }
            case NbtTagType.IntArray:
            {
                var arr = new JsonArray();
                foreach (int v in tag.IntArrayValue) arr.Add(v);
                return new JsonObject { ["ints"] = arr };
            }
            case NbtTagType.LongArray:
            {
                var arr = new JsonArray();
                foreach (long v in tag.LongArrayValue) arr.Add(v.ToString(CultureInfo.InvariantCulture));
                return new JsonObject { ["longs"] = arr };
            }
            case NbtTagType.List:
            {
                var list = (NbtList)tag;
                var items = new JsonArray();
                foreach (NbtTag t in list) items.Add(ToJson(t));
                return new JsonObject
                {
                    ["list"] = new JsonObject { ["type"] = list.ListType.ToString(), ["items"] = items },
                };
            }
            case NbtTagType.Compound:
            {
                var obj = new JsonObject();
                foreach (NbtTag t in (NbtCompound)tag) obj[t.Name!] = ToJson(t);
                return new JsonObject { ["compound"] = obj };
            }
            default:
                throw new NotSupportedException($"Cannot encode tag type {tag.TagType}.");
        }
    }

    /// <summary>Reconstructs an NBT tag from its JSON encoding, optionally named.</summary>
    public static NbtTag FromJson(JsonNode node, string? name = null) => FromJson(node, name, 0);

    private static NbtTag FromJson(JsonNode node, string? name, int depth)
    {
        if (depth > NbtCanonical.MaxDepth)
            throw new InvalidDataException($"NBT nesting exceeds {NbtCanonical.MaxDepth} levels");
        JsonObject obj = node.AsObject();
        var (key, val) = First(obj);
        switch (key)
        {
            case "byte":
            {
                int bv = val!.GetValue<int>();
                if (bv is < 0 or > 255) throw new FormatException($"byte value out of range: {bv}");
                return Named(name, (byte)bv, static (n, v) => new NbtByte(n, v), static v => new NbtByte(v));
            }
            case "short": return Named(name, val!.GetValue<short>(), static (n, v) => new NbtShort(n, v), static v => new NbtShort(v));
            case "int": return Named(name, val!.GetValue<int>(), static (n, v) => new NbtInt(n, v), static v => new NbtInt(v));
            case "long": return Named(name, long.Parse(val!.GetValue<string>(), CultureInfo.InvariantCulture), static (n, v) => new NbtLong(n, v), static v => new NbtLong(v));
            case "float": return Named(name, float.Parse(val!.GetValue<string>(), CultureInfo.InvariantCulture), static (n, v) => new NbtFloat(n, v), static v => new NbtFloat(v));
            case "double": return Named(name, double.Parse(val!.GetValue<string>(), CultureInfo.InvariantCulture), static (n, v) => new NbtDouble(n, v), static v => new NbtDouble(v));
            case "string": return Named(name, val!.GetValue<string>(), static (n, v) => new NbtString(n, v), static v => new NbtString(v));
            case "bytes":
            {
                byte[] data = val!.AsArray().Select(x => (byte)x!.GetValue<int>()).ToArray();
                return name is null ? new NbtByteArray(data) : new NbtByteArray(name, data);
            }
            case "ints":
            {
                int[] data = val!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
                return name is null ? new NbtIntArray(data) : new NbtIntArray(name, data);
            }
            case "longs":
            {
                long[] data = val!.AsArray().Select(x => long.Parse(x!.GetValue<string>(), CultureInfo.InvariantCulture)).ToArray();
                return name is null ? new NbtLongArray(data) : new NbtLongArray(name, data);
            }
            case "list": return BuildList(val!.AsObject(), name, depth);
            case "compound": return BuildCompound(val!.AsObject(), name, depth);
            default: throw new NotSupportedException($"Unknown NBT json tag '{key}'.");
        }
    }

    private static NbtTag BuildList(JsonObject spec, string? name, int depth)
    {
        // A missing type means an empty list (Unknown); a present-but-unrecognized type is a
        // corrupt/forward-version patch and must fail loudly, not silently become an empty list.
        string? typeStr = spec["type"]?.GetValue<string>();
        NbtTagType type = typeStr is null ? NbtTagType.Unknown
            : (Enum.TryParse(typeStr, out NbtTagType t) ? t : throw new NotSupportedException($"unknown NBT list type '{typeStr}'"));
        var list = name is null
            ? (type == NbtTagType.Unknown ? new NbtList() : new NbtList(type))
            : (type == NbtTagType.Unknown ? new NbtList(name) : new NbtList(name, type));
        if (spec["items"] is JsonArray items)
            foreach (JsonNode? item in items)
                list.Add(FromJson(item!.AsObject(), null, depth + 1));
        return list;
    }

    private static NbtTag BuildCompound(JsonObject members, string? name, int depth)
    {
        var c = name is null ? new NbtCompound() : new NbtCompound(name);
        foreach (var kv in members)
            c.Add(FromJson(kv.Value!.AsObject(), kv.Key, depth + 1));
        return c;
    }

    private static JsonObject One(string key, object? value) => new() { [key] = JsonValue.Create(value) };

    private static NbtTag Named<T>(string? name, T value, Func<string, T, NbtTag> named, Func<T, NbtTag> nameless)
        => name is null ? nameless(value) : named(name, value);

    private static (string Key, JsonNode? Value) First(JsonObject obj)
    {
        foreach (var kv in obj) return (kv.Key, kv.Value);
        throw new FormatException("Empty NBT json value object.");
    }
}
