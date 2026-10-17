using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WpfApplication = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RinkoCamera.Core;
using RinkoCamera.Infrastructure;

namespace RinkoCamera;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICaptureSource _capture;
    private readonly ISettingsStore _settingsStore;
    private readonly IFileStorage _storage;
    private readonly IAudioSource _audio;
    private readonly IRecordingController _controller;
    private readonly IAppLogger _logger;
    private GlobalHotkeyBridge? _hotkey;
    private string? _lastFile;

    public ObservableCollection<CaptureTarget> Targets { get; } = [];
    public ObservableCollection<AudioDevice> Microphones { get; } = [];
    public ObservableCollection<AudioDevice> SystemDevices { get; } = [];
    public IReadOnlyList<AudioModeChoice> AudioModes { get; } =
    [
        new(AudioSourceMode.System, "系统声音"), new(AudioSourceMode.Microphone, "麦克风"),
        new(AudioSourceMode.SystemAndMicrophone, "系统声音＋麦克风"), new(AudioSourceMode.Muted, "静音（不录声音）")
    ];
    public IReadOnlyList<int> FrameRates { get; } = [24, 30, 60];
    public IReadOnlyList<int> CountdownValues { get; } = [0, 3, 5];
    public IReadOnlyList<VideoQuality> Qualities { get; } = Enum.GetValues<VideoQuality>();

    [ObservableProperty] private CaptureTarget? selectedTarget;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioStatusText))]
    private AudioSourceMode audioMode = AudioSourceMode.System;
    [ObservableProperty] private AudioDevice? selectedMicrophone;
    [ObservableProperty] private AudioDevice? selectedSystemDevice;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioStatusText))]
    private double audioLevel;
    [ObservableProperty] private int width = 1920;
    [ObservableProperty] private int height = 1080;
    [ObservableProperty] private int frameRate = 30;
    [ObservableProperty] private VideoQuality quality = VideoQuality.Medium;
    [ObservableProperty] private string outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RinkoCamera");
    [ObservableProperty] private int countdownSeconds = 3;
    [ObservableProperty] private string statusText = "就绪";
    [ObservableProperty] private string captureModeText = "等待捕获";
    [ObservableProperty] private string elapsedText = "00:00:00";
    [ObservableProperty] private string errorText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    private bool isBusy;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    private bool isClosing;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private bool canOpenFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCountingDown))]
    private int countdownRemaining;

    public bool IsCountingDown => CountdownRemaining > 0;
    public bool CanEditSettings => !IsBusy && !IsClosing;
    public string AudioStatusText => AudioMode == AudioSourceMode.Muted
        ? "当前为静音，新录制的视频不会包含声音。"
        : $"录音电平：{AudioLevel:F1}%（持续为 0 时请检查声音来源、设备静音和应用音量）";

    public MainViewModel(ICaptureSource capture, IAudioSource audio, ISettingsStore settingsStore, IFileStorage storage, IRecordingController controller, IAppLogger logger)
    {
        _capture = capture;
        _audio = audio;
        _settingsStore = settingsStore;
        _storage = storage;
        _controller = controller;
        _logger = logger;
        _controller.StateChanged += OnStateChanged;
        _controller.DurationChanged += OnDurationChanged;
        _controller.ErrorRaised += OnErrorRaised;
        _controller.CaptureModeChanged += OnCaptureModeChanged;
        _controller.CountdownChanged += OnCountdownChanged;
        _controller.AudioLevelChanged += OnAudioLevelChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        OutputDirectory = settings.OutputDirectory;
        AudioMode = settings.AudioMode;
        Width = settings.Width;
        Height = settings.Height;
        FrameRate = settings.FrameRate;
        Quality = settings.Quality;
        CountdownSeconds = settings.CountdownSeconds;
        var targets = await _capture.EnumerateTargetsAsync(cancellationToken);
        Targets.Clear();
        foreach (var target in targets) Targets.Add(target);
        SelectedTarget = Targets.FirstOrDefault(t => t.Kind == settings.TargetKind &&
            (t.Kind != CaptureTargetKind.Window || t.DisplayName == settings.TargetDisplayName));
        if (SelectedTarget is null)
        {
            SelectedTarget = Targets.FirstOrDefault();
            if (settings.TargetKind == CaptureTargetKind.Window)
                ErrorText = "上次录制的窗口已关闭、最小化或标题已变化，请还原并刷新后重新选择。";
        }
        Microphones.Clear();
        foreach (var microphone in _audio.EnumerateDevices(input: true)) Microphones.Add(microphone);
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == settings.MicrophoneId) ?? Microphones.FirstOrDefault();
        SystemDevices.Clear();
        SystemDevices.Add(new AudioDevice("", "跟随 Windows 默认播放设备", true, false));
        foreach (var device in _audio.EnumerateDevices(input: false)) SystemDevices.Add(device);
        SelectedSystemDevice = SystemDevices.FirstOrDefault(d => d.Id == settings.SystemDeviceId) ?? SystemDevices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RefreshTargetsAsync()
    {
        if (IsBusy) return;
        try
        {
            var selected = SelectedTarget;
            var targets = await _capture.EnumerateTargetsAsync(CancellationToken.None);
            Targets.Clear();
            foreach (var target in targets) Targets.Add(target);
            SelectedTarget = Targets.FirstOrDefault(t => t.Kind == selected?.Kind && t.Handle == selected.Handle) ?? Targets.FirstOrDefault();
        }
        catch (Exception ex) { ErrorText = $"刷新窗口失败：{ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorText = string.Empty;
        AudioLevel = 0;
        CaptureModeText = "等待捕获";
        var target = SelectedTarget ?? Targets.FirstOrDefault();
        if (target is null) { ErrorText = "请选择录制目标。"; return; }
        if (Width < 2 || Height < 2) { ErrorText = "分辨率必须大于 1。"; return; }
        try
        {
            var systemDeviceId = string.IsNullOrEmpty(SelectedSystemDevice?.Id) ? null : SelectedSystemDevice.Id;
            var settings = new UserSettings { OutputDirectory = OutputDirectory, TargetKind = target.Kind, TargetDisplayName = target.DisplayName, AudioMode = AudioMode, MicrophoneId = SelectedMicrophone?.Id, SystemDeviceId = systemDeviceId, Width = Width, Height = Height, FrameRate = FrameRate, Quality = Quality, CountdownSeconds = CountdownSeconds };
            await _settingsStore.SaveAsync(settings, CancellationToken.None);
            if (IsClosing) return;
            await _controller.StartAsync(new RecordingOptions { Target = target, AudioMode = AudioMode, MicrophoneId = SelectedMicrophone?.Id, SystemDeviceId = systemDeviceId, Width = Width, Height = Height, FrameRate = FrameRate, Quality = Quality, OutputDirectory = OutputDirectory, CountdownSeconds = CountdownSeconds }, CancellationToken.None);
        }
        catch (Exception ex) { ErrorText = ex.Message; _logger.Error("开始录制失败", ex); }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync() => await _controller.PauseAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync() => await _controller.ResumeAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            var result = await _controller.StopAsync(CancellationToken.None);
            if (result is not null)
            {
                // Queue after the controller's Idle notification so it cannot
                // replace the saved-file message with "ready".
                RunOnUi(() =>
                {
                    _lastFile = result.FilePath;
                    CanOpenFile = true;
                    var metrics = result.Metrics;
                    StatusText = metrics is null
                        ? $"已保存：{result.FilePath}"
                        : $"已保存：{result.FilePath}（捕获 {metrics.CapturedFrames}，编码 {metrics.EncodedFrames}，丢帧 {metrics.DroppedFrames}）";
                });
            }
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    [RelayCommand]
    private void ChooseDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = OutputDirectory, Description = "选择录制保存目录" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) OutputDirectory = dialog.SelectedPath;
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() { if (_lastFile is { } path && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFolder() { if (_lastFile is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true }); }

    private bool CanStart() => !IsClosing && _controller.State is (RecordingState.Idle or RecordingState.Error);
    private bool CanPause() => !IsClosing && _controller.State == RecordingState.Recording;
    private bool CanResume() => !IsClosing && _controller.State == RecordingState.Paused;
    private bool CanStop() => !IsClosing && _controller.State is (RecordingState.Preparing or RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping);

    private void OnStateChanged(object? sender, RecordingState state)
    {
        RunOnUi(() => { IsBusy = state is not (RecordingState.Idle or RecordingState.Error); IsPaused = state == RecordingState.Paused; StatusText = state switch { RecordingState.Preparing => "准备中…", RecordingState.Recording => "录制中", RecordingState.Paused => "已暂停", RecordingState.Stopping => "停止中…", RecordingState.Error => "错误", _ => "就绪" }; StartCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); });
    }

    private void OnDurationChanged(object? sender, TimeSpan duration) => RunOnUi(() => ElapsedText = duration.ToString("hh\\:mm\\:ss"));
    private void OnErrorRaised(object? sender, string message) => RunOnUi(() => ErrorText = message);
    private void OnCaptureModeChanged(object? sender, string message) => RunOnUi(() => CaptureModeText = message);
    private void OnAudioLevelChanged(object? sender, double peak) => RunOnUi(() => AudioLevel = peak * 100);
    private void OnCountdownChanged(object? sender, int remaining) => RunOnUi(() =>
    {
        CountdownRemaining = remaining;
        if (remaining > 0) StatusText = $"{remaining} 秒后开始录制";
        else if (_controller.State == RecordingState.Preparing) StatusText = "准备中…";
    });
    private static void RunOnUi(Action action) { if (WpfApplication.Current?.Dispatcher is { } dispatcher) dispatcher.InvokeAsync(action); else action(); }

    public void AttachHotkey(GlobalHotkeyBridge hotkey) { _hotkey = hotkey; }
    public void BeginShutdown()
    {
        IsClosing = true;
        StatusText = IsBusy ? "正在关闭：停止录制并保存视频…" : "正在关闭…";
        _hotkey?.Dispose();
        _hotkey = null;
    }

    public void ReportShutdownFailure(Exception error)
    {
        IsClosing = false;
        StatusText = "关闭失败";
        ErrorText = $"关闭时发生错误：{error.Message}。请重试关闭，详细信息已写入日志。";
    }

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        // An in-flight settings save must not start recording after disposal.
        if (StartCommand.ExecutionTask is { } starting) await starting;
        _controller.StateChanged -= OnStateChanged;
        _controller.DurationChanged -= OnDurationChanged;
        _controller.ErrorRaised -= OnErrorRaised;
        _controller.CaptureModeChanged -= OnCaptureModeChanged;
        _controller.CountdownChanged -= OnCountdownChanged;
        _controller.AudioLevelChanged -= OnAudioLevelChanged;
        // WASAPI Stop/Dispose and MF finalization can synchronously wait on
        // native workers. Keep those waits off WPF's dispatcher thread.
        if (_controller is IAsyncDisposable disposable)
            await Task.Run(async () => await disposable.DisposeAsync().ConfigureAwait(false));
    }
}

public sealed record AudioModeChoice(AudioSourceMode Value, string Label);

public sealed class GlobalHotkeyBridge : IDisposable
{
    private readonly IGlobalHotkeyService _service;
    private readonly MainViewModel _viewModel;
    public GlobalHotkeyBridge(IGlobalHotkeyService service, MainViewModel viewModel) { _service = service; _viewModel = viewModel; _service.Pressed += OnPressed; }
    public void Register(uint modifiers, uint key) { if (!_service.Register(modifiers, key)) throw new InvalidOperationException("全局快捷键注册失败，可能已被其他程序占用。"); }
    private async void OnPressed(object? sender, EventArgs e)
    {
        if (_viewModel.IsBusy) await _viewModel.StopCommand.ExecuteAsync(null);
        else await _viewModel.StartCommand.ExecuteAsync(null);
    }
    public void Dispose() { _service.Pressed -= OnPressed; _service.Dispose(); }
}
