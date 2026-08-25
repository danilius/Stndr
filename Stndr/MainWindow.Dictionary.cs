using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private enum DictionaryReferenceContext
    {
        Unknown,
        Tanakh,
        Rabbinic
    }

    private sealed record DictionarySearchScopeOption(long? LexiconId, string Label)
    {
        public override string ToString() => $"Scope: {Label}";
    }

    private sealed record DictionarySearchModeOption(SefariaDictionarySearchMode Mode, string Label)
    {
        public override string ToString() => $"Find: {Label}";
    }

    private static readonly Regex DictionaryHtmlTagRegex = new("<.*?>", RegexOptions.Compiled);
    private static readonly Regex DictionaryCitationRegex = new(
        @"(?<![\p{L}\p{N}])(?<abbr>B\.?\s*Kam\.?|Gen\.?|Ge\.?|Ex\.?|Exod\.?|Lev\.?|Num\.?|Deut\.?|Josh\.?|Judg\.?|I\s+Sam\.?|II\s+Sam\.?|I\s+Kings?|II\s+Kings?|Isa\.?|Jer\.?|Ezek\.?|Ps\.?|Prov\.?|Job|Ruth|Lam\.?|Eccl\.?|Esth\.?|Dan\.?|Ezra|Neh\.?)\s+(?<loc>\d{1,4}(?::\d{1,4})?(?:[abABᵃᵇ])?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> DictionaryCitationTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B Kam"] = "Bava Kamma",
        ["B. Kam"] = "Bava Kamma",
        ["B Kam."] = "Bava Kamma",
        ["B. Kam."] = "Bava Kamma",
        ["Gen"] = "Genesis",
        ["Gen."] = "Genesis",
        ["Ge"] = "Genesis",
        ["Ge."] = "Genesis",
        ["Ex"] = "Exodus",
        ["Ex."] = "Exodus",
        ["Exod"] = "Exodus",
        ["Exod."] = "Exodus",
        ["Lev"] = "Leviticus",
        ["Lev."] = "Leviticus",
        ["Num"] = "Numbers",
        ["Num."] = "Numbers",
        ["Deut"] = "Deuteronomy",
        ["Deut."] = "Deuteronomy",
        ["Josh"] = "Joshua",
        ["Josh."] = "Joshua",
        ["Judg"] = "Judges",
        ["Judg."] = "Judges",
        ["I Sam"] = "I Samuel",
        ["I Sam."] = "I Samuel",
        ["II Sam"] = "II Samuel",
        ["II Sam."] = "II Samuel",
        ["I King"] = "I Kings",
        ["I Kings"] = "I Kings",
        ["II King"] = "II Kings",
        ["II Kings"] = "II Kings",
        ["Isa"] = "Isaiah",
        ["Isa."] = "Isaiah",
        ["Jer"] = "Jeremiah",
        ["Jer."] = "Jeremiah",
        ["Ezek"] = "Ezekiel",
        ["Ezek."] = "Ezekiel",
        ["Ps"] = "Psalms",
        ["Ps."] = "Psalms",
        ["Prov"] = "Proverbs",
        ["Prov."] = "Proverbs",
        ["Job"] = "Job",
        ["Ruth"] = "Ruth",
        ["Lam"] = "Lamentations",
        ["Lam."] = "Lamentations",
        ["Eccl"] = "Ecclesiastes",
        ["Eccl."] = "Ecclesiastes",
        ["Esth"] = "Esther",
        ["Esth."] = "Esther",
        ["Dan"] = "Daniel",
        ["Dan."] = "Daniel",
        ["Ezra"] = "Ezra",
        ["Neh"] = "Nehemiah",
        ["Neh."] = "Nehemiah"
    };
    private static readonly HashSet<char> HebrewPrefixLetters = new() { 'ו', 'ב', 'כ', 'ל', 'מ', 'ה', 'ש' };

    /// <summary>
    /// Common Hebrew inflectional endings, longest first. Used only as lookup fallbacks when the
    /// surface form is missing from Sefaria WordForm tables.
    /// </summary>
    private static readonly string[] DictionarySuffixes =
    {
        "ותיהם", "ותיהן", "יכם", "יכן", "יהם", "יהן",
        "כם", "כן", "הם", "הן", "נו", "ני",
        "ים", "ין", "ות",
        "ך", "ו", "ם", "ן", "י", "ה", "ת"
    };

    private const int MaxDictionaryLookupCandidates = 12;

    private static readonly HashSet<string> TanakhBooks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Genesis", "Exodus", "Leviticus", "Numbers", "Deuteronomy",
        "Joshua", "Judges", "I Samuel", "II Samuel", "I Kings", "II Kings",
        "Isaiah", "Jeremiah", "Ezekiel", "Hosea", "Joel", "Amos", "Obadiah",
        "Jonah", "Micah", "Nahum", "Habakkuk", "Zephaniah", "Haggai",
        "Zechariah", "Malachi", "Psalms", "Proverbs", "Job", "Song of Songs",
        "Ruth", "Lamentations", "Ecclesiastes", "Esther", "Daniel", "Ezra",
        "Nehemiah", "I Chronicles", "II Chronicles"
    };

    private DictionaryPopupWindow? _dictionaryPopupWindow;

    private void InitializeDictionaryUi()
    {
        if (_dictionaryPopup is not null)
        {
            _dictionaryPopup.IsVisible = false;
        }

        RefreshDictionarySurface();
    }

    private Control CreateDictionaryView()
    {
        ResetDictionaryNavigationHistory();
        _dictionaryLookupBox = null;
        _dictionaryLookupReference = null;
        _dictionaryLookupStatus = null;
        _dictionaryLookupResultsPanel = null;
        _dictionarySearchResultsExpander = null;
        _dictionarySearchModeBox = null;

        var header = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Dictionary",
                    FontSize = 24,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "Read an installed dictionary in entry order. Use Search to find a particular headword or words within entries.",
                    Foreground = new SolidColorBrush(Color.Parse("#475467")),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var dictionaryReader = CreateDictionaryCatalogueControl();

        var root = new Grid
        {
            Background = Brushes.White,
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 14,
            Margin = new Thickness(18),
            Children = { header, dictionaryReader }
        };
        Grid.SetRow(dictionaryReader, 1);

        _ = LoadDictionaryCatalogueAsync();
        return root;
    }

    private static Button CreateDictionaryHistoryButton(
        string glyph,
        string tooltip,
        Func<Task> navigate)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            FontSize = 20,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += async (_, _) => await navigate();
        return button;
    }

    private void ResetDictionaryNavigationHistory(long? entryId = null)
    {
        _dictionaryNavigationHistory.Clear();
        _dictionaryNavigationHistoryIndex = -1;
        if (entryId is > 0)
        {
            _dictionaryNavigationHistory.Add(entryId.Value);
            _dictionaryNavigationHistoryIndex = 0;
        }
        UpdateDictionaryHistoryButtons();
    }

    private void PushDictionaryNavigationHistory(long sourceEntryId, long targetEntryId)
    {
        TrimDictionaryForwardHistory();
        if (sourceEntryId > 0 &&
            (_dictionaryNavigationHistoryIndex < 0 ||
             _dictionaryNavigationHistory[_dictionaryNavigationHistoryIndex] != sourceEntryId))
        {
            _dictionaryNavigationHistory.Add(sourceEntryId);
            _dictionaryNavigationHistoryIndex = _dictionaryNavigationHistory.Count - 1;
        }

        TrimDictionaryForwardHistory();
        if (targetEntryId > 0 &&
            (_dictionaryNavigationHistoryIndex < 0 ||
             _dictionaryNavigationHistory[_dictionaryNavigationHistoryIndex] != targetEntryId))
        {
            _dictionaryNavigationHistory.Add(targetEntryId);
            _dictionaryNavigationHistoryIndex = _dictionaryNavigationHistory.Count - 1;
        }
        UpdateDictionaryHistoryButtons();
    }

    private void TrimDictionaryForwardHistory()
    {
        var firstForwardIndex = _dictionaryNavigationHistoryIndex + 1;
        if (firstForwardIndex >= 0 && firstForwardIndex < _dictionaryNavigationHistory.Count)
        {
            _dictionaryNavigationHistory.RemoveRange(
                firstForwardIndex,
                _dictionaryNavigationHistory.Count - firstForwardIndex);
        }
    }

    private void UpdateDictionaryHistoryButtons()
    {
        if (_dictionaryHistoryBackButton is not null)
        {
            _dictionaryHistoryBackButton.IsEnabled = _dictionaryNavigationHistoryIndex > 0;
        }
        if (_dictionaryHistoryForwardButton is not null)
        {
            _dictionaryHistoryForwardButton.IsEnabled =
                _dictionaryNavigationHistoryIndex >= 0 &&
                _dictionaryNavigationHistoryIndex < _dictionaryNavigationHistory.Count - 1;
        }
    }

    private async Task NavigateBackInDictionaryHistoryAsync()
    {
        await NavigateDictionaryHistoryAsync(_dictionaryNavigationHistoryIndex - 1);
    }

    private async Task NavigateForwardInDictionaryHistoryAsync()
    {
        await NavigateDictionaryHistoryAsync(_dictionaryNavigationHistoryIndex + 1);
    }

    private async Task NavigateDictionaryHistoryAsync(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _dictionaryNavigationHistory.Count)
        {
            return;
        }

        var entry = await _sefariaLibrary.GetOfflineDictionaryEntryAsync(
            _dictionaryNavigationHistory[targetIndex]);
        if (entry is null)
        {
            return;
        }

        _dictionaryNavigationHistoryIndex = targetIndex;
        UpdateDictionaryHistoryButtons();
        await OpenDictionaryEntryInBookAsync(entry, preserveNavigationHistory: true);
    }

    private bool TryHandleDictionaryHistoryShortcut(KeyEventArgs e)
    {
        if (_centerTabs?.SelectedItem is not TabItem selectedTab ||
            !string.Equals(selectedTab.Tag as string, DictionaryTabTitle, StringComparison.Ordinal) ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return false;
        }

        if (e.Key == Key.Left && _dictionaryNavigationHistoryIndex > 0)
        {
            _ = NavigateBackInDictionaryHistoryAsync();
            return true;
        }
        if (e.Key == Key.Right &&
            _dictionaryNavigationHistoryIndex >= 0 &&
            _dictionaryNavigationHistoryIndex < _dictionaryNavigationHistory.Count - 1)
        {
            _ = NavigateForwardInDictionaryHistoryAsync();
            return true;
        }

        return false;
    }

    private Control CreateDictionaryCatalogueControl()
    {
        _dictionaryBrowseTitle = new TextBlock
        {
            Text = "Dictionary reader",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _dictionaryBrowseStatus = new TextBlock
        {
            Text = "Loading installed dictionaries...",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap
        };
        _dictionaryBrowseInitialsPanel = null;
        _dictionaryBrowseEntriesPanel = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16)
        };
        _dictionaryBrowseScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _dictionaryBrowseEntriesPanel
        };
        _dictionaryBrowseScrollViewer.ScrollChanged += async (_, _) =>
            await LoadMoreDictionaryBrowseEntriesIfNeededAsync();

        _dictionaryHistoryBackButton = CreateDictionaryHistoryButton(
            "←",
            "Back (Alt+Left)",
            NavigateBackInDictionaryHistoryAsync);
        _dictionaryHistoryForwardButton = CreateDictionaryHistoryButton(
            "→",
            "Forward (Alt+Right)",
            NavigateForwardInDictionaryHistoryAsync);
        var historyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                _dictionaryHistoryBackButton,
                _dictionaryHistoryForwardButton
            }
        };
        var heading = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _dictionaryBrowseTitle,
                _dictionaryBrowseStatus
            }
        };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(16, 12),
            Children = { heading, historyButtons }
        };
        Grid.SetColumn(historyButtons, 1);
        UpdateDictionaryHistoryButtons();

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { header, _dictionaryBrowseScrollViewer }
        };
        Grid.SetRow(_dictionaryBrowseScrollViewer, 1);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private async Task LoadDictionaryCatalogueAsync()
    {
        var loadGeneration = ++_dictionaryCatalogueLoadGeneration;
        if (_dictionaryBrowseEntriesPanel is null)
        {
            return;
        }

        try
        {
            var lexicons = await _sefariaLibrary.GetOfflineLexiconsAsync();
            if (_dictionaryBrowseEntriesPanel is null || loadGeneration != _dictionaryCatalogueLoadGeneration)
            {
                return;
            }

            _dictionaryLexicons = lexicons;
            if (lexicons.Count == 0)
            {
                _dictionaryBrowseEntriesPanel.Children.Clear();
                _dictionaryBrowseEntriesPanel.Children.Add(new TextBlock
                {
                    Text = _sefariaLibrary.HasOfflineLibrary
                        ? "No dictionaries were found in the offline library."
                        : "Install the Sefaria library to browse dictionaries.",
                    Foreground = new SolidColorBrush(Color.Parse("#667085")),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(_dictionaryRequestedLexiconName))
            {
                ApplyRequestedDictionarySelection();
            }
            else if (_dictionarySelectedLexiconId is long selectedId &&
                     lexicons.FirstOrDefault(item => item.Id == selectedId) is { } selectedLexicon)
            {
                await LoadDictionaryFromStartAsync(selectedLexicon);
            }
            else
            {
                ShowDictionaryChooser();
            }
        }
        catch (Exception ex)
        {
            if (_dictionaryBrowseEntriesPanel is null)
            {
                return;
            }

            _dictionaryBrowseEntriesPanel.Children.Clear();
            _dictionaryBrowseEntriesPanel.Children.Add(new TextBlock
            {
                Text = $"Could not load dictionaries: {ex.Message}",
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void ShowDictionaryChooser()
    {
        ResetDictionaryNavigationHistory();
        CancelDictionaryBrowseInFlight();
        _dictionaryBrowseLexiconId = null;
        _dictionaryBrowseHighlightedEntryId = null;
        _dictionaryBrowseEntries.Clear();
        if (_dictionaryBrowseTitle is not null)
        {
            _dictionaryBrowseTitle.Text = "Choose a dictionary to browse";
        }
        if (_dictionaryBrowseStatus is not null)
        {
            _dictionaryBrowseStatus.Text =
                $"{_dictionaryLexicons.Count:N0} dictionaries · {_dictionaryLexicons.Sum(item => item.EntryCount):N0} entries installed.";
        }
        if (_dictionaryBrowseInitialsPanel is not null)
        {
            _dictionaryBrowseInitialsPanel.IsVisible = false;
            _dictionaryBrowseInitialsPanel.Children.Clear();
        }
        if (_dictionaryBrowseEntriesPanel is null)
        {
            return;
        }

        _dictionaryBrowseEntriesPanel.Children.Clear();
        foreach (var lexicon in _dictionaryLexicons)
        {
            var language = string.Join(" → ", new[] { lexicon.Language, lexicon.ToLanguage }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9),
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = lexicon.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = $"{lexicon.EntryCount:N0} entries{(language.Length > 0 ? $" · {language}" : "")}",
                            Foreground = new SolidColorBrush(Color.Parse("#667085"))
                        }
                    }
                }
            };
            button.Click += async (_, _) => await LoadDictionaryFromStartAsync(lexicon);
            _dictionaryBrowseEntriesPanel.Children.Add(button);
        }
    }

    private async Task LoadDictionaryFromStartAsync(SefariaLexiconInfo lexicon, string prefix = "")
    {
        ResetDictionaryNavigationHistory();
        _dictionaryCatalogueLoadGeneration++;
        CancelDictionaryBrowseInFlight();
        var cts = _dictionaryBrowseCts;
        _isDictionaryBrowseLoading = true;
        _dictionaryBrowseLexiconId = lexicon.Id;
        _dictionarySelectedLexiconId = lexicon.Id;
        _dictionaryDrillDownPrefix = prefix;
        _dictionaryBrowseHighlightedEntryId = null;
        ApplyDictionaryBrowseHeading(lexicon, "Loading entries...");
        try
        {
            var entries = await _sefariaLibrary.BrowseOfflineDictionaryAsync(
                lexicon.Id, prefix, 0, 30, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SetDictionaryBrowseEntries(entries, null);
            var location = prefix.Length == 0 ? "the beginning" : $"{prefix}";
            ApplyDictionaryBrowseHeading(
                lexicon,
                entries.Count == 0
                    ? $"No entries begin with {prefix}."
                    : $"Browsing from {location}. Scroll to load nearby entries.");
            UpdateReaderTools();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ApplyDictionaryBrowseHeading(lexicon, $"Could not browse this dictionary: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_dictionaryBrowseCts, cts))
            {
                _isDictionaryBrowseLoading = false;
            }
        }
    }

    private async Task OpenDictionaryEntryInBookAsync(
        SefariaDictionaryEntry entry,
        bool preserveNavigationHistory = false)
    {
        if (!entry.IsOffline || entry.LexiconId <= 0)
        {
            return;
        }

        if (!preserveNavigationHistory)
        {
            ResetDictionaryNavigationHistory(entry.EntryId);
        }

        _dictionaryCatalogueLoadGeneration++;
        var lexicon = _dictionaryLexicons.FirstOrDefault(item => item.Id == entry.LexiconId)
            ?? new SefariaLexiconInfo(entry.LexiconId, entry.LexiconName, "", "", 0);

        CancelDictionaryBrowseInFlight();
        var cts = _dictionaryBrowseCts;
        _isDictionaryBrowseLoading = true;
        _dictionaryBrowseLexiconId = entry.LexiconId;
        _dictionarySelectedLexiconId = entry.LexiconId;
        _dictionaryDrillDownPrefix = string.Empty;
        _dictionaryBrowseHighlightedEntryId = entry.EntryId;
        ApplyDictionaryBrowseHeading(lexicon, $"Opening {entry.Headword} in context...");
        try
        {
            var entries = await _sefariaLibrary.GetOfflineDictionaryContextAsync(entry.EntryId, 20, 20, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SetDictionaryBrowseEntries(entries, entry.EntryId);
            ApplyDictionaryBrowseHeading(lexicon, $"{entry.Headword} · Scroll up or down for nearby entries.");
            UpdateReaderTools();
            ScrollDictionaryBrowseEntryIntoView(entry.EntryId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ApplyDictionaryBrowseHeading(lexicon, $"Could not open this entry: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_dictionaryBrowseCts, cts))
            {
                _isDictionaryBrowseLoading = false;
            }
        }
    }

    private void ApplyDictionaryBrowseHeading(SefariaLexiconInfo lexicon, string status)
    {
        if (_dictionaryBrowseTitle is not null)
        {
            _dictionaryBrowseTitle.Text = lexicon.EntryCount > 0
                ? $"{lexicon.Name} · {lexicon.EntryCount:N0} entries"
                : lexicon.Name;
        }
        if (_dictionaryBrowseStatus is not null)
        {
            _dictionaryBrowseStatus.Text = status;
        }
    }

    private async Task LoadDictionaryAtPrefixAsync(SefariaLexiconInfo lexicon, string prefix)
    {
        ResetDictionaryNavigationHistory();
        _dictionaryCatalogueLoadGeneration++;
        CancelDictionaryBrowseInFlight();
        var cts = _dictionaryBrowseCts;
        _isDictionaryBrowseLoading = true;
        _dictionaryBrowseLexiconId = lexicon.Id;
        _dictionarySelectedLexiconId = lexicon.Id;
        _dictionaryDrillDownPrefix = prefix;
        ApplyDictionaryBrowseHeading(lexicon, $"Opening {prefix}...");
        try
        {
            var first = (await _sefariaLibrary.BrowseOfflineDictionaryAsync(
                lexicon.Id, prefix, 0, 1, cts.Token)).FirstOrDefault();
            if (cts.IsCancellationRequested)
            {
                return;
            }
            if (first is null)
            {
                ApplyDictionaryBrowseHeading(lexicon, $"No entries begin with {prefix}.");
                return;
            }

            var entries = await _sefariaLibrary.GetOfflineDictionaryContextAsync(
                first.EntryId, 20, 20, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            SetDictionaryBrowseEntries(entries, first.EntryId);
            ApplyDictionaryBrowseHeading(lexicon, $"Browsing from {prefix}. Scroll up or down for nearby entries.");
            UpdateReaderTools();
            ScrollDictionaryBrowseEntryIntoView(first.EntryId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ApplyDictionaryBrowseHeading(lexicon, $"Could not browse this dictionary: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_dictionaryBrowseCts, cts))
            {
                _isDictionaryBrowseLoading = false;
            }
        }
    }

    private void ScrollDictionaryBrowseEntryIntoView(long entryId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = _dictionaryBrowseEntriesPanel?.Children
                .OfType<Control>()
                .FirstOrDefault(control => control.Tag is long id && id == entryId);
            if (target is null || _dictionaryBrowseEntriesPanel is null ||
                _dictionaryBrowseScrollViewer is null)
            {
                return;
            }

            var location = target.TranslatePoint(new Point(0, 0), _dictionaryBrowseEntriesPanel);
            if (location is { } point)
            {
                _dictionaryBrowseScrollViewer.Offset = new Vector(
                    _dictionaryBrowseScrollViewer.Offset.X,
                    Math.Max(0, point.Y - (_dictionaryBrowseScrollViewer.Viewport.Height * 0.25)));
            }
        }, DispatcherPriority.Background);
    }

    private void SetDictionaryBrowseEntries(IReadOnlyList<SefariaDictionaryEntry> entries, long? highlightedEntryId)
    {
        _dictionaryBrowseEntries.Clear();
        _dictionaryBrowseEntries.AddRange(entries);
        _dictionaryBrowseHighlightedEntryId = highlightedEntryId;
        if (_dictionaryBrowseEntriesPanel is null)
        {
            return;
        }

        _dictionaryBrowseEntriesPanel.Children.Clear();
        foreach (var entry in entries)
        {
            _dictionaryBrowseEntriesPanel.Children.Add(CreateDictionaryBookEntry(entry, entry.EntryId == highlightedEntryId));
        }
        if (_dictionaryBrowseScrollViewer is not null && highlightedEntryId is null)
        {
            _dictionaryBrowseScrollViewer.Offset = new Vector(0, 0);
            _dictionaryBrowseLastScrollOffset = 0;
        }
    }

    private Control CreateDictionaryBookEntry(SefariaDictionaryEntry entry, bool highlighted)
    {
        return new Border
        {
            Tag = entry.EntryId,
            Background = highlighted ? new SolidColorBrush(Color.Parse("#FFF8E7")) : Brushes.Transparent,
            BorderBrush = highlighted ? new SolidColorBrush(Color.Parse("#F2C94C")) : Brushes.Transparent,
            BorderThickness = highlighted ? new Thickness(2) : new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = highlighted ? new Thickness(6) : new Thickness(0),
            Child = CreateDictionaryResultExpander(entry, expanded: true, showOpenInDictionary: false)
        };
    }

    private async Task LoadMoreDictionaryBrowseEntriesIfNeededAsync()
    {
        if (_dictionaryBrowseScrollViewer is null)
        {
            return;
        }

        var viewer = _dictionaryBrowseScrollViewer;
        var currentOffset = viewer.Offset.Y;
        var movingUp = currentOffset < _dictionaryBrowseLastScrollOffset - 1;
        var movingDown = currentOffset > _dictionaryBrowseLastScrollOffset + 1;
        _dictionaryBrowseLastScrollOffset = currentOffset;
        if (_isDictionaryBrowseLoading || _dictionaryBrowseEntries.Count == 0 ||
            _dictionaryBrowseEntriesPanel is null || (!movingUp && !movingDown))
        {
            return;
        }

        var nearTop = viewer.Offset.Y < 120;
        var nearBottom = viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height < 240;
        var before = nearTop && movingUp;
        var after = nearBottom && movingDown;
        if (!before && !after)
        {
            return;
        }

        var anchor = before ? _dictionaryBrowseEntries[0] : _dictionaryBrowseEntries[^1];
        _isDictionaryBrowseLoading = true;
        var cts = _dictionaryBrowseCts;
        var oldExtent = viewer.Extent.Height;
        var oldOffset = viewer.Offset.Y;
        try
        {
            var adjacent = await _sefariaLibrary.GetOfflineAdjacentDictionaryEntriesAsync(
                anchor.EntryId, before, 20, cts.Token);
            if (cts.IsCancellationRequested || adjacent.Count == 0)
            {
                return;
            }

            var existingIds = _dictionaryBrowseEntries.Select(item => item.EntryId).ToHashSet();
            var additions = adjacent.Where(item => existingIds.Add(item.EntryId)).ToList();
            if (before)
            {
                _dictionaryBrowseEntries.InsertRange(0, additions);
                for (var index = additions.Count - 1; index >= 0; index--)
                {
                    var item = additions[index];
                    _dictionaryBrowseEntriesPanel.Children.Insert(
                        0, CreateDictionaryBookEntry(item, item.EntryId == _dictionaryBrowseHighlightedEntryId));
                }
                Dispatcher.UIThread.Post(() =>
                {
                    var addedHeight = Math.Max(0, viewer.Extent.Height - oldExtent);
                    viewer.Offset = new Vector(viewer.Offset.X, oldOffset + addedHeight);
                }, DispatcherPriority.Background);
            }
            else
            {
                _dictionaryBrowseEntries.AddRange(additions);
                foreach (var item in additions)
                {
                    _dictionaryBrowseEntriesPanel.Children.Add(
                        CreateDictionaryBookEntry(item, item.EntryId == _dictionaryBrowseHighlightedEntryId));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_dictionaryBrowseCts, cts))
            {
                _isDictionaryBrowseLoading = false;
            }
        }
    }

    private void CancelDictionaryBrowseInFlight()
    {
        _dictionaryBrowseCts.Cancel();
        _dictionaryBrowseCts.Dispose();
        _dictionaryBrowseCts = new CancellationTokenSource();
    }

    private Task SubmitDictionarySearchAsync() =>
        RunOfflineDictionarySearchAsync(_dictionaryLookupBox?.Text);

    private async Task RunOfflineDictionarySearchAsync(string? query)
    {
        // Prefer the live textbox so a typed query wins over a previous right-click lookup word.
        var value = (_dictionaryLookupBox?.Text ?? query)?.Trim() ?? "";
        if (value.Length == 0)
        {
            _dictionaryTabStatusText = "Enter a headword, word form, identifier, transliteration, or definition term.";
            ApplyDictionaryTabHeaderState(syncLookupBox: false);
            return;
        }

        CancelDictionaryTabLookupInFlight();

        _dictionaryTabCurrentWord = value;
        _dictionaryTabCurrentReference = string.Empty;
        _dictionaryTabStatusText = $"Searching installed dictionaries for {value}...";
        ClearDictionaryTabResults();
        ApplyDictionaryTabHeaderState();
        try
        {
            var mode = (_dictionarySearchModeBox?.SelectedItem as DictionarySearchModeOption)?.Mode
                ?? SefariaDictionarySearchMode.Everything;
            var entries = await _sefariaLibrary.SearchOfflineDictionaryAsync(
                value,
                _dictionarySelectedLexiconId,
                100,
                mode: mode);
            if (!string.Equals(_dictionaryTabCurrentWord, value, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryTabStatusText = entries.Count == 0
                ? "No installed dictionary entries matched this search."
                : $"{entries.Count:N0} matching entr{(entries.Count == 1 ? "y" : "ies")}. Open a result to browse nearby entries.";
            ApplyDictionaryTabHeaderState();
            RenderDictionaryTabResults(entries);
        }
        catch (Exception ex)
        {
            if (!string.Equals(_dictionaryTabCurrentWord, value, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryTabStatusText = $"Dictionary search failed: {ex.Message}";
            ApplyDictionaryTabHeaderState();
        }
    }

    private void CancelDictionaryTabLookupInFlight()
    {
        _dictionaryTabLookupCts.Cancel();
        _dictionaryTabLookupCts.Dispose();
        _dictionaryTabLookupCts = new CancellationTokenSource();
    }

    private void CancelDictionaryLookupInFlight()
    {
        _dictionaryLookupCts.Cancel();
        _dictionaryLookupCts.Dispose();
        _dictionaryLookupCts = new CancellationTokenSource();
    }

    private bool TryOpenDictionaryWork(string workTitle)
    {
        if (!_sefariaLibrary.HasOfflineLibrary) return false;
        var lexiconName = workTitle switch
        {
            "Jastrow" => "Jastrow Dictionary",
            "BDB" => "BDB Dictionary",
            "BDB Aramaic" => "BDB Aramaic Dictionary",
            "Klein Dictionary" => "Klein Dictionary",
            "Sefer HaShorashim" => "Sefer HaShorashim",
            "Animadversions by Elias Levita on Sefer HaShorashim" => "Animadversions by Elias Levita on Sefer HaShorashim",
            _ => ""
        };
        if (lexiconName.Length == 0) return false;

        _dictionaryRequestedLexiconName = lexiconName;
        OpenOrSelectTab(DictionaryTabTitle);
        ApplyRequestedDictionarySelection();
        return true;
    }

    private void ApplyRequestedDictionarySelection()
    {
        if (string.IsNullOrWhiteSpace(_dictionaryRequestedLexiconName)) return;
        if (_dictionaryLexicons.FirstOrDefault(option =>
                string.Equals(option.Name, _dictionaryRequestedLexiconName, StringComparison.Ordinal)) is { } selected)
        {
            _dictionaryRequestedLexiconName = string.Empty;
            _ = LoadDictionaryFromStartAsync(selected);
        }
    }

    private Control CreateDockedDictionaryToolsControl()
    {
        var dictionaryFontSize = GetDictionaryFontSize();
        _dictionaryToolsWord = new SelectableTextBlock
        {
            FontSize = dictionaryFontSize,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _dictionaryToolsReference = new TextBlock
        {
            FontSize = Math.Max(11, dictionaryFontSize - 3),
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _dictionaryToolsResultsPanel = new StackPanel { Spacing = 4 };
        _dictionaryToolsStatus = new TextBlock
        {
            FontSize = dictionaryFontSize,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            TextWrapping = TextWrapping.Wrap
        };

        var popoutButton = new Button
        {
            Content = "Pop out",
            Padding = new Thickness(8, 2),
            MinHeight = 26
        };
        popoutButton.Click += (_, e) =>
        {
            PopOutDictionaryFromReaderTools();
            e.Handled = true;
        };

        var openButton = new Button
        {
            Content = "Open in tab",
            Padding = new Thickness(8, 2),
            MinHeight = 26
        };
        openButton.Click += async (_, e) =>
        {
            await OpenCurrentOnPageDictionaryInTabAsync();
            e.Handled = true;
        };

        var closeButton = new Button
        {
            Content = "✕",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0
        };
        closeButton.Click += (_, e) =>
        {
            CloseDictionarySurface();
            e.Handled = true;
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Dictionary",
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                openButton,
                popoutButton,
                closeButton
            }
        };
        Grid.SetColumn(openButton, 1);
        Grid.SetColumn(popoutButton, 2);
        Grid.SetColumn(closeButton, 3);

        var content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    header,
                    _dictionaryToolsWord,
                    _dictionaryToolsReference,
                    new ScrollViewer
                    {
                        MaxHeight = 360,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        Content = _dictionaryToolsResultsPanel
                    },
                    _dictionaryToolsStatus
                }
            }
        };

        ApplyDictionaryToolsContent();

        return CreateReaderToolsGroup(
            "Dictionary",
            content,
            _isDictionaryToolsExpanded,
            value => _isDictionaryToolsExpanded = value);
    }

    private void ApplyDictionaryToolsContent()
    {
        var hasContent = !string.IsNullOrWhiteSpace(_dictionaryCurrentWord) ||
            !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        var displayWord = string.IsNullOrWhiteSpace(_dictionaryCurrentWord)
            ? "Dictionary selection"
            : _dictionaryCurrentWord;
        var status = hasContent
            ? _dictionaryStatusText
            : "Right-click a word in the reader and choose Dictionary.";

        if (_dictionaryToolsWord is not null)
        {
            _dictionaryToolsWord.Text = displayWord;
        }

        if (_dictionaryToolsReference is not null)
        {
            _dictionaryToolsReference.Text = _dictionaryCurrentReference;
            _dictionaryToolsReference.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        }

        if (_dictionaryToolsResultsPanel is not null)
        {
            PopulateDictionaryEntriesPanel(_dictionaryToolsResultsPanel, GetDictionaryFontSize());
        }

        if (_dictionaryToolsStatus is not null)
        {
            _dictionaryToolsStatus.Text = status;
        }
    }

    private void ClearDictionaryToolsControls()
    {
        _dictionaryToolsWord = null;
        _dictionaryToolsReference = null;
        _dictionaryToolsResultsPanel = null;
        _dictionaryToolsStatus = null;
    }

    private async Task OpenCurrentOnPageDictionaryInTabAsync()
    {
        OpenOrSelectTab(DictionaryTabTitle);
        if (_dictionaryLexicons.Count == 0)
        {
            await LoadDictionaryCatalogueAsync();
        }

        var entry = _dictionaryDisplayedEntries.FirstOrDefault(item => item.IsOffline && item.LexiconId > 0);
        if (entry is not null)
        {
            await OpenDictionaryEntryInBookAsync(entry);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_dictionaryCurrentWord))
        {
            var entries = await LookupDictionaryEntriesRoutedAsync(
                NormalizeDictionaryLookupWord(_dictionaryCurrentWord),
                _dictionaryCurrentReference,
                CancellationToken.None);
            var offlineEntry = entries.FirstOrDefault(item => item.IsOffline && item.LexiconId > 0);
            if (offlineEntry is not null)
            {
                await OpenDictionaryEntryInBookAsync(offlineEntry);
            }
        }
    }

    private void ShowDictionaryEntry(string? word, string? reference, PixelPoint? screenAnchor = null)
    {
        _dictionaryCurrentWord = NormalizeDictionaryWord(word, reference);
        _dictionaryCurrentReference = reference?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_dictionaryCurrentWord) &&
            string.IsNullOrWhiteSpace(_dictionaryCurrentReference))
        {
            return;
        }

        if (screenAnchor is not null)
        {
            _dictionaryAnchorScreenPoint = screenAnchor;
            _dictionaryPopupUserPositioned = false;
        }

        var lookupWord = NormalizeDictionaryLookupWord(word);
        _dictionaryPrimaryGloss = string.Empty;
        _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
        _dictionaryStatusText = string.IsNullOrWhiteSpace(lookupWord)
            ? "Select a single word to look it up."
            : "Looking up dictionary entry...";

        RefreshDictionarySurface();
        // Reader lookups update only the floating/docked on-page dictionary surface.
        ShowDictionaryPopupWindow(repositionToAnchor: true);
        _ = RunOnPageDictionaryLookupAsync(lookupWord, _dictionaryCurrentReference);
        SaveLayoutState();
    }

    private void ApplyDictionaryTabHeaderState(bool syncLookupBox = true)
    {
        if (syncLookupBox && _dictionaryLookupBox is not null)
        {
            _dictionaryLookupBox.Text = _dictionaryTabCurrentWord;
        }

        if (_dictionaryLookupReference is not null)
        {
            _dictionaryLookupReference.Text = _dictionaryTabCurrentReference;
            _dictionaryLookupReference.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryTabCurrentReference);
        }

        if (_dictionaryLookupStatus is not null)
        {
            _dictionaryLookupStatus.Text = _dictionaryTabStatusText;
        }
    }

    private async Task RunOnPageDictionaryLookupAsync(string? word, string? reference)
    {
        var lookupWord = NormalizeDictionaryLookupWord(word);
        if (string.IsNullOrWhiteSpace(lookupWord))
        {
            _dictionaryStatusText = "Select a single word to look it up.";
            _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
            RefreshDictionarySurface();
            return;
        }

        CancelDictionaryLookupInFlight();
        var cts = _dictionaryLookupCts;
        var lookupGeneration = _dictionaryCurrentWord;

        try
        {
            var entries = await LookupDictionaryEntriesRoutedAsync(lookupWord, reference ?? string.Empty, cts.Token);
            if (cts.IsCancellationRequested ||
                !string.Equals(_dictionaryCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryDisplayedEntries = entries;
            if (entries.Count == 0)
            {
                _dictionaryPrimaryGloss = string.Empty;
                _dictionaryStatusText = "No dictionary entries found.";
            }
            else
            {
                _dictionaryPrimaryGloss = BuildPrimaryDictionaryGloss(entries[0]);
                _dictionaryStatusText = $"{entries.Count} dictionary entr{(entries.Count == 1 ? "y" : "ies")} found.";
            }

            RefreshDictionarySurface();
            SaveLayoutState();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            if (!string.Equals(_dictionaryCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
            _dictionaryStatusText = "Dictionary lookup failed. Check your internet connection and try again.";
            RefreshDictionarySurface();
        }
        catch (System.Text.Json.JsonException)
        {
            if (!string.Equals(_dictionaryCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
            _dictionaryStatusText = "Dictionary lookup failed due to an unexpected response.";
            RefreshDictionarySurface();
        }
    }

    private async Task RunDictionaryTabLookupAsync(string? word, string? reference)
    {
        var lookupWord = NormalizeDictionaryLookupWord(word);
        var displayQuery = string.IsNullOrWhiteSpace(word) ? lookupWord : word.Trim();
        _dictionaryTabCurrentWord = NormalizeDictionaryWord(displayQuery, reference);
        _dictionaryTabCurrentReference = reference?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(lookupWord))
        {
            _dictionaryTabStatusText = "Type a single word to look it up.";
            ClearDictionaryTabResults();
            ApplyDictionaryTabHeaderState();
            return;
        }

        _dictionaryTabStatusText = $"Looking up {lookupWord}...";
        ClearDictionaryTabResults();
        ApplyDictionaryTabHeaderState();

        CancelDictionaryTabLookupInFlight();
        var cts = _dictionaryTabLookupCts;
        var lookupGeneration = _dictionaryTabCurrentWord;

        try
        {
            var entries = await LookupDictionaryEntriesRoutedAsync(lookupWord, _dictionaryTabCurrentReference, cts.Token);
            if (cts.IsCancellationRequested ||
                !string.Equals(_dictionaryTabCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            if (entries.Count == 0)
            {
                _dictionaryTabStatusText = "No dictionary entries found.";
                ApplyDictionaryTabHeaderState();
                return;
            }

            _dictionaryTabStatusText = $"{entries.Count} dictionary entr{(entries.Count == 1 ? "y" : "ies")} found.";
            ApplyDictionaryTabHeaderState();
            RenderDictionaryTabResults(entries);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            if (!string.Equals(_dictionaryTabCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryTabStatusText = "Dictionary lookup failed. Check your internet connection and try again.";
            ApplyDictionaryTabHeaderState();
        }
        catch (System.Text.Json.JsonException)
        {
            if (!string.Equals(_dictionaryTabCurrentWord, lookupGeneration, StringComparison.Ordinal))
            {
                return;
            }

            _dictionaryTabStatusText = "Dictionary lookup failed due to an unexpected response.";
            ApplyDictionaryTabHeaderState();
        }
    }

    private void ClearDictionaryTabResults()
    {
        _dictionaryTabDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
        _dictionaryLookupResultsPanel?.Children.Clear();
        if (_dictionarySearchResultsExpander is not null)
        {
            _dictionarySearchResultsExpander.IsExpanded = true;
        }
    }

    private void RenderDictionaryTabResults(IReadOnlyList<SefariaDictionaryEntry> entries)
    {
        _dictionaryTabDisplayedEntries = entries;
        if (_dictionarySearchResultsExpander is not null)
        {
            _dictionarySearchResultsExpander.IsExpanded = true;
        }

        if (_dictionaryLookupResultsPanel is null)
        {
            return;
        }

        PopulateDictionaryTabResults(_dictionaryLookupResultsPanel, entries);
    }

    private void PopulateDictionaryTabResults(
        StackPanel panel,
        IReadOnlyList<SefariaDictionaryEntry> entries,
        IReadOnlyDictionary<string, bool>? expandedByKey = null)
    {
        panel.Children.Clear();
        foreach (var group in entries.GroupBy(entry => entry.LexiconName, StringComparer.Ordinal))
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(group.Key) ? "Dictionary" : group.Key,
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 8, 0, 2),
                Foreground = new SolidColorBrush(Color.Parse("#344054"))
            });

            foreach (var entry in group)
            {
                var key = GetDictionaryEntryKey(entry);
                var expanded = expandedByKey is null ||
                    !expandedByKey.TryGetValue(key, out var wasExpanded) || wasExpanded;
                panel.Children.Add(CreateDictionaryResultExpander(entry, expanded));
            }
        }
    }

    private void RefreshDictionaryPresentation()
    {
        var dictionaryFontSize = GetDictionaryFontSize();

        if (_dictionaryToolsWord is not null)
        {
            _dictionaryToolsWord.FontSize = dictionaryFontSize;
        }

        if (_dictionaryToolsReference is not null)
        {
            _dictionaryToolsReference.FontSize = Math.Max(11, dictionaryFontSize - 3);
        }

        if (_dictionaryToolsResultsPanel is not null)
        {
            PopulateDictionaryEntriesPanel(_dictionaryToolsResultsPanel, dictionaryFontSize);
        }

        if (_dictionaryToolsStatus is not null)
        {
            _dictionaryToolsStatus.FontSize = dictionaryFontSize;
        }

        if (_dictionaryLookupResultsPanel is not null && _dictionaryTabDisplayedEntries.Count > 0)
        {
            var expandedByKey = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var child in _dictionaryLookupResultsPanel.Children.OfType<Expander>())
            {
                if (child.Tag is string key)
                {
                    expandedByKey[key] = child.IsExpanded;
                }
            }

            PopulateDictionaryTabResults(_dictionaryLookupResultsPanel, _dictionaryTabDisplayedEntries, expandedByKey);
        }

        _dictionaryPopupWindow?.ApplyFontSize(dictionaryFontSize);
        RefreshDictionarySurface();
    }

    private double GetDictionaryFontSize() => GetSelectedEnglishFontSize();

    /// <summary>
    /// Builds a fresh, scroll-friendly list of all currently displayed dictionary entries for the
    /// dock/popup surfaces, or null when there are none. Each entry shows its headword, its source/
    /// rank label (e.g. "Concordance · #2 · Inferred · 41%"), and its definition — so the full ranked
    /// set is visible, not just the top guess.
    /// </summary>
    private StackPanel? BuildDictionaryEntriesPanel(double fontSize)
    {
        if (_dictionaryDisplayedEntries.Count == 0)
        {
            return null;
        }

        var panel = new StackPanel { Spacing = 4 };
        PopulateDictionaryEntriesPanel(panel, fontSize);
        return panel;
    }

    private void PopulateDictionaryEntriesPanel(StackPanel panel, double fontSize)
    {
        panel.Children.Clear();
        foreach (var entry in _dictionaryDisplayedEntries)
        {
            panel.Children.Add(BuildCompactDictionaryEntryCard(entry, fontSize));
        }
    }

    private Control BuildCompactDictionaryEntryCard(SefariaDictionaryEntry entry, double fontSize)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new SelectableTextBlock
        {
            Text = entry.Headword,
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(entry.LexiconName))
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.LexiconName,
                FontSize = Math.Max(10, fontSize - 4),
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var gloss = NormalizeDictionaryText(
            string.IsNullOrWhiteSpace(entry.ContentText) ? entry.Definition : entry.ContentText);
        stack.Children.Add(new SelectableTextBlock
        {
            Text = string.IsNullOrWhiteSpace(gloss) ? "(no definition)" : gloss,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 6),
            Child = stack
        };
    }

    private static string GetDictionaryEntryKey(SefariaDictionaryEntry entry) =>
        $"{entry.LexiconName}\u001f{entry.Headword}\u001f{entry.EntryId}\u001f{entry.StrongNumber}";

    private Control CreateDictionaryResultExpander(
        SefariaDictionaryEntry entry,
        bool expanded = true,
        bool showOpenInDictionary = true)
    {
        var dictionaryFontSize = GetDictionaryFontSize();
        var titleText = !showOpenInDictionary || string.IsNullOrWhiteSpace(entry.LexiconName)
            ? entry.Headword
            : $"{entry.Headword} - {entry.LexiconName}";
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8, 6, 8, 10)
        };

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Transliteration))
        {
            metaParts.Add(entry.Transliteration);
        }

        if (!string.IsNullOrWhiteSpace(entry.Pronunciation))
        {
            metaParts.Add($"/{entry.Pronunciation}/");
        }

        if (metaParts.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" | ", metaParts),
                FontSize = dictionaryFontSize,
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var identifiers = new[]
        {
            string.IsNullOrWhiteSpace(entry.StrongNumber) ? "" : $"Strong {entry.StrongNumber}",
            string.IsNullOrWhiteSpace(entry.GkNumber) ? "" : $"GK {entry.GkNumber}",
            string.IsNullOrWhiteSpace(entry.TwotNumber) ? "" : $"TWOT {entry.TwotNumber}",
            string.IsNullOrWhiteSpace(entry.Root) ? "" : $"Root {entry.Root}"
        }.Where(value => value.Length > 0).ToArray();
        if (identifiers.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", identifiers),
                FontSize = dictionaryFontSize,
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var definition = NormalizeDictionaryText(
            string.IsNullOrWhiteSpace(entry.ContentText) ? entry.Definition : entry.ContentText);
        AddDictionaryLinkedTextBlock(
            panel,
            string.IsNullOrWhiteSpace(definition) ? "Entry returned without a plain-text definition." : definition,
            FlowDirection.LeftToRight,
            entry,
            openHeadwordInDictionary: !showOpenInDictionary);

        if (entry.Refs.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "References",
                FontSize = dictionaryFontSize,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            });

            var refsPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 6
            };
            foreach (var reference in entry.Refs)
            {
                refsPanel.Children.Add(CreateDictionaryReferenceExpander(reference));
            }

            panel.Children.Add(refsPanel);
        }

        if (showOpenInDictionary &&
            (!string.IsNullOrWhiteSpace(entry.PreviousHeadword) || !string.IsNullOrWhiteSpace(entry.NextHeadword)))
        {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            if (!string.IsNullOrWhiteSpace(entry.PreviousHeadword))
            {
                var previous = new Button { Content = $"← {entry.PreviousHeadword}", FontSize = dictionaryFontSize };
                previous.Click += (_, _) => _ = RunDictionaryTabLookupAsync(entry.PreviousHeadword, null);
                navigation.Children.Add(previous);
            }
            if (!string.IsNullOrWhiteSpace(entry.NextHeadword))
            {
                var next = new Button { Content = $"{entry.NextHeadword} →", FontSize = dictionaryFontSize };
                next.Click += (_, _) => _ = RunDictionaryTabLookupAsync(entry.NextHeadword, null);
                navigation.Children.Add(next);
            }
            panel.Children.Add(navigation);
        }

        var titleBlock = new SelectableTextBlock
        {
            Text = titleText,
            FontSize = dictionaryFontSize,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var copyButton = new Button
        {
            Content = "Copy",
            Padding = new Thickness(8, 2),
            MinHeight = 26,
            FontSize = Math.Max(11, dictionaryFontSize - 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ToolTip.SetTip(copyButton, "Copy headword");
        copyButton.Click += async (_, e) =>
        {
            e.Handled = true;
            await CopyDictionaryHeadwordAsync(entry.Headword);
        };
        copyButton.PointerPressed += (_, e) => e.Handled = true;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                titleBlock,
                copyButton
            }
        };
        Grid.SetColumn(copyButton, 1);

        if (showOpenInDictionary && entry.IsOffline && entry.LexiconId > 0)
        {
            var openButton = new Button
            {
                Content = "Open in dictionary",
                Padding = new Thickness(8, 2),
                MinHeight = 26,
                FontSize = Math.Max(11, dictionaryFontSize - 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(openButton, "Browse this entry with the entries before and after it");
            openButton.Click += async (_, e) =>
            {
                e.Handled = true;
                await OpenDictionaryEntryInBookAsync(entry);
            };
            openButton.PointerPressed += (_, e) => e.Handled = true;
            header.Children.Add(openButton);
            Grid.SetColumn(openButton, 2);
        }

        return new Expander
        {
            Header = header,
            Tag = GetDictionaryEntryKey(entry),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = expanded,
            Content = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = panel
            }
        };
    }

    private async Task CopyDictionaryHeadwordAsync(string? headword)
    {
        var text = headword?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private Control CreateDictionaryReferenceExpander(string reference)
    {
        var contentPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8,
            Margin = new Thickness(10, 6, 10, 10),
            Children =
            {
                new TextBlock
                {
                    Text = "Expand to load this reference.",
                    Foreground = new SolidColorBrush(Color.Parse("#667085")),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var hasLoaded = false;
        var expander = new Expander
        {
            Header = reference,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#FCFCFD")),
                BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = contentPanel
            }
        };

        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty ||
                !expander.IsExpanded ||
                hasLoaded)
            {
                return;
            }

            hasLoaded = true;
            _ = LoadDictionaryReferencePreviewAsync(reference, contentPanel);
        };

        return expander;
    }

    private async Task LoadDictionaryReferencePreviewAsync(string reference, StackPanel contentPanel)
    {
        contentPanel.Children.Clear();
        contentPanel.Children.Add(new TextBlock
        {
            Text = "Loading reference from Sefaria...",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap
        });

        try
        {
            var trimmedReference = reference.Trim();
            var preview = await _sefariaLibrary.GetLinkPreviewAsync(
                new SefariaLinkItem
                {
                    Ref = trimmedReference,
                    SourceRef = trimmedReference,
                    IndexTitle = ExtractDictionaryReferenceTitle(trimmedReference)
                },
                CancellationToken.None);

            contentPanel.Children.Clear();
            if (preview is null)
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = "No preview text was available for this reference.",
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            AddDictionaryReferencePreviewText(contentPanel, preview.HebrewText, FlowDirection.RightToLeft);
            AddDictionaryReferencePreviewText(contentPanel, preview.EnglishText, FlowDirection.LeftToRight);

            if (string.IsNullOrWhiteSpace(preview.HebrewText) &&
                string.IsNullOrWhiteSpace(preview.EnglishText))
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = "Preview loaded, but no displayable text was returned.",
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        catch (Exception ex)
        {
            contentPanel.Children.Clear();
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"Could not load this reference: {ex.Message}",
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void AddDictionaryReferencePreviewText(StackPanel panel, string text, FlowDirection flowDirection)
    {
        var normalized = NormalizeDictionaryText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        AddDictionaryLinkedTextBlock(panel, normalized, flowDirection);
    }

    private void AddDictionaryLinkedTextBlock(
        StackPanel panel,
        string text,
        FlowDirection flowDirection,
        SefariaDictionaryEntry? sourceEntry = null,
        bool openHeadwordInDictionary = false)
    {
        var citations = FindDictionaryCitations(text);
        var links = citations
            .Select(citation => new DictionaryTextLink(
                citation.Start,
                citation.Length,
                citation.DisplayText,
                citation.FullReference,
                citation.WorkTitle,
                DictionaryTextLinkKind.Citation))
            .ToList();
        if (sourceEntry is not null)
        {
            foreach (var crossReference in DictionaryHeadwordCrossReferenceParser.Find(text))
            {
                if (links.Any(link =>
                        crossReference.Start < link.Start + link.Length &&
                        crossReference.Start + crossReference.Length > link.Start))
                {
                    continue;
                }

                links.Add(new DictionaryTextLink(
                    crossReference.Start,
                    crossReference.Length,
                    crossReference.DisplayText,
                    crossReference.Headword,
                    sourceEntry.LexiconName,
                    DictionaryTextLinkKind.Headword));
            }
        }

        panel.Children.Add(new DictionaryLinkedTextView(
            text,
            links,
            flowDirection,
            link =>
            {
                if (link.Kind == DictionaryTextLinkKind.Citation)
                {
                    _ = OpenDictionaryCitationAsync(link.Target, link.Context);
                }
                else if (sourceEntry is not null && openHeadwordInDictionary)
                {
                    _ = OpenDictionaryHeadwordCrossReferenceAsync(link.Target, sourceEntry);
                }
                else
                {
                    ShowDictionaryEntry(link.Target, null);
                }
            })
        {
            FontSize = GetDictionaryFontSize()
        });
    }

    private async Task OpenDictionaryHeadwordCrossReferenceAsync(
        string headword,
        SefariaDictionaryEntry sourceEntry)
    {
        if (string.IsNullOrWhiteSpace(headword) || sourceEntry.LexiconId <= 0)
        {
            return;
        }

        var query = headword.Trim();
        var entries = await _sefariaLibrary.SearchOfflineDictionaryAsync(
            query,
            sourceEntry.LexiconId,
            20,
            mode: SefariaDictionarySearchMode.Headwords);
        if (entries.Count == 0)
        {
            query = Regex.Replace(query, @"\s+(?:IV|V|I{1,3})$", string.Empty, RegexOptions.IgnoreCase);
            entries = await _sefariaLibrary.SearchOfflineDictionaryAsync(
                query,
                sourceEntry.LexiconId,
                20,
                mode: SefariaDictionarySearchMode.Headwords);
        }

        var targetKey = SefariaOfflineLibraryImporter.NormalizeDictionaryKey(query, keepSpaces: true);
        var target = entries.FirstOrDefault(entry =>
                string.Equals(
                    SefariaOfflineLibraryImporter.NormalizeDictionaryKey(entry.Headword, keepSpaces: true),
                    targetKey,
                    StringComparison.Ordinal))
            ?? entries.FirstOrDefault();
        if (target is not null)
        {
            PushDictionaryNavigationHistory(sourceEntry.EntryId, target.EntryId);
            await OpenDictionaryEntryInBookAsync(target, preserveNavigationHistory: true);
        }
    }

    private async Task OpenDictionaryCitationAsync(string fullReference, string workTitle)
    {
        if (string.IsNullOrWhiteSpace(fullReference) || string.IsNullOrWhiteSpace(workTitle))
        {
            return;
        }

        try
        {
            var preview = await _sefariaLibrary.GetLinkPreviewAsync(
                new SefariaLinkItem
                {
                    Ref = fullReference,
                    SourceRef = fullReference,
                    IndexTitle = workTitle
                },
                CancellationToken.None);

            if (preview is not null)
            {
                var fullVersions = _sefariaLibrary.GetFullInstalledVersionsForTitle(preview.WorkTitle);
                if (fullVersions.Count > 0)
                {
                    OpenInstalledLinkSource(preview, CommentaryLanguage.English, fullVersions);
                    return;
                }
            }
        }
        catch
        {
            // Fall back to a remote preview tab below; ambiguous dictionary links should fail softly.
        }

        OpenAdvancedSearchPreviewTab(new AdvancedSearchResult
        {
            Reference = fullReference,
            WorkTitle = workTitle
        });
    }

    private static IReadOnlyList<DictionaryCitationSpan> FindDictionaryCitations(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DictionaryCitationSpan>();
        }

        var citations = new List<DictionaryCitationSpan>();
        foreach (Match match in DictionaryCitationRegex.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            var abbreviation = match.Groups["abbr"].Value.Trim();
            var location = NormalizeDictionaryCitationLocation(match.Groups["loc"].Value);
            if (string.IsNullOrWhiteSpace(location) ||
                !TryResolveDictionaryCitationTitle(abbreviation, out var workTitle))
            {
                continue;
            }

            citations.Add(new DictionaryCitationSpan(
                match.Index,
                match.Length,
                match.Value,
                workTitle,
                $"{workTitle} {location}"));
        }

        return citations;
    }

    private static bool TryResolveDictionaryCitationTitle(string abbreviation, out string workTitle)
    {
        var normalized = Regex.Replace(abbreviation.Trim(), @"\s+", " ");
        return DictionaryCitationTitles.TryGetValue(normalized, out workTitle!);
    }

    private static string NormalizeDictionaryCitationLocation(string location)
    {
        return location
            .Trim()
            .Replace('ᵃ', 'a')
            .Replace('ᵇ', 'b')
            .Replace(':', '.');
    }

    private sealed record DictionaryCitationSpan(
        int Start,
        int Length,
        string DisplayText,
        string WorkTitle,
        string FullReference);

    private static string ExtractDictionaryReferenceTitle(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var endIndex = parts.Length;
        while (endIndex > 0 && parts[endIndex - 1].Any(char.IsDigit))
        {
            endIndex--;
        }

        return endIndex <= 0
            ? reference.Trim()
            : string.Join(' ', parts, 0, endIndex);
    }

    private async Task ResolveDictionaryEntryAsync(string lookupWord, string reference)
    {
        if (string.IsNullOrWhiteSpace(lookupWord))
        {
            return;
        }

        _dictionaryLookupCts.Cancel();
        _dictionaryLookupCts.Dispose();
        _dictionaryLookupCts = new CancellationTokenSource();
        var cts = _dictionaryLookupCts;

        try
        {
            var entry = await LookupDictionaryEntryWithFallbacksAsync(lookupWord, reference, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (entry is null)
            {
                _dictionaryPrimaryGloss = string.Empty;
                _dictionaryStatusText = "No dictionary entry found.";
            }
            else
            {
                _dictionaryCurrentWord = NormalizeDictionaryWord(entry.Headword, _dictionaryCurrentReference);
                _dictionaryPrimaryGloss = BuildPrimaryDictionaryGloss(entry);
                _dictionaryStatusText = FormatDictionaryStatus(entry);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryStatusText = "Dictionary lookup failed. Check your internet connection and try again.";
        }
        catch (System.Text.Json.JsonException)
        {
            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryStatusText = "Dictionary lookup failed due to an unexpected response.";
        }

        if (!cts.IsCancellationRequested)
        {
            RefreshDictionarySurface();
            SaveLayoutState();
        }
    }

    private async Task<SefariaDictionaryEntry?> LookupDictionaryEntryWithFallbacksAsync(
        string lookupWord,
        string reference,
        CancellationToken cancellationToken)
    {
        var context = GetDictionaryReferenceContext(reference);
        var lookupRef = FormatSefariaLookupRef(reference);
        foreach (var candidate in BuildDictionaryLookupCandidates(lookupWord))
        {
            var cacheKey = BuildDictionaryCacheKey(candidate, lookupRef);
            if (_dictionaryLookupCache.TryGetValue(cacheKey, out var cached))
            {
                var cachedBest = PickBestDictionaryEntry(candidate, cached, context);
                if (cachedBest is not null)
                {
                    return cachedBest;
                }

                continue;
            }

            var entries = await _sefariaLibrary.LookupDictionaryEntriesAsync(
                candidate,
                cancellationToken,
                lookupRef);
            _dictionaryLookupCache[cacheKey] = entries;
            var best = PickBestDictionaryEntry(candidate, entries, context);

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupDictionaryEntriesWithFallbacksAsync(
        string lookupWord,
        string reference,
        CancellationToken cancellationToken)
    {
        var context = GetDictionaryReferenceContext(reference);
        var lookupRef = FormatSefariaLookupRef(reference);
        foreach (var candidate in BuildDictionaryLookupCandidates(lookupWord))
        {
            var cacheKey = BuildDictionaryCacheKey(candidate, lookupRef);
            if (!_dictionaryLookupCache.TryGetValue(cacheKey, out var entries))
            {
                entries = await _sefariaLibrary.LookupDictionaryEntriesAsync(
                    candidate,
                    cancellationToken,
                    lookupRef);
                _dictionaryLookupCache[cacheKey] = entries;
            }

            if (entries.Count == 0)
            {
                continue;
            }

            var normalizedLookup = NormalizeDictionaryHebrewWord(candidate);
            return entries
                .OrderBy(entry => GetDictionaryResultPriority(entry.LexiconName))
                .ThenByDescending(entry => ScoreDictionaryEntry(entry, normalizedLookup, context))
                .ThenBy(entry => entry.LexiconName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Headword, StringComparer.Ordinal)
                .ToList();
        }

        return Array.Empty<SefariaDictionaryEntry>();
    }

    private static int GetDictionaryResultPriority(string lexiconName)
    {
        if (lexiconName.Contains("Klein", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (lexiconName.Contains("Jastrow", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (lexiconName.Contains("BDB", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 10;
    }

    private static string BuildDictionaryCacheKey(string candidate, string? lookupRef)
    {
        return string.IsNullOrWhiteSpace(lookupRef)
            ? candidate
            : candidate + "\u001f" + lookupRef;
    }

    private static string? FormatSefariaLookupRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var normalized = reference.Trim().Replace(' ', '.').Replace(':', '.');
        while (normalized.Contains("..", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('.');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static SefariaDictionaryEntry? PickBestDictionaryEntry(
        string lookupWord,
        IReadOnlyList<SefariaDictionaryEntry> entries,
        DictionaryReferenceContext context)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var normalizedLookup = NormalizeDictionaryHebrewWord(lookupWord);

        return entries
            .OrderByDescending(entry => ScoreDictionaryEntry(entry, normalizedLookup, context))
            .FirstOrDefault();
    }

    private static int ScoreDictionaryEntry(
        SefariaDictionaryEntry entry,
        string normalizedLookup,
        DictionaryReferenceContext context)
    {
        var score = 0;

        if (string.Equals(NormalizeDictionaryHebrewWord(entry.Headword), normalizedLookup, StringComparison.Ordinal))
        {
            score += 200;
        }

        if (!string.IsNullOrWhiteSpace(entry.Definition))
        {
            score += 25;
        }

        var lexicon = entry.LexiconName;
        if (context == DictionaryReferenceContext.Tanakh)
        {
            if (lexicon.Contains("BDB", StringComparison.OrdinalIgnoreCase) ||
                lexicon.Contains("Strong", StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
            }

            if (lexicon.Contains("Jastrow", StringComparison.OrdinalIgnoreCase))
            {
                score -= 20;
            }
        }
        else if (context == DictionaryReferenceContext.Rabbinic)
        {
            if (lexicon.Contains("Jastrow", StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
            }

            if (lexicon.Contains("BDB", StringComparison.OrdinalIgnoreCase) ||
                lexicon.Contains("Strong", StringComparison.OrdinalIgnoreCase))
            {
                score -= 20;
            }
        }

        return score;
    }

    private static string FormatDictionaryStatus(SefariaDictionaryEntry entry)
    {
        var headerParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Transliteration))
        {
            headerParts.Add(entry.Transliteration);
        }

        if (!string.IsNullOrWhiteSpace(entry.Pronunciation))
        {
            headerParts.Add($"/{entry.Pronunciation}/");
        }

        if (!string.IsNullOrWhiteSpace(entry.LexiconName))
        {
            headerParts.Add(entry.LexiconName);
        }

        var definition = NormalizeDictionaryText(entry.Definition);
        if (!string.IsNullOrWhiteSpace(definition) && definition.Length > 320)
        {
            definition = $"{definition[..317]}...";
        }

        var header = string.Join(" • ", headerParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return (header, definition) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{header}\n{definition}",
            ({ Length: > 0 }, _) => header,
            (_, { Length: > 0 }) => definition,
            _ => "Entry found."
        };
    }

    private static string BuildPrimaryDictionaryGloss(SefariaDictionaryEntry entry)
    {
        var definition = NormalizeDictionaryText(entry.Definition);
        if (string.IsNullOrWhiteSpace(definition))
        {
            return string.Empty;
        }

        var firstSentenceEnd = definition.IndexOfAny(new[] { '.', ';', ':' });
        var gloss = firstSentenceEnd > 0
            ? definition[..firstSentenceEnd].Trim()
            : definition;
        if (gloss.Length > 120)
        {
            gloss = $"{gloss[..117]}...";
        }

        return gloss;
    }

    private static DictionaryReferenceContext GetDictionaryReferenceContext(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return DictionaryReferenceContext.Unknown;
        }

        var trimmed = reference.Trim();
        var separator = trimmed.IndexOfAny(new[] { ' ', ':' });
        var work = separator > 0 ? trimmed[..separator] : trimmed;
        work = work.Replace('_', ' ');

        if (TanakhBooks.Contains(work))
        {
            return DictionaryReferenceContext.Tanakh;
        }

        if (trimmed.Contains("Mishnah", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Talmud", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Midrash", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Tosefta", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Jerusalem Talmud", StringComparison.OrdinalIgnoreCase))
        {
            return DictionaryReferenceContext.Rabbinic;
        }

        return DictionaryReferenceContext.Unknown;
    }

    private static string NormalizeDictionaryText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var withoutTags = DictionaryHtmlTagRegex.Replace(input, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return string.Join(" ", decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> BuildDictionaryLookupCandidates(string lookupWord)
    {
        // Two-phase peel so known WordForms and de-prefixed stems are tried before
        // suffix-only peels that leave a preposition attached (ביצורים → ביצור vs יצור).
        // Never rewrite final letters on the original query string.
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length < 2 || !seen.Add(trimmed) ||
                candidates.Count >= MaxDictionaryLookupCandidates)
            {
                return;
            }

            candidates.Add(trimmed);
        }

        Add(lookupWord);

        var consonants = StripDictionaryNiqqud(lookupWord);
        Add(consonants);

        // Phase 1: prefix chain only (ו/ב/כ/ל/מ/ה/ש), up to two letters.
        var prefixBases = new List<string> { consonants };
        var prefixCurrent = consonants;
        for (var removed = 0; removed < 2; removed++)
        {
            if (prefixCurrent.Length <= 2 || !HebrewPrefixLetters.Contains(prefixCurrent[0]))
            {
                break;
            }

            prefixCurrent = prefixCurrent[1..];
            Add(prefixCurrent);
            prefixBases.Add(prefixCurrent);
        }

        // Phase 2: suffix peels, deepest de-prefixed base first so ביצורים prefers יצור
        // over the still-prefixed ביצור.
        for (var i = prefixBases.Count - 1; i >= 0; i--)
        {
            if (candidates.Count >= MaxDictionaryLookupCandidates)
            {
                break;
            }

            foreach (var variant in ExpandDictionarySuffixVariants(prefixBases[i]))
            {
                Add(variant);
                if (candidates.Count >= MaxDictionaryLookupCandidates)
                {
                    break;
                }
            }
        }

        return candidates;
    }

    private static IEnumerable<string> ExpandDictionarySuffixVariants(string form)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { form };
        var queue = new Queue<string>();
        queue.Enqueue(form);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var suffix in DictionarySuffixes)
            {
                if (current.Length <= suffix.Length + 1 ||
                    !current.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var stem = current[..^suffix.Length];
                foreach (var variant in new[]
                         {
                             stem,
                             ApplyDictionaryFinalHebrewLetter(stem),
                             stem.Length > 2 && stem[^1] == 'ת' ? stem[..^1] + "ה" : null
                         })
                {
                    if (string.IsNullOrWhiteSpace(variant) || variant.Length < 2 || !seen.Add(variant))
                    {
                        continue;
                    }

                    yield return variant;
                    queue.Enqueue(variant);
                }
            }
        }
    }

    private static string NormalizeDictionaryLookupWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var collapsed = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        var token = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        token = token.Trim(
            '"', '\'', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}',
            '<', '>', '“', '”', '‘', '’', '-', '־');

        return token;
    }

    /// <summary>
    /// Strips niqqud/cantillation only. Preserves final-letter forms for lexicon queries.
    /// </summary>
    private static string StripDictionaryNiqqud(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Trim();
    }

    /// <summary>
    /// Scoring-only normalization: strip marks and fold final letters so headword equality
    /// is robust. Not used as a query candidate.
    /// </summary>
    private static string NormalizeDictionaryHebrewWord(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var stripped = StripDictionaryNiqqud(value);
        var builder = new StringBuilder(stripped.Length);
        foreach (var character in stripped)
        {
            builder.Append(NormalizeDictionaryFinalHebrewLetter(character));
        }

        return builder.ToString();
    }

    private static char NormalizeDictionaryFinalHebrewLetter(char character)
    {
        return character switch
        {
            'ך' => 'כ',
            'ם' => 'מ',
            'ן' => 'נ',
            'ף' => 'פ',
            'ץ' => 'צ',
            _ => character
        };
    }

    /// <summary>
    /// Converts a word-final medial letter to its sofit form (כ→ך, etc.) for lexicon queries.
    /// </summary>
    private static string ApplyDictionaryFinalHebrewLetter(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var last = value[^1];
        var sofit = last switch
        {
            'כ' => 'ך',
            'מ' => 'ם',
            'נ' => 'ן',
            'פ' => 'ף',
            'צ' => 'ץ',
            _ => last
        };

        return sofit == last ? value : value[..^1] + sofit;
    }

    private void DockDictionaryToReaderTools()
    {
        _isDictionaryDocked = true;
        CloseDictionaryPopupWindow();
        EnsureRightPanelExpandedForDictionary();
        UpdateReaderTools();
        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void PopOutDictionaryFromReaderTools()
    {
        _isDictionaryDocked = false;
        ClearDictionaryToolsControls();
        UpdateReaderTools();
        RefreshDictionarySurface();
        ShowDictionaryPopupWindow();
        SaveLayoutState();
    }

    private void CloseDictionarySurface()
    {
        var wasDocked = _isDictionaryDocked;
        _isDictionaryDocked = false;
        _dictionaryCurrentWord = string.Empty;
        _dictionaryCurrentReference = string.Empty;
        _dictionaryPrimaryGloss = string.Empty;
        _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
        _dictionaryStatusText = "Right-click a word in the reader and choose Dictionary.";
        _dictionaryLookupCts.Cancel();
        _dictionaryLookupCts.Dispose();
        _dictionaryLookupCts = new CancellationTokenSource();
        CloseDictionaryPopupWindow();
        if (wasDocked)
        {
            ClearDictionaryToolsControls();
            UpdateReaderTools();
        }

        RefreshDictionarySurface();
        SaveLayoutState();
    }

    private void EnsureRightPanelExpandedForDictionary()
    {
        if (!_rightCollapsed)
        {
            return;
        }

        ApplyRightPanelState(false, _rightExpandedWidth);
    }

    private void RefreshDictionarySurface()
    {
        var hasContent = !string.IsNullOrWhiteSpace(_dictionaryCurrentWord) ||
            !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        var displayWord = string.IsNullOrWhiteSpace(_dictionaryCurrentWord)
            ? "Dictionary selection"
            : _dictionaryCurrentWord;
        var status = hasContent
            ? _dictionaryStatusText
            : "Right-click a word in the reader and choose Dictionary.";

        if (_dictionaryPopupWindow is not null)
        {
            _dictionaryPopupWindow.ApplyFontSize(GetDictionaryFontSize());
            _dictionaryPopupWindow.UpdateEntry(displayWord, _dictionaryCurrentReference, status);
            _dictionaryPopupWindow.SetResultsContent(BuildDictionaryEntriesPanel(GetDictionaryFontSize()));
            if (!_isDictionaryDocked && _dictionaryPopupWindow.IsVisible && !_dictionaryPopupUserPositioned)
            {
                ScheduleDictionaryPopupReposition(_dictionaryPopupWindow);
            }
        }

        ApplyDictionaryToolsContent();
    }

    private void ShowDictionaryPopupWindow(bool repositionToAnchor = false)
    {
        if (_isDictionaryDocked ||
            (string.IsNullOrWhiteSpace(_dictionaryCurrentWord) &&
             string.IsNullOrWhiteSpace(_dictionaryCurrentReference)))
        {
            return;
        }

        if (repositionToAnchor)
        {
            _dictionaryPopupUserPositioned = false;
            _dictionaryAnchorScreenPoint ??= GetDefaultDictionaryAnchorScreenPoint();
        }

        var popup = EnsureDictionaryPopupWindow();
        popup.UpdateEntry(
            string.IsNullOrWhiteSpace(_dictionaryCurrentWord) ? "Dictionary selection" : _dictionaryCurrentWord,
            _dictionaryCurrentReference,
            _dictionaryStatusText);
        popup.SetResultsContent(BuildDictionaryEntriesPanel(GetDictionaryFontSize()));
        ApplyDictionaryPopupPosition(popup);
        if (!popup.IsVisible)
        {
            popup.Show(this);
            ScheduleDictionaryPopupReposition(popup);
        }
        else
        {
            popup.Activate();
            if (!_dictionaryPopupUserPositioned)
            {
                ScheduleDictionaryPopupReposition(popup);
            }
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
        popup.DockRequested += (_, _) => DockDictionaryToReaderTools();
        popup.OpenInDictionaryRequested += async (_, _) => await OpenCurrentOnPageDictionaryInTabAsync();
        popup.DismissRequested += (_, _) => CloseDictionarySurface();
        // Click-away / focus-away dismissal. Guards: docking sets _isDictionaryDocked
        // BEFORE closing the popup, and CloseDictionaryPopupWindow nulls the field before
        // Close(), so neither programmatic close path can bounce back in here.
        popup.Deactivated += (_, _) =>
        {
            if (ReferenceEquals(_dictionaryPopupWindow, popup) && !_isDictionaryDocked)
            {
                // Deferred for the same focus-transition reason as
                // DismissDictionaryPopupIfFloating, but with NO Activate(): this
                // path also fires when the user switches to another application,
                // and stealing the foreground back would be hostile.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_dictionaryPopupWindow, popup) && !_isDictionaryDocked)
                    {
                        CloseDictionarySurface();
                    }
                });
            }
        };
        EnsureDictionaryPopupDismissalHooks();
        popup.PositionCommitted += (_, position) =>
        {
            _dictionaryPopupUserPositioned = true;
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

    private bool _dictionaryPopupDismissHooked;

    private void EnsureDictionaryPopupDismissalHooks()
    {
        if (_dictionaryPopupDismissHooked)
        {
            return;
        }

        _dictionaryPopupDismissHooked = true;
        // The popup is a separate window, so any pointer press reaching the main
        // window is by definition outside it. handledEventsToo: buttons and the
        // WebView chrome mark events handled before they'd bubble here.
        AddHandler(PointerPressedEvent, (_, _) => DismissDictionaryPopupIfFloating(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                DismissDictionaryPopupIfFloating();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void DismissDictionaryPopupIfFloating()
    {
        if (_dictionaryPopupWindow is not null && !_isDictionaryDocked)
        {
            // Deferred: closing the popup synchronously inside the very pointer/key
            // event that is moving focus makes Windows treat it as an active-window
            // death and foreground the PREVIOUS application (involuntary alt-tab).
            // Let the transition settle, close, then keep the foreground on us —
            // this path only runs for in-app clicks/Esc, so re-activating never
            // yanks the user back from another program.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CloseDictionarySurface();
                Activate();
            });
        }
    }

    private void ScheduleDictionaryPopupReposition(DictionaryPopupWindow popup)
    {
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            popup.LayoutUpdated -= OnLayoutUpdated;
            if (!ReferenceEquals(_dictionaryPopupWindow, popup) ||
                !popup.IsVisible ||
                _dictionaryPopupUserPositioned ||
                _isDictionaryDocked)
            {
                return;
            }

            ApplyDictionaryPopupPosition(popup);
        }

        popup.LayoutUpdated += OnLayoutUpdated;
        popup.InvalidateMeasure();
        popup.InvalidateArrange();
    }

    private void ApplyDictionaryPopupPosition(DictionaryPopupWindow popup)
    {
        if (_dictionaryPopupUserPositioned)
        {
            popup.Position = GetSavedDictionaryPopupScreenPosition();
            return;
        }

        var anchor = _dictionaryAnchorScreenPoint ?? GetDefaultDictionaryAnchorScreenPoint();
        popup.Position = CalculateDictionaryPopupScreenPosition(popup, anchor);
        _dictionaryPopupLeft = popup.Position.X - Position.X;
        _dictionaryPopupTop = popup.Position.Y - Position.Y;
    }

    private PixelPoint GetSavedDictionaryPopupScreenPosition()
    {
        return new PixelPoint(
            Position.X + (int)Math.Round(_dictionaryPopupLeft),
            Position.Y + (int)Math.Round(_dictionaryPopupTop));
    }

    private PixelPoint GetDefaultDictionaryAnchorScreenPoint()
    {
        var bounds = Bounds;
        return new PixelPoint(
            Position.X + (int)Math.Round(Math.Max(0, bounds.Width * 0.5)),
            Position.Y + (int)Math.Round(Math.Max(0, bounds.Height * 0.45)));
    }

    private PixelPoint CalculateDictionaryPopupScreenPosition(DictionaryPopupWindow popup, PixelPoint anchor)
    {
        const int gap = 12;
        const int margin = 8;
        const double fallbackWidth = 280;
        const double fallbackHeight = 160;

        var width = popup.Bounds.Width > 1
            ? popup.Bounds.Width
            : (popup.Width > 1 ? popup.Width : fallbackWidth);
        var height = popup.Bounds.Height > 1
            ? popup.Bounds.Height
            : (popup.Height > 1 ? popup.Height : fallbackHeight);

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * popup.DesktopScaling));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * popup.DesktopScaling));

        var workArea = GetDictionaryPopupWorkArea(anchor);
        var minX = workArea.X + margin;
        var maxX = workArea.X + workArea.Width - pixelWidth - margin;
        var x = Math.Clamp(anchor.X, minX, Math.Max(minX, maxX));

        var yBelow = anchor.Y + gap;
        var yAbove = anchor.Y - pixelHeight - gap;
        var maxY = workArea.Y + workArea.Height - pixelHeight - margin;
        var minY = workArea.Y + margin;

        int y;
        if (yBelow <= maxY)
        {
            y = yBelow;
        }
        else if (yAbove >= minY)
        {
            y = yAbove;
        }
        else
        {
            y = Math.Clamp(yBelow, minY, Math.Max(minY, maxY));
        }

        return new PixelPoint(x, y);
    }

    private PixelRect GetDictionaryPopupWorkArea(PixelPoint anchor)
    {
        var screens = Screens;
        var screen = screens?.ScreenFromPoint(anchor) ?? screens?.Primary;
        if (screen is not null)
        {
            return screen.WorkingArea;
        }

        return new PixelRect(Position.X, Position.Y, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
    }

    private void ConstrainDictionaryPopupPosition()
    {
        if (_dictionaryPopupWindow is null || !_dictionaryPopupWindow.IsVisible)
        {
            return;
        }

        ApplyDictionaryPopupPosition(_dictionaryPopupWindow);
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
