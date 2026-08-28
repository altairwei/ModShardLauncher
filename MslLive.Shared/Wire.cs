using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MslLive.Shared;

/// <summary>4 字节 LE 长度前缀 + UTF8 JSON 帧。信封 {"type":..., "data":...}。
/// 单帧上限 64MB（table_lines 级病态载荷 23MB 的 2.5 倍余量；超出即 bug，throw）。</summary>
public static class Wire
{
    public const int MaxFrame = 64 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Send<T>(Stream s, string type, T payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(type, payload), Json);
        Span<byte> head = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        s.Write(head);
        s.Write(body);
        s.Flush();
    }

    public static (string Type, JsonElement Data) Receive(Stream s)
    {
        byte[] head = ReadExact(s, 4);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len <= 0 || len > MaxFrame) throw new InvalidDataException($"bad frame length {len}");
        byte[] body = ReadExact(s, len);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string type = root.GetProperty("type").GetString()
            ?? throw new InvalidDataException("frame missing type");
        // Clone：doc 随 using 释放，payload 交调用方持有
        return (type, root.GetProperty("data").Clone());
    }

    public static T Decode<T>(JsonElement data) =>
        JsonSerializer.Deserialize<T>(data.GetRawText(), Json)
        ?? throw new InvalidDataException($"cannot decode {typeof(T).Name}");

    static byte[] ReadExact(Stream s, int n)
    {
        byte[] buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int got = s.Read(buf, off, n - off);
            if (got == 0) throw new EndOfStreamException("pipe closed mid-frame");
            off += got;
        }
        return buf;
    }

    sealed record Envelope<T>(string type, T data);
}
