using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MslLive.Shared;

namespace MslLive.Agent;

/// <summary>\\.\pipe\msl-live-&lt;pid&gt; 单客户端服务。后台线程自持，永远不阻塞游戏线程。
/// 首连跑自检（结果进 hello）；helloAck 拒收 → 关连接继续监听（MSL 可能换版本重来）。
/// proof 走 ProofVerify + Trampoline（Task 14）；batch 走 ApplyEngine 两阶段（Task 15）：
/// Phase 1 失败立即回执；成功则等游戏线程 Pump 完置出站信箱，本循环在收件空闲间隙轮询转发
/// （游戏线程写 pipe 可能阻塞 VM——MSL 30s 超时兜底，游戏暂停 = 诚实超时）。
/// 管道全程零 overlapped/IOCP 依赖（fix-loop #7/#8）：
/// accept 用非 overlapped 句柄上的 ConnectNamedPipe(h,NULL)——.NET WaitForConnection 的
/// overlapped 完成投递在游戏宿主内实测会丢（内核配对后 Handle 24.5 分钟未执行，
/// 真机 15:01/15:37 两现，诊断扰动才醒——见 smoke 结果文档循环 #7）；
/// 数据面单线程 peek-poll（PeekNamedPipe 查余量 + 有字节才 ReadFile）——同步句柄上并发
/// 读写会互相锁死（内核对同步句柄的 I/O 串行化，专用读线程方案已被最小复现证伪）；
/// CreateNamedPipeW 显式 64K 配额——零配额管道在「对端暂无挂起读者」时写会阻塞，
/// 请求-响应间隙的写恰好踩中（64K 内写入直接进内核缓冲）。</summary>
public static class PipeServer
{
    static bool started;
    static bool selfChecked;

    /// <summary>测试复位：下次连接重跑自检（selfChecked 是进程级一次性，测试需要每用例重跑）。</summary>
    internal static void ResetForTest() => selfChecked = false;

    public static void Start()
    {
        if (started) return;
        started = true;
        new Thread(AcceptLoop) { IsBackground = true, Name = "msl-live-pipe" }.Start();
    }

    // ---- raw accept：绕开 BCL 的 overlapped/IOCP 完成链（fix-loop #7 根因）----

    const uint PIPE_ACCESS_DUPLEX = 0x3;
    const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    const int ERROR_PIPE_CONNECTED = 535;   // 0x217：客户端在 create 与 connect 之间已连上（文档化行为）
    const int ERROR_BROKEN_PIPE = 109;
    const int ERROR_NO_DATA = 232;
    const int HandshakeTimeoutMs = 15000;   // MSL 收 hello 即回 ack，此界只兜对端已死/异版本

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuf, uint inBuf, uint defaultTimeout, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ConnectNamedPipe(IntPtr h, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(IntPtr h, byte[]? buffer, int bufferSize,
        out int bytesRead, out int totalBytesAvail, out int bytesLeftThisMessage);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>建实例并阻塞等首个客户端。等价于托管 ctor(name, InOut, 1, Byte, Asynchronous)
    /// 但句柄不带 FILE_FLAG_OVERLAPPED：ConnectNamedPipe(h,NULL) 是纯内核阻塞唤醒，
    /// 不经 ValueTask/IOCP 回调——那条链在游戏宿主内的投递可靠性没有保障。
    /// 配额显式 64K：0/0 配额（BCL 默认）下对端无挂起读者时写会阻塞（最小复现变体 D）。</summary>
    static NamedPipeServerStream Accept(string name)
    {
        // Win32 管道名要全限定 \\.\pipe\ 前缀（托管 ctor 内部代加，raw 调用得自己来）
        IntPtr h = CreateNamedPipeW(@"\\.\pipe\" + name,
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            0 /* PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT */, 1, 65536, 65536, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new IOException($"CreateNamedPipe failed: {Marshal.GetLastWin32Error()}");
        try
        {
            if (!ConnectNamedPipe(h, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_PIPE_CONNECTED)
                    throw new IOException($"ConnectNamedPipe failed: {err}");
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: false, isConnected: true,
                new SafePipeHandle(h, ownsHandle: true));
        }
        catch { CloseHandle(h); throw; }   // 流没建成就得自己收句柄
    }

    static void AcceptLoop()
    {
        string name = $"msl-live-{Environment.ProcessId}";
        while (true)
        {
            NamedPipeServerStream? s = null;
            try
            {
                s = Accept(name);
                Handle(s);
            }
            catch (Exception ex) { AgentState.Log("pipe conn: " + ex.Message); }
            finally { s?.Dispose(); }
        }
    }

    static void Handle(NamedPipeServerStream s)
    {
        if (!selfChecked) { SelfCheck(); selfChecked = true; }

        // 单线程数据面：本线程独占句柄的全部读写（同步句柄并发 I/O 互相锁死，fix-loop #8）。
        // 入站 = PeekNamedPipe 查余量、有字节才 ReadFile（立即返回），帧增量拼装、残段跨轮保留
        // （MSL 握手后背靠背连发 vars+proof）；出站 = Wire.Send（单写者）+ 空闲间隙转发回执。
        var reader = new FrameReader(s);

        Wire.Send(s, "hello", new HelloMsg
        {
            AgentVersion = AgentState.AgentVersion,
            Pid = Environment.ProcessId,
            BootHash = AgentState.BootHash,
            StubPresent = AgentState.StubPresent,
            AgentStatus = AgentState.Status,
        });

        // 握手有界等待：无限等会把后续连接全堵死在一条死连接上
        if (!reader.TryReadFrameUntil(Environment.TickCount64 + HandshakeTimeoutMs, out var first))
        { AgentState.Log("handshake timeout: no helloAck"); return; }
        if (first.Type != "helloAck") { AgentState.Log($"expected helloAck, got {first.Type}"); return; }
        var ack = Wire.Decode<HelloAck>(first.Data);
        if (!ack.Accept) { AgentState.Log("MSL rejected: " + ack.Reason); return; }

        while (true)
        {
            var msg = reader.TryReadFrame();
            if (msg == null)
            {
                if (reader.Dead) return;   // 对端断开：收尾回监听
                FlushReceipt(s);   // 空闲间隙：转发 Pump 完成的回执
                Thread.Sleep(25);
                continue;
            }
            var (type, data) = msg.Value;
            switch (type)
            {
                case "queryBlanks":
                    Wire.Send(s, "blanks", AgentState.Blanks.Ready
                        ? new BlanksMsg { SpriteFirst = AgentState.Blanks.SpriteFirst, PathFirst = AgentState.Blanks.PathFirst }
                        : new BlanksMsg());   // 未校准 = 全 -1，MSL 侧会重试或拒收
                    break;
                case "vars":
                    AgentState.VarMap = Wire.Decode<VarsMsg>(data).Ids;
                    AgentState.Log($"vars received: {AgentState.VarMap.Count} entries");
                    break;
                case "proof":
                {
                    var pm = Wire.Decode<ProofMsg>(data);
                    var pAck = ProofVerify.Run(pm);
                    // 编码自证过了才装 trampoline；装不上 = 自证不可信（编译器形态/注册表假设破裂），
                    // 整个 proofAck 拒掉（fail-closed，MSL 侧拒绝开会话）
                    if (pAck.Ok && !Trampoline.Install())
                        pAck = new ProofAck
                        {
                            Ok = false, Verified = pAck.Verified, Failed = pAck.Failed,
                            Error = "trampoline install failed: " + Trampoline.LastError,
                        };
                    Wire.Send(s, "proofAck", pAck);
                    break;
                }
                case "batch":
                {
                    var b = Wire.Decode<BatchMsg>(data);
                    if (b.Ops.Count == 0)   // 空批 = vacuous 成功，立即回执（否则无 op 可泵、回执永远不来）
                    {
                        Wire.Send(s, "receipt", new BatchReceipt { BatchSeq = b.BatchSeq, AllOk = true });
                        break;
                    }
                    var failed = ApplyEngine.Enqueue(b);
                    if (failed != null)   // Phase 1 整批弃（spec D4）→ 立即回执，无 Pump 必要
                        Wire.Send(s, "receipt", new BatchReceipt
                        { BatchSeq = b.BatchSeq, AllOk = false, Ops = failed });
                    // 成功入队：回执待游戏线程 Pump 完成后进出站信箱，由本循环超时轮询转发
                    break;
                }
                default:
                    AgentState.Log("unknown msg: " + type);
                    break;
            }
            FlushReceipt(s);
        }
    }

    static void FlushReceipt(NamedPipeServerStream s)
    {
        var r = ApplyEngine.TryTakeReceipt();
        if (r != null) Wire.Send(s, "receipt", r);
    }

    static void SelfCheck()
    {
        int nodes = NodeIndex.Build();
        if (nodes < 30000) AgentState.Fail($"node index too small ({nodes})");
        if (!Registry.Bootstrap()) AgentState.Fail("registry bootstrap failed");
        AgentState.StubPresent = NodeIndex.TryGet("gml_Object_o_msl_live_Step_0", out _);
        AgentState.Log($"self-check: nodes={nodes} registry={Registry.Count} stub={AgentState.StubPresent} status={AgentState.Status}");
    }

    /// <summary>非阻塞增量帧读取器（fix-loop #8：替代专用读线程）。
    /// 帧格式与 Wire 一致：4 字节 LE 长度 + JSON 体；单帧上限校验同 Wire.MaxFrame。
    /// 断管后仍会先拼出已缓冲的残帧（对端写完最后一帧即关的尾巴不丢）。</summary>
    sealed class FrameReader
    {
        // 缓冲上限 = 单帧最大 + 头 + 一个管道配额量级的下一帧前奏；超限帧由长度校验拒掉
        const int Cap = Wire.MaxFrame + 4 + 65536;

        readonly NamedPipeServerStream s;
        readonly IntPtr h;
        byte[] buf = new byte[8192];
        int len;             // 已缓冲字节数
        int frameLen = -1;   // 当前帧体长（-1 = 头未凑齐）

        public bool Dead { get; private set; }   // 对端断开且管道无字节可读

        public FrameReader(NamedPipeServerStream s)
        {
            this.s = s;
            h = s.SafePipeHandle.DangerousGetHandle();
        }

        /// <summary>非阻塞：吸收当前可用字节，返回一个完整帧（无则 null）。</summary>
        public (string Type, JsonElement Data)? TryReadFrame()
        {
            Drain();
            return TakeOne();
        }

        /// <summary>轮询等一帧直到 deadline（25ms 节奏）；超时 false，断管抛 EOF（与旧直读语义一致）。</summary>
        public bool TryReadFrameUntil(long deadlineTick, out (string Type, JsonElement Data) frame)
        {
            while (true)
            {
                var f = TryReadFrame();
                if (f != null) { frame = f.Value; return true; }
                if (Dead) throw new EndOfStreamException("pipe closed mid-frame");
                if (Environment.TickCount64 > deadlineTick) { frame = default; return false; }
                Thread.Sleep(25);
            }
        }

        void Drain()
        {
            while (!Dead)
            {
                if (!PeekNamedPipe(h, null, 0, out _, out int avail, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    Dead = true;
                    if (err != ERROR_BROKEN_PIPE && err != ERROR_NO_DATA)
                        AgentState.Log($"peek failed: {err}");   // 意料外错误也按连接结束处理，但留痕
                    return;
                }
                if (avail == 0) return;   // 空闲：下轮 poll 再看
                if (buf.Length - len < avail && buf.Length < Cap)
                    Array.Resize(ref buf, Math.Min(Math.Max(buf.Length * 2, len + avail), Cap));
                int space = buf.Length - len;
                if (space == 0) return;   // 仅 Cap 满时发生：此时帧头校验保证已有完整帧可解析，TakeOne 消化后自回
                int got = s.Read(buf, len, Math.Min(avail, space));
                if (got <= 0) { Dead = true; return; }   // EOF
                len += got;
            }
        }

        (string Type, JsonElement Data)? TakeOne()
        {
            if (frameLen < 0)
            {
                if (len < 4) return null;
                frameLen = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
                if (frameLen <= 0 || frameLen > Wire.MaxFrame)
                    throw new InvalidDataException($"bad frame length {frameLen}");
            }
            if (len < 4 + frameLen) return null;
            using var doc = JsonDocument.Parse(buf.AsMemory(4, frameLen));
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString()
                ?? throw new InvalidDataException("frame missing type");
            // Clone：doc 随 using 释放，payload 交调用方持有
            var frame = (type, root.GetProperty("data").Clone());
            int consumed = 4 + frameLen;
            Buffer.BlockCopy(buf, consumed, buf, 0, len - consumed);   // 残段前移（背靠背帧）
            len -= consumed;
            frameLen = -1;
            if (len == 0 && buf.Length > 65536) buf = new byte[65536];   // 巨帧消化完缩回，不长期占内存
            return frame;
        }
    }
}
