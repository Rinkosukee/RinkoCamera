# 1.0.2 声音与指定窗口修复

日期：2026-10-17。验证环境：当前 Windows 11 x64、.NET SDK 8.0.426、Windows SDK 10.0.26100.86。

## 问题与实现

1. 当前用户配置的 `AudioMode=0` 是静音，旧界面只显示英文 `Muted`。改为明确的中文选项及静音提示；新配置默认系统声音。本机已在备份后将原配置的声音选项设为系统声音，原保存目录 原本的用户保存目录 等设置保留。
2. 旧音频转换未处理部分 Extensible/PCM24/PCM32 格式，也没有正确约束采样率。现在 WASAPI 共享模式请求 48 kHz 双声道浮点，让 Windows 负责设备格式转换/重采样；随后显式转换为编码器要求的 PCM16。不支持的格式和不完整数据包会报错。
3. 旧混音必须同时等到系统声音与麦克风的数据，系统未播放声音时可能阻塞麦克风输出。现在每路使用最多两秒的缓冲区，工作线程每 10ms 混音；缺少数据的通道贡献静音，另一通道仍然输出。输出设备可选并保存。录制中显示电平；整段没有信号时保存后明确提示。
4. 旧窗口校验错误地把原生 HWND 当作 `Windows.UI.WindowId`。另有 WinRT/COM 投影造成的 ABI 问题。现在窗口和显示器统一通过 `IGraphicsCaptureItemInterop.CreateForWindow/CreateForMonitor` 创建，纹理接口从原生 WinRT ABI 查询。实际 WGC 路径已经工作，指定窗口不会退化为复制被遮挡的屏幕矩形。
5. 窗口大小变化重建帧池；静态窗口复用已有像素并补充时间戳，避免录制长时间静态画面却只有极短视频。关闭、最小化和已知显示保护明确失败。新增刷新窗口入口；记住窗口标题，失效时提示重选，不自动选另一个无关窗口。
6. 音视频从首个视频帧就绪时启动，暂停停止媒体时钟；音频样本序号和视频活动时钟不包含暂停。异步收尾使用 SemaphoreSlim 串行化。首帧前取消不提交空文件；编码器收尾失败不再伪报保存成功。

捕获模式有独立显示，不再被“录制中”状态覆盖；较小电平显示一位小数。

## 修改文件

- `src/LocalRecorder.Core/Contracts.cs`、`Domain.cs`：设备/目标配置、音频电平事件及峰值统计。
- `src/LocalRecorder.Infrastructure/GraphicsCaptureInterop.cs`（新增）、`Direct3D11WgcCapture.cs`、`WindowsGraphicsCaptureSource.cs`：原生互操作、帧池、窗口状态、模式事件与静态帧处理。
- `src/LocalRecorder.Infrastructure/PcmAudio.cs`（新增）、`NaudioCaptureSource.cs`：格式转换、固定节拍混音、设备选择、缓冲限制及清理。
- `src/LocalRecorder.Infrastructure/RecordingController.cs`：同步时钟、电平、失败/取消与 MP4 收尾。
- `src/LocalRecorder.Infrastructure/MediaFoundationMp4Encoder.cs`：及时释放媒体类型和属性对象。
- `src/LocalRecorder/MainViewModel.cs`、`MainWindow.xaml`：中文声音选项、设备选择、电平、刷新窗口、独立捕获模式和目标恢复。
- `src/LocalRecorder/LocalRecorder.csproj`、`scripts/build-installer.ps1`：版本 1.0.2。
- `tests/LocalRecorder.Tests/PcmAudioTests.cs`、`AudioCaptureIntegrationTests.cs`、`WindowCaptureIntegrationTests.cs`（新增）：转换、混音和真实桌面/音频测试。
- `tests/LocalRecorder.Tests/StabilityTests.cs`、`RecordingTests.cs`、`LocalRecorder.Tests.csproj`：空录制/收尾错误、配置保存和测试窗口依赖。
- `tools/LocalRecorder.MediaInspect/LocalRecorder.MediaInspect.csproj`、`Program.cs`（新增）：纯本地 Media Foundation 解码检查工具，输出音视频统计及首帧灰度缩略图。
- `README.md`、`docs/architecture.md`、本记录：使用方法、实现与验证说明。

## 构建、测试、发布命令

```powershell
dotnet build LocalRecorder.sln -c Release --no-restore --nologo
$env:LOCALRECORDER_DESKTOP_TESTS = '1'
dotnet test LocalRecorder.sln -c Release --no-restore --nologo --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=final-1.0.2.trx' --results-directory artifacts/regression/1.0.2
dotnet publish src/LocalRecorder/LocalRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -o artifacts/publish/win-x64 --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installer.ps1 -Version 1.0.2
```

最终构建 0 警告、0 错误；含桌面硬件测试共 27 项通过。未设置环境变量时，真实桌面/WASAPI 测试会明确跳过；本机验证启用了这些测试。测试结果在 `artifacts/regression/1.0.2/final-1.0.2.trx`。便携版及 WiX MSI 已构建，本轮未安装 MSI 或执行干净 Windows 环境的升级测试。

真实测试覆盖：WGC 指定窗口的非对称画面、遮挡、移动、缩放、关闭；最小化和已知显示保护拒绝；两次启动显示器捕获并分别报告 WGC 模式；系统声音、麦克风、混音三种模式输出 AAC MP4，均可解码且有非零信号。每种音频模式均执行暂停/继续，解码音频长度与媒体时钟误差不超过测试阈值 200ms。原倒计时、方向、配置、文件命名和错误测试继续通过。

## 实际界面录制与解码

在发布的 self-contained 便携版中选择中文“系统声音”，刷新并选择 `window-target.txt - Notepad`；倒数后录制，播放本地已验证的测试音视频，再点击停止。控制窗口始终可用，目标窗口被其他窗口遮挡仍能独立录制。

文件：`artifacts/regression/1.0.2/ui-smoke/Recording-20261017-160605.mp4`，6,751,415 字节。日志：841 个编码输入视频帧、2,346,720 个 PCM 双声道采样帧、峰值 0.0035、媒体时钟 48.957 秒。MP4 的固定帧率输出可以补帧，因此解码帧数与编码输入计数不同。

实际解码命令：

```powershell
dotnet run --project tools/LocalRecorder.MediaInspect -c Release -p:NuGetAudit=false -- artifacts/regression/1.0.2/ui-smoke/Recording-20261017-160605.mp4 artifacts/regression/1.0.2/ui-smoke/decoded-window.png
```

结果：1,467 个视频解码帧、视频 48.9226665 秒、音频 2,347,008 个双声道采样帧（48.896 秒）、解码峰值 0.003540。音视频末端时长差约 27ms。已查看从 MP4 解码的缩略图，内容确实只有指定的记事本窗口且文字正向。缩略图为灰度诊断图；原 MP4 为彩色视频。临时 MP4 已正确改名，没有残留 `.tmp.mp4`。

## 交付与限制

- 便携版：`artifacts/publish/win-x64/LocalRecorder.exe`，需要保留同目录依赖。
- 安装包：`artifacts/installer/LocalRecorder-1.0.2-win-x64.msi`。
- 配置：`%LOCALAPPDATA%/LocalRecorder/settings.json`；本轮验证前备份在 `artifacts/regression/1.0.2/settings-before-ui.json`。

窗口请先还原再刷新选择。DRM/受保护内容仍不能录制；只检测可获取的保护标志和捕获失败，不绕过保护，也不能保证识别所有应用产生的黑帧。系统音频是所选播放设备的声音，不是指定进程独占音频。独占音频、应用另选的播放设备、系统隐私拒绝和硬件静音仍可能限制采集。本轮真实测试未物理拔插设备、未验证所有声卡/应用，也未完成 4K60/长时间性能认证。GPU NV12 零拷贝与多屏拼接仍属后续工程。

所有捕获、编码、诊断都本地运行；未加入 FFmpeg、云服务或遥测。
