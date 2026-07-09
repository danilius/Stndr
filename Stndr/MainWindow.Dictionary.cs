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

    private static readonly Regex DictionaryHtmlTagRegex = new("<.*?>", RegexOptions.Compiled);
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

    private Control CreateDockedDictionaryToolsControl()
    {
        _dictionaryToolsWord = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _dictionaryToolsReference = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _dictionaryToolsPrimaryGloss = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1D2939")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _dictionaryToolsStatus = new TextBlock
        {
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
        if (_isDictionaryDocked)
        {
            EnsureRightPanelExpandedForDictionary();
            if (_dictionaryToolsWord is null)
            {
                UpdateReaderTools();
                RefreshDictionarySurface();
            }
        }
        else
        {
            ShowDictionaryPopupWindow(repositionToAnchor: true);
        }

        _ = ResolveDictionaryEntryAsync(lookupWord, _dictionaryCurrentReference);
        SaveLayoutState();
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
