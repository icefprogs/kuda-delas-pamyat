using System.Runtime.InteropServices;
using DiskSpace;

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

var sandbox = Path.Combine(Path.GetTempPath(), "disk-space-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    var folder = Directory.CreateDirectory(Path.Combine(sandbox, "files"));
    var random = new byte[16_384];
    new Random(80).NextBytes(random);
    var file = Path.Combine(folder.FullName, "a.bin");
    File.WriteAllBytes(file, random);
    File.WriteAllBytes(Path.Combine(folder.FullName, "b.bin"), random);
    File.WriteAllBytes(Path.Combine(folder.FullName, "other.bin"), new byte[random.Length]);
    var link = Path.Combine(folder.FullName, "hardlink.bin");
    Require(NativeHardLink.CreateHardLink(link, file, IntPtr.Zero), "create test hardlink");
    using var pause = new ManualResetEventSlim(true);
    var result = DiskScanner.Scan(sandbox, pause, CancellationToken.None);
    Require(result.Files == 4 && result.AllFiles.Count == 3, "hardlinks are counted once");
    Require(result.Folders.Single().Bytes == result.Rows.Sum(row => row.Bytes) && result.Bytes > 0, "allocated size aggregates correctly");
    Require(Duplicates.Find(result.AllFiles, CancellationToken.None).Length == 2, "duplicate hash distinguishes equal-size files");
    Require(DiskScanner.Volumes().Any(item => item.Ready && item.Capacity >= item.Free && item.Free >= 0), "native volume enumeration");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        DiskScanner.Scan(sandbox, pause, cancelled.Token);
        throw new InvalidOperationException("Scanner ignored cancellation");
    }
    catch (OperationCanceledException) { Console.WriteLine("PASS cancellation"); }

    var outside = Path.Combine(sandbox, "outside.tmp");
    File.WriteAllText(outside, "sentinel");
    var wrong = new Candidate(outside, 8, 8, DateTime.UtcNow.AddDays(-15), "test");
    Require(Cleanup.Remove(wrong, 7, []).Bytes == 0 && File.Exists(outside), "cleanup refuses files outside the approved root");
    Require(Cleanup.Remove(wrong with { Demo = true }, 7, []).Bytes == 8 && File.Exists(outside), "demo cleanup does not change actual files");
    Require(LocalStore.IsUnder(file, sandbox) && !LocalStore.IsUnder(sandbox + "-other", sandbox), "path containment is segment-based");
    var temp = Directory.CreateDirectory(Path.Combine(sandbox, "temp", "nested"));
    var old = Path.Combine(temp.FullName, "stale.temp");
    File.WriteAllBytes(old, new byte[8192]);
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
    File.WriteAllText(Path.Combine(temp.FullName, "fresh.tmp"), "keep");
    File.WriteAllText(Path.Combine(temp.FullName, "notes.docx"), "keep");
    var cache = Directory.CreateDirectory(Path.Combine(sandbox, "cache"));
    var cacheFile = Path.Combine(cache.FullName, "index");
    File.WriteAllBytes(cacheFile, new byte[8192]);
    File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddDays(-10));
    CleanupRule[] rules =
    [
        new(Path.Combine(sandbox, "temp"), CleanupKind.Temp, "Test temp"),
        new(cache.FullName, CleanupKind.Browser, "Test cache")
    ];
    var preview = Cleanup.FindForRules(rules, 7, [], CancellationToken.None);
    Require(preview.Length == 2 && preview.Any(item => item.Path == cacheFile), "manual preview finds nested temporary files and browser cache");
    Require(Cleanup.FindForRules(rules, 7, [temp.FullName], CancellationToken.None, true).Length == 0, "automatic rules exclude browsers and ignored folders");
    var automatic = Cleanup.FindForRules(rules, 7, [], CancellationToken.None, true);
    Require(automatic.Length == 1 && automatic[0].Path == old, "automatic preview selects eligible nested files only");
    using (var handle = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        Require(Cleanup.Remove(automatic[0], 7, []).Bytes == 0 && File.Exists(old), "live file remains untouched");
    var resultCleanup = Cleanup.Remove(automatic[0], 7, []);
    Require(resultCleanup.Status == "Удалён" && !File.Exists(old) && File.Exists(cacheFile), "cleanup removes fixture and preserves other categories");
    Require(Recycle.PathRestriction(Path.GetPathRoot(sandbox)!) is not null &&
        Recycle.PathRestriction(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is not null, "recycle protects disk roots and Windows");
    var now = DateTimeOffset.UtcNow;
    Require(CleanupSchedule.Empty.Due(now) && !new CleanupSchedule(now, 0, 0, "done").Due(now)
        && new CleanupSchedule(now.AddMinutes(-10), 0, 0, "retry", now.AddMinutes(-5)).Due(now), "auto scan schedule records first run, next day and retry");
    Require(Review.Apps(CancellationToken.None).All(app => !string.IsNullOrWhiteSpace(app.Name)), "installed-app inventory returns labeled rows");
}
finally { Directory.Delete(sandbox, true); }

internal static class NativeHardLink
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
