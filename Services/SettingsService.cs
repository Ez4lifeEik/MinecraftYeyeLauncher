using System.IO;
using System.Text.Json;
using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.Services;

/// <summary>
/// 启动器设置读写服务，持久化到 %APPDATA%\ArclightLauncher\settings.json
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger<SettingsService> _logger;

    public LauncherSettings Current { get; private set; } = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

    /// <summary>从 settings.json 加载设置；文件不存在时使用默认值</summary>
    public async Task LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            Current = new LauncherSettings();
            _logger.LogInformation("settings.json 不存在，使用默认设置");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(SettingsPath);
            Current = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
            _logger.LogInformation("设置已加载");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 settings.json 失败，使用默认设置");
            Current = new LauncherSettings();
        }
    }

    /// <summary>将 Current 写入 settings.json</summary>
    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            await File.WriteAllTextAsync(SettingsPath, json);
            _logger.LogDebug("设置已保存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 settings.json 失败");
        }
    }
}
