using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class TrayIconEntryTests
{
    /// <summary>构造一个最小 ICO 容器:文件头,每个(宽度, 载荷)一项目录项。</summary>
    private static byte[] BuildIco(params (int Width, byte[] Payload)[] entries)
    {
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 1, 0 });                                   // 保留字段 + 类型=图标
        buffer.AddRange(BitConverter.GetBytes((ushort)entries.Length));                // 数量
        var dataOffset = 6 + 16 * entries.Length;
        var payloads = new List<byte>();
        foreach (var (width, payload) in entries)
        {
            buffer.Add((byte)(width >= 256 ? 0 : width)); // 0 表示 256
            buffer.Add((byte)(width >= 256 ? 0 : width));
            buffer.Add(0);                                 // 调色板
            buffer.Add(0);                                 // 保留字段
            buffer.AddRange(BitConverter.GetBytes((ushort)1));                // 位面数
            buffer.AddRange(BitConverter.GetBytes((ushort)32));               // 位深
            buffer.AddRange(BitConverter.GetBytes((uint)payload.Length));
            buffer.AddRange(BitConverter.GetBytes((uint)(dataOffset + payloads.Count)));
            payloads.AddRange(payload);
        }
        buffer.AddRange(payloads);
        return buffer.ToArray();
    }

    [Fact]
    public void PickIcoEntry_ChoosesWidthClosestToTraySize()
    {
        var ico = BuildIco((16, new byte[4]), (32, new byte[8]), (256, new byte[16]));
        var picked = TrayIconService.PickIcoEntry(ico, 16);
        Assert.NotNull(picked);
        Assert.Equal(16, picked!.Value.Width);
        Assert.Equal(4, picked.Value.DataLength);
    }

    [Fact]
    public void PickIcoEntry_TreatsZeroWidthByteAs256()
    {
        var ico = BuildIco((16, new byte[4]), (256, new byte[12]));
        var picked = TrayIconService.PickIcoEntry(ico, 256);
        Assert.NotNull(picked);
        Assert.Equal(256, picked!.Value.Width);
        Assert.Equal(12, picked.Value.DataLength);
    }

    [Fact]
    public void PickIcoEntry_ReturnsPayloadInsideBuffer()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var ico = BuildIco((16, payload));
        var picked = TrayIconService.PickIcoEntry(ico, 16);
        Assert.NotNull(picked);
        var slice = ico.AsSpan(picked!.Value.DataOffset, picked.Value.DataLength).ToArray();
        Assert.Equal(payload, slice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void PickIcoEntry_RejectsTruncatedContainer(int length)
    {
        var ico = BuildIco((16, new byte[8]));
        Assert.Null(TrayIconService.PickIcoEntry(ico[..length], 16));
    }

    [Fact]
    public void PickIcoEntry_RejectsEmptyContainer()
    {
        var ico = BuildIco();
        Assert.Null(TrayIconService.PickIcoEntry(ico, 16));
    }

    [Fact]
    public void PickIcoEntry_RejectsPayloadOutsideBuffer()
    {
        var ico = BuildIco((16, new byte[8]));
        // 谎报载荷长度,让声明的范围越过末尾
        var entry = 6;
        BitConverter.GetBytes(4096u).CopyTo(ico, entry + 8);
        Assert.Null(TrayIconService.PickIcoEntry(ico, 16));
    }
}
