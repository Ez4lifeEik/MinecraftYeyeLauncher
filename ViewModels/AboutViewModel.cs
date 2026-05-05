using System.Reflection;
using System.Runtime.InteropServices;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace ArclightLauncher.ViewModels;

/// <summary>
/// 关于页 ViewModel
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    public string AppVersion    { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
    public string AppName       { get; } = "ArclightLauncher";
    public string AppDesc       { get; } = "迷你世界专属启动器，极简安装，一键上车。";

    public string OsVersion     { get; } = RuntimeInformation.OSDescription;
    public string DotNetVersion { get; } = RuntimeInformation.FrameworkDescription;

    [ObservableProperty] private string _javaPath = "检测中……";

    public AboutViewModel(JavaService javaService)
    {
        _ = DetectJavaAsync(javaService);
    }

    private async Task DetectJavaAsync(JavaService javaService)
    {
        var result = await javaService.FindJava17Async();
        Application.Current.Dispatcher.Invoke(() =>
            JavaPath = result ?? "未检测到 Java 17+");
    }
}
