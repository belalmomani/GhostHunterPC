using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GhostHunterPC.Services;

public enum OrganizerCategory
{
    Images,
    Videos,
    Audio,
    Documents,
    Pdf,
    Archives,
    Code,
    Other
}

public enum OrganizerConflictResolution
{
    Skip,
    Rename,
    Replace
}

public enum OrganizerOperationStatus
{
    Moved,
    Skipped,
    Failed,
    Undone
}

public sealed record OrganizerPlanItem(
    string SourcePath,
    string DestinationPath,
    OrganizerCategory Category,
    long SizeBytes,
    DateTime LastModified,
    bool HasNameConflict,
    string TypeBasis);

public sealed record OrganizerPreview(
    IReadOnlyList<OrganizerPlanItem> Items,
    IReadOnlyList<string> Errors,
    long TotalBytes,
    int ConflictCount);

public sealed record OrganizerExecutionProgress(int Completed, int Total, string CurrentPath);

public sealed class OrganizerOperationLogEntry(
    string sourcePath,
    string destinationPath,
    OrganizerOperationStatus status,
    string detail,
    string? sourceSha256 = null,
    string? replacedFilePath = null,
    string? replacedFileSha256 = null)
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
    public string SourcePath { get; } = sourcePath;
    public string DestinationPath { get; set; } = destinationPath;
    public OrganizerOperationStatus Status { get; set; } = status;
    public string Detail { get; set; } = detail;
    public string? SourceSha256 { get; } = sourceSha256;
    public string? ReplacedFilePath { get; } = replacedFilePath;
    public string? ReplacedFileSha256 { get; } = replacedFileSha256;
}

public sealed class SmartFileOrganizerService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cpp", ".hpp", ".cs", ".fs", ".vb", ".java", ".kt", ".kts",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".pyw", ".rb", ".go", ".rs", ".php",
        ".html", ".htm", ".css", ".scss", ".less", ".json", ".xml", ".yaml", ".yml",
        ".toml", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".psd1"
    };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rtf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods",
        ".ppt", ".pptx", ".odp", ".csv"
    };
    private readonly List<OrganizerOperationLogEntry> _operationLog = [];

    public IReadOnlyList<OrganizerOperationLogEntry> OperationLog => _operationLog;

    public OrganizerPreview CreatePreview(
        string sourceFolder,
        string destinationFolder,
        bool organizeByMonth,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedFolders = null)
    {
        var source = ValidateFolder(sourceFolder, "Choose an existing source folder.");
        var destination = ValidateFolder(destinationFolder, "Choose an existing destination folder.", mustExist: false);
        if (IsProtectedPath(source) || IsProtectedPath(destination))
        {
            throw new UnauthorizedAccessException("Windows and installed application folders cannot be used as organizer source or destination.");
        }

        var excluded = excludedFolders ?? [];
        if (FolderExclusion.Contains(source, excluded)
            || FolderExclusion.Contains(destination, excluded))
        {
            throw new UnauthorizedAccessException("The source or destination folder is in the exclusion list.");
        }

        if (IsWithin(source, destination) || IsWithin(destination, source))
        {
            throw new ArgumentException("Source and destination folders must not overlap.");
        }

        var items = new List<OrganizerPlanItem>();
        var errors = new List<string>();
        var directories = new Stack<string>();
        var reservedTargets = new HashSet<string>(PathComparer);
        directories.Push(source);

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
                    var (category, typeBasis) = ClassifyFile(entry);
                    var targetDirectory = Path.Combine(destination, GetCategoryFolderName(category));
                    if (organizeByMonth)
                    {
                        targetDirectory = Path.Combine(
                            targetDirectory,
                            info.LastWriteTime.ToString("yyyy"),
                            info.LastWriteTime.ToString("MM"));
                    }

                    var target = Path.Combine(targetDirectory, info.Name);
                    var conflict = File.Exists(target) || !reservedTargets.Add(target);
                    items.Add(new OrganizerPlanItem(
                        entry,
                        target,
                        category,
                        info.Length,
                        info.LastWriteTime,
                        conflict,
                        typeBasis));
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    errors.Add($"{entry}: {exception.Message}");
                }
            }
        }

        return new OrganizerPreview(
            items,
            errors,
            items.Sum(item => item.SizeBytes),
            items.Count(item => item.HasNameConflict));
    }

    public static (OrganizerCategory Category, string Basis) ClassifyFile(string path)
    {
        Span<byte> header = stackalloc byte[512];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var read = stream.Read(header);
            var bytes = header[..read];
            var extension = Path.GetExtension(path);

            if (IsPng(bytes) || IsJpeg(bytes) || IsGif(bytes) || IsBmp(bytes) || IsWebp(bytes))
            {
                return (OrganizerCategory.Images, "Image file signature");
            }

            if (IsPdf(bytes))
            {
                return (OrganizerCategory.Pdf, "PDF file signature");
            }

            if (IsVideo(bytes))
            {
                return (OrganizerCategory.Videos, "Video container signature");
            }

            if (IsAudio(bytes))
            {
                return (OrganizerCategory.Audio, "Audio file signature");
            }

            if (IsArchive(bytes))
            {
                if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".odt", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".ods", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".odp", StringComparison.OrdinalIgnoreCase))
                {
                    return (OrganizerCategory.Documents, "Office document extension and ZIP container signature");
                }

                return (OrganizerCategory.Archives, "Archive file signature");
            }

            if (IsOleDocument(bytes))
            {
                return extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
                    ? (OrganizerCategory.Documents, "Office document extension and OLE container signature")
                    : (OrganizerCategory.Other, "OLE container signature; document subtype not confirmed");
            }

            if (IsRtf(bytes))
            {
                return (OrganizerCategory.Documents, "Rich Text file signature");
            }

            if (IsPlausibleText(bytes))
            {
                if (CodeExtensions.Contains(extension))
                {
                    return (OrganizerCategory.Code, "Text content and source/configuration extension");
                }

                if (DocumentExtensions.Contains(extension))
                {
                    return (OrganizerCategory.Documents, "Text content and document extension");
                }
            }

            return (OrganizerCategory.Other, "Content type not confirmed");
        }
    }

    public async Task ExecuteAsync(
        OrganizerPreview preview,
        OrganizerConflictResolution resolution,
        IProgress<OrganizerExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        foreach (var item in preview.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = item.DestinationPath;
            string? backupPath = null;
            string? backupHash = null;
            var replaceExisting = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    if (resolution == OrganizerConflictResolution.Skip)
                    {
                        _operationLog.Add(new OrganizerOperationLogEntry(
                            item.SourcePath, target, OrganizerOperationStatus.Skipped, "Name conflict; file skipped."));
                        continue;
                    }

                    if (resolution == OrganizerConflictResolution.Rename)
                    {
                        target = GetUniquePath(target);
                    }
                    else
                    {
                        backupPath = GetUniquePath(target + ".ghpc-backup");
                        backupHash = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
                        replaceExisting = true;
                    }
                }

                var sourceHash = await ComputeSha256Async(item.SourcePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (replaceExisting)
                    {
                        File.Move(item.DestinationPath, backupPath!);
                    }

                    File.Move(item.SourcePath, target);
                }
                catch
                {
                    if (backupPath is not null && File.Exists(backupPath) && !File.Exists(item.DestinationPath))
                    {
                        File.Move(backupPath, item.DestinationPath);
                    }

                    throw;
                }

                _operationLog.Add(new OrganizerOperationLogEntry(
                    item.SourcePath,
                    target,
                    OrganizerOperationStatus.Moved,
                    backupPath is not null
                        ? $"File moved; replaced file preserved at '{backupPath}'."
                        : target == item.DestinationPath ? "File moved." : "File moved using a unique name.",
                    sourceHash,
                    backupPath,
                    backupHash));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                var backupDetail = backupPath is not null && File.Exists(backupPath)
                    ? $" Preserved backup: '{backupPath}'."
                    : string.Empty;
                _operationLog.Add(new OrganizerOperationLogEntry(
                    item.SourcePath, target, OrganizerOperationStatus.Failed, exception.Message + backupDetail));
            }
            finally
            {
                completed++;
                progress?.Report(new OrganizerExecutionProgress(completed, preview.Items.Count, item.SourcePath));
            }
        }
    }

    public async Task<bool> UndoLastAsync(CancellationToken cancellationToken)
    {
        var operation = _operationLog.LastOrDefault(entry => entry.Status == OrganizerOperationStatus.Moved);
        if (operation is null)
        {
            return false;
        }

        if (File.Exists(operation.SourcePath) || !File.Exists(operation.DestinationPath))
        {
            throw new IOException("Undo was not performed: the source path is occupied or the organized file is missing.");
        }

        if (operation.SourceSha256 is null
            || !string.Equals(
                await ComputeSha256Async(operation.DestinationPath, cancellationToken).ConfigureAwait(false),
                operation.SourceSha256,
                StringComparison.Ordinal))
        {
            throw new IOException("Undo was not performed: the organized file changed after it was moved.");
        }

        if (operation.ReplacedFilePath is not null)
        {
            if (!File.Exists(operation.ReplacedFilePath)
                || !string.Equals(
                    await ComputeSha256Async(operation.ReplacedFilePath, cancellationToken).ConfigureAwait(false),
                    operation.ReplacedFileSha256,
                    StringComparison.Ordinal))
            {
                throw new IOException("Undo was not performed: the replaced file backup is missing or changed.");
            }
        }

        File.Move(operation.DestinationPath, operation.SourcePath);
        try
        {
            if (operation.ReplacedFilePath is not null)
            {
                File.Move(operation.ReplacedFilePath, operation.DestinationPath);
            }
        }
        catch
        {
            if (!File.Exists(operation.DestinationPath) && File.Exists(operation.SourcePath))
            {
                File.Move(operation.SourcePath, operation.DestinationPath);
            }

            throw;
        }

        operation.Status = OrganizerOperationStatus.Undone;
        operation.Detail = "Undo completed after file integrity checks.";
        return true;
    }

    public static string GetCategoryFolderName(OrganizerCategory category) => category switch
    {
        OrganizerCategory.Images => "Images",
        OrganizerCategory.Videos => "Videos",
        OrganizerCategory.Audio => "Audio",
        OrganizerCategory.Documents => "Documents",
        OrganizerCategory.Pdf => "PDF",
        OrganizerCategory.Archives => "Archives",
        OrganizerCategory.Code => "Code",
        _ => "Other"
    };

    private static string ValidateFolder(string path, string message, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(message);
        }

        var fullPath = Path.GetFullPath(path);
        if (mustExist && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(message);
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool IsWithin(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        if (!normalizedDirectory.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedDirectory += Path.DirectorySeparatorChar;
        }
        return PathComparer.Equals(path, directory)
            || path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedPath(string path)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        return protectedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => IsWithin(Path.GetFullPath(path), Path.GetFullPath(root)));
    }

    private static string GetUniquePath(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
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

    private static bool IsPlausibleText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Contains((byte)0))
        {
            return false;
        }

        try
        {
            var encoding = new UTF8Encoding(false, true);
            var text = encoding.GetString(bytes);
            return text.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t' or '\f');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF });

    private static bool IsGif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));

    private static bool IsBmp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2 && bytes[..2].SequenceEqual("BM"u8);

    private static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);

    private static bool IsPdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8);

    private static bool IsVideo(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("AVI "u8))
        || (bytes.Length >= 12
            && bytes[4..8].SequenceEqual("ftyp"u8)
            && !IsMp4AudioBrand(bytes[8..12]))
        || (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }));

    private static bool IsAudio(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8))
        || (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WAVE"u8))
        || (bytes.Length >= 4 && bytes[..4].SequenceEqual("fLaC"u8))
        || (bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8))
        || (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && IsMp4AudioBrand(bytes[8..12]))
        || (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0);

    private static bool IsMp4AudioBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) || brand.SequenceEqual("M4P "u8);

    private static bool IsArchive(ReadOnlySpan<byte> bytes) =>
        (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        || (bytes.Length >= 6 && bytes[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        || (bytes.Length >= 4 && bytes[..4].SequenceEqual("Rar!"u8))
        || (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        || (bytes.Length >= 262 && bytes[257..262].SequenceEqual("ustar"u8));

    private static bool IsOleDocument(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    private static bool IsRtf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 && bytes[..5].SequenceEqual("{\\rtf"u8);

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException;
}
