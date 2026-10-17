using RinkoCamera.Core;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RinkoCamera.Infrastructure;

/// <summary>
/// Media Foundation sink-writer wrapper. Frames are supplied as BGRA32 and are
/// converted by the system H.264 MFT; audio is optional 48 kHz stereo PCM.
/// </summary>
public sealed class MediaFoundationMp4Encoder : IVideoEncoder
{
    private readonly object _gate = new();
    private IMFSinkWriter? _writer;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private bool _started;
    private bool _finalized;
    private int _sampleRate;
    private short _channels;
    private short _bitsPerSample;
    private long _audioBytesWritten;
    private int _width;
    private int _height;

    public Task StartAsync(string temporaryPath, RecordingOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started && !_finalized) throw new InvalidOperationException("编码器已启动。");
        _started = false;
        _finalized = false;
        _videoStream = -1;
        _audioStream = -1;
        _audioBytesWritten = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            _frameRate = Math.Clamp(options.FrameRate, 1, 120);
            _width = options.Width;
            _height = options.Height;
            using var attributes = MediaFactory.MFCreateAttributes(4);
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u).CheckError();
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
            _writer = MediaFactory.MFCreateSinkWriterFromURL(temporaryPath, null, attributes);

            using var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            output.Set(MediaTypeAttributeKeys.AvgBitrate, BitrateFor(options)).CheckError();
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
            MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)options.Width, (uint)options.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)options.FrameRate, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            _videoStream = _writer.AddStream(output);

            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            // System-memory MF RGB32 is bottom-up. VideoFrame is explicitly
            // top-down, so WriteFrameAsync reverses rows at this boundary.
            input.Set(MediaTypeAttributeKeys.DefaultStride, unchecked((uint)(-checked(_width * 4)))).CheckError();
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
            MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)options.Width, (uint)options.Height).CheckError();
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)options.FrameRate, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            _writer.SetInputMediaType(_videoStream, input, null);

            if (options.AudioMode != AudioSourceMode.Muted)
            {
                _sampleRate = 48000;
                _channels = 2;
                _bitsPerSample = 16;
                using var audioOutput = MediaFactory.MFCreateMediaType();
                audioOutput.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                audioOutput.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
                audioOutput.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_sampleRate).CheckError();
                audioOutput.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_channels).CheckError();
                audioOutput.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)_bitsPerSample).CheckError();
                audioOutput.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192000u / 8).CheckError();
                _audioStream = _writer.AddStream(audioOutput);

                using var audioInput = MediaFactory.MFCreateMediaType();
                audioInput.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_sampleRate).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_channels).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)_bitsPerSample).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(_channels * _bitsPerSample / 8)).CheckError();
                audioInput.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_sampleRate * _channels * _bitsPerSample / 8)).CheckError();
                _writer.SetInputMediaType(_audioStream, audioInput, null);
            }

            _writer.BeginWriting();
            _started = true;
            return Task.CompletedTask;
        }
        catch
        {
            _writer?.Dispose();
            _writer = null;
            MediaFactory.MFShutdown();
            throw;
        }
    }

    public ValueTask WriteFrameAsync(VideoFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureWritable();
            if (frame.Width != _width || frame.Height != _height)
                throw new RecordingException("视频帧尺寸与编码器设置不一致。");
            var rowBytes = checked(frame.Width * 4);
            if (frame.Stride < rowBytes || frame.Bgra32.LongLength < (long)frame.Stride * frame.Height)
                throw new RecordingException("视频帧像素缓冲区或行跨度无效。");
            var length = checked(frame.Height * rowBytes);
            var buffer = MediaFactory.MFCreateMemoryBuffer(length);
            try
            {
                buffer.Lock(out var pointer, out _, out _);
                try
                {
                    // Copy directly into the MF buffer, without allocating a
                    // second bitmap. Source row zero is the top of the image;
                    // MF RGB32 buffer row zero is the bottom of the image.
                    for (var row = 0; row < frame.Height; row++)
                        System.Runtime.InteropServices.Marshal.Copy(frame.Bgra32,
                            row * frame.Stride, pointer + (frame.Height - 1 - row) * rowBytes, rowBytes);
                }
                finally { buffer.Unlock(); }
                buffer.CurrentLength = length;
                var sample = MediaFactory.MFCreateSample();
                try
                {
                    sample.AddBuffer(buffer);
                    sample.SampleTime = frame.Timestamp100Ns;
                    sample.SampleDuration = TimeSpan.TicksPerSecond / Math.Max(1, _frameRate);
                    _writer!.WriteSample(_videoStream, sample);
                }
                finally { sample.Dispose(); }
            }
            finally { buffer.Dispose(); }
        }
        return ValueTask.CompletedTask;
    }

    private int _frameRate = 30;

    public ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm, long timestamp100Ns, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureWritable();
            if (_audioStream < 0 || pcm.IsEmpty) return ValueTask.CompletedTask;
            var bytes = pcm.ToArray();
            var buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
            try
            {
                buffer.Lock(out var pointer, out _, out _);
                try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { buffer.Unlock(); }
                buffer.CurrentLength = bytes.Length;
                var sample = MediaFactory.MFCreateSample();
                try
                {
                    sample.AddBuffer(buffer);
                    sample.SampleTime = timestamp100Ns;
                    var sampleCount = bytes.Length / Math.Max(1, _channels * _bitsPerSample / 8);
                    sample.SampleDuration = sampleCount * TimeSpan.TicksPerSecond / Math.Max(1, _sampleRate);
                    _writer!.WriteSample(_audioStream, sample);
                    _audioBytesWritten += bytes.Length;
                }
                finally { sample.Dispose(); }
            }
            finally { buffer.Dispose(); }
        }
        return ValueTask.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started || _finalized) return Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            try { _writer!.Finalize(); }
            finally
            {
                _finalized = true;
                _writer?.Dispose();
                _writer = null;
                MediaFactory.MFShutdown();
                _started = false;
            }
        }
        return Task.CompletedTask;
    }

    private void EnsureWritable()
    {
        if (!_started || _finalized || _writer is null) throw new InvalidOperationException("编码器尚未启动或已结束。");
    }

    private static uint BitrateFor(RecordingOptions options) => options.Quality switch
    {
        VideoQuality.Low => (uint)Math.Max(2_000_000, options.Width * options.Height),
        VideoQuality.High => (uint)Math.Max(12_000_000, options.Width * options.Height * 4),
        _ => (uint)Math.Max(6_000_000, options.Width * options.Height * 2)
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_started && !_finalized)
            {
                try { _writer?.Finalize(); } catch { }
                _writer?.Dispose();
                MediaFactory.MFShutdown();
            }
            _writer = null;
            _finalized = true;
            _started = false;
        }
        return ValueTask.CompletedTask;
    }
}
