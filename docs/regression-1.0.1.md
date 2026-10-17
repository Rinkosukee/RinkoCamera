# 1.0.1 修复验证记录

验证环境：当前 Windows 11 x64，.NET SDK 8.0.426。日期：2026-10-17。

## 修改内容

- `src/LocalRecorder.Core/Contracts.cs`：倒计时进度事件。
- `src/LocalRecorder.Core/Domain.cs`：明确帧为正行跨度、从上往下的 BGRA；允许准备中取消。
- `src/LocalRecorder.Infrastructure/MediaFoundationMp4Encoder.cs`：在 MF RGB32 输入边界反转行顺序，声明负默认行跨度，支持有行尾填充的输入，并验证尺寸与缓冲区。
- `src/LocalRecorder.Infrastructure/RecordingController.cs`：每秒发布倒计时，倒计时完成后启动编码与音频，取消倒计时不生成空视频；删除控制窗口隐藏回调。
- `src/LocalRecorder/MainViewModel.cs`：显示剩余秒数；修复停止后保存状态被“就绪”覆盖、打开文件/目录按钮未刷新。
- `src/LocalRecorder/MainWindow.xaml`：大号倒计时数字、持续可见的停止入口、状态换行。
- `src/LocalRecorder/MainWindow.xaml.cs`：删除隐藏/恢复窗口逻辑。
- `src/LocalRecorder/LocalRecorder.csproj`、`scripts/build-installer.ps1`：版本更新为 1.0.1。
- `tests/LocalRecorder.Tests/EncoderOrientationTests.cs`、`StabilityTests.cs`、`TestArtifacts.cs`：真实 MP4 编解码方向回归、倒计时与取消测试，以及测试产物目录。
- `README.md`、`docs/architecture.md`：更新行为说明及手工验证步骤。

## 构建与测试

初次还原受 NuGet 审计服务网络不可达影响（NU1900）。使用本地已缓存依赖完成还原，无持久配置更改：

```powershell
dotnet restore LocalRecorder.sln -p:NuGetAudit=false --ignore-failed-sources --nologo
dotnet build LocalRecorder.sln -c Release --no-restore --nologo
dotnet test LocalRecorder.sln -c Release --no-restore --nologo --logger 'console;verbosity=normal'
dotnet publish src/LocalRecorder/LocalRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -o artifacts/publish/win-x64 --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installer.ps1 -Version 1.0.1
```

最终构建：成功，0 警告、0 错误。测试：14/14 通过。便携发布与 WiX MSI 构建成功，EXE ProductVersion 为 1.0.1、FileVersion 为 1.0.1.0。本轮未安装 MSI，未重新执行干净系统安装/升级测试。

方向测试使用四个不同亮度的象限，分别测试紧密行跨度与额外 32 字节行填充。真实编码 H.264 MP4 后，使用 Media Foundation 解码为始终从上往下的 NV12，逐帧检查四个象限；两组各 12 帧均通过，首帧亮度值 TL=236、TR=154、BL=85、BR=16。

倒计时测试检查 3/2/1、相邻间隔至少 900ms、总等待至少 2900ms、倒计时期间编码器未启动。分别在剩余 3 秒和 1 秒时停止，状态恢复 Idle、显示清零，输出目录无视频与临时文件。

## 实际界面冒烟

1. Release 构建运行：看到大号倒计时；录制中控制窗口持续可见，点击停止后生成 `artifacts/regression/ui-smoke/Recording-20261017-154628.mp4`（约 26 秒）。
2. 发布的 self-contained 便携版运行：显示倒计时并进入录制，停止后显示保存路径；捕获 94 帧、编码 93 帧、队列丢帧计数 0。输出 `artifacts/regression/ui-smoke/Recording-20261017-154820.mp4`（约 7 秒，2,931,861 字节）。
3. 点击已启用的“打开文件”，Windows 媒体播放器能打开实际录屏；画面中的文字、控制窗口与任务栏均为正向。临时 MP4 已正常改名，没有残留 `.tmp.mp4`。
4. 测试采用全屏、1920×1080、30 FPS 设置、静音。本机 WGC 不可用时 UI 明确显示兼容捕获模式；这些短录制不构成持续 30/60 FPS 性能认证，也未覆盖本轮无改动的音频路径。原保存目录等用户设置已在验证后恢复。

## 交付位置

- 便携版：`artifacts/publish/win-x64/LocalRecorder.exe`（保留同目录全部依赖）。
- 安装包：`artifacts/installer/LocalRecorder-1.0.1-win-x64.msi`。

修复影响新录制的视频；已有倒置视频不会被自动改写。控制窗口不再自动隐藏；录制显示器时需要用户自行移动或最小化控制窗口，停止快捷键仍为 Ctrl+Shift+F10。
