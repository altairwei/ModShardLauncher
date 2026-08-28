using MslLive.Shared;
using Xunit;

namespace ModShardLauncherTest;

public class WireTests
{
    [Fact]
    public void RoundTrip_BatchMsg_PreservesAllFields()
    {
        var batch = new BatchMsg
        {
            BatchSeq = 7,
            Ops =
            {
                new OpMsg
                {
                    Seq = 0, Kind = "swap", Entry = "gml_Object_o_x_Create_0", LocalsCount = 2, ArgCount = 1,
                    Instructions =
                    {
                        new SemInstruction { Kind = 0xC0, T1 = 1, T2 = 2, Inst = -20, Low16 = 42, Int = 42 },
                        new SemInstruction { Kind = 0xD9, Fn = "gml_Script_scr_foo", Var = null, Str = "héllo" },
                    },
                    Variables = { "global.foo", "_local" },
                    Functions = { "gml_Script_scr_foo" },
                    Strings = { new StrRef { Content = "héllo", StrgIndex = 2480 } },
                    Assets = { new AssetRef { Kind = "Sprite", Index = 17250, Name = "s_new", RuntimeIndex = 17248 } },
                },
            },
        };
        using var ms = new MemoryStream();
        Wire.Send(ms, "batch", batch);
        ms.Position = 0;
        var (type, data) = Wire.Receive(ms);
        Assert.Equal("batch", type);
        var back = Wire.Decode<BatchMsg>(data);
        Assert.Equal(7, back.BatchSeq);
        var op = Assert.Single(back.Ops);
        Assert.Equal("gml_Object_o_x_Create_0", op.Entry);
        Assert.Equal(2, op.Instructions.Count);
        Assert.Equal(42, op.Instructions[0].Int);
        Assert.Equal("héllo", op.Instructions[1].Str);
        Assert.Equal(2480, op.Strings[0].StrgIndex);
        Assert.Equal(17248, op.Assets[0].RuntimeIndex);
    }

    [Fact]
    public void Receive_TruncatedFrame_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        Wire.Send(ms, "hello", new HelloMsg { Pid = 1 });
        byte[] all = ms.ToArray();
        using var cut = new MemoryStream(all[..(all.Length - 3)]);
        Assert.Throws<EndOfStreamException>(() => Wire.Receive(cut));
    }

    [Fact]
    public void Receive_NegativeLength_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Throws<InvalidDataException>(() => Wire.Receive(ms));
    }
}
