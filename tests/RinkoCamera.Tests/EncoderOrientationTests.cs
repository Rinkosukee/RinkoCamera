using System.Runtime.InteropServices;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;
using Vortice.MediaFoundation;
using Xunit.Abstractions;

namespace RinkoCamera.Tests;

public sealed class EncoderOrientationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [Trait("Category", "WindowsMediaIntegration")]
    public async Task Mp4RoundTrip_PreservesTopBottomAndLeftRight(int paddingBytes)
    {
        const int width = 128, height = 96;
        var stride = width * 4 + paddingBytes;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Four asymmetric quadrants reveal both vertical and horizontal flips.
            var shade = (byte)(y < height / 2 ? x < width / 2 ? 255 : 160 : x < width / 2 ? 80 : 0);
            var offset = y * stride + x * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
            pixels[offset + 3] = 255;
        }

        var directory = TestArtifacts.CreateDirectory("orientation");
        var path = Path.Combine(directory, $"quadrants-padding-{paddingBytes}.mp4");
        await using (var encoder = new MediaFoundationMp4Encoder())
        {
            await encoder.StartAsync(path, new RecordingOptions
            {
                Width = width, Height = height, FrameRate = 24, AudioMode = AudioSourceMode.Muted
            }, CancellationToken.None);
            for (var i = 0; i < 12; i++)
                await encoder.WriteFrameAsync(new VideoFrame(width, height, stride, pixels,
                    i * TimeSpan.TicksPerSecond / 24), CancellationToken.None);
            await encoder.FinalizeAsync(CancellationToken.None);
        }

        MediaFactory.MFStartup(false).CheckError();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            // NV12 is always top-down. Reading RGB32 here would reintroduce a
            // bottom-up ambiguity and could mask an inverted encoded picture.
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            var decodedFrames = 0;
            while (decodedFrames < 24)
            {
                using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                    SourceReaderControlFlag.None, out _, out var flags, out _);
                if (flags.HasFlag(SourceReaderFlag.EndOfStream)) break;
                if (sample is null) continue;
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var pointer, out _, out var length);
                try
                {
                    Assert.True(length >= width * height * 3 / 2);
                    var luma = new byte[width * height];
                    Marshal.Copy(pointer, luma, 0, luma.Length);
                    // Limited-range luma: white ~235, light gray ~154,
                    // dark gray ~85, black ~16 (allow encoder rounding).
                    var topLeft = luma[height / 4 * width + width / 4];
                    var topRight = luma[height / 4 * width + width * 3 / 4];
                    var bottomLeft = luma[height * 3 / 4 * width + width / 4];
                    var bottomRight = luma[height * 3 / 4 * width + width * 3 / 4];
                    Assert.True(topLeft > topRight + 40 && topRight > bottomLeft + 40 && bottomLeft > bottomRight + 40,
                        $"Wrong decoded orientation: TL={topLeft}, TR={topRight}, BL={bottomLeft}, BR={bottomRight}");
                    if (decodedFrames == 0)
                        output.WriteLine($"Decoded NV12 luma TL={topLeft}, TR={topRight}, BL={bottomLeft}, BR={bottomRight}");
                }
                finally { buffer.Unlock(); }
                decodedFrames++;
            }
            Assert.Equal(12, decodedFrames);
            output.WriteLine($"Decoded all {decodedFrames} MP4 frames successfully: {path}");
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }
}
