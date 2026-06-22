using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Stndr;

/// <summary>
/// Modal dialog shown at startup (or from Settings) asking the user where their Stndr
/// Data folder should live. Returns the chosen folder path, or null if cancelled.
/// </summary>
public sealed class DataFolderDialog : Window
{
    private readonly TextBox _pathBox;
    private readonly TextBlock _errorText;
    private string? _result;

    public DataFolderDialog(string suggestedFolder)
    {
        Title = "Choose your Stndr Data folder";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pathBox = new TextBox
        {
            Text = suggestedFolder,
            MinWidth = 360
        };

        var browseButton = new Button { Content = "Browse…", MinWidth = 90 };
        browseButton.Click += async (_, _) => await BrowseAsync();

        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#B42318")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };

        var okButton = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        okButton.Click += (_, _) => Confirm();

        var cancelButton = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancelButton.Click += (_, _) => Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Stndr stores everything it downloads — Hebrew sources, translations and its " +
                           "databases — inside a single Data folder, alongside its settings file. Choose where " +
                           "that folder should live, or where it should be created.",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Inside it you'll find a 'sources' folder, a 'database' folder and a settings.json file, " +
                           "so you always know where your data is and can reset things by deleting them.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#475467"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _pathBox, browseButton }
                },
                _errorText,
                new TextBlock
                {
                    Text = "You can change this later in the Settings tab.",
                    Foreground = new SolidColorBrush(Color.Parse("#475467")),
                    FontStyle = FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton }
                }
            }
        };
    }

    public static async Task<string?> ShowAsync(Window owner, string suggestedFolder)
    {
        var dialog = new DataFolderDialog(suggestedFolder);
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task BrowseAsync()
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                Title = "Select or create your Stndr Data folder",
                AllowMultiple = false
            };

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            var picked = folders?.FirstOrDefault();
            var path = picked?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                _pathBox.Text = path;
                _errorText.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not open the folder picker: {ex.Message}");
        }
    }

    private void Confirm()
    {
        var candidate = _pathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ShowError("Please enter or choose a folder path.");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            Directory.CreateDirectory(fullPath);
            _result = fullPath;
            Close(_result);
        }
        catch (Exception ex)
        {
            ShowError($"That folder can't be used: {ex.Message}");
        }
    }

    private void Cancel()
    {
        _result = null;
        Close(_result);
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }
}
