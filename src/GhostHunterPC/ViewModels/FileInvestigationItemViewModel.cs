using System.Globalization;
using System.IO;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class FileInvestigationItemViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private string? _sha256;
    private bool _isHashing;
    private string? _hashError;

    public FileInvestigationItemViewModel(FileInvestigationItem item, UiLocalizationService localization)
    {
        Item = item;
        _localization = localization;
    }

    public FileInvestigationItem Item { get; }
    public string Name => Item.Name;
    public string Path => Item.Path;
    public string Extension => string.IsNullOrEmpty(Item.Extension) ? _localization.GetText("no-extension") : Item.Extension;
    public string DetectedType => Item.DetectedType;
    public long SortSizeBytes => Item.SizeBytes ?? -1;
    public DateTime SortLastModified => Item.LastModified ?? DateTime.MinValue;
    public FileSignatureStatus SortSignatureStatus => Item.SignatureStatus;
    public string Publisher => Item.Publisher ?? _localization.GetText("unavailable");
    public string Warning => Item.Warning ?? Item.Error ?? string.Empty;
    public string SignatureLabel => Item.SignatureStatus switch
    {
        FileSignatureStatus.Signed => _localization.GetText("file-signature-signed"),
        FileSignatureStatus.Unsigned => _localization.GetText("file-signature-unsigned"),
        _ => _localization.GetText("file-signature-unavailable")
    };
    public string SizeLabel => Item.SizeBytes is long size
        ? $"{size.ToString("N0", CultureInfo.CurrentCulture)} B"
        : _localization.GetText("unavailable");
    public string ModifiedLabel => Item.LastModified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        ?? _localization.GetText("unavailable");
    public string Sha256 => _sha256 ?? _hashError ?? _localization.GetText(_isHashing ? "file-hash-loading" : "file-hash-not-calculated");
    public string HashStatusLabel => _localization.GetText(_isHashing ? "file-hash-loading" : "file-hash-not-calculated");
    public bool IsHashing => _isHashing;

    public void RefreshLocalizedValues()
    {
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(SignatureLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(Sha256));
        OnPropertyChanged(nameof(HashStatusLabel));
    }

    public async Task CalculateHashAsync(FileInvestigationService service, CancellationToken cancellationToken)
    {
        _isHashing = true;
        _hashError = null;
        OnPropertyChanged(nameof(Sha256));
        OnPropertyChanged(nameof(HashStatusLabel));

        try
        {
            _sha256 = await service.ComputeSha256Async(Item.Path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException)
        {
            _hashError = $"{_localization.GetText("file-hash-failed")}: {exception.Message}";
        }
        finally
        {
            _isHashing = false;
            OnPropertyChanged(nameof(Sha256));
            OnPropertyChanged(nameof(HashStatusLabel));
        }
    }
}
