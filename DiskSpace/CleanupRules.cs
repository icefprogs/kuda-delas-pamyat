using System.Diagnostics;

namespace DiskSpace;

internal enum CleanupKind { Temp, Shader, Browser, Crash }
internal sealed record CleanupRule(string Root, CleanupKind Kind, string Category, string? Process = null)
{
    public bool Automatic => Kind is CleanupKind.Temp or CleanupKind.Shader;

    public bool Accepts(string path)
    {
        if (!LocalStore.IsUnder(path, Root)) return false;
        var extension = Path.GetExtension(path);
        return Kind switch
        {
            CleanupKind.Temp => extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".temp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".etl", StringComparison.OrdinalIgnoreCase),
            CleanupKind.Crash => extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }
}

internal static class CleanupRules
{
    public static IEnumerable<CleanupRule> Get()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) yield break;
        yield return new(Path.Combine(local, "Temp"), CleanupKind.Temp, "Временные файлы");
        yield return new(Path.Combine(local, "D3DSCache"), CleanupKind.Shader, "Кеш шейдеров DirectX");
        yield return new(Path.Combine(local, "CrashDumps"), CleanupKind.Crash, "Отчёты о сбоях");
        foreach (var (product, process, name) in new[]
        {
            (@"Google\Chrome\User Data", "chrome", "Google Chrome"),
            (@"Microsoft\Edge\User Data", "msedge", "Microsoft Edge"),
            (@"BraveSoftware\Brave-Browser\User Data", "brave", "Brave"),
            (@"Yandex\YandexBrowser\User Data", "browser", "Яндекс Браузер")
        })
        {
            foreach (var profile in Children(Path.Combine(local, product)))
            {
                var label = Path.GetFileName(profile);
                if (label != "Default" && !label.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                yield return new(Path.Combine(profile, "Cache", "Cache_Data"), CleanupKind.Browser, name, process);
                yield return new(Path.Combine(profile, "Code Cache", "js"), CleanupKind.Browser, name, process);
                yield return new(Path.Combine(profile, "Code Cache", "wasm"), CleanupKind.Browser, name, process);
            }
        }
        foreach (var profile in Children(Path.Combine(local, "Mozilla", "Firefox", "Profiles")))
            yield return new(Path.Combine(profile, "cache2", "entries"), CleanupKind.Browser, "Firefox", "firefox");
    }

    private static string[] Children(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public static bool Running(string? name)
    {
        if (name is null) return false;
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
