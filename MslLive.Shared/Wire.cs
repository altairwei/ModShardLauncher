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

    /// <summary>可恢复的部分帧接收状态（TryReceive 跨超时续收用——超时只意味着「暂时没有完整帧」，
    /// 已读的字节留在状态里，下次调用原地继续，绝不丢字节/错帧）。</summary>
    public sealed class ReceiveState
    {
        public readonly byte[] Head = new byte[4];
        public int HeadFilled;
        public byte[]? Body;
        public int BodyFilled;
    }

    /// <summary>带超时的接收：timeoutMs 内收到完整帧 → 返回之；超时 → null（连接保持，状态可续）。
    /// EOF/坏帧照旧 throw（与 Receive 同语义）。服务端轮询循环用（receipt 出站信箱在超时间隙转发）；
    /// 流必须支持可取消 ReadAsync（NamedPipeServerStream 需 PipeOptions.Asynchronous）。</summary>
    public static (string Type, JsonElement Data)? TryReceive(Stream s, ReceiveState st, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var ct = cts.Token;
        try
        {
            while (st.HeadFilled < 4)   // 逐次读、逐次记进度：取消落在两次读之间时状态始终一致
            {
                int got = s.ReadAsync(st.Head, st.HeadFilled, 4 - st.HeadFilled, ct).GetAwaiter().GetResult();
                if (got == 0) throw new EndOfStreamException("pipe closed mid-frame");
                st.HeadFilled += got;
            }
            if (st.Body == null)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(st.Head);
                if (len <= 0 || len > MaxFrame) throw new InvalidDataException($"bad frame length {len}");
                st.Body = new byte[len];
            }
            while (st.BodyFilled < st.Body.Length)
            {
                int got = s.ReadAsync(st.Body, st.BodyFilled, st.Body.Length - st.BodyFilled, ct).GetAwaiter().GetResult();
                if (got == 0) throw new EndOfStreamException("pipe closed mid-frame");
                st.BodyFilled += got;
            }
            using var doc = JsonDocument.Parse(st.Body);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString()
                ?? throw new InvalidDataException("frame missing type");
            var data = root.GetProperty("data").Clone();   // doc 随 using 释放
            st.HeadFilled = 0; st.Body = null; st.BodyFilled = 0;   // 复位，迎下一帧
            return (type, data);
        }
        catch (OperationCanceledException) { return null; }
    }

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
