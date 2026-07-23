using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Lorerim.Gui.Services;

/// <summary>Native folder/file pickers, opened on the main window.</summary>
public static class FolderPicker
{
    /// <summary>Returns the chosen directory path, or null if the user cancelled.</summary>
    public static async Task<string?> PickFolderAsync(string title, string? startingPath)
    {
        if (MainWindowStorage() is not { } storage)
        {
            return null;
        }
        var folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StartLocationAsync(storage, startingPath),
            }
        );
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>Returns the chosen .wabbajack file path, or null if the user cancelled.</summary>
    public static async Task<string?> PickWabbajackFileAsync(string? startingPath)
    {
        if (MainWindowStorage() is not { } storage)
        {
            return null;
        }
        var files = await storage.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select a .wabbajack file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Wabbajack modlist") { Patterns = ["*.wabbajack"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
                SuggestedStartLocation = await StartLocationAsync(
                    storage,
                    startingPath is null ? null : Path.GetDirectoryName(startingPath)
                ),
            }
        );
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private static IStorageProvider? MainWindowStorage() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.StorageProvider
            : null;

    /// <summary>
    /// Opens the picker somewhere useful: the configured path if it exists yet, else the
    /// nearest parent that does (the default install dir usually hasn't been created).
    /// </summary>
    private static async Task<IStorageFolder?> StartLocationAsync(
        IStorageProvider storage,
        string? path
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var current = Path.GetFullPath(path);
            while (!Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    return null;
                }
                current = parent;
            }
            return await storage.TryGetFolderFromPathAsync(current);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
