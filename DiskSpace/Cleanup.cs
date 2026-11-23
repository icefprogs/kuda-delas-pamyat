using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace;

internal sealed record Candidate(string Path, long Bytes, long Logical, DateTime Changed, string Reason, bool Demo = false, ulong FileId = 0, CleanupRule? Rule = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool Selected { get; set; } = true;
    public string Category => Rule?.Category ?? "Временные файлы";
    public bool Automatic => Rule?.Automatic ?? true;
}

internal static class Cleanup
{
    private static string TempRoot =>
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));

    private const FileAttributes Disallowed = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.ReadOnly
        | FileAttributes.System | FileAttributes.Directory;

    public static Candidate[] Find(int days, string[] exclusions, CancellationToken token, bool automatic = false)
    {
        return FindForRules(CleanupRules.Get(), days, exclusions, token, automatic);
    }

    internal static Candidate[] FindForRules(IEnumerable<CleanupRule> rules, int days, string[] exclusions, CancellationToken token, bool automatic = false)
    {
        var limit = DateTime.UtcNow.AddDays(-days);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System
        };
        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            if ((automatic && !rule.Automatic) || !IsPlainDirectory(rule.Root)) continue;
            var cutoff = rule.Kind == CleanupKind.Browser ? DateTime.UtcNow.AddHours(-1) : limit;
            try
            {
                foreach (var file in new DirectoryInfo(rule.Root).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (!rule.Accepts(file.FullName) || !seen.Add(file.FullName)
                        || exclusions.Any(parent => LocalStore.IsUnder(file.FullName, parent))) continue;
                    try
                    {
                        if ((file.Attributes & Disallowed) != 0 || file.LastWriteTimeUtc >= cutoff
                            || !NativeFiles.Measure(file.FullName, out var allocated, out var links, out var id) || links != 1) continue;
                        var reason = rule.Process is null ? $"Старше {days} дн." : "Кеш · закройте браузер перед очисткой";
                        list.Add(new(file.FullName, allocated, file.Length, file.LastWriteTimeUtc, reason, FileId: id, Rule: rule)
                        { Selected = rule.Kind != CleanupKind.Crash });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(item => item.Bytes).ToArray();
    }

    public static CleanupLog Remove(Candidate item, int days, string[] exclusions)
    {
        var time = DateTimeOffset.Now;
        if (item.Demo) return new(time, item.Path, "Демо · имитация", item.Bytes);
        var rule = item.Rule ?? new CleanupRule(TempRoot, CleanupKind.Temp, "Временные файлы");
        if (!rule.Accepts(item.Path) || Path.GetDirectoryName(item.Path) is not { } parentPath
            || !IsPlainDirectory(parentPath) || exclusions.Any(parent => LocalStore.IsUnder(item.Path, parent)))
            return new(time, item.Path, "За пределами правил", 0);
        if (CleanupRules.Running(rule.Process)) return new(time, item.Path, "Закройте браузер", 0);
        var cutoff = rule.Kind == CleanupKind.Browser ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddDays(-days);

        try
        {
            using var file = OpenCandidate(item.Path, 0x10080);
            if (file.IsInvalid) return new(time, item.Path, "Файл занят или недоступен", 0);
            var resolved = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(file, resolved, (uint)resolved.Capacity, 0);
            var expected = @"\\?\" + Path.GetFullPath(item.Path);
            if (length == 0 || length >= resolved.Capacity || !string.Equals(resolved.ToString(), expected, StringComparison.OrdinalIgnoreCase)
                || !GetFileInformationByHandle(file, out var info) || (info.Attributes & (uint)Disallowed) != 0 || info.Links != 1)
                return new(time, item.Path, "Объект изменился или находится за пределами папки", 0);

            var size = ((long)info.SizeHigh << 32) | info.SizeLow;
            var write = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
            var id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            if (id != item.FileId || write != item.Changed || size != item.Logical || write > cutoff)
                return new(time, item.Path, "Размер, дата или правило изменились", 0);
            byte remove = 1;
            if (!SetFileInformationByHandle(file, 4, ref remove, 1))
                return new(time, item.Path, "Windows отказала в удалении", 0);
            return new(time, item.Path, "Удалён", item.Bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(time, item.Path, "Объект недоступен", 0);
        }
    }

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            for (var folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
            {
                if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static SafeFileHandle OpenCandidate(string path, uint access) =>
        CreateFile(@"\\?\" + path, access, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);

    public static void OpenWindowsCleanup() => Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });

    public static Task<int> RunSystemCleanupAsync(string root, bool demo)
    {
        if (demo || root.Length != 3 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
            return Task.FromResult(-1);
        return Task.Run(() =>
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.SystemDirectory
            };
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add(root[..1]);
            try
            {
                using var process = Process.Start(info);
                process?.WaitForExit();
                return process?.ExitCode ?? -1;
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return -1; }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Serial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref byte info, uint size);
}
