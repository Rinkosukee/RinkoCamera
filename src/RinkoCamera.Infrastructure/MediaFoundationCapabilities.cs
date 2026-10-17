using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace RinkoCamera.Infrastructure;

public sealed record MediaFoundationCapabilityReport(
    bool IsAvailable,
    bool H264PathAvailable,
    bool AacPathAvailable,
    bool HardwareTransformsRequested,
    string Message);

public static class MediaFoundationCapabilities
{
    public static MediaFoundationCapabilityReport Probe()
    {
        var system = Environment.SystemDirectory;
        var mfPlatform = Path.Combine(system, "mfplat.dll");
        var mfReadWrite = Path.Combine(system, "mfreadwrite.dll");
        var available = File.Exists(mfPlatform) && File.Exists(mfReadWrite);
        if (!available)
            return new(false, false, false, false, "系统缺少 Media Foundation（mfplat.dll 或 mfreadwrite.dll），无法生成 MP4。请安装 Windows Media Foundation 功能。");

        try
        {
            MediaFactory.MFStartup(false).CheckError();
            // The sink writer performs the final MFT negotiation. Creating the
            // media types here catches a missing projection/runtime early.
            using var video = MediaFactory.MFCreateMediaType();
            video.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            video.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            using var audio = MediaFactory.MFCreateMediaType();
            audio.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            audio.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            return new(true, true, true, true, "Media Foundation H.264/AAC 路径可用；编码器将优先请求硬件变换，失败时由 Sink Writer 协商软件 MFT。");
        }
        catch (Exception ex)
        {
            return new(true, false, false, false, $"Media Foundation 已加载，但 H.264/AAC 编码器不可用：{ex.Message}");
        }
        finally
        {
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }
}
