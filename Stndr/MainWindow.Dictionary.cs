using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Stndr;

public partial class MainWindow
{
    private const double DefaultDictionaryPopupWidth = 280;
    private const double DefaultDictionaryPopupHeight = 170;
    private const double DictionaryPopupMargin = 12;

    private void InitializeDictionaryUi()
    {
        if (_dictionaryPopup is not null)
        {
            Canvas.SetLeft(_dictionaryPopup, _dictionaryPopupLeft);
            Canvas.SetTop(_dictionaryPopup, _dictionaryPopupTop);
        }

        if (_dictionaryPopupDockButton is not null)
        {
            _dictionaryPopupDockButton.Click += (_, e) =>
            {
                DockDictionaryToSidebar();
                e.Handled = true;
            };
        }

        if (_dictionaryPopupCloseButton is not null)
        {
            _dictionaryPopupCloseButton.Click += (_, e) =>
            {
                CloseDictionarySurface();
                e.Handled = true;
            };
        }

        if (_dictionarySidebarPopoutButton is not null)
        {
            _dictionarySidebarPopoutButton.Click += (_, e) =>
            {
                PopOutDictionaryFromSidebar();
                e.Handled = true;
            };
        }

        if (_dictionarySidebarCloseButton is not null)
        {
            _dictionarySidebarCloseButton.Click += (_, e) =>
            {
                CloseDictionarySurface();
                e.Handled = true;
            };
        }

        if (_dictionaryPopupDragHandle is not null)
        {
            _dictionaryPopupDragHandle.PointerPressed += OnDictionaryPopupDragPointerPressed;
            _dictionaryPopupDragHandle.PointerMoved += OnDictionaryPopupDragPointerMoved;
            _dictionaryPopupDragHandle.PointerReleased += OnDictionaryPopupDragPointerReleased;
            _dictionaryPopupDragHandle.PointerCaptureLost += (_, _) => ResetDictionaryPopupDrag();
        }

        RefreshDictionarySurface();
    }

    private void ShowDictionaryEntry(string? word, string? reference)
    {
        _dictionaryCurrentWord = NormalizeDictionaryWord(word, reference);
        _dictionaryCurrentReference = reference?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_dictionaryCurrentWord) &&
            string.IsNullOrWhiteSpace(_dictionaryCurrentReference))
        {
            return;
        }

        if (_isDictionaryDocked)
        {
            RefreshDictionarySurface();
            SaveLayoutState();
            return;
        }

        if (_dictionaryPopupLeft <= 0 || _dictionaryPopupTop <= 0)
        {
            _dictionaryPopupLeft = 360;
            _dictionaryPopupTop = 140;
        }

        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = true;
        }

        ConstrainDictionaryPopupPosition();
        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void DockDictionaryToSidebar()
    {
        _isDictionaryDocked = true;
        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = false;
        }

        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void PopOutDictionaryFromSidebar()
    {
        _isDictionaryDocked = false;
        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = true;
        }

        ConstrainDictionaryPopupPosition();
        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void CloseDictionarySurface()
    {
        _isDictionaryDocked = false;
        _dictionaryCurrentWord = string.Empty;
        _dictionaryCurrentReference = string.Empty;
        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = false;
        }

        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void RefreshDictionarySurface()
    {
        var hasContent = !string.IsNullOrWhiteSpace(_dictionaryCurrentWord) ||
            !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        var displayWord = string.IsNullOrWhiteSpace(_dictionaryCurrentWord)
            ? "Dictionary selection"
            : _dictionaryCurrentWord;
        var status = hasContent
            ? "Dictionary lookup is not connected yet."
            : "Right-click a word in the reader and choose Dictionary.";

        if (_dictionaryPopupWord is not null)
        {
            _dictionaryPopupWord.Text = displayWord;
        }

        if (_dictionaryPopupReference is not null)
        {
            _dictionaryPopupReference.Text = _dictionaryCurrentReference;
            _dictionaryPopupReference.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        }

        if (_dictionaryPopupStatus is not null)
        {
            _dictionaryPopupStatus.Text = status;
        }

        if (_dictionarySidebarWord is not null)
        {
            _dictionarySidebarWord.Text = displayWord;
        }

        if (_dictionarySidebarReference is not null)
        {
            _dictionarySidebarReference.Text = _dictionaryCurrentReference;
            _dictionarySidebarReference.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        }

        if (_dictionarySidebarStatus is not null)
        {
            _dictionarySidebarStatus.Text = status;
        }

        if (_dictionarySidebarCard is not null)
        {
            _dictionarySidebarCard.IsVisible = _isDictionaryDocked && hasContent;
        }

        if (_dictionaryPopup is not null && !_isDictionaryDocked)
        {
            _dictionaryPopup.IsVisible = hasContent && _dictionaryPopup.IsVisible;
        }
    }

    private void ConstrainDictionaryPopupPosition()
    {
        if (_dictionaryPopup is null)
        {
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            Canvas.SetLeft(_dictionaryPopup, _dictionaryPopupLeft);
            Canvas.SetTop(_dictionaryPopup, _dictionaryPopupTop);
            return;
        }

        var popupWidth = _dictionaryPopup.Bounds.Width > 0 ? _dictionaryPopup.Bounds.Width : DefaultDictionaryPopupWidth;
        var popupHeight = _dictionaryPopup.Bounds.Height > 0 ? _dictionaryPopup.Bounds.Height : DefaultDictionaryPopupHeight;
        var maxLeft = Math.Max(DictionaryPopupMargin, Bounds.Width - popupWidth - DictionaryPopupMargin);
        var maxTop = Math.Max(DictionaryPopupMargin, Bounds.Height - popupHeight - DictionaryPopupMargin);
        _dictionaryPopupLeft = Math.Clamp(_dictionaryPopupLeft, DictionaryPopupMargin, maxLeft);
        _dictionaryPopupTop = Math.Clamp(_dictionaryPopupTop, DictionaryPopupMargin, maxTop);
        Canvas.SetLeft(_dictionaryPopup, _dictionaryPopupLeft);
        Canvas.SetTop(_dictionaryPopup, _dictionaryPopupTop);
    }

    private void OnDictionaryPopupDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dictionaryPopup is null || _dictionaryPopupDragHandle is null)
        {
            return;
        }

        var properties = e.GetCurrentPoint(_dictionaryPopupDragHandle).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _dictionaryPopupDragPointerOrigin = e.GetPosition(this);
        _dictionaryPopupDragPopupOrigin = new Point(_dictionaryPopupLeft, _dictionaryPopupTop);
        e.Pointer.Capture(_dictionaryPopupDragHandle);
        e.Handled = true;
    }

    private void OnDictionaryPopupDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dictionaryPopup is null ||
            _dictionaryPopupDragPointerOrigin is null ||
            _dictionaryPopupDragPopupOrigin is null)
        {
            return;
        }

        var pointerPosition = e.GetPosition(this);
        var origin = _dictionaryPopupDragPointerOrigin.Value;
        var popupOrigin = _dictionaryPopupDragPopupOrigin.Value;
        _dictionaryPopupLeft = popupOrigin.X + (pointerPosition.X - origin.X);
        _dictionaryPopupTop = popupOrigin.Y + (pointerPosition.Y - origin.Y);
        ConstrainDictionaryPopupPosition();
        e.Handled = true;
    }

    private void OnDictionaryPopupDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dictionaryPopupDragHandle is not null)
        {
            e.Pointer.Capture(null);
        }

        if (_dictionaryPopupDragPointerOrigin is not null)
        {
            SaveLayoutState();
        }

        ResetDictionaryPopupDrag();
        e.Handled = true;
    }

    private void ResetDictionaryPopupDrag()
    {
        _dictionaryPopupDragPointerOrigin = null;
        _dictionaryPopupDragPopupOrigin = null;
    }

    private static string NormalizeDictionaryWord(string? word, string? reference)
    {
        var text = (word ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(reference) ? string.Empty : "Dictionary selection";
        }

        var collapsed = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= 48)
        {
            return collapsed;
        }

        return $"{collapsed[..45]}...";
    }
}
