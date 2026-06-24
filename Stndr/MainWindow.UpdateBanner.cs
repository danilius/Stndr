using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private readonly AppUpdateService _appUpdateService = new();
    private CancellationTokenSource? _updateActionCts;
    private Border? _updateBanner;
    private TextBlock? _updateBannerMessage;
    private Button? _updateBannerActionButton;
    private Button? _updateBannerDismissButton;

    private void InitializeUpdateBanner()
    {
        _updateBanner = this.FindControl<Border>("UpdateBanner");
        _updateBannerMessage = this.FindControl<TextBlock>("UpdateBannerMessage");
        _updateBannerActionButton = this.FindControl<Button>("UpdateBannerActionButton");
        _updateBannerDismissButton = this.FindControl<Button>("UpdateBannerDismissButton");

        _appUpdateService.StateChanged += OnAppUpdateStateChanged;
        ApplyAppUpdateState(_appUpdateService.CurrentState);

        this.Closed += (_, _) =>
        {
            _updateActionCts?.Cancel();
            _updateActionCts?.Dispose();
            _appUpdateService.StopBackgroundChecks();
        };
    }

    private void StartUpdateChecks()
    {
        _appUpdateService.StartBackgroundChecks();
    }

    private void OnAppUpdateStateChanged(AppUpdateState state)
    {
        Dispatcher.UIThread.Post(() => ApplyAppUpdateState(state), DispatcherPriority.Background);
    }

    private void ApplyAppUpdateState(AppUpdateState state)
    {
        if (_updateBanner is null ||
            _updateBannerMessage is null ||
            _updateBannerActionButton is null ||
            _updateBannerDismissButton is null)
        {
            return;
        }

        var showBanner = state.Mode != AppUpdateBannerMode.Hidden;
        _updateBanner.IsVisible = showBanner;
        if (!showBanner)
        {
            return;
        }

        _updateBannerMessage.Text = state.Message;

        switch (state.Mode)
        {
            case AppUpdateBannerMode.UpdateAvailable:
                _updateBannerActionButton.Content = "Download";
                _updateBannerActionButton.IsEnabled = true;
                _updateBannerDismissButton.IsVisible = true;
                break;

            case AppUpdateBannerMode.Downloading:
                _updateBannerActionButton.Content = "Downloading…";
                _updateBannerActionButton.IsEnabled = false;
                _updateBannerDismissButton.IsVisible = false;
                break;

            case AppUpdateBannerMode.ReadyToRestart:
                _updateBannerActionButton.Content = "Restart now";
                _updateBannerActionButton.IsEnabled = true;
                _updateBannerDismissButton.IsVisible = true;
                break;

            case AppUpdateBannerMode.Error:
                _updateBannerActionButton.Content = "Retry";
                _updateBannerActionButton.IsEnabled = true;
                _updateBannerDismissButton.IsVisible = true;
                break;
        }
    }

    private async void UpdateBannerActionClicked(object? sender, RoutedEventArgs e)
    {
        switch (_appUpdateService.CurrentState.Mode)
        {
            case AppUpdateBannerMode.UpdateAvailable:
                await RunUpdateActionAsync(_appUpdateService.DownloadAsync);
                break;

            case AppUpdateBannerMode.ReadyToRestart:
                SaveLayoutState();
                _appUpdateService.ApplyAndRestart();
                break;

            case AppUpdateBannerMode.Error:
                await RunUpdateActionAsync(_appUpdateService.CheckNowAsync);
                break;
        }
    }

    private void UpdateBannerDismissClicked(object? sender, RoutedEventArgs e)
    {
        _appUpdateService.DismissBanner();
    }

    private async Task RunUpdateActionAsync(Func<CancellationToken, Task> action)
    {
        _updateActionCts?.Cancel();
        _updateActionCts?.Dispose();
        _updateActionCts = new CancellationTokenSource();

        try
        {
            await action(_updateActionCts.Token);
        }
        catch (OperationCanceledException) when (_updateActionCts.IsCancellationRequested)
        {
        }
    }
}