using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Stendr;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _loadingTimer;
    private double _loadingPosition;

    public SplashWindow()
    {
        InitializeComponent();

        _loadingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _loadingTimer.Tick += (_, _) => MoveLoadingPill();

        Opened += (_, _) => _loadingTimer.Start();
        Closed += (_, _) => _loadingTimer.Stop();
    }

    private void MoveLoadingPill()
    {
        if (LoadingTrack.Bounds.Width <= 0)
        {
            return;
        }

        var pillWidth = LoadingPill.Bounds.Width > 0 ? LoadingPill.Bounds.Width : LoadingPill.Width;
        var travelWidth = LoadingTrack.Bounds.Width + pillWidth;
        _loadingPosition = (_loadingPosition + 5) % travelWidth;
        Canvas.SetLeft(LoadingPill, _loadingPosition - pillWidth);
    }
}
