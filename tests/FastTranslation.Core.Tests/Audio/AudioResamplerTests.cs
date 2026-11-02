using FastTranslation.Core.Audio;
using Xunit;

namespace FastTranslation.Core.Tests.Audio;

public class AudioResamplerTests
{
    private static float[] Constant(int frames, int channels, float value)
    {
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        return samples;
    }

    private static short Pcm16(ReadOnlySpan<byte> bytes, int index)
    {
        return (short)(bytes[index * 2] | (bytes[index * 2 + 1] << 8));
    }

    [Fact]
    public void Resamples_48k_stereo_to_16k_mono()
    {
        var resampler = new AudioResampler(48000, 2);
        var output = new byte[40000];
        int written = resampler.Process(Constant(48000, 2, 0.5f), output);

        // 1 秒 48k 输入应产出 16000 个 16k 样本 = 32000 字节（插值需要 frame+1，允许 ±2 字节出入）
        Assert.InRange(written, 31996, 32002);
        for (int i = 0; i < written / 2; i++)
        {
            Assert.InRange(Pcm16(output.AsSpan(0, written), i), 16382, 16386);
        }
    }

    [Fact]
    public void MaxOutputBytes_matches_what_a_block_actually_produces()
    {
        // 48k 立体声 100ms 块:4800 帧 → 1600 帧 16k = 3200 字节。
        // 按 MaxOutputBytes 开缓冲既不能截断,也不该像「采样数×6」那样白扔 9 倍内存。
        var resampler = new AudioResampler(48000, 2);
        var output = new byte[resampler.MaxOutputBytes(4800 * 2)];

        int written = resampler.Process(Constant(4800, 2, 0.5f), output);

        Assert.InRange(written, 3196, 3204);
        Assert.InRange(output.Length, 3200, 4096);
    }

    [Fact]
    public void Keeps_remainder_between_blocks()
    {
        var input = Constant(24000, 2, 0.5f);
        var chunked = Run(new AudioResampler(48000, 2), input, chunk: 997);
        var whole = Run(new AudioResampler(48000, 2), input, chunk: input.Length);

        // 分块喂入与整段喂入必须逐字节一致：相位余数必须留在重采样器里，绝不能丢
        Assert.Equal(whole.Length, chunked.Length);
        Assert.True(chunked.AsSpan().SequenceEqual(whole));
    }

    [Fact]
    public void Handles_44100_rate()
    {
        var resampler = new AudioResampler(44100, 2);
        var output = new byte[40000];
        int written = resampler.Process(Constant(44100, 2, 0.25f), output);

        // 44100 → 16000 不是整倍率：1 秒输入应有 16000±1 个样本，值 = 0.25*32767 ≈ 8192
        Assert.InRange(written, 31998, 32002);
        for (int i = 0; i < written / 2; i++)
        {
            Assert.InRange(Pcm16(output.AsSpan(0, written), i), 8190, 8194);
        }
    }

    [Fact]
    public void Downmixes_stereo_by_average()
    {
        var resampler = new AudioResampler(48000, 2);
        var samples = new float[48000 * 2];
        for (int f = 0; f < 48000; f++)
        {
            samples[f * 2] = 1f;
            samples[f * 2 + 1] = 0f;
        }

        var output = new byte[40000];
        int written = resampler.Process(samples, output);

        Assert.InRange(written, 31996, 32002);
        for (int i = 0; i < written / 2; i++)
        {
            Assert.InRange(Pcm16(output.AsSpan(0, written), i), 16382, 16386);
        }
    }

    [Fact]
    public void Flush_emits_the_final_frame()
    {
        var resampler = new AudioResampler(16000, 1);
        var output = new byte[16];
        // 3 帧 16k mono：Process 能插值到 frame 1（需要 frame+1），Flush 按最后一帧保持补出 frame 2
        int written = resampler.Process(Constant(3, 1, 0.5f), output);
        int tail = resampler.Flush(output.AsSpan(written));

        Assert.Equal(4, written);
        Assert.Equal(2, tail);
    }

    private static byte[] Run(AudioResampler resampler, float[] input, int chunk)
    {
        var buffer = new List<byte>();
        var output = new byte[65536];  // 整段路径一次 Process 产 8000 样本=16000 字节，缓冲必须够大，否则整段被截断、比对失去意义
        for (int offset = 0; offset < input.Length; offset += chunk)
        {
            int count = Math.Min(chunk, input.Length - offset);
            int written = resampler.Process(input.AsSpan(offset, count), output);
            buffer.AddRange(output.AsSpan(0, written).ToArray());
        }

        int tail = resampler.Flush(output);
        buffer.AddRange(output.AsSpan(0, tail).ToArray());
        return [.. buffer];
    }
}
