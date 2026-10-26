using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace GhostHunterPC.Services;

public sealed record ForgottenFile(
    string Path,
    long SizeBytes,
    DateTime LastModified,
    string Sha256);

public sealed record DuplicateFileGroup(
    string Sha256,
    long SizeBytes,
    IReadOnlyList<ForgottenFile> Files,
    string RecommendedKeepPath)
{
    public long PotentialSavingsBytes => SizeBytes * Math.Max(0, Files.Count - 1);
}

public sealed record ForgottenFilesProgress(int FilesDiscovered, int FilesHashed, string CurrentPath);
public sealed record RecycleFileRequest(string Path, string ExpectedSha256);
public sealed record RecycleBatchResult(IReadOnlyList<RecycleFileResult> Results, bool WasCancelled);

public sealed record ForgottenFilesScanResult(
    IReadOnlyList<DuplicateFileGroup> DuplicateGroups,
    IReadOnlyList<ForgottenFile> OldFiles,
    IReadOnlyList<string> Errors,
    long CandidateBytesHashed);

public sealed record RecycleFileResult(string Path, bool Succeeded, string? Error);

public sealed class ForgottenFilesService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<ForgottenFilesScanResult> ScanAsync(
        string folderPath,
        int oldAfterMonths,
        IProgress<ForgottenFilesProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedFolders = null)
    {
        if (oldAfterMonths is < 1 or > 1200)
        {
            throw new ArgumentOutOfRangeException(nameof(oldAfterMonths), "Choose a duration from 1 to 1200 months.");
        }

        var root = ValidateScanRoot(folderPath);
        var errors = new List<string>();
        var files = new List<ForgottenFile>();
        var excluded = excludedFolders ?? [];
        if (FolderExclusion.Contains(root, excluded))
        {
            throw new UnauthorizedAccessException("The selected folder is in the exclusion list.");
        }

        var oldCutoff = DateTime.Now.AddMonths(-oldAfterMonths);
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                errors.Add($"{directory}: {exception.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    errors.Add($"{entry}: {exception.Message}");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"{entry}: Symbolic links and other reparse points are not followed.");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IsProtectedPath(entry) && !FolderExclusion.Contains(entry, excluded))
                    {
                        directories.Push(entry);
                    }

                    continue;
                }

                try
                {
                    var info = new FileInfo(entry);
                    files.Add(new ForgottenFile(entry, info.Length, info.LastWriteTime, string.Empty));
                    progress?.Report(new ForgottenFilesProgress(files.Count, 0, entry));
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    errors.Add($"{entry}: {exception.Message}");
                }
            }
        }

        var duplicateGroups = new List<DuplicateFileGroup>();
        var candidateBytes = 0L;
        var hashedFiles = 0;
        var candidates = files.GroupBy(file => file.SizeBytes).Where(group => group.Count() > 1);
        foreach (var sizeGroup in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hashGroups = new Dictionary<string, List<ForgottenFile>>(StringComparer.Ordinal);
            foreach (var file in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateBytes = checked(candidateBytes + file.SizeBytes);
                try
                {
                    var hash = await ComputeSha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                    var hashed = file with { Sha256 = hash };
                    if (!hashGroups.TryGetValue(hash, out var matchingFiles))
                    {
                        matchingFiles = [];
                        hashGroups.Add(hash, matchingFiles);
                    }

                    matchingFiles.Add(hashed);
                    hashedFiles++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    errors.Add($"{file.Path}: {exception.Message}");
                }

                progress?.Report(new ForgottenFilesProgress(files.Count, hashedFiles, file.Path));
            }

            foreach (var (hash, matchingFiles) in hashGroups.Where(pair => pair.Value.Count > 1))
            {
                var ordered = matchingFiles
                    .OrderBy(file => file.LastModified)
                    .ThenBy(file => file.Path, PathComparer)
                    .ToArray();
                duplicateGroups.Add(new DuplicateFileGroup(hash, sizeGroup.Key, ordered, ordered[0].Path));
            }
        }

        var oldFiles = files
            .Where(file => file.LastModified < oldCutoff)
            .OrderBy(file => file.LastModified)
            .ToArray();
        return new ForgottenFilesScanResult(
            duplicateGroups.OrderByDescending(group => group.PotentialSavingsBytes).ToArray(),
            oldFiles,
            errors,
            candidateBytes);
    }

    public async Task<RecycleBatchResult> MoveToRecycleBinAsync(
        IEnumerable<RecycleFileRequest> selectedFiles,
        IProgress<RecycleFileResult>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<RecycleFileResult>();
        foreach (var file in selectedFiles.DistinctBy(file => file.Path, PathComparer))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new RecycleBatchResult(results, WasCancelled: true);
            }

            try
            {
                if (!File.Exists(file.Path))
                {
                    throw new FileNotFoundException("The selected file no longer exists.", file.Path);
                }

                var currentHash = await ComputeSha256Async(file.Path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentHash, file.ExpectedSha256, StringComparison.Ordinal))
                {
                    throw new IOException("File contents changed after the scan; it was not moved to the Recycle Bin.");
                }

                await Task.Run(
                    () => FileSystem.DeleteFile(file.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin),
                    cancellationToken).ConfigureAwait(false);
                results.Add(new RecycleFileResult(file.Path, true, null));
            }
            catch (OperationCanceledException)
            {
                return new RecycleBatchResult(results, WasCancelled: true);
            }
            catch (Exception exception) when (IsExpectedFileException(exception) || exception is InvalidOperationException)
            {
                results.Add(new RecycleFileResult(file.Path, false, exception.Message));
            }

            var result = results[^1];
            progress?.Report(result);
        }

        return new RecycleBatchResult(results, WasCancelled: false);
    }

    private static string ValidateScanRoot(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Choose an existing folder before scanning.", nameof(folderPath));
        }

        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Choose an existing folder before scanning.");
        }

        if (IsProtectedPath(root))
        {
            throw new UnauthorizedAccessException("Windows and installed application folders are excluded from scanning.");
        }

        return Path.TrimEndingDirectorySeparator(root);
    }

    private static bool IsProtectedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root =>
            {
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                return PathComparer.Equals(fullPath, normalizedRoot)
                    || fullPath.StartsWith(
                        normalizedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            });
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException;
}
