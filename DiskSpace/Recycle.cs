using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskSpace;

internal static class Recycle
{
    internal static string? PathRestriction(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length <= 3 || path[1] != ':') return "Выберите файл или папку на локальном диске";
        var name = Path.GetFullPath(path);
        var restricted = Protected.Value;
        foreach (var root in restricted)
            if (LocalStore.SamePath(name, root) || LocalStore.IsUnder(root, name)
                || (root != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) && LocalStore.IsUnder(name, root)))
                return "Windows или профиль использует эту папку";
        var volume = Path.GetPathRoot(name)!;
        var first = Path.GetRelativePath(volume, name).Split(Path.DirectorySeparatorChar)[0];
        if (new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "WindowsApps", "WpSystem" }.Contains(first, StringComparer.OrdinalIgnoreCase))
            return "Этот раздел обслуживает Windows";
        return null;
    }

    private static readonly Lazy<string[]> Protected = new(() => ProtectedRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private static IEnumerable<string> ProtectedRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.UserProfile })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && Directory.GetParent(profile) is { } profiles)
        {
            string[] others;
            try { others = Directory.GetDirectories(profiles.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { others = []; }
            foreach (var other in others)
                if (!LocalStore.SamePath(other, profile)) yield return other;
        }
    }

    internal static string? Validate(string path, bool folder)
    {
        var reason = PathRestriction(path);
        if (reason is not null) return reason;
        try
        {
            var attrs = File.GetAttributes(path);
            if (((attrs & FileAttributes.Directory) != 0) != folder) return "Тип объекта изменился, обновите анализ";
            for (var parent = path; parent is not null && parent.Length > 3; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System)) != 0)
                    return "Объект связан с другой папкой, облаком или системным каталогом";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Объект недоступен, обновите анализ"; }
    }

    internal static string? Move(string path, bool folder, IntPtr owner)
    {
        var reason = Validate(path, folder);
        if (reason is not null) return reason;
        var op = new ShellOperation { Owner = owner, Operation = 3, From = path + "\0\0", Flags = 0x4040 };
        var error = SHFileOperation(ref op);
        if (op.Aborted) return "Операция отменена";
        if (error != 0) return $"Windows не смогла удалить объект (код {error})";
        if (File.Exists(path) || Directory.Exists(path)) return "Объект остался на месте";
        return null;
    }

    internal static (long Bytes, long Count) Count()
    {
        var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.Bytes, info.Count) : (0, 0);
    }

    internal static string? Empty(IntPtr owner)
    {
        var result = SHEmptyRecycleBin(owner, null, 0x4);
        return result switch { 0 => null, unchecked((int)0x800704C7) => "Очистка корзины отменена", _ => $"Корзину не удалось очистить (0x{result:X8})" };
    }

    internal static void Open() => Process.Start(new ProcessStartInfo("shell:RecycleBinFolder") { UseShellExecute = true });

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellOperation
    {
        public IntPtr Owner;
        public uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInfo { public uint Size; public long Bytes, Count; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellOperation operation);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? root, uint flags);
}
