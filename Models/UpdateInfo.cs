namespace ArclightLauncher.Models;

public class UpdateInfo
{
    public Version NewVersion   { get; init; } = new();
    public string  DownloadUrl  { get; init; } = string.Empty;
    public long    Size         { get; init; }
    public string? Sha256       { get; init; }
    public string? ReleaseNotes { get; init; }
}
