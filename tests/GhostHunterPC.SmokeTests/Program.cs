using GhostHunterPC.Services;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

var checks = 0;
var doItForMeService = new DoItForMeService();
var doItForMeTasks = doItForMeService.GetTasks();
var allowedTaskPages = new HashSet<string>(
    ["organizer", "investigator", "duplicates", "fingerprint", "dashboard"],
    StringComparer.Ordinal);
if (doItForMeTasks.Count != 5
    || doItForMeTasks.Select(task => task.Id).Distinct().Count() != 5
    || doItForMeTasks.Any(task => !allowedTaskPages.Contains(task.TargetPageKey)))
{
    throw new InvalidOperationException("Do It For Me must expose five distinct tasks routed only to existing pages.");
}

checks++;
foreach (var task in doItForMeTasks)
{
    var plan = doItForMeService.CreatePlan(task.Id);
    var confirmationRejected = false;
    try
    {
        doItForMeService.AuthorizePlan(plan, confirmed: false);
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("confirmation", StringComparison.OrdinalIgnoreCase))
    {
        confirmationRejected = true;
    }

    if (!confirmationRejected)
    {
        throw new InvalidOperationException($"Task {task.Id} was authorized without confirmation.");
    }

    checks++;
    if (task.IsAvailable)
    {
        var authorized = doItForMeService.AuthorizePlan(plan, confirmed: true);
        if (authorized != task)
        {
            throw new InvalidOperationException($"Task {task.Id} resolved to an unexpected action.");
        }

        checks++;
    }
    else
    {
        var unavailableRejected = false;
        try
        {
            doItForMeService.AuthorizePlan(plan, confirmed: true);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("not available", StringComparison.OrdinalIgnoreCase))
        {
            unavailableRejected = true;
        }

        if (!unavailableRejected)
        {
            throw new InvalidOperationException($"Unavailable task {task.Id} was authorized.");
        }

        checks++;
    }
}

var organizerPlan = doItForMeService.CreatePlan(DoItForMeTaskId.OrganizeDownloads);
var forgedPlanAuthorized = true;
try
{
    doItForMeService.AuthorizePlan(
        organizerPlan with { Task = organizerPlan.Task with { TargetPageKey = "settings" } },
        confirmed: true);
}
catch (InvalidOperationException)
{
    forgedPlanAuthorized = false;
}

if (forgedPlanAuthorized)
{
    throw new InvalidOperationException("A plan modified to route outside the fixed allow-list was authorized.");
}

checks++;
try
{
    doItForMeService.CreatePlan((DoItForMeTaskId)int.MaxValue);
    throw new InvalidOperationException("An unknown task was accepted.");
}
catch (ArgumentOutOfRangeException)
{
    checks++;
}

Console.WriteLine("PASS: predefined Do It For Me actions require confirmation and reject unavailable or unknown tasks.");
var deviceInfo = new DeviceInfoService().Read();
checks++;

if (deviceInfo.LogicalProcessors is <= 0)
{
    throw new InvalidOperationException("Logical processor count must be positive when available.");
}

checks++;
if (deviceInfo.TotalMemoryBytes is ulong totalMemory
    && deviceInfo.AvailableMemoryBytes is ulong availableMemory
    && (totalMemory == 0 || availableMemory > totalMemory))
{
    throw new InvalidOperationException("Memory values are inconsistent.");
}

checks++;
if (deviceInfo.Uptime is TimeSpan uptime && uptime < TimeSpan.Zero)
{
    throw new InvalidOperationException("System uptime cannot be negative.");
}

checks++;
foreach (var drive in deviceInfo.Drives)
{
    if (string.IsNullOrWhiteSpace(drive.Name)
        || drive.TotalBytes == 0
        || drive.AvailableBytes > drive.TotalBytes)
    {
        throw new InvalidOperationException($"Drive information is inconsistent for '{drive.Name}'.");
    }

    checks++;
}

var localization = new UiLocalizationService();
if (localization.GetText("dashboard") != "لوحة التحكم")
{
    throw new InvalidOperationException("Arabic localization did not load.");
}

localization.ToggleLanguage();
if (localization.GetText("dashboard") != "Dashboard")
{
    throw new InvalidOperationException("English localization did not load after switching.");
}

checks += 2;
if (localization.GetText("report-title") != "Create a report"
    || localization.GetText("settings-language") != "Interface language"
    || CultureInfo.CurrentCulture.Name != "en-US")
{
    throw new InvalidOperationException("English external resources or locale formatting did not load.");
}

localization.SetLanguage("ar");
if (localization.GetText("report-title") != "إنشاء تقرير"
    || localization.GetText("settings-language") != "لغة الواجهة"
    || CultureInfo.CurrentCulture.Name != "ar-SA"
    || !localization.IsArabic)
{
    throw new InvalidOperationException("Arabic resources, culture, or RTL state did not load.");
}

checks++;
localization.SetLanguage("en");
using (var englishResources = JsonDocument.Parse(await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Resources", "UiStrings.en.json"))))
using (var arabicResources = JsonDocument.Parse(await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Resources", "UiStrings.ar.json"))))
{
    var englishKeys = englishResources.RootElement.EnumerateObject()
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
    var arabicKeys = arabicResources.RootElement.EnumerateObject()
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
    if (!englishKeys.SetEquals(arabicKeys))
    {
        throw new InvalidOperationException("Arabic and English resources do not have matching keys.");
    }

    foreach (var key in englishKeys)
    {
        localization.SetLanguage("en");
        _ = localization.GetText(key);
        localization.SetLanguage("ar");
        _ = localization.GetText(key);
    }

    checks += englishKeys.Count;
}

localization.SetLanguage("en");
var settingsTestRoot = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-Settings-{Guid.NewGuid():N}");
Directory.CreateDirectory(settingsTestRoot);
try
{
    var settingsPath = Path.Combine(settingsTestRoot, "settings.json");
    var persistedSettings = new UserSettingsService(settingsPath);
    persistedSettings.Settings.Language = "ar";
    persistedSettings.Settings.LightTheme = true;
    persistedSettings.Settings.MonitorIntervalSeconds = "10";
    persistedSettings.Settings.SaveHistory = false;
    persistedSettings.Settings.ExcludedFolders.Add(settingsTestRoot);
    persistedSettings.Save();
    var loadedSettings = new UserSettingsService(settingsPath);
    if (loadedSettings.Settings.Language != "ar"
        || !loadedSettings.Settings.LightTheme
        || loadedSettings.Settings.MonitorIntervalSeconds != "10"
        || loadedSettings.Settings.SaveHistory
        || !loadedSettings.Settings.ExcludedFolders.Contains(settingsTestRoot, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("User settings did not persist and reload correctly.");
    }

    checks++;
    if (!FolderExclusion.Contains(Path.Combine(settingsTestRoot, "child"), loadedSettings.Settings.ExcludedFolders)
        || FolderExclusion.Contains(settingsTestRoot + "-sibling", loadedSettings.Settings.ExcludedFolders))
    {
        throw new InvalidOperationException("Folder exclusions matched a sibling or missed a child.");
    }

    checks++;
    var scanRoot = Path.Combine(settingsTestRoot, "scan");
    var excludedChild = Path.Combine(scanRoot, "excluded");
    Directory.CreateDirectory(excludedChild);
    await File.WriteAllTextAsync(Path.Combine(scanRoot, "included.txt"), "visible");
    await File.WriteAllTextAsync(Path.Combine(excludedChild, "private.txt"), "excluded");
    var excludedScan = await new FileInvestigationService(new FakeSignatureVerifier())
        .ScanFolderAsync(scanRoot, null, CancellationToken.None, [excludedChild]);
    if (excludedScan.Files.Count != 1 || excludedScan.Files[0].Name != "included.txt")
    {
        throw new InvalidOperationException("Configured folders were not excluded from recursive file inspection.");
    }

    checks++;
    var reportService = new ReportExportService();
    var report = new ReportDocument("Test", DateTimeOffset.UnixEpoch, [new ReportRow("A", "quoted,item", "line\nvalue")]);
    var csvPath = Path.Combine(settingsTestRoot, "report.csv");
    var jsonPath = Path.Combine(settingsTestRoot, "report.json");
    reportService.ExportCsv(csvPath, report);
    reportService.ExportJson(jsonPath, report);
    var csv = await File.ReadAllTextAsync(csvPath);
    var json = await File.ReadAllTextAsync(jsonPath);
    if (!csv.Contains("\"quoted,item\"", StringComparison.Ordinal)
        || !csv.Contains("\"line\nvalue\"", StringComparison.Ordinal)
        || !json.Contains("\"Report\": \"Test\"", StringComparison.Ordinal)
        || !json.Contains("quoted,item", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("CSV escaping or JSON report export failed.");
    }

    checks += 2;
}
finally
{
    if (Directory.Exists(settingsTestRoot))
    {
        Directory.Delete(settingsTestRoot, recursive: true);
    }
}

var simulatedDrives = new DeviceInfoService(new FakeDriveProvider(
[
    new FakeDrive("T:\\", DriveType.Fixed, isReady: true, totalSize: 100, availableFreeSpace: 40),
    new FakeDrive("U:\\", DriveType.Fixed, isReady: false, totalSize: 0, availableFreeSpace: 0),
    new FakeDrive("V:\\", DriveType.Fixed, isReady: true, totalSize: 100, availableFreeSpace: 50, throwOnSizeRead: true),
    new FakeDrive("W:\\", DriveType.Fixed, isReady: true, totalSize: 100, availableFreeSpace: 150)
])).Read();

if (simulatedDrives.Drives.Count != 1
    || !simulatedDrives.Errors.Contains(DeviceInfoSection.Drives)
    || simulatedDrives.Drives[0].Name != "T:\\")
{
    throw new InvalidOperationException("Unavailable or invalid drives were not isolated from readable drives.");
}

checks++;
Console.WriteLine($"PASS: {checks} device information and localization checks.");
Console.WriteLine($"Actual drives read: {deviceInfo.Drives.Count}; partial read sections: {deviceInfo.Errors.Count}.");
Console.WriteLine("PASS: unavailable and inconsistent simulated drives did not interrupt readable drive results.");

var testFilePath = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-SHA256-{Guid.NewGuid():N}.txt");
var emptyTestFilePath = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-SHA256-empty-{Guid.NewGuid():N}.txt");
try
{
    await File.WriteAllBytesAsync(testFilePath, [0x61, 0x62, 0x63]);
    await File.WriteAllBytesAsync(emptyTestFilePath, []);
    var hashService = new FileInvestigationService(new FakeSignatureVerifier());
    var hash = await hashService.ComputeSha256Async(testFilePath, CancellationToken.None);
    if (hash != "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")
    {
        throw new InvalidOperationException($"Unexpected SHA-256 for known test content: {hash}");
    }

    checks++;
    var emptyHash = await hashService.ComputeSha256Async(emptyTestFilePath, CancellationToken.None);
    if (emptyHash != "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")
    {
        throw new InvalidOperationException($"Unexpected SHA-256 for an empty file: {emptyHash}");
    }

    checks++;
}
finally
{
    if (File.Exists(testFilePath))
    {
        File.Delete(testFilePath);
    }

    if (File.Exists(emptyTestFilePath))
    {
        File.Delete(emptyTestFilePath);
    }
}

var scanFolder = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-FileScan-{Guid.NewGuid():N}");
Directory.CreateDirectory(scanFolder);
var mismatchedImage = Path.Combine(scanFolder, "photo.txt");
try
{
    await File.WriteAllBytesAsync(mismatchedImage, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    var investigator = new FileInvestigationService(new FakeSignatureVerifier());
    var scan = await investigator.ScanFolderAsync(scanFolder, progress: null, CancellationToken.None);
    var mismatch = scan.Files.Count == 1 ? scan.Files[0] : null;
    var mismatchWarning = mismatch?.Warning;
    if (mismatch is null
        || mismatch.Name != "photo.txt"
        || mismatch.DetectedType != "PNG image"
        || mismatchWarning is null
        || !mismatchWarning.Contains("extension", StringComparison.OrdinalIgnoreCase)
        || !mismatchWarning.Contains("does not indicate malware", StringComparison.OrdinalIgnoreCase)
        || mismatch.SignatureStatus != FileSignatureStatus.Unsigned)
    {
        throw new InvalidOperationException("File metadata, type, signature, or combined non-alarmist warnings were incorrect.");
    }

    checks++;
}
finally
{
    if (File.Exists(mismatchedImage))
    {
        File.Delete(mismatchedImage);
    }

    if (Directory.Exists(scanFolder))
    {
        Directory.Delete(scanFolder, recursive: false);
    }
}

var accessTestFolder = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-FileAccess-{Guid.NewGuid():N}");
Directory.CreateDirectory(accessTestFolder);
var lockedPath = Path.Combine(accessTestFolder, "locked.txt");
var readablePath = Path.Combine(accessTestFolder, "readable.png");
try
{
    await File.WriteAllTextAsync(lockedPath, "locked");
    await File.WriteAllBytesAsync(readablePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    using var exclusiveLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
    var accessScan = await new FileInvestigationService(new FakeSignatureVerifier())
        .ScanFolderAsync(accessTestFolder, progress: null, CancellationToken.None);
    if (accessScan.Files.Count != 2
        || accessScan.Files.All(file => file.Path != lockedPath || file.Error is null)
        || accessScan.Files.All(file => file.Path != readablePath || file.Error is not null))
    {
        throw new InvalidOperationException("An inaccessible file interrupted inspection of accessible files.");
    }

    checks++;
}
finally
{
    if (File.Exists(lockedPath))
    {
        File.Delete(lockedPath);
    }

    if (File.Exists(readablePath))
    {
        File.Delete(readablePath);
    }

    if (Directory.Exists(accessTestFolder))
    {
        Directory.Delete(accessTestFolder, recursive: false);
    }
}

var organizerRoot = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-Organizer-{Guid.NewGuid():N}");
var organizerSource = Path.Combine(organizerRoot, "source");
var organizerDestination = Path.Combine(organizerRoot, "destination");
Directory.CreateDirectory(organizerSource);
Directory.CreateDirectory(organizerDestination);
var classificationFolder = Path.Combine(organizerRoot, "classification");
Directory.CreateDirectory(classificationFolder);
try
{
    var pngNamedText = Path.Combine(organizerSource, "photo.txt");
    var unknownNamedCode = Path.Combine(organizerSource, "unknown.cs");
    var codeFile = Path.Combine(organizerSource, "program.cs");
    await File.WriteAllBytesAsync(pngNamedText, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    await File.WriteAllBytesAsync(unknownNamedCode, [0x01, 0x02, 0x03, 0x04]);
    await File.WriteAllTextAsync(codeFile, "class Example { }");

    var categoryFixtures = new (string Name, byte[] Bytes, string? Text, OrganizerCategory Expected)[]
    {
        ("sample.pdf", Encoding.ASCII.GetBytes("%PDF-1.7"), null, OrganizerCategory.Pdf),
        ("sample.zip", [0x50, 0x4B, 0x03, 0x04], null, OrganizerCategory.Archives),
        ("sample.mp4", [0, 0, 0, 0, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D], null, OrganizerCategory.Videos),
        ("sample.mp3", Encoding.ASCII.GetBytes("ID3\u0003\u0000\u0000\u0000\u0000\u0000\u000F"), null, OrganizerCategory.Audio),
        ("notes.txt", [], "Notes for testing", OrganizerCategory.Documents)
    };
    foreach (var fixture in categoryFixtures)
    {
        var fixturePath = Path.Combine(classificationFolder, fixture.Name);
        if (fixture.Text is null)
        {
            await File.WriteAllBytesAsync(fixturePath, fixture.Bytes);
        }
        else
        {
            await File.WriteAllTextAsync(fixturePath, fixture.Text);
        }

        if (SmartFileOrganizerService.ClassifyFile(fixturePath).Category != fixture.Expected)
        {
            throw new InvalidOperationException($"Unexpected organizer category for {fixture.Name}.");
        }
    }

    if (SmartFileOrganizerService.ClassifyFile(pngNamedText).Category != OrganizerCategory.Images
        || SmartFileOrganizerService.ClassifyFile(unknownNamedCode).Category != OrganizerCategory.Other
        || SmartFileOrganizerService.ClassifyFile(codeFile).Category != OrganizerCategory.Code)
    {
        throw new InvalidOperationException("Organizer classification did not prioritize content evidence over file extensions.");
    }

    checks++;
    var monthPreview = new SmartFileOrganizerService()
        .CreatePreview(organizerSource, organizerDestination, organizeByMonth: true, CancellationToken.None);
    var codePreview = monthPreview.Items.Single(item => item.SourcePath == codeFile);
    if (!codePreview.DestinationPath.Contains(
            Path.Combine(codePreview.LastModified.ToString("yyyy"), codePreview.LastModified.ToString("MM")),
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Year/month organization did not use the file modification date.");
    }

    checks++;

    var protectedRejected = false;
    try
    {
        _ = new SmartFileOrganizerService().CreatePreview(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            organizerDestination,
            organizeByMonth: false,
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException)
    {
        protectedRejected = true;
    }

    if (!protectedRejected)
    {
        throw new InvalidOperationException("Organizer accepted a protected Windows system folder.");
    }

    checks++;
    var destinationImages = Path.Combine(organizerDestination, "Images");
    Directory.CreateDirectory(destinationImages);
    var existingImage = Path.Combine(destinationImages, "photo.txt");
    await File.WriteAllTextAsync(existingImage, "existing");
    var organizer = new SmartFileOrganizerService();
    var preview = organizer.CreatePreview(organizerSource, organizerDestination, organizeByMonth: false, CancellationToken.None);
    var conflict = preview.Items.Single(item => item.SourcePath == pngNamedText);
    if (!conflict.HasNameConflict
        || preview.ConflictCount != 1
        || preview.Items.Count != 3
        || !File.Exists(pngNamedText)
        || await File.ReadAllTextAsync(existingImage) != "existing")
    {
        throw new InvalidOperationException("Organizer preview did not report conflicts or changed files before confirmation.");
    }

    checks++;
    var conflictOnlyPreview = new OrganizerPreview([conflict], [], conflict.SizeBytes, 1);
    var skipOrganizer = new SmartFileOrganizerService();
    await skipOrganizer.ExecuteAsync(conflictOnlyPreview, OrganizerConflictResolution.Skip, progress: null, CancellationToken.None);
    if (!File.Exists(pngNamedText)
        || await File.ReadAllTextAsync(existingImage) != "existing"
        || skipOrganizer.OperationLog.Single().Status != OrganizerOperationStatus.Skipped)
    {
        throw new InvalidOperationException("Skip conflict resolution moved or overwrote a file.");
    }

    checks++;
    var renameProgress = new Progress<OrganizerExecutionProgress>();
    await organizer.ExecuteAsync(conflictOnlyPreview, OrganizerConflictResolution.Rename, renameProgress, CancellationToken.None);
    var renamedPath = Path.Combine(destinationImages, "photo (1).txt");
    if (!File.Exists(renamedPath) || File.Exists(pngNamedText) || await File.ReadAllTextAsync(existingImage) != "existing")
    {
        throw new InvalidOperationException("Rename conflict resolution overwrote an existing file or failed to move safely.");
    }

    checks++;
    if (!await organizer.UndoLastAsync(CancellationToken.None)
        || !File.Exists(pngNamedText)
        || File.Exists(renamedPath))
    {
        throw new InvalidOperationException("Organizer undo did not restore the moved file.");
    }

    checks++;

    var replaceOrganizer = new SmartFileOrganizerService();
    await replaceOrganizer.ExecuteAsync(conflictOnlyPreview, OrganizerConflictResolution.Replace, progress: null, CancellationToken.None);
    if (!File.Exists(existingImage)
        || !File.ReadAllBytes(existingImage).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
    {
        var latestOperation = replaceOrganizer.OperationLog.LastOrDefault();
        throw new InvalidOperationException(
            $"Explicit replace failed: {latestOperation?.Status} {latestOperation?.Detail}; target exists={File.Exists(existingImage)}.");
    }

    checks++;
    if (!await replaceOrganizer.UndoLastAsync(CancellationToken.None)
        || await File.ReadAllTextAsync(existingImage) != "existing"
        || !File.Exists(pngNamedText))
    {
        throw new InvalidOperationException("Undo did not restore the preserved pre-existing file.");
    }

    checks++;

    var mutableFile = Path.Combine(organizerSource, "mutable.bin");
    await File.WriteAllBytesAsync(mutableFile, [0x10, 0x20, 0x30]);
    var integrityOrganizer = new SmartFileOrganizerService();
    var integrityPreview = integrityOrganizer.CreatePreview(
        organizerSource,
        organizerDestination,
        organizeByMonth: false,
        CancellationToken.None);
    var mutablePlan = integrityPreview.Items.Single(item => item.SourcePath == mutableFile);
    await integrityOrganizer.ExecuteAsync(
        new OrganizerPreview([mutablePlan], [], mutablePlan.SizeBytes, 0),
        OrganizerConflictResolution.Rename,
        progress: null,
        CancellationToken.None);
    await File.WriteAllTextAsync(mutablePlan.DestinationPath, "changed after move");
    var refusedUnsafeUndo = false;
    try
    {
        _ = await integrityOrganizer.UndoLastAsync(CancellationToken.None);
    }
    catch (IOException)
    {
        refusedUnsafeUndo = true;
    }

    if (!refusedUnsafeUndo || File.Exists(mutableFile))
    {
        throw new InvalidOperationException("Organizer did not refuse undo after the moved file changed.");
    }

    checks++;
}
finally
{
    if (Directory.Exists(organizerRoot))
    {
        Directory.Delete(organizerRoot, recursive: true);
    }
}

Console.WriteLine($"PASS: {checks} total smoke checks, including organizer classification, collision preview, rename, explicit replace, and verified undo.");

var duplicateRoot = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-DuplicateScan-{Guid.NewGuid():N}");
var duplicateNested = Path.Combine(duplicateRoot, "nested");
Directory.CreateDirectory(duplicateNested);
try
{
    var identicalOne = Path.Combine(duplicateRoot, "first.dat");
    var identicalTwo = Path.Combine(duplicateRoot, "second.bin");
    var sameNameDifferentContent = Path.Combine(duplicateRoot, "same.txt");
    var nestedSameName = Path.Combine(duplicateNested, "same.txt");
    await File.WriteAllTextAsync(identicalOne, "repeat");
    await File.WriteAllTextAsync(identicalTwo, "repeat");
    await File.WriteAllTextAsync(sameNameDifferentContent, "alpha");
    await File.WriteAllTextAsync(nestedSameName, "bravo");
    File.SetLastWriteTime(identicalOne, DateTime.Now.AddMonths(-8));
    File.SetLastWriteTime(identicalTwo, DateTime.Now.AddMonths(-8));
    File.SetLastWriteTime(sameNameDifferentContent, DateTime.Now.AddMonths(-8));
    File.SetLastWriteTime(nestedSameName, DateTime.Now);

    var forgottenService = new ForgottenFilesService();
    var forgottenScan = await forgottenService.ScanAsync(
        duplicateRoot,
        oldAfterMonths: 6,
        progress: null,
        CancellationToken.None);
    if (forgottenScan.DuplicateGroups.Count != 1
        || forgottenScan.DuplicateGroups[0].Files.Count != 2
        || forgottenScan.DuplicateGroups[0].Files.Any(file => file.Path.Contains("same.txt", StringComparison.OrdinalIgnoreCase))
        || forgottenScan.DuplicateGroups[0].PotentialSavingsBytes != 6
        || forgottenScan.CandidateBytesHashed != 22
        || forgottenScan.OldFiles.Count != 3
        || forgottenScan.DuplicateGroups[0].Files[0].Sha256 != forgottenScan.DuplicateGroups[0].Files[1].Sha256)
    {
        throw new InvalidOperationException("Duplicate matching, content comparison, or old-file age calculation was incorrect.");
    }

    checks++;
    var changedCandidate = await forgottenService.MoveToRecycleBinAsync(
        [new RecycleFileRequest(sameNameDifferentContent, "NOT-THE-SCANNED-HASH")],
        progress: null,
        CancellationToken.None);
    if (changedCandidate.Results.Count != 1
        || changedCandidate.Results[0].Succeeded
        || !File.Exists(sameNameDifferentContent))
    {
        throw new InvalidOperationException("A file that no longer matches its scanned hash was not safely rejected.");
    }

    checks++;

    var emptyDuplicateFolder = Path.Combine(duplicateRoot, "empty");
    Directory.CreateDirectory(emptyDuplicateFolder);
    var emptyResult = await forgottenService.ScanAsync(
        emptyDuplicateFolder,
        oldAfterMonths: 6,
        progress: null,
        CancellationToken.None);
    if (emptyResult.DuplicateGroups.Count != 0
        || emptyResult.OldFiles.Count != 0
        || emptyResult.Errors.Count != 0)
    {
        throw new InvalidOperationException("An empty folder did not produce explicit empty scan results.");
    }

    checks++;

    var systemFolderRejected = false;
    try
    {
        _ = await forgottenService.ScanAsync(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            oldAfterMonths: 6,
            progress: null,
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException)
    {
        systemFolderRejected = true;
    }

    if (!systemFolderRejected)
    {
        throw new InvalidOperationException("Forgotten file detection accepted a protected Windows folder.");
    }

    checks++;
}
finally
{
    if (Directory.Exists(duplicateRoot))
    {
        Directory.Delete(duplicateRoot, recursive: true);
    }
}

Console.WriteLine($"PASS: {checks} smoke checks, including duplicate hashing and content-difference detection.");

var monitorDataSource = new FakeSystemMonitorDataSource(
    [new CpuTimes(100, 200, 100), new CpuTimes(150, 300, 200)],
    [
        [
            new FakeMonitoredProcess(101, "readable", 4096),
            new FakeMonitoredProcess(202, "protected", 8192, memoryError: new UnauthorizedAccessException("Simulated access denied.")),
            new FakeMonitoredProcess(303, "exited", 0, nameError: new InvalidOperationException("Simulated process exit."))
        ],
        [new FakeMonitoredProcess(404, "replacement", 16384)]
    ]);
var monitorService = new SystemMonitorService(monitorDataSource);
var firstMonitorSnapshot = monitorService.ReadSnapshot(CancellationToken.None);
if (firstMonitorSnapshot.CpuPercent is not null
    || firstMonitorSnapshot.Processes.Count != 3
    || firstMonitorSnapshot.Processes.Count(process => process.Error is not null) != 2
    || firstMonitorSnapshot.Processes.Single(process => process.ProcessId == 202).MemoryBytes is not null
    || firstMonitorSnapshot.Processes.Single(process => process.ProcessId == 303).Name != "Unknown process")
{
    throw new InvalidOperationException("System monitor did not isolate inaccessible or exited processes.");
}

checks++;
var secondMonitorSnapshot = monitorService.ReadSnapshot(CancellationToken.None);
if (secondMonitorSnapshot.CpuPercent is not double cpuPercent
    || Math.Abs(cpuPercent - 75d) > 0.01
    || secondMonitorSnapshot.Processes.Count != 1
    || secondMonitorSnapshot.Processes[0].ProcessId != 404
    || secondMonitorSnapshot.Processes[0].Name != "replacement")
{
    throw new InvalidOperationException("System monitor CPU sampling or changing-process refresh was incorrect.");
}

checks++;
Console.WriteLine($"PASS: {checks} smoke checks, including system monitor access errors, process changes, and CPU sampling.");

var historyRoot = Path.Combine(Path.GetTempPath(), $"GhostHunterPC-History-{Guid.NewGuid():N}");
Directory.CreateDirectory(historyRoot);
try
{
    var historyPath = Path.Combine(historyRoot, "history.json");
    var baselineTime = DateTimeOffset.Now.AddMinutes(-2);
    var comparisonTime = DateTimeOffset.Now;
    var baselineSnapshot = new InventorySnapshot(
        baselineTime,
        [
            new InventoryItem("system", "OS", "Windows A", baselineTime),
            new InventoryItem("program", "app-a", "App A v1", baselineTime),
            new InventoryItem("startup", "run-a", "startup-a.exe", baselineTime),
            new InventoryItem("process", "11", "old-process", baselineTime)
        ],
        []);
    var currentSnapshot = new InventorySnapshot(
        comparisonTime,
        [
            new InventoryItem("system", "OS", "Windows B", comparisonTime),
            new InventoryItem("program", "app-a", "App A v2", comparisonTime),
            new InventoryItem("program", "app-b", "App B v1", comparisonTime),
            new InventoryItem("process", "22", "new-process", comparisonTime)
        ],
        []);
    var historyService = new PcHistoryService(
        historyPath,
        new FakeComputerInventorySource([baselineSnapshot, currentSnapshot]));
    var createdBaseline = historyService.CreateBaseline();
    var comparison = historyService.CompareWithBaseline();
    if (createdBaseline.CreatedAt != baselineTime
        || createdBaseline.Items.Any(item => item.CollectedAt != baselineTime)
        || comparison.Changes.Count(change => change.Status == "Added") != 2
        || comparison.Changes.Count(change => change.Status == "Removed") != 2
        || comparison.Changes.Count(change => change.Status == "Changed") != 2)
    {
        throw new InvalidOperationException("PC DNA baseline comparison did not report added, removed, and changed items accurately.");
    }

    checks++;
    var historyReloaded = new PcHistoryService(historyPath, new FakeComputerInventorySource([]));
    if (historyReloaded.Data.Baseline?.Items.Count != 4
        || historyReloaded.Data.Events.Count != 2
        || historyReloaded.Data.Events.Any(item => item.Source != "GhostHunterPC"))
    {
        throw new InvalidOperationException("PC history baseline or application timeline events did not persist locally.");
    }

    checks++;
    historyService.Export(Path.Combine(historyRoot, "export.json"));
    var exportedHistory = await File.ReadAllTextAsync(Path.Combine(historyRoot, "export.json"));
    if (!exportedHistory.Contains("Baseline comparison", StringComparison.Ordinal)
        || exportedHistory.Contains("PERSONAL_FILE_CONTENT", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("PC history export was incomplete or contained uncollected file contents.");
    }

    checks++;
    var inaccessibleCurrent = new[]
    {
        new InventoryItem("system", "OS", "Windows B", comparisonTime)
    };
    var partialChanges = PcHistoryService.Compare(
        baselineSnapshot.Items,
        inaccessibleCurrent,
        comparisonTime,
        currentErrors: ["Installed programs HKLM64: access denied", "Process list: access denied"]);
    if (partialChanges.Any(change => change.Status == "Removed" && change.Category is "program" or "process"))
    {
        throw new InvalidOperationException("A partial inventory incorrectly marked inaccessible categories as removed.");
    }

    checks++;
    var corruptHistoryPath = Path.Combine(historyRoot, "corrupt.json");
    const string corruptHistoryContents = "{ invalid json";
    await File.WriteAllTextAsync(corruptHistoryPath, corruptHistoryContents);
    var corruptHistory = new PcHistoryService(corruptHistoryPath, new FakeComputerInventorySource([]));
    if (string.IsNullOrWhiteSpace(corruptHistory.LoadError))
    {
        throw new InvalidOperationException("Corrupt local history was not surfaced as an error.");
    }

    var writeBlocked = false;
    try
    {
        corruptHistory.RecordApplicationEvent("test", "should not overwrite corrupt history");
    }
    catch (InvalidDataException)
    {
        writeBlocked = true;
    }

    if (!writeBlocked || await File.ReadAllTextAsync(corruptHistoryPath) != corruptHistoryContents)
    {
        throw new InvalidOperationException("Corrupt local history was silently overwritten.");
    }

    checks++;
}
finally
{
    if (Directory.Exists(historyRoot))
    {
        Directory.Delete(historyRoot, recursive: true);
    }
}

Console.WriteLine($"PASS: {checks} smoke checks, including local PC DNA persistence, comparison, partial access, and export.");

sealed class FakeDriveProvider(IEnumerable<IDeviceDrive> drives) : IDeviceDriveProvider
{
    public IEnumerable<IDeviceDrive> GetDrives() => drives;
}

sealed class FakeDrive(
    string name,
    DriveType driveType,
    bool isReady,
    long totalSize,
    long availableFreeSpace,
    bool throwOnSizeRead = false) : IDeviceDrive
{
    public string Name => name;
    public DriveType DriveType => driveType;
    public bool IsReady => isReady;
    public long TotalSize => throwOnSizeRead
        ? throw new IOException("Simulated inaccessible drive.")
        : totalSize;
    public long AvailableFreeSpace => availableFreeSpace;
}

sealed class FakeSignatureVerifier : IFileSignatureVerifier
{
    public FileSignatureResult Verify(string filePath) =>
        new(FileSignatureStatus.Unsigned, null, null);
}

sealed class FakeSystemMonitorDataSource(
    IReadOnlyList<CpuTimes> cpuSamples,
    IReadOnlyList<IReadOnlyList<IMonitoredProcess>> processSnapshots) : ISystemMonitorDataSource
{
    private int _cpuIndex;
    private int _processIndex;

    public CpuTimes ReadCpuTimes() => cpuSamples[Math.Min(_cpuIndex++, cpuSamples.Count - 1)];
    public MonitorMemorySnapshot ReadMemory() => new(1000, 400);
    public IEnumerable<MonitorDriveSnapshot> ReadDrives() =>
        [new MonitorDriveSnapshot("T:\\", 100, 40, null)];
    public IEnumerable<IMonitoredProcess> GetProcesses() =>
        processSnapshots[Math.Min(_processIndex++, processSnapshots.Count - 1)];
}

sealed class FakeMonitoredProcess(
    int id,
    string name,
    long workingSet,
    Exception? memoryError = null,
    Exception? nameError = null) : IMonitoredProcess
{
    public int Id => id;
    public string Name => nameError is null ? name : throw nameError;
    public long WorkingSet64 => memoryError is null ? workingSet : throw memoryError;
    public void Dispose() { }
}

sealed class FakeComputerInventorySource(IReadOnlyList<InventorySnapshot> snapshots) : IComputerInventorySource
{
    private int _index;

    public InventorySnapshot Capture() => snapshots[Math.Min(_index++, snapshots.Count - 1)];
}
