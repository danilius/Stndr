using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stndr;

public sealed class DictionaryPopupWindow : Window
{
    private static readonly IBrush PopupBackgroundBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush PopupBorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD"));
    private static readonly IBrush PopupSectionDividerBrush = new SolidColorBrush(Color.Parse("#EAECF0"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#667085"));
    private static readonly IBrush StatusTextBrush = new SolidColorBrush(Color.Parse("#475467"));

    private readonly TextBlock _wordText;
    private readonly TextBlock _referenceText;
    private readonly TextBlock _primaryGlossText;
    private readonly TextBlock _statusText;
    private readonly Border _dragHandle;
    private Point? _dragPointerOrigin;
    private PixelPoint? _dragWindowOrigin;

    public event EventHandler? DockRequested;
    public event EventHandler? DismissRequested;
    public event EventHandler<PixelPoint>? PositionCommitted;

    public DictionaryPopupWindow()
    {
        Width = 280;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = Avalonia.Controls.WindowDecorations.None;
        Background = Brushes.Transparent;

        _wordText = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _referenceText = new TextBlock
        {
            FontSize = 11,
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _statusText = new TextBlock
        {
            Foreground = StatusTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        _primaryGlossText = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#1D2939")),
            IsVisible = false
        };

        var dockButton = new Button
        {
            Content = "Dock",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2),
            MinHeight = 26
        };
        dockButton.Click += (_, e) =>
        {
            DockRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };

        var closeButton = new Button
        {
            Content = "✕",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0
        };
        closeButton.Click += (_, e) =>
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };

        _dragHandle = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = PopupSectionDividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                ColumnSpacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Dictionary",
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    dockButton,
                    closeButton
                }
            }
        };
        Grid.SetColumn(dockButton, 1);
        Grid.SetColumn(closeButton, 2);

        _dragHandle.PointerPressed += OnDragHandlePointerPressed;
        _dragHandle.PointerMoved += OnDragHandlePointerMoved;
        _dragHandle.PointerReleased += OnDragHandlePointerReleased;
        _dragHandle.PointerCaptureLost += (_, _) => ResetDrag();

        Content = new Border
        {
            Background = PopupBackgroundBrush,
            BorderBrush = PopupBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    _dragHandle,
                    _wordText,
                    _referenceText,
                    _primaryGlossText,
                    _statusText
                }
            }
        };
    }

    public void UpdateEntry(string word, string reference, string primaryGloss, string status)
    {
        _wordText.Text = word;
        _referenceText.Text = reference;
        _referenceText.IsVisible = !string.IsNullOrWhiteSpace(reference);
        _primaryGlossText.Text = primaryGloss;
        _primaryGlossText.IsVisible = !string.IsNullOrWhiteSpace(primaryGloss);
        _statusText.Text = status;
    }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_dragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragPointerOrigin = e.GetPosition(this);
        _dragWindowOrigin = Position;
        e.Pointer.Capture(_dragHandle);
        e.Handled = true;
    }

    private void OnDragHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPointerOrigin is null || _dragWindowOrigin is null)
        {
            return;
        }

        var current = e.GetPosition(this);
        var origin = _dragPointerOrigin.Value;
        var windowOrigin = _dragWindowOrigin.Value;
        Position = new PixelPoint(
            windowOrigin.X + (int)Math.Round(current.X - origin.X),
            windowOrigin.Y + (int)Math.Round(current.Y - origin.Y));
        e.Handled = true;
    }

    private void OnDragHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        PositionCommitted?.Invoke(this, Position);
        ResetDrag();
        e.Handled = true;
    }

    private void ResetDrag()
    {
        _dragPointerOrigin = null;
        _dragWindowOrigin = null;
    }
}
