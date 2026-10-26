using Microsoft.Win32;

namespace GhostHunterPC.Services;

public sealed class FileFolderPickerService
{
    public string? PickFolder(string title = "Choose a folder")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
