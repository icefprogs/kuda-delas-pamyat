using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace DiskSpace;

internal record AppEntry(string Name, string Publisher, string? Location, string? Executable, long Bytes, DateTime? Installed)
{
    public string Key => Executable ?? Location ?? Name + "|" + Publisher;
}

internal static class Review
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ShellFolders = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    public static FileRow[] OldFiles(IEnumerable<FileRow> index, int days, CancellationToken token)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var results = new PriorityQueue<FileRow, long>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.ExpandEnvironmentVariables(Registry.GetValue(ShellFolders,
                "{374DE290-123F-4565-9164-39C4925E467B}", @"%USERPROFILE%\Downloads") as string ?? "")
        };
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip =
            FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System | FileAttributes.Hidden };
        void Add(FileRow row)
        {
            if (!seen.Add(row.Path)) return;
            results.Enqueue(row, row.Bytes);
            if (results.Count > 350) results.Dequeue();
        }
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = new DirectoryInfo(root);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in directory.EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (file.Length < 1024 * 1024 || file.LastWriteTimeUtc >= cutoff
                            || !NativeFiles.Measure(file.FullName, out var bytes, out _, out _)) continue;
                        Add(new(file.FullName, bytes, file.Length, file.LastWriteTimeUtc, "Давние файлы", false));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var file in index)
        {
            token.ThrowIfCancellationRequested();
            if (file.Changed < cutoff && file.Bytes >= 1024 * 1024 && Recycle.PathRestriction(file.Path) is null)
                Add(file);
        }
        return results.UnorderedItems.Select(item => item.Element).OrderByDescending(file => file.Bytes).ToArray();
    }

    public static AppEntry[] Apps(CancellationToken token)
    {
        var apps = new List<AppEntry>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var list = baseKey.OpenSubKey(Uninstall);
                if (list is null) continue;
                foreach (var subKey in list.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    using var app = list.OpenSubKey(subKey);
                    if (app?.GetValue("DisplayName") is not string { Length: > 0 } name ||
                        app.GetValue("SystemComponent") is 1 || app.GetValue("ParentKeyName") is not null ||
                        app.GetValue("UninstallString") is not string { Length: > 0 }) continue;
                    var location = LocalPath(app.GetValue("InstallLocation") as string);
                    var icon = app.GetValue("DisplayIcon") as string;
                    var executable = LocalPath(icon?.Split(',')[0]);
                    if (!".exe".Equals(Path.GetExtension(executable), StringComparison.OrdinalIgnoreCase)) executable = null;
                    long bytes = app.GetValue("EstimatedSize") is int kib && kib > 0 ? (long)kib * 1024 : 0;
                    DateTime? installed = DateTime.TryParseExact(app.GetValue("InstallDate") as string, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
                    apps.Add(new(name, app.GetValue("Publisher") as string ?? "", location, executable, bytes, installed));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return apps.DistinctBy(app => (app.Name.ToUpperInvariant(), app.Publisher.ToUpperInvariant())).ToArray();
    }

    private static string? LocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim().Trim('"');
        return path.Length > 3 && path[1] == ':' && Path.IsPathFullyQualified(path) ? path : null;
    }

    public static string[] ProcessPaths()
    {
        var result = new List<string>();
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            try { if (process.MainModule?.FileName is { } path) result.Add(path); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return result.ToArray();
    }

    public static string? LaunchAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }) is null ? null : "Открылся список установленных приложений";
}
