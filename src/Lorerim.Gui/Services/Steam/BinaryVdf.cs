using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lorerim.Gui.Services.Steam;

/// <summary>
/// Minimal binary KeyValues1 reader/writer, enough for Steam's shortcuts.vdf.
/// Node types: 0x00 map, 0x01 UTF-8 string, 0x02 int32 LE; 0x08 ends a map.
/// Maps are Dictionary&lt;string, object&gt; where object is string | int | nested dictionary.
/// </summary>
public static class BinaryVdf
{
    private const byte TypeMap = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt = 0x02;
    private const byte TypeFloat = 0x03;
    private const byte TypeUInt64 = 0x07;
    private const byte TypeInt64 = 0x0A;
    private const byte EndMap = 0x08;

    public static Dictionary<string, object> Read(byte[] data)
    {
        var pos = 0;
        return ReadMap(data, ref pos);
    }

    public static byte[] Write(Dictionary<string, object> root)
    {
        using var ms = new MemoryStream();
        WriteMap(ms, root);
        return ms.ToArray();
    }

    private static Dictionary<string, object> ReadMap(byte[] data, ref int pos)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < data.Length)
        {
            var type = data[pos++];
            if (type == EndMap)
            {
                return map;
            }
            var name = ReadCString(data, ref pos);
            switch (type)
            {
                case TypeMap:
                    map[name] = ReadMap(data, ref pos);
                    break;
                case TypeString:
                    map[name] = ReadCString(data, ref pos);
                    break;
                case TypeInt:
                    Need(data, pos, 4);
                    map[name] = BitConverter.ToInt32(data, pos);
                    pos += 4;
                    break;
                // Passthrough types written by other tools (Steam ROM Manager, Heroic, …);
                // aborting on them would break users with existing third-party shortcuts.
                case TypeFloat:
                    Need(data, pos, 4);
                    map[name] = BitConverter.ToSingle(data, pos);
                    pos += 4;
                    break;
                case TypeUInt64:
                    Need(data, pos, 8);
                    map[name] = BitConverter.ToUInt64(data, pos);
                    pos += 8;
                    break;
                case TypeInt64:
                    Need(data, pos, 8);
                    map[name] = BitConverter.ToInt64(data, pos);
                    pos += 8;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported binary VDF node type 0x{type:x2} at {pos - 1}");
            }
        }
        // Ran off the end without an EndMap terminator: the file is truncated. Throwing here
        // stops Upsert from "laundering" a corrupt shortcuts.vdf into a valid one that
        // silently drops every entry past the truncation point.
        throw new InvalidDataException("Truncated binary VDF: map not terminated (file may be corrupt)");
    }

    private static void Need(byte[] data, int pos, int count)
    {
        if (pos + count > data.Length)
        {
            throw new InvalidDataException("Truncated binary VDF: value runs past end of file");
        }
    }

    private static string ReadCString(byte[] data, ref int pos)
    {
        var start = pos;
        while (pos < data.Length && data[pos] != 0)
        {
            pos++;
        }
        if (pos >= data.Length)
        {
            throw new InvalidDataException("Truncated binary VDF: unterminated string");
        }
        var s = Encoding.UTF8.GetString(data, start, pos - start);
        pos++;
        return s;
    }

    private static void WriteMap(Stream s, Dictionary<string, object> map)
    {
        foreach (var (key, value) in map)
        {
            switch (value)
            {
                case Dictionary<string, object> child:
                    s.WriteByte(TypeMap);
                    WriteCString(s, key);
                    WriteMap(s, child);
                    break;
                case string str:
                    s.WriteByte(TypeString);
                    WriteCString(s, key);
                    WriteCString(s, str);
                    break;
                case int i:
                    s.WriteByte(TypeInt);
                    WriteCString(s, key);
                    s.Write(BitConverter.GetBytes(i));
                    break;
                case float f:
                    s.WriteByte(TypeFloat);
                    WriteCString(s, key);
                    s.Write(BitConverter.GetBytes(f));
                    break;
                case ulong u:
                    s.WriteByte(TypeUInt64);
                    WriteCString(s, key);
                    s.Write(BitConverter.GetBytes(u));
                    break;
                case long l:
                    s.WriteByte(TypeInt64);
                    WriteCString(s, key);
                    s.Write(BitConverter.GetBytes(l));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported value type {value?.GetType().Name} for key '{key}'");
            }
        }
        s.WriteByte(EndMap);
    }

    private static void WriteCString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        s.Write(bytes);
        s.WriteByte(0);
    }
}
