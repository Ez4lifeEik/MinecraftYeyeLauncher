namespace ArclightLauncher.Models;

/// <summary>
/// 游戏启动模式
/// </summary>
public enum LaunchMode
{
    OfficialServer,   // 朝夕服（强制全量同步，自动连服）
    Singleplayer,     // 单机（尊重 mod 禁用列表，不传 --server 参数）
    CustomServer      // 自定义服务器（尊重 mod 禁用列表，使用用户输入的 IP/端口）
}
