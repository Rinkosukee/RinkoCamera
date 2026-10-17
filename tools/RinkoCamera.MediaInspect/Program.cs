using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using RinkoCamera.Infrastructure;
using Vortice.MediaFoundation;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: RinkoCamera.MediaInspect recording.mp4 thumbnail.png");
    return 2;
}
MediaFactory.MFStartup(false).CheckError();
try
{
    var path = Path.GetFullPath(args[0]);
    long videoFrames = 0, videoEnd = 0;
    using (var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null))
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var dimensions = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        var width = (int)(dimensions >> 32); var height = (int)(dimensions & uint.MaxValue);
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
            if (flags.HasFlag(SourceReaderFlag.EndOfStream)) break;
            if (sample is null) continue;
            videoEnd = timestamp + sample.SampleDuration;
            if (videoFrames++ != 0) continue;
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var pointer, out _, out var length);
            try
            {
                var luma = new byte[width * height]; Marshal.Copy(pointer, luma, 0, luma.Length);
                using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var pixels = new byte[width * height * 4];
                for (var i = 0; i < luma.Length; i++)
                {
                    var shade = (byte)Math.Clamp((luma[i] - 16) * 255 / 219, 0, 255);
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = shade; pixels[i * 4 + 3] = 255;
                }
                var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { for (var row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4); }
                finally { image.UnlockBits(data); }
                image.Save(Path.GetFullPath(args[1]), ImageFormat.Png);
            }
            finally { buffer.Unlock(); }
        }
    }
    long audioBytes = 0; double peak = 0;
    using (var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null))
    {
        using var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
        type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u).CheckError();
        type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
        type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
        reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if (flags.HasFlag(SourceReaderFlag.EndOfStream)) break;
            if (sample is null) continue;
            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var pointer, out _, out var length);
            try
            {
                var pcm = new byte[length]; Marshal.Copy(pointer, pcm, 0, length);
                audioBytes += length; peak = Math.Max(peak, PcmAudio.Peak(pcm));
            }
            finally { buffer.Unlock(); }
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { path, videoFrames, videoSeconds = videoEnd / 10000000d,
        audioSamples = audioBytes / 4, audioSeconds = audioBytes / 192000d, audioPeak = peak, thumbnail = Path.GetFullPath(args[1]) }, new JsonSerializerOptions { WriteIndented = true }));
    if (videoFrames == 0 || audioBytes == 0) return 1;
    return 0;
}
finally { MediaFactory.MFShutdown().CheckError(); }
