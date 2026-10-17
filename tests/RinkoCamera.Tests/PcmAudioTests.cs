using System.Buffers.Binary;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;
using NAudio.Wave;

namespace RinkoCamera.Tests;

public sealed class PcmAudioTests
{
    [Fact]
    public void ExtensibleFloat_RemainsAudibleAndKeepsChannels()
    {
        var input = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(input, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(4), -0.25f);
        var pcm = PcmAudio.ConvertToStereo16(new WaveFormatExtensible(48000, 32, 2), input);
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(pcm), 16000, 17000);
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2)), -8300, -8100);
    }

    [Fact]
    public void Mono24Bit_DuplicatesToStereo()
    {
        var pcm = PcmAudio.ConvertToStereo16(new WaveFormat(48000, 24, 1), new byte[] { 0, 0, 64, 0, 0, 192 });
        Assert.Equal(new short[] { 16384, 16384, -16384, -16384 },
            Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2))));
    }

    [Fact]
    public void MixingSilentEndpoint_DoesNotDiscardOtherEndpoint()
    {
        var left = new byte[PcmAudio.ChunkBytes];
        for (var i = 0; i < left.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i), 12000);
        var output = new byte[left.Length];
        PcmAudio.Mix(left, new byte[left.Length], output);
        Assert.Equal(left, output);
        Assert.InRange(PcmAudio.Peak(output), 0.36, 0.37);
    }

    [Fact]
    public void DifferentSampleRate_FailsClearlyInsteadOfMislabelingAudio()
        => Assert.Throws<RecordingException>(() => PcmAudio.ConvertToStereo16(new WaveFormat(44100, 16, 2), new byte[4]));

    [Fact]
    public void InvalidPacket_FailsClearly()
        => Assert.Throws<RecordingException>(() => PcmAudio.ConvertToStereo16(new WaveFormat(48000, 16, 2), new byte[3]));
}
