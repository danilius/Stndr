using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace Stndr;

public enum AppUpdateBannerMode
{
    Hidden,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Error
}

public sealed class AppUpdateState
{
    public AppUpdateBannerMode Mode { get; init; } = AppUpdateBannerMode.Hidden;
    public string Message { get; init; } = string.Empty;
    public int DownloadProgress { get; init; }
    public UpdateInfo? AvailableUpdate { get; init; }
    public VelopackAsset? PendingAsset { get; init; }
}

public sealed class AppUpdateService
{
    public const string GitHubRepoUrl = "https://github.com/danilius/Stndr";

    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private UpdateInfo? _availableUpdate;
    private bool _hideUntilNextCheck;
    private bool _isDownloading;

    public AppUpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(GitHubRepoUrl, string.Empty, prerelease: true));
            IsEnabled = _manager.IsInstalled;
        }
        catch
        {
            IsEnabled = false;
        }
    }

    public bool IsEnabled { get; }

    public AppUpdateState CurrentState { get; private set; } = new();

    public event Action<AppUpdateState>? StateChanged;

    public void StartBackgroundChecks()
    {
        if (!IsEnabled || _manager is null)
        {
            return;
        }

        RefreshPendingRestartState();

        _ = RunBackgroundChecksAsync(_lifetimeCts.Token);
    }

    public void StopBackgroundChecks()
    {
        _lifetimeCts.Cancel();
    }

    public void DismissBanner()
    {
        _hideUntilNextCheck = true;
        PublishHidden();
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _manager is null)
        {
            return;
        }

        if (_manager.UpdatePendingRestart is not null)
        {
            _hideUntilNextCheck = false;
            RefreshPendingRestartState();
            return;
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            _availableUpdate = update;
            _hideUntilNextCheck = false;

            if (update is null)
            {
                PublishHidden();
                return;
            }

            PublishState(new AppUpdateState
            {
                Mode = AppUpdateBannerMode.UpdateAvailable,
                Message = $"Stndr {update.TargetFullRelease.Version} is available.",
                AvailableUpdate = update
            });
        }
        catch (NotInstalledException)
        {
            PublishHidden();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishState(new AppUpdateState
            {
                Mode = AppUpdateBannerMode.Error,
                Message = $"Could not check for updates: {ex.Message}"
            });
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _manager is null || _availableUpdate is null || _isDownloading)
        {
            return;
        }

        _isDownloading = true;
        _hideUntilNextCheck = false;

        try
        {
            PublishState(new AppUpdateState
            {
                Mode = AppUpdateBannerMode.Downloading,
                Message = "Downloading update…",
                AvailableUpdate = _availableUpdate,
                DownloadProgress = 0
            });

            await _manager.DownloadUpdatesAsync(
                _availableUpdate,
                progress => PublishState(new AppUpdateState
                {
                    Mode = AppUpdateBannerMode.Downloading,
                    Message = $"Downloading update… {progress}%",
                    AvailableUpdate = _availableUpdate,
                    DownloadProgress = progress
                }),
                cancellationToken);

            var pending = _manager.UpdatePendingRestart ?? _availableUpdate.TargetFullRelease;
            PublishState(new AppUpdateState
            {
                Mode = AppUpdateBannerMode.ReadyToRestart,
                Message = $"Stndr {pending.Version} is ready. Restart to install.",
                PendingAsset = pending
            });
        }
        catch (NotInstalledException)
        {
            PublishHidden();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishState(new AppUpdateState
            {
                Mode = AppUpdateBannerMode.Error,
                Message = $"Update download failed: {ex.Message}",
                AvailableUpdate = _availableUpdate
            });
        }
        finally
        {
            _isDownloading = false;
        }
    }

    public void ApplyAndRestart()
    {
        if (!IsEnabled || _manager is null)
        {
            return;
        }

        var pending = _manager.UpdatePendingRestart;
        _manager.ApplyUpdatesAndRestart(pending);
    }

    private async Task RunBackgroundChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialCheckDelay, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                _hideUntilNextCheck = false;
                await CheckNowAsync(cancellationToken);
                await Task.Delay(RecheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RefreshPendingRestartState()
    {
        if (_manager?.UpdatePendingRestart is not { } pending)
        {
            if (CurrentState.Mode == AppUpdateBannerMode.ReadyToRestart)
            {
                PublishHidden();
            }

            return;
        }

        if (_hideUntilNextCheck)
        {
            PublishHidden();
            return;
        }

        PublishState(new AppUpdateState
        {
            Mode = AppUpdateBannerMode.ReadyToRestart,
            Message = $"Stndr {pending.Version} is ready. Restart to install.",
            PendingAsset = pending
        });
    }

    private void PublishHidden()
    {
        if (_hideUntilNextCheck || _manager?.UpdatePendingRestart is null)
        {
            PublishState(new AppUpdateState());
            return;
        }

        RefreshPendingRestartState();
    }

    private void PublishState(AppUpdateState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }
}