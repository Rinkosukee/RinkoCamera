# 1.0.3 界面文字垂直对齐

修复日期：2026-10-17。

## 修改

- `src/LocalRecorder/App.xaml`：统一 TextBox、ComboBox、ComboBoxItem、Button 的 VerticalContentAlignment；输入控件增加左右内边距，下拉列表行和按钮使用一致的最小高度。保留 Windows 默认主题模板及焦点/禁用行为。
- `src/LocalRecorder/MainWindow.xaml`：启用 UseLayoutRounding、SnapsToDevicePixels 和 Display 文本格式，减少缩放时边缘落在半像素上的问题。
- `src/LocalRecorder/LocalRecorder.csproj`、`scripts/build-installer.ps1`：版本 1.0.3。
- `README.md`：更新交付版本和验证记录链接。

## 实际验证

```powershell
dotnet build LocalRecorder.sln -c Release --no-restore --nologo
dotnet test LocalRecorder.sln -c Release --no-restore --nologo
dotnet publish src/LocalRecorder/LocalRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -o artifacts/publish/win-x64 --nologo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-installer.ps1 -Version 1.0.3
```

构建成功，0 警告、0 错误；21 项常规测试通过，4 个桌面硬件测试条目按未启用环境变量的设计跳过。本轮只修改界面样式，没有重跑需要声音播放/设备采集的硬件测试；音视频回归结果见 1.0.2 记录。

已实际启动发布版，检查分辨率两个输入框、保存路径、录制目标、声音来源及设备、帧率、质量、倒计时、按钮文字。控件内容均按垂直居中排列。展开帧率下拉列表，检查 24/30/60 各行的居中位置和行高，未出现裁切。验证使用当前 Windows 桌面的显示设置，未改动系统缩放设置，也未声称覆盖所有 DPI/字体组合。

便携版：`artifacts/publish/win-x64/LocalRecorder.exe`。

安装包：`artifacts/installer/LocalRecorder-1.0.3-win-x64.msi`（本轮构建，未执行 MSI 安装/升级）。
