using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Stndr;

public sealed class SefariaOfflineLibrarySetupDialog : Window
{
    private readonly string _dataFolder;
    private readonly SefariaOfflineLibraryInstaller _installer = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _downloadButton;
    private readonly Button _existingButton;
    private readonly Button _laterButton;
    private CancellationTokenSource? _cancellation;

    private SefariaOfflineLibrarySetupDialog(string dataFolder)
    {
        _dataFolder = dataFolder;
        var isUpdate = SefariaOfflineLibraryInstaller.IsInstalled(dataFolder);
        Title = isUpdate ? "Update the Sefaria library" : "Install the Sefaria library";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Cursor = new Cursor(StandardCursorType.Arrow);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            Text = isUpdate
                ? "Choose how to install the latest complete library."
                : "Stndr needs the complete Sefaria library before you can open books, links, and dictionaries."
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 8,
            IsVisible = false,
            IsIndeterminate = true
        };
        _downloadButton = new Button
        {
            Content = isUpdate ? "Download and update" : "Download and install",
            MinWidth = 150,
            IsDefault = true
        };
        _existingButton = new Button
        {
            Content = "Use an existing archive...",
            MinWidth = 170
        };
        _laterButton = new Button
        {
            Content = "Not now",
            MinWidth = 90,
            IsCancel = true
        };
        _downloadButton.Click += async (_, _) => await RunAsync(null);
        _existingButton.Click += async (_, _) => await ChooseExistingAsync();
        _laterButton.Click += (_, _) =>
        {
            _cancellation?.Cancel();
            if (_cancellation is null)
            {
                Close(false);
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = isUpdate ? "Update the Sefaria library" : "Install the Sefaria library",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = isUpdate
                        ? "Stndr will install Sefaria's latest complete library in the background of this window. " +
                          "Open books keep their place until the update finishes."
                        : "Stndr is installing the complete Sefaria library — books, translations, commentaries, " +
                          "links, and dictionaries — into a local database on this computer.",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#F2F4F7")),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12),
                    Child = new TextBlock
                    {
                        Text = "Allow at least 8 GiB free during setup. The download is about 2.3 GiB. " +
                               "Downloading and installing usually takes a good while depending on your connection and computer — " +
                               "you can leave this window open and wait; progress will update as it works.",
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                _progress,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { _laterButton, _existingButton, _downloadButton }
                }
            }
        };
    }

    public static Task<bool> ShowAsync(Window owner, string dataFolder) =>
        new SefariaOfflineLibrarySetupDialog(dataFolder).ShowDialog<bool>(owner);

    private async Task ChooseExistingAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Sefaria dump_small.tar.gz",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Sefaria compressed archive")
                {
                    Patterns = ["*.tar.gz", "*.tgz"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await RunAsync(path);
        }
    }

    private async Task RunAsync(string? archivePath)
    {
        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        var lastUiPublish = DateTime.UtcNow;
        var progress = new Progress<SefariaOfflineLibraryProgress>(update =>
        {
            // Keep the UI responsive and the bar visibly alive during long BSON scans.
            var now = DateTime.UtcNow;
            if (update.Fraction is null &&
                (now - lastUiPublish).TotalMilliseconds < 200 &&
                update.Stage is not (SefariaOfflineLibraryStage.Complete or SefariaOfflineLibraryStage.Installing))
            {
                return;
            }

            lastUiPublish = now;
            Dispatcher.UIThread.Post(() =>
            {
                _status.Text = update.Message;
                if (update.Fraction is { } fraction)
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = Math.Clamp(fraction, 0, 1);
                }
                else
                {
                    // Pulse indeterminate so long stages without totals still look alive.
                    _progress.IsIndeterminate = true;
                }
            }, DispatcherPriority.Background);
        });

        try
        {
            _status.Text = archivePath is null
                ? "Starting download..."
                : "Starting import from your archive...";

            // Run download/import on the thread pool so the dialog stays responsive
            // (arrow cursor, live status) during long BSON stages such as terms.
            var token = _cancellation.Token;
            var result = await Task.Run(async () => archivePath is null
                ? await _installer.DownloadAndInstallAsync(_dataFolder, progress, token)
                : await _installer.InstallAsync(_dataFolder, archivePath, progress, token), token);

            _status.Text =
                $"Installed {result.Works:N0} books, {result.Versions:N0} versions, {result.Links:N0} links " +
                $"and {result.LexiconEntries:N0} dictionary entries.";
            Close(true);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Setup cancelled. A partial download can resume next time.";
            SetBusy(false);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = $"Setup could not be completed: {ex.Message}";
            SetBusy(false);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    private void SetBusy(bool busy)
    {
        // Keep a normal arrow cursor so long imports do not freeze the window with an hourglass.
        Cursor = new Cursor(StandardCursorType.Arrow);
        _progress.IsVisible = busy;
        if (busy)
        {
            _progress.IsIndeterminate = true;
        }

        _downloadButton.IsEnabled = !busy;
        _existingButton.IsEnabled = !busy;
        _laterButton.Content = busy ? "Cancel" : "Not now";
    }
}
