using System.Buffers.Binary;
using RinkoCamera.Core;
using NAudio.Wave;

namespace RinkoCamera.Infrastructure;

public static class PcmAudio
{
    public static readonly WaveFormat Format = new(48000, 16, 2);
    public const int ChunkBytes = 480 * 4; // 10 ms, 48 kHz stereo PCM16

    public static byte[] ConvertToStereo16(WaveFormat format, ReadOnlySpan<byte> source)
    {
        if (format is WaveFormatExtensible extensible) format = extensible.ToStandardWaveFormat();
        if (format.SampleRate != 48000)
            throw new RecordingException($"音频设备返回 {format.SampleRate} Hz，无法与 48000 Hz 编码同步。请重新选择设备。");
        if (format.Channels is < 1 or > 8 ||
            !(format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32 ||
              format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            throw new RecordingException($"不支持音频设备格式：{format}。请使用共享模式 PCM 或浮点格式。");
        if (source.Length % format.BlockAlign != 0) throw new RecordingException("音频设备返回了不完整的采样帧。");
        var frames = source.Length / format.BlockAlign;
        var result = new byte[frames * 4];
        for (var frame = 0; frame < frames; frame++)
        for (var channel = 0; channel < 2; channel++)
        {
            var offset = frame * format.BlockAlign + Math.Min(channel, format.Channels - 1) * (format.BitsPerSample / 8);
            var input = source[offset..];
            int value;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(input));
                value = float.IsFinite(sample) ? (int)(Math.Clamp(sample, -1, 1) * short.MaxValue) : 0;
            }
            else value = format.BitsPerSample switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(input),
                24 => (input[0] | input[1] << 8 | (sbyte)input[2] << 16) >> 8,
                _ => BinaryPrimitives.ReadInt32LittleEndian(input) >> 16
            };
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(frame * 4 + channel * 2), (short)value);
        }
        return result;
    }

    public static void Mix(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> output)
    {
        for (var i = 0; i < output.Length; i += 2)
        {
            var sum = BinaryPrimitives.ReadInt16LittleEndian(left[i..]) + BinaryPrimitives.ReadInt16LittleEndian(right[i..]);
            BinaryPrimitives.WriteInt16LittleEndian(output[i..], (short)Math.Clamp(sum, short.MinValue, short.MaxValue));
        }
    }

    public static double Peak(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        return peak / 32768d;
    }
}
