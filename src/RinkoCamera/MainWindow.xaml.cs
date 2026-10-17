using System.Windows;
using System.Diagnostics;
using RinkoCamera.Infrastructure;

namespace RinkoCamera;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly FileLogger _logger;
    private readonly WindowCloseCoordinator _closeCoordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _initializationTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        _logger = new FileLogger();
        _logger.Info($"启动 RinkoCamera {typeof(MainWindow).Assembly.GetName().Version}，PID {Environment.ProcessId}。");
        var capture = new WindowsGraphicsCaptureSource();
        var audio = new NaudioCaptureSource();
        var controller = new RecordingController(capture, audio, new MediaFoundationMp4Encoder(), new LocalFileStorage(), _logger);
        _viewModel = new MainViewModel(capture, audio, new JsonSettingsStore(), new LocalFileStorage(), controller, _logger);
        DataContext = _viewModel;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        _closeCoordinator = new WindowCloseCoordinator(this, ShutdownAsync, OnShutdownFailed);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _initializationTask = InitializeAsync();

    private async Task InitializeAsync()
    {
        try
        {
            var media = MediaFoundationCapabilities.Probe();
            if (!media.IsAvailable || !media.H264PathAvailable || !media.AacPathAvailable)
                _viewModel.ErrorText = media.Message;
            await _viewModel.InitializeAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _viewModel.ErrorText = $"初始化失败：{ex.Message}"; }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hotkey = new GlobalHotkeyBridge(new GlobalHotkeyService(new System.Windows.Interop.WindowInteropHelper(this).Handle), _viewModel);
            hotkey.Register(0x0002u | 0x0004u, 0x79u); // Ctrl+Shift+F10
            _viewModel.AttachHotkey(hotkey);
        }
        catch (Exception ex) { _viewModel.ErrorText = ex.Message; }
    }

    private async Task ShutdownAsync()
    {
        var timer = Stopwatch.StartNew();
        _viewModel.BeginShutdown();
        _lifetime.Cancel();
        await _initializationTask;
        await _viewModel.DisposeAsync();
        await Task.Run(() =>
        {
            _logger.Info($"关闭资源释放完成，耗时 {timer.Elapsed.TotalMilliseconds:F0} ms。");
            _logger.Dispose();
        });
        _lifetime.Dispose();
    }

    private void OnShutdownFailed(Exception error)
    {
        _logger.Error("关闭失败", error);
        _viewModel.ReportShutdownFailure(error);
    }
}
