using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private readonly SefariaLibraryUpdateService _libraryUpdateService = new();
    private CancellationTokenSource? _libraryUpdateCheckCts;
    private Border? _libraryUpdateBanner;
    private TextBlock? _libraryUpdateBannerMessage;
    private Button? _libraryUpdateBannerActionButton;
    private Button? _librarySnapshotUpdateButton;

    private void InitializeLibraryUpdateBanner()
    {
        _libraryUpdateBanner = this.FindControl<Border>("LibraryUpdateBanner");
        _libraryUpdateBannerMessage = this.FindControl<TextBlock>("LibraryUpdateBannerMessage");
        _libraryUpdateBannerActionButton = this.FindControl<Button>("LibraryUpdateBannerActionButton");
        _libraryUpdateService.StateChanged += state =>
            Dispatcher.UIThread.Post(() => ApplyLibraryUpdateState(state), DispatcherPriority.Background);
        this.Closed += (_, _) =>
        {
            _libraryUpdateCheckCts?.Cancel();
            _libraryUpdateCheckCts?.Dispose();
            _libraryUpdateService.Dispose();
        };
    }

    private void StartLibraryUpdateChecks()
    {
        _libraryUpdateService.StartBackgroundChecks(
            () => _sefariaLibrary.StorageRootFolder,
            () => _settings.CheckForLibraryUpdatesAutomatically);
    }

    private void ApplyLibraryUpdateState(SefariaLibraryUpdateState state)
    {
        if (_libraryUpdateBanner is not null && _libraryUpdateBannerMessage is not null)
        {
            _libraryUpdateBanner.IsVisible = state.Mode == SefariaLibraryUpdateMode.UpdateAvailable;
            if (_libraryUpdateBanner.IsVisible) _libraryUpdateBannerMessage.Text = state.Message;
        }
        if (_librarySnapshotUpdateButton is not null)
        {
            var actionState = _libraryUpdateService.CurrentState;
            _librarySnapshotUpdateButton.Content = actionState.Mode == SefariaLibraryUpdateMode.UpdateAvailable
                ? "Update library"
                : actionState.Mode == SefariaLibraryUpdateMode.Checking ? "Checking..." : "Check for updates";
            _librarySnapshotUpdateButton.IsEnabled = actionState.Mode != SefariaLibraryUpdateMode.Checking && _sefariaLibrary.HasOfflineLibrary;
        }
        if (_libraryStatus is not null && state.Mode is SefariaLibraryUpdateMode.UpToDate or SefariaLibraryUpdateMode.Error)
            _libraryStatus.Text = state.Message;
    }

    private async Task CheckLibraryUpdatesAsync()
    {
        _libraryUpdateCheckCts?.Cancel();
        _libraryUpdateCheckCts?.Dispose();
        _libraryUpdateCheckCts = new CancellationTokenSource();
        try
        {
            await _libraryUpdateService.CheckNowAsync(_sefariaLibrary.StorageRootFolder, _libraryUpdateCheckCts.Token);
        }
        catch (OperationCanceledException) when (_libraryUpdateCheckCts.IsCancellationRequested) { }
    }

    private async Task CheckOrInstallLibraryUpdateAsync()
    {
        if (_libraryUpdateService.CurrentState.Mode != SefariaLibraryUpdateMode.UpdateAvailable)
        {
            await CheckLibraryUpdatesAsync();
            return;
        }
        await InstallAvailableLibraryUpdateAsync();
    }

    private async Task InstallAvailableLibraryUpdateAsync()
    {
        var folder = _sefariaLibrary.StorageRootFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;
        var installed = await SefariaOfflineLibrarySetupDialog.ShowAsync(this, folder);
        if (!installed) return;
        ApplyDataFolder(folder);
        await CheckLibraryUpdatesAsync();
    }

    private void LibraryUpdateBannerActionClicked(object? sender, RoutedEventArgs e)
    {
        OpenOrSelectTab(LibraryManagerTabTitle);
        ApplyLibraryUpdateState(_libraryUpdateService.CurrentState);
    }

    private void LibraryUpdateBannerDismissClicked(object? sender, RoutedEventArgs e) =>
        _libraryUpdateService.DismissBanner();
}
