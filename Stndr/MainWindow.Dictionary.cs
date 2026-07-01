using System;
using Avalonia;
using Avalonia.Controls;

namespace Stndr;

public partial class MainWindow
{
    private DictionaryPopupWindow? _dictionaryPopupWindow;

    private void InitializeDictionaryUi()
    {
        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = false;
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

        RefreshDictionarySurface();
        ShowDictionaryPopupWindow();
        SaveLayoutState();
    }

    private void DockDictionaryToSidebar()
    {
        _isDictionaryDocked = true;
        CloseDictionaryPopupWindow();
        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void PopOutDictionaryFromSidebar()
    {
        _isDictionaryDocked = false;
        RefreshDictionarySurface();
        ShowDictionaryPopupWindow();
        SaveLayoutState();
    }

    private void CloseDictionarySurface()
    {
        _isDictionaryDocked = false;
        _dictionaryCurrentWord = string.Empty;
        _dictionaryCurrentReference = string.Empty;
        CloseDictionaryPopupWindow();
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

        _dictionaryPopupWindow?.UpdateEntry(displayWord, _dictionaryCurrentReference, status);

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
    }

    private void ShowDictionaryPopupWindow()
    {
        if (_isDictionaryDocked ||
            (string.IsNullOrWhiteSpace(_dictionaryCurrentWord) &&
             string.IsNullOrWhiteSpace(_dictionaryCurrentReference)))
        {
            return;
        }

        var popup = EnsureDictionaryPopupWindow();
        popup.UpdateEntry(
            string.IsNullOrWhiteSpace(_dictionaryCurrentWord) ? "Dictionary selection" : _dictionaryCurrentWord,
            _dictionaryCurrentReference,
            "Dictionary lookup is not connected yet.");
        popup.Position = GetDictionaryPopupScreenPosition();
        if (!popup.IsVisible)
        {
            popup.Show(this);
        }
        else
        {
            popup.Activate();
        }
    }

    private void CloseDictionaryPopupWindow()
    {
        if (_dictionaryPopupWindow is null)
        {
            return;
        }

        var popup = _dictionaryPopupWindow;
        _dictionaryPopupWindow = null;
        popup.Close();
    }

    private DictionaryPopupWindow EnsureDictionaryPopupWindow()
    {
        if (_dictionaryPopupWindow is not null)
        {
            return _dictionaryPopupWindow;
        }

        var popup = new DictionaryPopupWindow();
        popup.DockRequested += (_, _) => DockDictionaryToSidebar();
        popup.DismissRequested += (_, _) => CloseDictionarySurface();
        popup.PositionCommitted += (_, position) =>
        {
            _dictionaryPopupLeft = position.X - Position.X;
            _dictionaryPopupTop = position.Y - Position.Y;
            SaveLayoutState();
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dictionaryPopupWindow, popup))
            {
                _dictionaryPopupWindow = null;
            }
        };
        _dictionaryPopupWindow = popup;
        return popup;
    }

    private PixelPoint GetDictionaryPopupScreenPosition()
    {
        return new PixelPoint(
            Position.X + (int)Math.Round(_dictionaryPopupLeft),
            Position.Y + (int)Math.Round(_dictionaryPopupTop));
    }

    private void ConstrainDictionaryPopupPosition()
    {
        if (_dictionaryPopupWindow is null || !_dictionaryPopupWindow.IsVisible)
        {
            return;
        }

        _dictionaryPopupWindow.Position = GetDictionaryPopupScreenPosition();
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
