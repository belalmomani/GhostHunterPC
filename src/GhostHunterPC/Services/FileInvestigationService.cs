using System.IO;
using System.Security.Cryptography;

namespace GhostHunterPC.Services;

public sealed class FileInvestigationService
{
    private readonly IFileSignatureVerifier _signatureVerifier;

    public FileInvestigationService(IFileSignatureVerifier? signatureVerifier = null)
    {
        _signatureVerifier = signatureVerifier ?? new WinTrustFileSignatureVerifier();
    }

    public async Task<FileInvestigationScanResult> ScanFolderAsync(
        string folderPath,
        IProgress<FileInvestigationProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludedFolders = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("Choose an existing folder before scanning.");
        }

        var files = new List<FileInvestigationItem>();
        var errors = new List<string>();
        var directories = new Stack<string>();
        var excluded = excludedFolders ?? [];
        var root = Path.GetFullPath(folderPath);
        if (FolderExclusion.Contains(root, excluded))
        {
            throw new UnauthorizedAccessException("The selected folder is in the exclusion list.");
        }

        directories.Push(root);

        while (directories.TryPop(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
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
                        progress?.Report(new FileInvestigationProgress(files.Count, null, errors.Count));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if ((attributes & FileAttributes.ReparsePoint) == 0
                            && !FolderExclusion.Contains(entry, excluded))
                        {
                            directories.Push(entry);
                        }
                        else
                        {
                            errors.Add($"{entry}: Reparse points are not followed.");
                            progress?.Report(new FileInvestigationProgress(files.Count, null, errors.Count));
                        }

                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        errors.Add($"{entry}: Reparse points are not followed.");
                        progress?.Report(new FileInvestigationProgress(files.Count, null, errors.Count));
                        continue;
                    }

                    var item = await InspectFileAsync(entry, cancellationToken).ConfigureAwait(false);
                    files.Add(item);
                    if (item.Error is not null)
                    {
                        errors.Add($"{entry}: {item.Error}");
                    }

                    progress?.Report(new FileInvestigationProgress(files.Count, item, errors.Count));
                }
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                errors.Add($"{currentDirectory}: {exception.Message}");
                progress?.Report(new FileInvestigationProgress(files.Count, null, errors.Count));
            }
        }

        return new FileInvestigationScanResult(files, errors);
    }

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private Task<FileInvestigationItem> InspectFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        try
        {
            var fileInfo = new FileInfo(path);
            var length = fileInfo.Length;
            var modified = fileInfo.LastWriteTime;
            var (detectedType, warning) = DetectFileType(path, extension);
            var signature = _signatureVerifier.Verify(path);
            var signatureWarning = signature.Status switch
            {
                FileSignatureStatus.Unsigned => "No trusted digital signature was found; this alone does not indicate malware.",
                FileSignatureStatus.VerificationUnavailable => string.IsNullOrWhiteSpace(signature.Detail)
                    ? "Digital signature could not be verified."
                    : $"Digital signature could not be verified: {signature.Detail}",
                FileSignatureStatus.Signed when !string.IsNullOrWhiteSpace(signature.Detail) =>
                    $"The digital signature is valid, but publisher information could not be read: {signature.Detail}",
                _ => null
            };
            var warningText = string.Join(
                " ",
                new[] { warning, signatureWarning }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return Task.FromResult(new FileInvestigationItem(
                name,
                path,
                length,
                extension,
                modified,
                detectedType,
                signature.Status,
                signature.Publisher,
                string.IsNullOrEmpty(warningText) ? null : warningText,
                null));
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return Task.FromResult(new FileInvestigationItem(
                name,
                path,
                null,
                extension,
                null,
                "Unknown",
                FileSignatureStatus.VerificationUnavailable,
                null,
                "File could not be fully inspected.",
                exception.Message));
        }
    }

    private static (string Type, string? Warning) DetectFileType(string path, string extension)
    {
        Span<byte> header = stackalloc byte[16];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var read = stream.Read(header);
            var type = DetectByHeader(header[..read]);
            if (type is null)
            {
                return (string.IsNullOrEmpty(extension) ? "Unknown" : $"Unknown ({extension})", null);
            }

            var expectedExtension = type.Value.Extension;
            var warning = expectedExtension is not null
                && !string.Equals(extension, expectedExtension, StringComparison.OrdinalIgnoreCase)
                    ? $"Detected content is {type.Value.Name}, but the filename extension is '{(string.IsNullOrEmpty(extension) ? "(none)" : extension)}'."
                    : null;
            return (type.Value.Name, warning);
        }
    }

    private static (string Name, string? Extension)? DetectByHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == 0x4D && header[1] == 0x5A)
        {
            return ("Windows executable (PE)", ".exe");
        }

        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            return ("OLE compound document", null);
        }

        if (header.Length >= 5 && header[..5].SequenceEqual("%PDF-"u8))
        {
            return ("PDF document", ".pdf");
        }

        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ("PNG image", ".png");
        }

        if (header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return ("JPEG image", ".jpg");
        }

        if (header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return ("ZIP archive", ".zip");
        }

        if (header.Length >= 6 && header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        {
            return ("7-Zip archive", ".7z");
        }

        if (header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21 }))
        {
            return ("RAR archive", ".rar");
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return ("GZIP archive", ".gz");
        }

        if (header.Length >= 2 && header[..2].SequenceEqual("BM"u8))
        {
            return ("BMP image", ".bmp");
        }

        if (header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..12].SequenceEqual("WEBP"u8))
        {
            return ("WebP image", ".webp");
        }

        if (header.Length >= 5 && header[..5].SequenceEqual("{\\rtf"u8))
        {
            return ("Rich Text document", ".rtf");
        }

        if (header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }))
        {
            return ("ELF executable", null);
        }

        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
        {
            return ("GIF image", ".gif");
        }

        return null;
    }

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or NotSupportedException
        or ArgumentException;
}

public sealed record FileInvestigationItem(
    string Name,
    string Path,
    long? SizeBytes,
    string Extension,
    DateTime? LastModified,
    string DetectedType,
    FileSignatureStatus SignatureStatus,
    string? Publisher,
    string? Warning,
    string? Error);

public sealed record FileInvestigationProgress(int FilesScanned, FileInvestigationItem? LatestItem, int Errors);

public sealed record FileInvestigationScanResult(
    IReadOnlyList<FileInvestigationItem> Files,
    IReadOnlyList<string> Errors);
