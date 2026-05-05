using System.Reflection;
using System.Windows;
using ArclightLauncher.Models;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArclightLauncher.ViewModels.Dialogs;

public partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updateService;
    private readonly UpdateInfo    _info;
    private CancellationTokenSource? _downloadCts;

    public event Action<bool?>? CloseRequested;

    public string  CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    public string  NewVersion     { get; }
    public long    DownloadSize   { get; }
    public string  DownloadSizeText { get; }
    public string? ReleaseNotes   { get; }
    public bool    HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(SkipCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _downloadComplete;

    public UpdateViewModel(UpdateInfo info, UpdateService updateService)
    {
        _info          = info;
        _updateService = updateService;
        NewVersion     = info.NewVersion.ToString(3);
        DownloadSize   = info.Size;
        ReleaseNotes   = info.ReleaseNotes;
        DownloadSizeText = FormatBytes(info.Size);
        StatusText      = $"新版本 {NewVersion}，大小 {DownloadSizeText}";
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task UpdateNowAsync()
    {
        IsDownloading = true;
        StatusText    = "正在连接下载服务器...";
        Progress      = 0;

        _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        try
        {
            await _updateService.DownloadAndApplyUpdateAsync(
                _info,
                (downloaded, total) =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var effectiveTotal = total > 0 ? total : _info.Size;
                        if (effectiveTotal > 0)
                        {
                            Progress = (double)downloaded / effectiveTotal * 100;
                            StatusText = $"正在下载... {FormatBytes(downloaded)} / " +
                                         $"{FormatBytes(effectiveTotal)}";
                        }
                        else
                        {
                            StatusText = $"正在下载... {FormatBytes(downloaded)}";
                        }
                    });
                },
                _downloadCts.Token);

            DownloadComplete = true;
            StatusText       = "更新已就绪，正在关闭启动器...";
            Progress         = 100;

            await Task.Delay(800);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            StatusText    = "下载已取消";
            IsDownloading = false;
        }
        catch (Exception ex)
        {
            StatusText    = $"下载失败：{ex.Message}";
            IsDownloading = false;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
        StatusText = "正在取消...";
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Skip() => CloseRequested?.Invoke(false);

    private bool CanAct() => !IsDownloading;

    private bool CanCancelDownload() => IsDownloading && !DownloadComplete;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024         => $"{bytes / 1_024.0:F0} KB",
            _                => $"{bytes} B"
        };
    }
}
