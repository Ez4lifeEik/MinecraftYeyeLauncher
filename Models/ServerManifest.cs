using System.Text.Json.Serialization;

namespace ArclightLauncher.Models;

/// <summary>
/// manifest.json 根节点
/// </summary>
public class ServerManifest
{
    /// <summary>整合包版本号（如 "0.1.0"）</summary>
    [JsonPropertyName("pack_version")]
    public string PackVersion { get; set; } = string.Empty;

    /// <summary>Minecraft 版本（如 "1.20.1"）</summary>
    [JsonPropertyName("minecraft_version")]
    public string MinecraftVersion { get; set; } = string.Empty;

    /// <summary>Mod 加载器信息</summary>
    [JsonPropertyName("loader")]
    public LoaderInfo Loader { get; set; } = new();

    /// <summary>要求的 Java 主版本号（如 17）</summary>
    [JsonPropertyName("java_version")]
    public int JavaVersion { get; set; }

    /// <summary>服务器连接信息</summary>
    [JsonPropertyName("server")]
    public ServerInfo Server { get; set; } = new();

    /// <summary>Mod 列表</summary>
    [JsonPropertyName("mods")]
    public List<PackFile> Mods { get; set; } = [];

    /// <summary>资源包列表</summary>
    [JsonPropertyName("resourcepacks")]
    public List<PackFile> Resourcepacks { get; set; } = [];

    /// <summary>光影包列表</summary>
    [JsonPropertyName("shaderpacks")]
    public List<PackFile> Shaderpacks { get; set; } = [];
}

/// <summary>
/// Mod 加载器信息
/// </summary>
public class LoaderInfo
{
    /// <summary>类型：目前只实现 "forge"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>版本号（如 "47.2.0"）</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// 服务器地址信息
/// </summary>
public class ServerInfo
{
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 25565;
}

/// <summary>
/// 单个文件条目（mods / resourcepacks / shaderpacks 通用）
/// </summary>
public class PackFile
{
    /// <summary>文件名（含扩展名）</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>下载地址</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>SHA1 校验值</summary>
    [JsonPropertyName("sha1")]
    public string Sha1 { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>"client" 或 "both"；服务端独有 mod 不会出现在 manifest 中</summary>
    [JsonPropertyName("side")]
    public string Side { get; set; } = "both";

    /// <summary>是否必须</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    /// <summary>用户是否可以临时禁用此文件（缺省为 false，向后兼容）</summary>
    [JsonPropertyName("user_removable")]
    public bool UserRemovable { get; set; } = false;

    /// <summary>mod 的显示名称，用于 Mod 管理对话框（缺省取 filename）</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>分类标签（如 "qol" "performance" "content"），供后续过滤使用</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}
