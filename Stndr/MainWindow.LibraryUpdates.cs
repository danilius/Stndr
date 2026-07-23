using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private readonly SefariaLibraryUpdateService _libraryUpdateService = new();
    private readonly SefariaOfflineLibraryInstaller _libraryUpdateInstaller = new();
    private CancellationTokenSource? _libraryUpdateCheckCts;
    private CancellationTokenSource? _libraryUpdateInstallCts;
    private Border? _libraryUpdateBanner;
    private TextBlock? _libraryUpdateBannerMessage;
    private Button? _libraryUpdateBannerActionButton;
    private Button? _libraryUpdateBannerDismissButton;
    private Button? _libraryUpdateBannerCancelButton;
    private ProgressBar? _libraryUpdateBannerProgress;
    private DateTime _libraryUpdateLastUiPublishUtc;

    private void InitializeLibraryUpdateBanner()
    {
        _libraryUpdateBanner = this.FindControl<Border>("LibraryUpdateBanner");
        _libraryUpdateBannerMessage = this.FindControl<TextBlock>("LibraryUpdateBannerMessage");
        _libraryUpdateBannerActionButton = this.FindControl<Button>("LibraryUpdateBannerActionButton");
        _libraryUpdateBannerDismissButton = this.FindControl<Button>("LibraryUpdateBannerDismissButton");
        _libraryUpdateBannerCancelButton = this.FindControl<Button>("LibraryUpdateBannerCancelButton");
        _libraryUpdateBannerProgress = this.FindControl<ProgressBar>("LibraryUpdateBannerProgress");
        _libraryUpdateService.StateChanged += state =>
            Dispatcher.UIThread.Post(() => ApplyLibraryUpdateState(state), DispatcherPriority.Background);
        this.Closed += (_, _) =>
        {
            _libraryUpdateCheckCts?.Cancel();
            _libraryUpdateCheckCts?.Dispose();
            _libraryUpdateInstallCts?.Cancel();
            _libraryUpdateInstallCts?.Dispose();
            _libraryUpdateService.Dispose();
        };
    }

    private void StartLibraryUpdateChecks()
    {
        _libraryUpdateService.StartBackgroundChecks(
            () => _sefariaLibrary.StorageRootFolder,
            () => _settings.CheckForLibraryUpdatesAutomatically,
            GetLibraryUpdateSnoozeState);
    }

    private LibraryUpdateSnoozeState GetLibraryUpdateSnoozeState() =>
        new(_settings.LibraryUpdateSnoozedRemoteKey, _settings.LibraryUpdateSnoozedUntilUtc);

    private void ApplyLibraryUpdateState(SefariaLibraryUpdateState state)
    {
        if (_libraryUpdateBanner is not null && _libraryUpdateBannerMessage is not null)
        {
            var showBanner = state.Mode is
                SefariaLibraryUpdateMode.UpdateAvailable or
                SefariaLibraryUpdateMode.Downloading or
                SefariaLibraryUpdateMode.Importing or
                SefariaLibraryUpdateMode.Activating or
                SefariaLibraryUpdateMode.Complete or
                SefariaLibraryUpdateMode.Cancelled or
                SefariaLibraryUpdateMode.Error;

            _libraryUpdateBanner.IsVisible = showBanner;
            if (showBanner)
            {
                _libraryUpdateBannerMessage.Text = state.Message;
                _libraryUpdateBanner.Background = new SolidColorBrush(state.Mode switch
                {
                    SefariaLibraryUpdateMode.Error => Color.Parse("#FEF3F2"),
                    SefariaLibraryUpdateMode.Complete => Color.Parse("#ECFDF3"),
                    SefariaLibraryUpdateMode.Cancelled => Color.Parse("#FFFAEB"),
                    SefariaLibraryUpdateMode.UpdateAvailable => Color.Parse("#ECFDF3"),
                    _ => Color.Parse("#EFF8FF")
                });
            }

            var isOffer = state.Mode == SefariaLibraryUpdateMode.UpdateAvailable;
            var isProgress = state.Mode is
                SefariaLibraryUpdateMode.Downloading or
                SefariaLibraryUpdateMode.Importing or
                SefariaLibraryUpdateMode.Activating;
            var isTerminal = state.Mode is
                SefariaLibraryUpdateMode.Complete or
                SefariaLibraryUpdateMode.Cancelled or
                SefariaLibraryUpdateMode.Error;

            if (_libraryUpdateBannerActionButton is not null)
            {
                _libraryUpdateBannerActionButton.IsVisible = isOffer ||
                    (isTerminal && state.Mode != SefariaLibraryUpdateMode.Complete);
                _libraryUpdateBannerActionButton.Content = state.Mode switch
                {
                    SefariaLibraryUpdateMode.UpdateAvailable => "Update",
                    SefariaLibraryUpdateMode.Error => "Retry",
                    SefariaLibraryUpdateMode.Cancelled => "Try again",
                    _ => "Update"
                };
                _libraryUpdateBannerActionButton.IsEnabled = !_libraryUpdateService.IsBusy;
            }

            if (_libraryUpdateBannerDismissButton is not null)
            {
                _libraryUpdateBannerDismissButton.IsVisible = isOffer || isTerminal;
                _libraryUpdateBannerDismissButton.Content = isTerminal ? "Dismiss" : "Later";
            }

            if (_libraryUpdateBannerCancelButton is not null)
            {
                _libraryUpdateBannerCancelButton.IsVisible = isProgress && state.CanCancel;
                _libraryUpdateBannerCancelButton.IsEnabled = state.CanCancel;
                if (state.CanCancel)
                {
                    _libraryUpdateBannerCancelButton.Content = "Cancel";
                }
            }

            if (_libraryUpdateBannerProgress is not null)
            {
                _libraryUpdateBannerProgress.IsVisible = isProgress;
                if (isProgress)
                {
                    if (state.ProgressFraction is { } fraction)
                    {
                        _libraryUpdateBannerProgress.IsIndeterminate = false;
                        _libraryUpdateBannerProgress.Value = Math.Clamp(fraction, 0, 1);
                    }
                    else
                    {
                        _libraryUpdateBannerProgress.IsIndeterminate = true;
                    }
                }
            }
        }

    }

    private async Task CheckLibraryUpdatesAsync()
    {
        if (_libraryUpdateService.IsBusy)
        {
            return;
        }

        _libraryUpdateCheckCts?.Cancel();
        _libraryUpdateCheckCts?.Dispose();
        _libraryUpdateCheckCts = new CancellationTokenSource();
        try
        {
            await _libraryUpdateService.CheckNowAsync(
                _sefariaLibrary.StorageRootFolder,
                GetLibraryUpdateSnoozeState(),
                _libraryUpdateCheckCts.Token);
        }
        catch (OperationCanceledException) when (_libraryUpdateCheckCts.IsCancellationRequested)
        {
        }
    }

    private async Task CheckOrInstallLibraryUpdateAsync()
    {
        if (_libraryUpdateService.IsBusy)
        {
            return;
        }

        if (_libraryUpdateService.CurrentState.Mode != SefariaLibraryUpdateMode.UpdateAvailable)
        {
            await CheckLibraryUpdatesAsync();
            return;
        }

        await StartBackgroundLibraryUpdateAsync();
    }

    private Task InstallAvailableLibraryUpdateAsync() => StartBackgroundLibraryUpdateAsync();

    private async Task StartBackgroundLibraryUpdateAsync()
    {
        var folder = _sefariaLibrary.StorageRootFolder;
        if (string.IsNullOrWhiteSpace(folder) || _libraryUpdateService.IsBusy)
        {
            return;
        }

        _libraryUpdateInstallCts?.Cancel();
        _libraryUpdateInstallCts?.Dispose();
        _libraryUpdateInstallCts = new CancellationTokenSource();
        var token = _libraryUpdateInstallCts.Token;
        var remote = _libraryUpdateService.CurrentState.RemoteSnapshot;

        _libraryUpdateService.BeginProgress(
            SefariaLibraryUpdateMode.Downloading,
            "Starting Sefaria library update...",
            remote);

        var progress = new Progress<SefariaOfflineLibraryProgress>(update =>
        {
            // Throttle UI updates during multi-GB downloads.
            var now = DateTime.UtcNow;
            if (update.Stage == SefariaOfflineLibraryStage.Downloading &&
                update.Fraction is not null &&
                (now - _libraryUpdateLastUiPublishUtc).TotalMilliseconds < 250)
            {
                return;
            }

            _libraryUpdateLastUiPublishUtc = now;
            Dispatcher.UIThread.Post(
                () => _libraryUpdateService.ReportProgress(update, remote),
                DispatcherPriority.Background);
        });

        try
        {
            var result = await Task.Run(
                async () => await _libraryUpdateInstaller.DownloadAndInstallAsync(folder, progress, token),
                token);

            ClearLibraryUpdateSnooze();
            ApplyOfflineLibraryUpdateCutover(folder);
            _libraryUpdateService.Complete(
                $"Library updated: {result.Works:N0} books, {result.Versions:N0} versions, " +
                $"{result.Links:N0} links. Open books keep their place; tools use the new library.",
                remote);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _libraryUpdateService.Cancelled(
                "Update cancelled. Your existing offline library is unchanged. A partial download can resume next time.");
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            _libraryUpdateService.Fail($"Library update failed: {ex.Message}");
        }
    }

    private void ApplyOfflineLibraryUpdateCutover(string folder)
    {
        // Soft cutover: release SQLite pools and offline caches, refresh library UI, but do not
        // force-reload open reader tabs (in-memory text stays until the user navigates).
        _sefariaLibrary.NotifyOfflineLibraryReplaced();
        if (!string.Equals(_sefariaLibrary.StorageRootFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            _sefariaLibrary.SetStorageRootFolder(folder);
        }

        RefreshInstalledBooksTree();
        UpdateReaderTools();
        _ = LoadDictionaryCatalogueAsync();
        _libraryLoadTask = LoadSefariaLibraryAsync();
    }

    private async void LibraryUpdateBannerActionClicked(object? sender, RoutedEventArgs e)
    {
        switch (_libraryUpdateService.CurrentState.Mode)
        {
            case SefariaLibraryUpdateMode.UpdateAvailable:
            case SefariaLibraryUpdateMode.Cancelled:
            case SefariaLibraryUpdateMode.Error:
                await StartBackgroundLibraryUpdateAsync();
                break;
        }
    }

    private async void LibraryUpdateBannerDismissClicked(object? sender, RoutedEventArgs e)
    {
        var mode = _libraryUpdateService.CurrentState.Mode;
        if (mode == SefariaLibraryUpdateMode.UpdateAvailable)
        {
            await DismissLibraryUpdateOfferAsync();
            return;
        }

        if (mode is SefariaLibraryUpdateMode.Complete or
            SefariaLibraryUpdateMode.Cancelled or
            SefariaLibraryUpdateMode.Error)
        {
            _libraryUpdateService.Hide();
        }
    }

    private void LibraryUpdateBannerCancelClicked(object? sender, RoutedEventArgs e)
    {
        if (!_libraryUpdateService.CurrentState.CanCancel)
        {
            return;
        }

        _libraryUpdateInstallCts?.Cancel();
        if (_libraryUpdateBannerCancelButton is not null)
        {
            _libraryUpdateBannerCancelButton.IsEnabled = false;
            _libraryUpdateBannerCancelButton.Content = "Cancelling...";
        }
    }

    private async Task DismissLibraryUpdateOfferAsync()
    {
        var remote = _libraryUpdateService.CurrentState.RemoteSnapshot;
        if (remote is not null)
        {
            SnoozeLibraryUpdate(remote);
        }

        if (!_settings.LibraryUpdateLaterTipAcknowledged)
        {
            await ShowLibraryUpdateLaterTipAsync();
        }

        _libraryUpdateService.Hide();
    }

    private void SnoozeLibraryUpdate(SefariaRemoteSnapshot remote)
    {
        var days = NormalizeLibraryUpdateSnoozeDays(_settings.LibraryUpdateSnoozeDays);
        _settings.LibraryUpdateSnoozeDays = days;
        _settings.LibraryUpdateSnoozedRemoteKey = remote.IdentityKey;
        _settings.LibraryUpdateSnoozedUntilUtc = DateTime.UtcNow.AddDays(days);
        _settingsService.Save(_settings);
    }

    private void ClearLibraryUpdateSnooze()
    {
        _settings.LibraryUpdateSnoozedRemoteKey = string.Empty;
        _settings.LibraryUpdateSnoozedUntilUtc = null;
        _settingsService.Save(_settings);
    }

    private static int NormalizeLibraryUpdateSnoozeDays(int days) =>
        days is 1 or 3 or 7 or 14 or 30 or 90 ? days : 14;

    private async Task ShowLibraryUpdateLaterTipAsync()
    {
        var dialog = new Window
        {
            Title = "Update later",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = null
        };

        var dontShowAgain = new CheckBox
        {
            Content = "Don't show this tip again",
            Margin = new Thickness(0, 8, 0, 0)
        };
        var closeButton = new Button
        {
            Content = "OK",
            MinWidth = 80,
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) =>
        {
            if (dontShowAgain.IsChecked == true)
            {
                _settings.LibraryUpdateLaterTipAcknowledged = true;
                _settingsService.Save(_settings);
            }

            dialog.Close();
        };

        var snoozeDays = NormalizeLibraryUpdateSnoozeDays(_settings.LibraryUpdateSnoozeDays);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "You can update the offline library later",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text =
                        "When you are ready, open Settings → Sefaria library updates and choose Update now. " +
                        $"This update offer will stay quiet for about {snoozeDays} day{(snoozeDays == 1 ? "" : "s")} " +
                        "(configurable in Settings).",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#475467"))
                },
                dontShowAgain,
                closeButton
            }
        };

        await dialog.ShowDialog(this);
    }
}
