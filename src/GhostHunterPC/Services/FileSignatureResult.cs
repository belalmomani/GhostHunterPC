namespace GhostHunterPC.Services;

public enum FileSignatureStatus
{
    Signed,
    Unsigned,
    VerificationUnavailable
}

public sealed record FileSignatureResult(FileSignatureStatus Status, string? Publisher, string? Detail);

public interface IFileSignatureVerifier
{
    FileSignatureResult Verify(string filePath);
}
