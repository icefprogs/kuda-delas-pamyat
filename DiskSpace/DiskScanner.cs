using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace;

internal record Volume(string Id, string Root, string Label, string Format, long Capacity, long Free, bool Ready)
{
    public long Used => Capacity - Free;
    public double Fill => Capacity == 0 ? 0 : 100.0 * Used / Capacity;
    public string Title => Root.TrimEnd('\\') + (Label.Length == 0 ? "" : " · " + Label);
}

internal record FileRow(string Path, long Bytes, long Logical, DateTime Changed, string Category, bool Folder)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
}

internal record ScanResult(string Scope, long Bytes, int Files, int Skipped, IReadOnlyList<FileRow> Rows,
    IReadOnlyList<FileRow> Folders, IReadOnlyList<FileRow> AllFiles, bool Truncated);
internal record ScanProgress(int Files, int Skipped, string Path);

internal static class DiskScanner
{
    private const int IndexedFileLimit = 250_000;
    private const int FolderDetailLimit = 80_000;

    public static Volume[] Volumes()
    {
        var drives = DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Unknown)
            .Select(drive => ReadVolume(drive.RootDirectory.FullName, drive));
        var mountPoints = NativeFiles.VolumeMounts()
            .Where(location => location.Length > 3)
            .Select(location => ReadVolume(location));
        return drives.Concat(mountPoints).DistinctBy(volume => volume.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Volume ReadVolume(string root, DriveInfo? drive = null)
    {
        var id = NativeFiles.VolumeId(root);
        try
        {
            if (drive?.IsReady == false || !NativeFiles.FreeSpace(root, out var capacity, out var free))
                return new(id, root, "", "", 0, 0, false);
            var label = "";
            var format = "";
            try
            {
                if (drive is not null) { label = drive.VolumeLabel; format = drive.DriveFormat; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new(id, root, label, format, capacity, free, true);
        }
        catch (IOException) { return new(id, root, "", "", 0, 0, false); }
        catch (UnauthorizedAccessException) { return new(id, root, "", "", 0, 0, false); }
    }

    public static ScanResult Scan(string root, ManualResetEventSlim pause, CancellationToken token, IProgress<ScanProgress>? progress = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var folders = new List<FileRow>();
        var largest = new PriorityQueue<FileRow, long>();
        var indexed = new List<FileRow>();
        var seenLinks = new HashSet<ulong>();
        var stack = new Stack<Frame>();
        int files = 0, skipped = 0;
        bool truncated = false;
        long total = 0;
        try
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            stack.Push(new(root));
            while (stack.TryPeek(out var frame))
            {
                token.ThrowIfCancellationRequested();
                pause.Wait(token);
                FileSystemInfo? entry;
                try
                {
                    entry = frame.Entries.MoveNext() ? frame.Entries.Current : null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    entry = null;
                }
                if (entry is null)
                {
                    if (stack.Count > 1)
                    {
                        if (folders.Count < FolderDetailLimit)
                            folders.Add(new(frame.Path, frame.Bytes, frame.Bytes, DateTime.MinValue, "Папки", true));
                        else truncated = true;
                    }
                    stack.Pop().Dispose();
                    if (stack.TryPeek(out var parent)) parent.Bytes += frame.Bytes;
                    else total = frame.Bytes;
                    continue;
                }

                try
                {
                    var flags = entry.Attributes;
                    if ((flags & FileAttributes.Directory) != 0)
                    {
                        if ((flags & FileAttributes.ReparsePoint) == 0) stack.Push(new(entry.FullName));
                        else skipped++;
                        continue;
                    }
                    if (entry is not FileInfo file) continue;
                    files++;
                    if (files % 2048 == 0) progress?.Report(new(files, skipped, frame.Path));
                    if ((flags & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    long size = file.Length;
                    if (!NativeFiles.Measure(file.FullName, out var allocated, out var links, out var id))
                    {
                        skipped++;
                        continue;
                    }
                    if (links > 1 && !seenLinks.Add(id)) continue;
                    var row = new FileRow(file.FullName, allocated, size, file.LastWriteTimeUtc, Category(file.Extension), false);
                    frame.Bytes += allocated;
                    if (indexed.Count < IndexedFileLimit) indexed.Add(row);
                    else truncated = true;
                    largest.Enqueue(row, row.Bytes);
                    if (largest.Count > 1500) largest.Dequeue();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
            }
        }
        finally
        {
            while (stack.TryPop(out var pending)) pending.Dispose();
        }
        return new(root, total, files, skipped,
            largest.UnorderedItems.Select(item => item.Element).OrderByDescending(row => row.Bytes).ToArray(),
            folders, indexed, truncated);
    }

    private sealed class Frame(string path) : IDisposable
    {
        public string Path { get; } = path;
        public long Bytes;
        public IEnumerator<FileSystemInfo> Entries { get; } = new DirectoryInfo(path)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }).GetEnumerator();
        public void Dispose() => Entries.Dispose();
    }

    private static string Category(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => "Видео",
        ".jpg" or ".png" or ".webp" or ".jpeg" or ".heic" => "Изображения",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Архивы",
        ".pdf" or ".docx" or ".xlsx" or ".txt" or ".pptx" => "Документы",
        ".vhd" or ".vhdx" or ".vmdk" or ".iso" => "Образы дисков",
        ".tmp" => "Временные",
        ".exe" or ".dll" or ".msi" => "Программы",
        ".mp3" or ".wav" or ".flac" => "Аудио",
        _ => "Прочее"
    };
}

internal static class NativeFiles
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, [Out] char[] name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume([Out] char[] name, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolume(IntPtr handle, [Out] char[] name, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeName(string name, [Out] char[] paths, uint length, out uint needed);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out long callerFree, out long callerTotal, out long totalFree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long Allocation, End;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Serial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    public static string VolumeId(string root)
    {
        var buffer = new char[260];
        return GetVolumeNameForVolumeMountPoint(root, buffer, buffer.Length)
            ? new string(buffer).TrimEnd('\0') : root;
    }

    public static IEnumerable<string> VolumeMounts()
    {
        var volume = new char[260];
        var handle = FindFirstVolume(volume, (uint)volume.Length);
        if (handle == new IntPtr(-1)) yield break;
        try
        {
            do
            {
                var name = new string(volume, 0, Array.IndexOf(volume, '\0'));
                var paths = new char[512];
                if (!GetVolumePathNamesForVolumeName(name, paths, (uint)paths.Length, out uint needed) && needed > paths.Length)
                {
                    paths = new char[needed];
                    if (!GetVolumePathNamesForVolumeName(name, paths, needed, out _)) continue;
                }
                foreach (var path in new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries)) yield return path;
            } while (FindNextVolume(handle, volume, (uint)volume.Length));
        }
        finally { FindVolumeClose(handle); }
    }

    public static bool FreeSpace(string root, out long capacity, out long free)
    {
        if (!GetDiskFreeSpaceEx(root, out _, out capacity, out free)) return false;
        free = Math.Min(free, capacity);
        return capacity > 0;
    }

    public static bool Measure(string file, out long bytes, out uint links, out ulong id)
    {
        bytes = 0;
        links = 0;
        id = 0;
        if (!file.StartsWith(@"\\", StringComparison.Ordinal)) file = @"\\?\" + file;
        else if (!file.StartsWith(@"\\?\", StringComparison.Ordinal)) file = @"\\?\UNC\" + file[2..];
        using var handle = CreateFile(file, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 1, out var info, 24)) return false;
        bytes = info.Allocation;
        links = info.Links;
        if (!GetFileInformationByHandle(handle, out var fileInfo)) return false;
        id = ((ulong)fileInfo.FileIndexHigh << 32) | fileInfo.FileIndexLow;
        return true;
    }
}
