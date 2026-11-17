using System.Diagnostics;
using System.Drawing;
using LarkCloud.App.Service;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

internal static class Ui
{
    public static readonly Font Body = new("Segoe UI", 9.75f);

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { ConfirmDialog.Tell(null, "Couldn't open that", ex.Message); }
    }

    public static void ShowInFolder(string full)
    {
        if (File.Exists(full) || Directory.Exists(full))
            Process.Start("explorer.exe", $"/select,\"{full}\"");
        else
        {
            var dir = Path.GetDirectoryName(full);
            while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (dir != null) Open(dir);
        }
    }

    public static string Ago(DateTime? t)
    {
        if (t == null) return "not yet";
        var s = DateTime.UtcNow - t.Value;
        if (s.TotalSeconds < 60) return "just now";
        if (s.TotalMinutes < 60) return $"{(int)s.TotalMinutes} min ago";
        return $"{(int)s.TotalHours} h ago";
    }

    /// <summary>The app icon at the size dialogs ask for (picked from the .ico's own frames).</summary>
    public static Icon AppIcon => TrayIcons.AppIcon;

    /// <summary>Can this PC folder hold a Lark cloud folder? (error, warning)</summary>
    public static (string? Error, string? Warning) CheckPcFolder(string path, Config cfg, string? excludePairId = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("Choose a folder on this PC.", null);
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())); }
        catch { return ("That isn't a valid folder path.", null); }
        var root = Path.GetPathRoot(full) ?? "";
        if (full.TrimEnd('\\').Length <= root.TrimEnd('\\').Length) return ("A whole drive can't be used. Choose or create a folder inside it.", null);
        if (root.StartsWith(@"\\")) return ("Network folders can't be used. Choose a folder on this PC (like C:).", null);
        try
        {
            var d = new DriveInfo(root);
            if (d.DriveType != DriveType.Fixed) return ("Choose a folder on this PC's own drive (not a USB, network or CD drive).", null);
            if (d.IsReady && !string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return ($"That drive is {d.DriveFormat}; files on demand needs an NTFS drive (like C:).", null);
        }
        catch { }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sys in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
            if (!string.IsNullOrEmpty(sys) && Config.SameOrInside(full, sys)) return ("This is a system or program folder. Choose a folder in your own files instead.", null);
        foreach (var od in new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveCommercial"), Environment.GetEnvironmentVariable("OneDriveConsumer") })
            if (!string.IsNullOrEmpty(od) && Config.SameOrInside(full, od)) return ("This folder is inside OneDrive. Two cloud services can't share a folder – choose a folder outside OneDrive.", null);
        if (string.Equals(full.TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return ("Your whole user folder can't be used. Choose or create a folder inside it.", null);
        foreach (var special in new[] { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music" })
            if (string.Equals(full.TrimEnd('\\'), Path.Combine(home, special), StringComparison.OrdinalIgnoreCase))
                return ($"Your whole {special} folder can't be used. Create a new folder inside it (for example {special}\\Lark Cloud) and choose that.", null);
        if (cfg.LocalOverlap(full, excludePairId) is { } o) return (o, null);
        if (File.Exists(full)) return ("That is a file, not a folder.", null);
        if (Directory.Exists(full))
        {
            var n = 0;
            try { n = Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Take(10000).Count(); } catch { }
            if (n > 0)
                return (null, $"This folder already has {n:N0}{(n >= 10000 ? "+" : "")} item(s). They will be uploaded to Lark " +
                              "(files with the same name and content are just linked, not duplicated).");
        }
        return (null, null);
    }
}
