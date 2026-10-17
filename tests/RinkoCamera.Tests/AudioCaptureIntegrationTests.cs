using System.Runtime.InteropServices;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vortice.MediaFoundation;
using Xunit.Abstractions;
using DataFlow = NAudio.CoreAudioApi.DataFlow;
using Role = NAudio.CoreAudioApi.Role;

namespace RinkoCamera.Tests;

[Collection("Desktop capture")]
public sealed class AudioCaptureIntegrationTests(ITestOutputHelper output)
{
    [DesktopTheory]
    [InlineData(AudioSourceMode.System)]
    [InlineData(AudioSourceMode.SystemAndMicrophone)]
    [InlineData(AudioSourceMode.Microphone)]
    public async Task RealWasapiToMp4_HasDecodableAacAndAlignedDuration(AudioSourceMode mode)
    {
        var directory = TestArtifacts.CreateDirectory($"audio-{mode}");
        await using var audio = new NaudioCaptureSource();
        await using var encoder = new MediaFoundationMp4Encoder();
        var capture = new WindowsGraphicsCaptureSource();
        await using var controller = new RecordingController(capture, audio, encoder, new LocalFileStorage(), new TestLogger(output));
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var playback = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        playback.Init(new SignalGenerator(48000, 2) { Gain = 0.08, Frequency = 660, Type = SignalGeneratorType.Sin });
        var errors = new List<string>();
        controller.ErrorRaised += (_, error) => { lock (errors) errors.Add(error); };
        await controller.StartAsync(new RecordingOptions
        {
            Target = new(CaptureTargetKind.FullScreen, "primary", "Primary"), AudioMode = mode,
            Width = 640, Height = 360, FrameRate = 30, CountdownSeconds = 0, OutputDirectory = directory
        }, CancellationToken.None);
        for (var i = 0; i < 100 && controller.State == RecordingState.Preparing; i++) await Task.Delay(50);
        Assert.Equal(RecordingState.Recording, controller.State);
        playback.Play();
        await Task.Delay(2200);
        await controller.PauseAsync(CancellationToken.None);
        await Task.Delay(500);
        await controller.ResumeAsync(CancellationToken.None);
        await Task.Delay(1200);
        var result = await controller.StopAsync(CancellationToken.None);
        playback.Stop();
        Assert.NotNull(result);
        Assert.Equal(RecordingState.Idle, controller.State);
        Assert.True(result.Metrics!.AudioSamples >= 48000 * 3);
        var decoded = DecodeAudio(result.FilePath);
        Assert.InRange(decoded.Duration.TotalSeconds, result.Duration.TotalSeconds - 0.20, result.Duration.TotalSeconds + 0.20);
        if (mode != AudioSourceMode.Microphone) Assert.True(decoded.Peak > 0.005, $"Loopback was silent: peak={decoded.Peak}");
        output.WriteLine($"Mode={mode}, samples={result.Metrics.AudioSamples}, decoded={decoded.Duration.TotalSeconds:F3}s, clock={result.Duration.TotalSeconds:F3}s, peak={decoded.Peak:F4}, path={result.FilePath}");
        foreach (var error in errors) output.WriteLine(error);
    }

    public static (TimeSpan Duration, double Peak) DecodeAudio(string path)
    {
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u).CheckError();
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, type);
            long bytes = 0; double peak = 0;
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
                    bytes += length; peak = Math.Max(peak, PcmAudio.Peak(pcm));
                }
                finally { buffer.Unlock(); }
            }
            Assert.True(bytes > 0, "MP4 did not contain decodable PCM audio.");
            return (TimeSpan.FromSeconds(bytes / (48000d * 4)), peak);
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }

    private sealed class TestLogger(ITestOutputHelper output) : IAppLogger
    {
        public void Info(string message) => output.WriteLine(message);
        public void Error(string message, Exception? exception = null) => output.WriteLine($"{message}: {exception}");
    }
}

public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOCALRECORDER_DESKTOP_TESTS") != "1")
            Skip = "Set LOCALRECORDER_DESKTOP_TESTS=1 to run real WASAPI playback/capture tests.";
    }
}
