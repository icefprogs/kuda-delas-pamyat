using System.Security.Cryptography;

namespace DiskSpace;

internal record DuplicateRow(string Group, string Path, long Bytes);

internal static class Duplicates
{
    public static DuplicateRow[] Find(IEnumerable<FileRow> files, CancellationToken token)
    {
        var result = new List<DuplicateRow>();
        var buffer = new byte[256 * 1024];
        foreach (var sizes in files.Where(file => file.Logical > 0).GroupBy(file => file.Logical).Where(group => group.Count() > 1))
        {
            var hashes = new Dictionary<string, List<FileRow>>();
            foreach (var file in sizes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var current = new FileInfo(file.Path);
                    if ((current.Attributes & (FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0
                        || current.Length != file.Logical || current.LastWriteTimeUtc != file.Changed) continue;
                    using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int size;
                    while ((size = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        hash.AppendData(buffer.AsSpan(0, size));
                    }
                    var key = Convert.ToHexString(hash.GetHashAndReset());
                    if (!hashes.TryGetValue(key, out var matches)) hashes.Add(key, matches = []);
                    matches.Add(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            foreach (var (key, matches) in hashes.Where(pair => pair.Value.Count > 1))
                result.AddRange(matches.Select(file => new DuplicateRow(key[..10], file.Path, file.Logical)));
        }
        return result.OrderByDescending(row => row.Bytes).ThenBy(row => row.Group).ToArray();
    }
}
