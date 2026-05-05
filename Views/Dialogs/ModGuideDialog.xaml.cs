using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ArclightLauncher.Views.Dialogs;

public partial class ModGuideDialog : Window, INotifyPropertyChanged
{
    private readonly string _modsDir;
    private string _modsFolderText = string.Empty;

    public ModGuideDialog(string modsDir)
    {
        InitializeComponent();
        _modsDir = modsDir;
        DataContext = this;
        ModsFolderText = $"当前 mods 文件夹：{_modsDir}";
        LoadGuideText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModsFolderText
    {
        get => _modsFolderText;
        private set
        {
            if (_modsFolderText == value)
                return;
            _modsFolderText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModsFolderText)));
        }
    }

    private void LoadGuideText()
    {
        GuideText.Text = TryReadGuideText() ?? BuildFallbackGuide();
        GuideText.ScrollToHome();
    }

    private static string? TryReadGuideText()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "服务器Mod使用说明.md"),
            Path.Combine(baseDir, "Docs", "服务器Mod使用说明.md"),
            Path.Combine(baseDir, "..", "..", "..", "Docs", "服务器Mod使用说明.md")
        };

        var path = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        return path is null ? null : File.ReadAllText(path);
    }

    private static string BuildFallbackGuide()
        => """
           # 烨夜服 Mod 使用说明

           官方服务器会自动同步必需 Mod。玩家可以额外添加 Fabric 客户端 Mod，例如小地图、HUD、截图、画质优化类。

           不要添加需要服务器也安装的内容类 Mod，例如新增方块、新物品、新生物、新维度、地形生成类 Mod。

           常用入口：
           - Xaero 小地图 / 世界地图：按键绑定搜索 Xaero 或 World Map
           - Jade：对准方块或生物自动显示信息
           - AppleSkin：自动显示食物恢复量
           - Litematica：按键绑定搜索 Litematica
           - MiniHUD：按键绑定搜索 MiniHUD
           - Tweakeroo：按键绑定搜索 Tweakeroo
           - Zoomify：按键绑定搜索 Zoomify
           - Sodium / Iris：选项 -> 视频设置
           - Mod Menu：主菜单点击 Mods / 模组

           如果游戏崩溃，先删除最近添加的 jar，再重新启动。
           """;

    private void OpenModsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_modsDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = _modsDir,
            UseShellExecute = true
        });
    }

    private void ReloadGuideButton_Click(object sender, RoutedEventArgs e)
        => LoadGuideText();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
