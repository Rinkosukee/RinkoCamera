# 1.0.4 关闭卡顿修复

日期：2026-10-17。环境：当前 Windows 11 x64，.NET SDK 8.0.426。

## 原因与修改

Windows Application 日志在 1.0.2、1.0.3 的关闭操作中多次记录事件 1026 和 1000。异常为 `System.InvalidOperationException`：窗口正在关闭时再次调用 Close。堆栈定位在旧 `MainWindow.OnClosing` 第 58 行。旧代码先取消关闭，再 await 资源清理，最后立即 Close；空闲时清理可能同步完成，于是在第一次 Closing 尚未返回时重入 Close，造成未处理异常。之前只确认窗口消失，没有检查正常退出码，因此遗漏了该缺陷。

- 新增 `src/LocalRecorder/WindowCloseCoordinator.cs`：先取消当前关闭请求，使用 Dispatcher 排队执行清理；只有清理完成后才允许一次最终 Close。连续点击 X/Alt+F4 不重复执行清理，也不会绕过正在收尾的 MP4。清理错误被捕获并反馈，允许重试。
- `src/LocalRecorder/MainWindow.xaml.cs`：使用协调器替代 async void Closing；跟踪初始化任务，关闭时先取消并等待初始化；记录版本、PID 和资源清理耗时。
- `src/LocalRecorder/MainViewModel.cs`：关闭时禁用录制命令、注销 UI 线程所属的热键；阻止待完成的设置保存再启动录制；把录制控制器、WASAPI 和 MF 的清理放到工作线程，保留“正在关闭：停止录制并保存视频…”状态。
- 新增 `tests/LocalRecorder.Tests/WindowCloseTests.cs`：在真实 WPF STA Dispatcher 中验证同步完成的清理不会重入关闭、重复关闭只执行一次并保持消息循环响应、清理错误不变成未处理异常且可以重试。
- `tests/LocalRecorder.Tests/LocalRecorder.Tests.csproj`：加入 WPF 及应用项目引用，以测试实际关闭协调器。
- 新增 `scripts/measure-exit.ps1`：只观察指定进程的退出时间和退出码；不发送关闭消息，不强行结束进程。关闭由正常界面操作执行。
- `src/LocalRecorder/LocalRecorder.csproj`、`scripts/build-installer.ps1`、`README.md`：更新版本与发布说明。

## 构建和测试

```powershell
dotnet build LocalRecorder.sln -c Release --no-restore --nologo
dotnet test LocalRecorder.sln -c Release --no-restore --nologo --logger 'console;verbosity=normal'
dotnet publish src/LocalRecorder/LocalRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -o artifacts/publish/win-x64 --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installer.ps1 -Version 1.0.4
```

构建成功，0 警告、0 错误。24 项常规测试通过，其中包含新增 3 项真实 WPF 窗口关闭测试；4 个可选桌面硬件测试条目按未启用环境变量的设计跳过。本轮用发布版正常界面实际覆盖空闲和正在录制两种退出情形。

## 正常界面退出实测

| 情形 | 点击关闭 UTC | 进程退出 UTC | 耗时 | 退出码 | 资源清理 |
|---|---|---|---:|---:|---:|
| 空闲 | 08:29:03.794 | 08:29:03.9594953 | 165.5 ms | 0 | 6 ms |
| 全屏 1080p30、系统声音＋麦克风录制中 | 08:30:10.324 | 08:30:10.5964947 | 272.5 ms | 0 | 97 ms |

点击时间在界面自动化发出点击前记录，因此上述耗时包含输入投递开销，只代表本机短录制实测。原始进程结果在 `artifacts/regression/1.0.4/idle-exit.json` 和 `recording-exit.json`。第一次空闲操作同样正常关闭，资源清理 5 ms；观察脚本最初未保留进程句柄，读取退出时间失败，修正脚本后重测取得表中数据，该问题不在录屏应用中。

录制中点击窗口 X 后，应用按正常停止流程收尾，并生成：

`artifacts/regression/1.0.4/recording-close/Recording-20261017-162949.mp4`

文件 7,822,684 字节，无 `.tmp.mp4` 残留。使用本地 Media Foundation 解码检查工具验证：视频 516 帧、17.3504 秒；音频 826,368 个双声道采样帧、17.216 秒，峰值 0.02194。音视频可以完整解码。验证期间没有新增 LocalRecorder 的 1026/1000 崩溃事件。原用户配置已恢复，保存目录仍为 原本的用户保存目录。

## 交付

- 便携版：`artifacts/publish/win-x64/LocalRecorder.exe`，版本 1.0.4。
- 安装包：`artifacts/installer/LocalRecorder-1.0.4-win-x64.msi`。

本轮构建了 MSI，未执行系统安装/升级。长视频的 MP4 索引写入、慢盘或异常驱动仍可能延长安全收尾；应用会显示关闭状态并继续处理消息，不能以直接终止进程来保证任意场景下的固定退出时间。
