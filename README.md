# RinkoCamera · TSUKI Edition

<img src="src/RinkoCamera/Assets/tsuki-icon.png" width="88" alt="TSUKI / RinkoCamera icon" />

一款完全本地运行的 Windows 录屏软件，带有 TSUKI 原创角色主题。
无需账号，不上传录制内容，不含遥测。支持窗口、显示器和主屏全屏录制，
系统声音与麦克风、暂停与继续、可见倒计时，以及 MP4 输出。

## 下载 / Downloads

**[前往最新版本](https://github.com/Rinkosukee/RinkoCamera/releases/latest)**

| 文件 | 用途 |
| --- | --- |
| `RinkoCamera-1.0.7-Setup.exe` | 推荐的带角色图标离线安装程序 |
| `RinkoCamera-1.0.7-win-x64.msi` | Windows Installer 安装包 |
| `RinkoCamera-1.0.7-win-x64-portable.zip` | 解压整个文件夹，运行 `RinkoCamera.exe`；无需另装 .NET |
| `RinkoCamera-1.0.7-source.zip` | 对应发布版本的完整应用源码、测试和构建脚本 |
| `SHA256SUMS.txt` | 文件 SHA-256 校验值 |
| `wix-v5.0.2-source.tar.gz` | 安装引导器使用的未修改 WiX 组件对应上游源码 |

运行要求：Windows 10 2004（19041）及以上 / Windows 11 x64，带可用的
Media Foundation H.264/AAC 编码器和音频设备。Windows N 版本需要系统媒体组件。
安装在当前用户的 `%LOCALAPPDATA%\RinkoCamera`，无需管理员权限。
发布文件目前未做代码签名，安装与升级尚未在干净 Windows 虚拟机中验证。

配置与日志：`%LOCALAPPDATA%\RinkoCamera\settings.json` 和 `logs\recorder.log`。
首次使用新名称时，若没有新配置，会回读旧版 LocalRecorder 的配置；下一次
录制将偏好写入 RinkoCamera 配置，不改写旧配置。录制文件仍保存到你选择的目录。

1. 选择录制目标和声音来源，确认播放设备与实际发声设备一致。
2. 选择分辨率、帧率、质量、倒计时和保存目录，点击「开始录制」。
3. 用固定面板中的暂停、继续、停止按钮控制；全局开始/停止快捷键为 `Ctrl+Shift+F10`。
4. 停止后可打开视频或所在目录；临时 MP4 只有成功收尾后才会改名。

### 当前限制

- 24/30/60 FPS 是目标设置，尚未认证持续 1080p60 / 1440p60 / 4K60；GPU NV12 零拷贝路径尚未完成。
- 全屏指主显示器，不会合并多显示器。控制窗口不会自动隐藏，可以手动最小化。
- 最小化窗口及受保护内容不支持；检测到时会提示并安全失败，不提供绕过保护的功能。
- 全局快捷键当前固定为 Ctrl+Shift+F10，尚未提供自定义编辑界面。
- 暂无区域、摄像头画中画、托盘、计划录制及 MKV/WebM；不捆绑 FFmpeg。

代码与文档采用 [MIT](LICENSE.txt)；TSUKI 角色素材保留作者版权，
按 [素材许可](ARTWORK-LICENSE.md) 随项目分发；[第三方声明](THIRD-PARTY-NOTICES.md)
随源码和软件一起提供。欢迎阅读 [贡献说明](CONTRIBUTING.md)。

## English / engineering notes

RinkoCamera is a Windows 10/11 x64 WPF screen recorder. It keeps capture, audio, encoding, storage, settings, hotkeys, and logging in separate components and writes an MP4 file through Media Foundation.

## Build

Requirements: .NET 8 SDK, Windows 10 version 2004 (build 19041) or later, and a Windows SDK. Restore and build with:

    dotnet restore .\RinkoCamera.sln
    dotnet build .\RinkoCamera.sln -c Release
    dotnet test .\tests\RinkoCamera.Tests\RinkoCamera.Tests.csproj -c Release

The tested environment is Windows 11 x64, .NET SDK 8.0.426, and Windows SDK 10.0.26100.86.

## Run and publish

    dotnet run --project .\src\RinkoCamera\RinkoCamera.csproj -c Release
    .\scripts\publish.ps1

The self-contained win-x64 output is placed under artifacts\publish\RinkoCamera\win-x64.

Build the per-user MSI installer with:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1

The MSI is written to `artifacts\installer\RinkoCamera-1.0.7-win-x64.msi`; the branded offline installer is `artifacts\installer\RinkoCamera-1.0.7-Setup.exe`. It installs under `%LOCALAPPDATA%\RinkoCamera`, creates a desktop shortcut, registers an uninstall entry, and does not require administrator elevation.

## Use

Version 1.0.7 renames the product, solution, projects, namespaces, executable,
shortcut and installers to RinkoCamera. It reads legacy LocalRecorder settings
when no new profile exists. Standard tests pass locally; optional desktop
hardware tests are not enabled for this release verification.

Version 1.0.6 uses the TSUKI character theme and application icon, with the sidebar greeting `(｡･ω･｡)ﾉ♡`. The portable app, MSI, and branded Setup are built from the same published files.

Version 1.0.4 fixes a crash during exit: closing work is posted after WPF's initial `Closing` callback returns, native recorder cleanup runs off the dispatcher, and repeated close requests wait for the same cleanup. Active recordings finalize before the window exits. See [the shutdown verification record](docs/regression-1.0.4.md).

Version 1.0.3 aligns text vertically in all text boxes, combo boxes, dropdown rows, and buttons. Shared WPF styles provide consistent padding; layout rounding keeps control edges aligned at display scaling boundaries. See [the UI verification record](docs/regression-1.0.3.md).

Choose a window, monitor, or primary-display full screen; choose system, microphone, both audio, or explicitly muted; set resolution, 24/30/60 FPS, quality, countdown, and output directory. Click **刷新窗口** after opening or restoring a target application, then select **窗口 · title**. Keep the target restored; it may be covered by other windows. The Chinese sound selector distinguishes **系统声音**, **麦克风**, **系统声音＋麦克风**, and **静音（不录声音）**. Select the headphones/speakers on which the source audio is playing; recording a specific window still captures the selected endpoint's system audio, not only that application's audio. The level meter shows incoming audio during recording; silence throughout the recording produces a clear notice after saving. New profiles default to system sound; existing preferences are restored.

Start, pause, resume, and stop from the main window. Ctrl+Shift+F10 is the default global stop/start hotkey. The countdown visibly displays 3, 2, 1 (or five seconds when selected), and audio/video encoding starts only after it finishes. Stop also cancels a countdown without creating an empty recording. The control window stays visible during recording; automatic hiding has been removed. For monitor/full-screen capture, move the controls away or minimize them yourself if you do not want them in the video; Ctrl+Shift+F10 remains available to stop. A recording is first written to *.tmp.mp4 and renamed only after Media Foundation finalizes it.

### 1.0.2 audio/window regression checks

27 tests passed on the current interactive Windows desktop, including real WGC window capture through occlusion/resize, closure, minimized/protected-window rejection, and WASAPI-to-AAC MP4 recording in all three audio modes. A separate UI recording captured only a specified Notepad window: decoded video duration 48.923 s, decoded audio duration 48.896 s, 2,347,008 decoded stereo audio frames, and nonzero audio peak 0.00354. See [the detailed verification record](docs/regression-1.0.2.md). This is a functional check, not a high-resolution 60 FPS certification.

Hardware tests are opt-in because they open test windows and play/capture a quiet local tone. Run them on an interactive Windows desktop with audio devices:

```powershell
$env:LOCALRECORDER_DESKTOP_TESTS = '1'
dotnet test RinkoCamera.sln -c Release --logger 'console;verbosity=detailed'
```

### 1.0.1 regression checks

The encoder now converts the capture sources' top-down BGRA rows to Media Foundation's bottom-up system-memory RGB32 layout, including padded source strides. Two real H.264/MP4 encode/decode tests verify all four asymmetric image quadrants across 12 decoded frames each. Countdown tests verify the 3/2/1 sequence, one-second intervals, deferred encoder start, and cancellation at both 3 and 1 seconds without output files. UI smoke checks cover the visible countdown and recording/stop controls.

## Troubleshooting

- If capture is unavailable, update Windows and the display driver and ensure Graphics Capture is supported.
- If a video has no sound, ensure the source is not **静音**, select the playback endpoint actually used by the source app, and check the meter while sound is playing. Check microphone mute and Windows permissions when using microphone capture. The app does not change device volumes or privacy settings.
- If saving fails, choose a writable directory with at least 256 MB free.
- If the hotkey is unavailable, another application may already own Ctrl+Shift+F10.
- Protected content, minimized windows, device removal, and encoder failures are reported as errors and do not produce a success result.

## Architecture and limits

The state machine is Idle, Preparing, Recording, Paused, Stopping, and Error. All long-running work accepts cancellation tokens and runs off the WPF UI thread. Media Foundation and NAudio are Windows-local dependencies; no cloud service, browser MediaRecorder, upload, or telemetry is used. Window, monitor, and primary-display full-screen targets share a Windows.Graphics.Capture + Direct3D11 frame pool. The native HWND/HMONITOR factory and WinRT surface ABI queries were corrected in 1.0.2 and now pass real capture tests on this machine. A bounded queue and reusable staging texture feed the existing BGRA encoder boundary. Static windows receive repeated timed frames. Window capture fails explicitly if WGC is unavailable; it never substitutes a screen-region copy of an obscured window. Monitor/full-screen capture can use an explicitly reported compatibility path when WGC is unsupported. Multi-monitor virtual-screen compositing and GPU NV12 zero-copy are still pending.

### Performance smoke results

These are historical 1.0.0 short runs on the current Windows 11 x64 machine, before the 1.0.2 WinRT interop correction (WGC initialization incorrectly fell back to `CompatibilityGdi`). They do not describe current WGC performance. CPU is process CPU averaged over the run; GPU is the maximum sampled `GPU Engine` counter; values are diagnostic, not a certification for every GPU.

| Output | Duration | CPU | GPU max | Captured / encoded / dropped | Result |
|---|---:|---:|---:|---:|---|
| 1920×1080 @ 60 | 5 s | 3.7% | 3.08% | 82 / 81 / 0 | MP4 finalized |
| 2560×1440 @ 60 | 3 s | 6.6% | 3.41% | 21 / 21 / 0 | MP4 finalized |
| 3840×2160 @ 60 | 3 s | 11.5% | 3.04% | 13 / 13 / 0 | MP4 finalized |

The low encoded frame counts in the 1440p/4K rows reflect the compatibility path's CPU readback and short test window. These rows are evidence of safe finalization and metrics collection, not a claim of sustained 60 FPS.

FFmpeg is not bundled or required. Media Foundation uses Windows' H.264/AAC MFTs; the startup probe reports missing `mfplat.dll`/`mfreadwrite.dll` before recording. Sink Writer requests hardware transforms and falls back to a software MFT when Windows can negotiate one. No FFmpeg fallback is enabled, so there is no additional FFmpeg license or process to package.

## Manual smoke test

1. Launch the Release build.
2. Select a visible window, start a three-second countdown, verify the visible 3/2/1 sequence, record for 10 seconds, stop, and open the MP4. Verify text and asymmetric objects are upright. Also cancel during countdown and check that no MP4 or temporary file is created.
3. Repeat with a monitor and full screen; verify the control window stays visible and its Stop button works during recording. Move or minimize it manually when it should be outside the recording.
4. Test mute, system audio, microphone, and both audio modes.
5. Pause for five seconds, resume, stop, and seek through the output.
6. Press Ctrl+Shift+F10 to start/stop; click Start or Stop repeatedly and verify no duplicate files or hanging process remains.
7. Use an invalid directory and a nearly full drive to verify a clear error.
8. Disable or unplug the active microphone/output device during recording; verify a clear fault, safe `Finalize`, and an atomically saved partial MP4.
9. Minimize, move, resize, cover, and close the target window; verify minimized/closed targets are reported and no black “success” file is produced.

## License

The application code is MIT licensed; TSUKI character artwork has the separate permission in ARTWORK-LICENSE.md. Third-party packages retain their own licenses. Full notices are shipped in licenses/ and summarized in THIRD-PARTY-NOTICES.md. The unchanged WiX bootstrapper uses MS-RL; its source archive accompanies each Setup release. Windows Media Foundation and Windows.Graphics.Capture are supplied by Windows and are not redistributed by this project.
