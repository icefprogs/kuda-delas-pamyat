using System.Text.Json;

namespace DiskSpace;

internal sealed record FolderSize(string Path, long Bytes);
internal sealed record Snapshot(string VolumeId, string Scope, DateTimeOffset Time, long Free, long Capacity, int Skipped, FolderSize[] Folders, string? Mark);
internal sealed record CleanupLog(DateTimeOffset Time, string Path, string Status, long Bytes);
internal sealed record Preferences(bool Watch = false, bool Notify = true, bool AutoClean = false, bool Animate = true,
    int MinimumAge = 7, int LimitMb = 512, string Theme = "Системная", string[]? Excluded = null);

internal sealed class LocalStore
{
    public string Location { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KudaDelasPamyat");
    public string? Error { get; private set; }

    public T Read<T>(string name, T fallback)
    {
        try
        {
            var path = Path.Combine(Location, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? fallback : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Не удалось прочитать " + name;
            return fallback;
        }
    }

    public void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Location);
        var path = Path.Combine(Location, name);
        var staging = path + ".new";
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        File.Move(staging, path, true);
    }

    public static Snapshot MakeSnapshot(Volume volume, ScanResult scan, string? mark = null)
    {
        var folders = scan.Folders.Where(row => SamePath(Path.GetDirectoryName(row.Path), scan.Scope))
            .OrderByDescending(row => row.Bytes).Take(1000)
            .Select(row => new FolderSize(row.Path, row.Bytes)).ToArray();
        return new(volume.Id, scan.Scope, DateTimeOffset.Now, volume.Free, volume.Capacity, scan.Skipped, folders, mark);
    }

    public static bool SamePath(string? a, string? b) =>
        string.Equals(a is null ? null : Path.TrimEndingDirectorySeparator(a),
            b is null ? null : Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != "." && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
