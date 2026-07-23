using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

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
        Title = isUpdate ? "Update the complete Sefaria library" : "Set up the complete Sefaria library";
        Width = 610;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#475467")) };
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, IsVisible = false, IsIndeterminate = true };
        _downloadButton = new Button { Content = isUpdate ? "Download and update" : "Download and install", MinWidth = 150, IsDefault = true };
        _existingButton = new Button { Content = "Use an existing archive...", MinWidth = 170 };
        _laterButton = new Button { Content = "Not now", MinWidth = 90, IsCancel = true };
        _downloadButton.Click += async (_, _) => await RunAsync(null);
        _existingButton.Click += async (_, _) => await ChooseExistingAsync();
        _laterButton.Click += (_, _) => { _cancellation?.Cancel(); if (_cancellation is null) Close(false); };

        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = isUpdate ? "Update your offline Sefaria library" : "Keep Sefaria available offline", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = (isUpdate ? "Stndr will replace the installed library with Sefaria's latest complete snapshot. " :
                           "Stndr can install the complete Sefaria text library, its book metadata, dictionaries and more than five million links in one operation. ") +
                           "The download is about 2.3 GiB and the installed database is about 3.0 GiB.",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#F2F4F7")), CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
                    Child = new TextBlock
                    {
                        Text = "Allow at least 8 GiB free during setup. The archive is streamed directly into the database; Stndr does not unpack the 11+ GiB Mongo dump.",
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                _progress,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
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
            FileTypeFilter = new[] { new FilePickerFileType("Sefaria compressed archive") { Patterns = new[] { "*.tar.gz", "*.tgz" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) await RunAsync(path);
    }

    private async Task RunAsync(string? archivePath)
    {
        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<SefariaOfflineLibraryProgress>(update =>
        {
            _status.Text = update.Message;
            _progress.IsIndeterminate = update.Fraction is null;
            if (update.Fraction is { } fraction) _progress.Value = fraction;
        });
        try
        {
            var result = archivePath is null
                ? await _installer.DownloadAndInstallAsync(_dataFolder, progress, _cancellation.Token)
                : await _installer.InstallAsync(_dataFolder, archivePath, progress, _cancellation.Token);
            _status.Text = $"Installed {result.Works:N0} books, {result.Versions:N0} versions, {result.Links:N0} links " +
                           $"and {result.LexiconEntries:N0} dictionary entries.";
            Close(true);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Setup paused. A partial download will resume next time.";
            SetBusy(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = $"Setup could not be completed: {ex.Message}";
            SetBusy(false);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void SetBusy(bool busy)
    {
        _progress.IsVisible = busy;
        _downloadButton.IsEnabled = !busy;
        _existingButton.IsEnabled = !busy;
        _laterButton.Content = busy ? "Cancel" : "Not now";
    }
}
