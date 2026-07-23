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
        public override string ToString() => Label;
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
        _dictionaryLookupBox = new TextBox
        {
            PlaceholderText = "Enter a Hebrew or Aramaic word...",
            MinWidth = 280,
            VerticalAlignment = VerticalAlignment.Center,
            Text = _dictionaryCurrentWord
        };
        _dictionaryLookupBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            await RunOfflineDictionarySearchAsync(_dictionaryLookupBox.Text);
        };

        var searchButton = new Button
        {
            Content = "Search",
            MinWidth = 90
        };
        ToolTip.SetTip(searchButton, "Search headwords, word forms, transliterations, identifiers and definitions");
        searchButton.Click += async (_, _) => await RunOfflineDictionarySearchAsync(_dictionaryLookupBox.Text);

        _dictionarySearchScopeBox = new ComboBox
        {
            MinWidth = 260,
            ItemsSource = new[] { new DictionarySearchScopeOption(null, "All dictionaries") },
            SelectedIndex = 0
        };
        _dictionarySearchScopeBox.SelectionChanged += (_, _) =>
        {
            if (_dictionarySearchScopeBox.SelectedItem is not DictionarySearchScopeOption option) return;
            _dictionarySelectedLexiconId = option.LexiconId;
            _dictionaryStatusText = $"Search scope: {option.Label}.";
            ApplyDictionaryTabHeaderState();
        };

        _dictionaryLookupReference = new TextBlock
        {
            Text = _dictionaryCurrentReference,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = !string.IsNullOrWhiteSpace(_dictionaryCurrentReference)
        };

        _dictionaryLookupStatus = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_dictionaryCurrentWord)
                ? "Type a word, or right-click a word in the reader and choose Dictionary."
                : _dictionaryStatusText,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            TextWrapping = TextWrapping.Wrap
        };

        _dictionaryLookupResultsPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8
        };

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
                    Text = "Search headwords, word forms, transliterations, identifiers and definitions.",
                    Foreground = new SolidColorBrush(Color.Parse("#475467")),
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        _dictionaryLookupBox,
                        searchButton
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Dictionary:", VerticalAlignment = VerticalAlignment.Center },
                        _dictionarySearchScopeBox
                    }
                },
                _dictionaryLookupReference,
                _dictionaryLookupStatus
            }
        };

        var content = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(18),
            Children =
            {
                header,
                CreateDictionaryCatalogueControl(),
                _dictionaryLookupResultsPanel
            }
        };

        return new ScrollViewer
        {
            Background = Brushes.White,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        };
    }

    private Control CreateDictionaryCatalogueControl()
    {
        _dictionaryCatalogueStatus = new TextBlock
        {
            Text = "Loading installed dictionaries...",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap
        };
        _dictionaryCataloguePanel = new StackPanel { Spacing = 8, Children = { _dictionaryCatalogueStatus } };
        var expander = new Expander
        {
            Header = "Navigation — browse installed dictionaries",
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = _dictionaryCataloguePanel
            }
        };
        _ = LoadDictionaryCatalogueAsync();
        return expander;
    }

    private async Task LoadDictionaryCatalogueAsync()
    {
        if (_dictionaryCataloguePanel is null) return;
        try
        {
            var lexicons = await _sefariaLibrary.GetOfflineLexiconsAsync();
            _dictionaryCataloguePanel.Children.Clear();
            _dictionaryLexiconExpanders.Clear();
            if (lexicons.Count == 0)
            {
                _dictionaryCataloguePanel.Children.Add(new TextBlock
                {
                    Text = _sefariaLibrary.HasOfflineLibrary
                        ? "The installed library predates offline dictionaries. Install the latest Sefaria snapshot to add them."
                        : "Install the Sefaria offline library to browse dictionaries without an internet connection.",
                    Foreground = new SolidColorBrush(Color.Parse("#667085")), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            _dictionaryCataloguePanel.Children.Add(new TextBlock
            {
                Text = $"{lexicons.Count} dictionaries · {lexicons.Sum(item => item.EntryCount):N0} entries. Expand a dictionary, then drill down by initial letters.",
                Foreground = new SolidColorBrush(Color.Parse("#475467")), TextWrapping = TextWrapping.Wrap
            });
            if (_dictionarySearchScopeBox is not null)
            {
                var options = new List<DictionarySearchScopeOption> { new(null, "All dictionaries") };
                options.AddRange(lexicons.Select(item => new DictionarySearchScopeOption(item.Id, item.Name)));
                _dictionarySearchScopeBox.ItemsSource = options;
                _dictionarySearchScopeBox.SelectedItem = options.FirstOrDefault(option => option.LexiconId == _dictionarySelectedLexiconId) ?? options[0];
            }
            foreach (var lexicon in lexicons)
            {
                var expander = CreateLexiconNavigationExpander(lexicon);
                _dictionaryLexiconExpanders[lexicon.Name] = expander;
                _dictionaryCataloguePanel.Children.Add(expander);
            }
            ApplyRequestedDictionarySelection();
        }
        catch (Exception ex)
        {
            _dictionaryCataloguePanel.Children.Clear();
            _dictionaryCataloguePanel.Children.Add(new TextBlock { Text = $"Could not load dictionaries: {ex.Message}", TextWrapping = TextWrapping.Wrap });
        }
    }

    private Expander CreateLexiconNavigationExpander(SefariaLexiconInfo lexicon)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        var loaded = false;
        var language = string.Join(" → ", new[] { lexicon.Language, lexicon.ToLanguage }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var expander = new Expander
        {
            Header = $"{lexicon.Name}  ({lexicon.EntryCount:N0} entries{(language.Length > 0 ? $", {language}" : "")})",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = panel
        };
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty || !expander.IsExpanded || loaded) return;
            loaded = true;
            _ = LoadDictionaryPrefixLevelAsync(lexicon, "", panel);
        };
        return expander;
    }

    private async Task LoadDictionaryPrefixLevelAsync(SefariaLexiconInfo lexicon, string prefix, StackPanel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "Loading headwords...", Foreground = new SolidColorBrush(Color.Parse("#667085")) });
        try
        {
            var prefixes = await _sefariaLibrary.GetOfflineDictionaryPrefixesAsync(lexicon.Id, prefix, prefix.Length + 1);
            var entries = await _sefariaLibrary.BrowseOfflineDictionaryAsync(lexicon.Id, prefix, 0, 60);
            panel.Children.Clear();
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (prefix.Length > 0)
            {
                var back = new Button { Content = "← Back" };
                back.Click += (_, _) => _ = LoadDictionaryPrefixLevelAsync(lexicon, prefix[..^1], panel);
                heading.Children.Add(back);
            }
            heading.Children.Add(new TextBlock
            {
                Text = prefix.Length == 0 ? "Initial letter" : $"Prefix: {prefix}",
                FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(heading);

            if (prefixes.Count > 1 || (prefixes.Count == 1 && prefixes[0].Prefix.Length > prefix.Length))
            {
                var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var item in prefixes)
                {
                    var button = new Button { Content = $"{item.Prefix}  {item.EntryCount:N0}", Margin = new Thickness(0, 0, 6, 6) };
                    button.Click += (_, _) => _ = LoadDictionaryPrefixLevelAsync(lexicon, item.Prefix, panel);
                    buttons.Children.Add(button);
                }
                panel.Children.Add(buttons);
            }

            panel.Children.Add(new TextBlock
            {
                Text = entries.Count == 60 ? "First 60 matching headwords (refine the prefix to narrow the list)" : $"{entries.Count:N0} matching headwords",
                Foreground = new SolidColorBrush(Color.Parse("#667085"))
            });
            foreach (var entry in entries) panel.Children.Add(CreateDictionaryResultExpander(entry, false));
        }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = $"Could not browse this dictionary: {ex.Message}", TextWrapping = TextWrapping.Wrap });
        }
    }

    private async Task RunOfflineDictionarySearchAsync(string? query)
    {
        var value = query?.Trim() ?? "";
        if (value.Length == 0)
        {
            _dictionaryStatusText = "Enter a headword, word form, identifier, transliteration, or definition term.";
            ApplyDictionaryTabHeaderState();
            return;
        }
        _dictionaryStatusText = $"Searching installed dictionaries for {value}...";
        ClearDictionaryTabResults();
        ApplyDictionaryTabHeaderState();
        try
        {
            var entries = await _sefariaLibrary.SearchOfflineDictionaryAsync(value, _dictionarySelectedLexiconId, 100);
            _dictionaryStatusText = entries.Count == 0
                ? "No installed dictionary entries matched this search."
                : $"{entries.Count:N0} matching entr{(entries.Count == 1 ? "y" : "ies")} in {(_dictionarySelectedLexiconId is null ? "all dictionaries" : "the selected dictionary")}.";
            ApplyDictionaryTabHeaderState();
            RenderDictionaryTabResults(entries);
        }
        catch (Exception ex)
        {
            _dictionaryStatusText = $"Dictionary search failed: {ex.Message}";
            ApplyDictionaryTabHeaderState();
        }
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
        if (_dictionarySearchScopeBox?.ItemsSource is IEnumerable<DictionarySearchScopeOption> options &&
            options.FirstOrDefault(option => string.Equals(option.Label, _dictionaryRequestedLexiconName, StringComparison.Ordinal)) is { } selected)
        {
            _dictionarySearchScopeBox.SelectedItem = selected;
        }
        if (_dictionaryLexiconExpanders.TryGetValue(_dictionaryRequestedLexiconName, out var expander))
        {
            expander.IsExpanded = true;
            _dictionaryRequestedLexiconName = string.Empty;
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
        _dictionaryToolsPrimaryGloss = new TextBlock
        {
            FontSize = dictionaryFontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1D2939")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
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
                popoutButton,
                closeButton
            }
        };
        Grid.SetColumn(popoutButton, 1);
        Grid.SetColumn(closeButton, 2);

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
                    _dictionaryToolsPrimaryGloss,
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

        if (_dictionaryToolsPrimaryGloss is not null)
        {
            _dictionaryToolsPrimaryGloss.Text = _dictionaryPrimaryGloss;
            _dictionaryToolsPrimaryGloss.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryPrimaryGloss);
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
        _dictionaryToolsPrimaryGloss = null;
        _dictionaryToolsStatus = null;
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
        _dictionaryStatusText = string.IsNullOrWhiteSpace(lookupWord)
            ? "Select a single word to look it up."
            : "Looking up dictionary entry...";

        RefreshDictionarySurface();
        OpenOrSelectTab(DictionaryTabTitle);
        ApplyDictionaryTabHeaderState();
        _ = RunDictionaryTabLookupAsync(lookupWord, _dictionaryCurrentReference);
        SaveLayoutState();
    }

    private void ApplyDictionaryTabHeaderState()
    {
        if (_dictionaryLookupBox is not null)
        {
            _dictionaryLookupBox.Text = _dictionaryCurrentWord;
        }

        if (_dictionaryLookupReference is not null)
        {
            _dictionaryLookupReference.Text = _dictionaryCurrentReference;
            _dictionaryLookupReference.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryCurrentReference);
        }

        if (_dictionaryLookupStatus is not null)
        {
            _dictionaryLookupStatus.Text = _dictionaryStatusText;
        }
    }

    private async Task RunDictionaryTabLookupAsync(string? word, string? reference)
    {
        var lookupWord = NormalizeDictionaryLookupWord(word);
        var displayQuery = string.IsNullOrWhiteSpace(word) ? lookupWord : word.Trim();
        _dictionaryCurrentWord = NormalizeDictionaryWord(displayQuery, reference);
        _dictionaryCurrentReference = reference?.Trim() ?? _dictionaryCurrentReference;
        _dictionaryPrimaryGloss = string.Empty;

        if (string.IsNullOrWhiteSpace(lookupWord))
        {
            _dictionaryStatusText = "Type or select a single word to look it up.";
            ClearDictionaryTabResults();
            ApplyDictionaryTabHeaderState();
            RefreshDictionarySurface();
            return;
        }

        _dictionaryStatusText = $"Looking up {lookupWord}...";
        ClearDictionaryTabResults();
        ApplyDictionaryTabHeaderState();
        RefreshDictionarySurface();

        _dictionaryLookupCts.Cancel();
        _dictionaryLookupCts.Dispose();
        _dictionaryLookupCts = new CancellationTokenSource();
        var cts = _dictionaryLookupCts;

        try
        {
            var entries = await LookupDictionaryEntriesWithFallbacksAsync(lookupWord, _dictionaryCurrentReference, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (entries.Count == 0)
            {
                _dictionaryStatusText = "No dictionary entries found.";
                ApplyDictionaryTabHeaderState();
                RefreshDictionarySurface();
                return;
            }

            var best = entries[0];
            _dictionaryPrimaryGloss = BuildPrimaryDictionaryGloss(best);
            _dictionaryStatusText = $"{entries.Count} dictionary entr{(entries.Count == 1 ? "y" : "ies")} found.";
            ApplyDictionaryTabHeaderState();
            RenderDictionaryTabResults(entries);
            RefreshDictionarySurface();
            SaveLayoutState();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryStatusText = "Dictionary lookup failed. Check your internet connection and try again.";
            ApplyDictionaryTabHeaderState();
            RefreshDictionarySurface();
        }
        catch (System.Text.Json.JsonException)
        {
            _dictionaryPrimaryGloss = string.Empty;
            _dictionaryStatusText = "Dictionary lookup failed due to an unexpected response.";
            ApplyDictionaryTabHeaderState();
            RefreshDictionarySurface();
        }
    }

    private void ClearDictionaryTabResults()
    {
        _dictionaryDisplayedEntries = Array.Empty<SefariaDictionaryEntry>();
        _dictionaryLookupResultsPanel?.Children.Clear();
    }

    private void RenderDictionaryTabResults(IReadOnlyList<SefariaDictionaryEntry> entries)
    {
        if (_dictionaryLookupResultsPanel is null)
        {
            return;
        }

        _dictionaryDisplayedEntries = entries;
        _dictionaryLookupResultsPanel.Children.Clear();
        foreach (var entry in entries)
        {
            _dictionaryLookupResultsPanel.Children.Add(CreateDictionaryResultExpander(entry));
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

        if (_dictionaryToolsPrimaryGloss is not null)
        {
            _dictionaryToolsPrimaryGloss.FontSize = dictionaryFontSize;
        }

        if (_dictionaryToolsStatus is not null)
        {
            _dictionaryToolsStatus.FontSize = dictionaryFontSize;
        }

        if (_dictionaryLookupResultsPanel is not null && _dictionaryDisplayedEntries.Count > 0)
        {
            var expandedByKey = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var child in _dictionaryLookupResultsPanel.Children.OfType<Expander>())
            {
                if (child.Tag is string key)
                {
                    expandedByKey[key] = child.IsExpanded;
                }
            }

            _dictionaryLookupResultsPanel.Children.Clear();
            foreach (var entry in _dictionaryDisplayedEntries)
            {
                var key = GetDictionaryEntryKey(entry);
                var expanded = !expandedByKey.TryGetValue(key, out var wasExpanded) || wasExpanded;
                _dictionaryLookupResultsPanel.Children.Add(CreateDictionaryResultExpander(entry, expanded));
            }
        }

        _dictionaryPopupWindow?.ApplyFontSize(dictionaryFontSize);
        RefreshDictionarySurface();
    }

    private double GetDictionaryFontSize() => GetSelectedEnglishFontSize();

    private static string GetDictionaryEntryKey(SefariaDictionaryEntry entry) =>
        $"{entry.LexiconName}\u001f{entry.Headword}\u001f{entry.EntryId}\u001f{entry.StrongNumber}";

    private Control CreateDictionaryResultExpander(SefariaDictionaryEntry entry, bool expanded = true)
    {
        var dictionaryFontSize = GetDictionaryFontSize();
        var titleText = string.IsNullOrWhiteSpace(entry.LexiconName)
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
            FlowDirection.LeftToRight);

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

        if (!string.IsNullOrWhiteSpace(entry.PreviousHeadword) || !string.IsNullOrWhiteSpace(entry.NextHeadword))
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                titleBlock,
                copyButton
            }
        };
        Grid.SetColumn(copyButton, 1);

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

            contentPanel.Children.Add(new TextBlock
            {
                Text = preview.IsFromInstalledBook ? "Preview from local data" : "Preview from Sefaria",
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });

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

    private void AddDictionaryLinkedTextBlock(StackPanel panel, string text, FlowDirection flowDirection)
    {
        var citations = FindDictionaryCitations(text);
        if (citations.Count == 0)
        {
            panel.Children.Add(new SelectableTextBlock
            {
                Text = text,
                FontSize = GetDictionaryFontSize(),
                FlowDirection = flowDirection,
                TextAlignment = flowDirection == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        panel.Children.Add(new DictionaryLinkedTextView(
            text,
            citations.Select(citation => new DictionaryCitationLink(
                citation.Start,
                citation.Length,
                citation.DisplayText,
                citation.FullReference,
                citation.WorkTitle)),
            flowDirection,
            citation => _ = OpenDictionaryCitationAsync(citation.FullReference, citation.WorkTitle))
        {
            FontSize = GetDictionaryFontSize()
        });
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
            _dictionaryPopupWindow.UpdateEntry(displayWord, _dictionaryCurrentReference, _dictionaryPrimaryGloss, status);
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
            _dictionaryPrimaryGloss,
            _dictionaryStatusText);
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
        popup.DismissRequested += (_, _) => CloseDictionarySurface();
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
