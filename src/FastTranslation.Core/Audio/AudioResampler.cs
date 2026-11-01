namespace FastTranslation.Core.Audio;

/// <summary>
/// 把 WASAPI 混音格式的 float 采样重采样为 16kHz 单声道 PCM16 小端字节流。
/// </summary>
public sealed class AudioResampler
{
    // 相位以 1/16000 输入帧为单位累加：第 k 个输出样本恒对应输入位置 k*sourceRate/16000，
    // 有理数增量无累积漂移（44100Hz 这类非整倍率长会话也不偏移）。
    private readonly int _srcRate;
    private readonly int _ch;
    private float[] _buf = [];
    private int _head;   // 缓冲内第一个有效采样下标
    private int _tail;   // 缓冲内有效采样结束下标（不含）
    private long _acc;

    public AudioResampler(int sourceRate, int sourceChannels)
    {
        if (sourceRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        }

        if (sourceChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        }

        _srcRate = sourceRate;
        _ch = sourceChannels;
    }

    /// <summary>混入一段交错 float 采样，尽可能产出输出字节；返回写入 output 的字节数（缓冲不足时截断，相位不推进）。</summary>
    public int Process(ReadOnlySpan<float> input, Span<byte> output)
    {
        Append(input);
        return Drain(output, final: false);
    }

    /// <summary>冲出剩余可解码样本（末尾不足两帧时按最后一帧保持），返回写入 output 的字节数。</summary>
    public int Flush(Span<byte> output)
    {
        int written = Drain(output, final: true);
        _head = 0;
        _tail = 0;
        _acc = 0;
        return written;
    }

    private void Append(ReadOnlySpan<float> input)
    {
        if (_tail + input.Length > _buf.Length)
        {
            // 头部已消费过半时先压实再扩容，避免长会话下 _buf 无界增长
            int live = _tail - _head;
            if (_head >= _buf.Length / 2)
            {
                Array.Copy(_buf, _head, _buf, 0, live);
                _head = 0;
                _tail = live;
            }

            if (_tail + input.Length > _buf.Length)
            {
                var grown = new float[Math.Max(_buf.Length * 2, _tail + input.Length)];
                Array.Copy(_buf, _head, grown, 0, live);
                _head = 0;
                _tail = live;
                _buf = grown;
            }
        }

        input.CopyTo(_buf.AsSpan(_tail));
        _tail += input.Length;
    }

    private int Drain(Span<byte> output, bool final)
    {
        int written = 0;
        while (written + 2 <= output.Length)
        {
            // frames 必须每轮重算：_head 随消费前进，用循环外的初值会越过缓冲读出已消费的旧数据
            int frames = (_tail - _head) / _ch;
            long frame = _acc / 16000;
            // 非终局必须拿得到 frame 与 frame+1 两帧才能插值；终局允许按最后一帧保持
            if (frame + 1 > frames - 1 && !(final && frame <= frames - 1))
            {
                break;
            }

            int frac = (int)(_acc % 16000);
            float a = MonoAt((int)frame);
            float b = frame + 1 < frames ? MonoAt((int)frame + 1) : a;
            float value = a + (b - a) * (frac / 16000f);
            short pcm = (short)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
            output[written] = (byte)pcm;
            output[written + 1] = (byte)((uint)pcm >> 8);
            written += 2;
            // 位置相位与消费指针分开记账：位置前进 srcRate/16000 帧，但插值只读过 frame 与
            // frame+1 两帧，缓冲只能消费到 frame+1——若按位置推进 head，48k 下每个输出会
            // 永久多消费 1 个样本（跳过的 frame+2 本不该丢，它是下一个输出的插值下限之一，
            // 下个位置 = frame+3 只需从 frame+1 起重算相位）。acc 同步减去同量保持相对性。
            _acc += _srcRate;
            _acc -= 16000 * (frame + 1);
            _head += (int)(frame + 1) * _ch;
        }

        return written;
    }

    private float MonoAt(int frame)
    {
        int offset = _head + frame * _ch;
        if (_ch == 1)
        {
            return _buf[offset];
        }

        // 多声道按平均下混：WASAPI 混音里左右声道能量大体相当，平均保住人声主能量
        float sum = 0f;
        for (int ch = 0; ch < _ch; ch++)
        {
            sum += _buf[offset + ch];
        }

        return sum / _ch;
    }
}
