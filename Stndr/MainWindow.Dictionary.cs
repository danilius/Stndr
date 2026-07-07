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

        var lookupWord = NormalizeDictionaryLookupWord(word);
        _dictionaryPrimaryGloss = string.Empty;
        _dictionaryStatusText = string.IsNullOrWhiteSpace(lookupWord)
            ? "Select a single word to look it up."
            : "Looking up dictionary entry...";

        RefreshDictionarySurface();
        if (!_isDictionaryDocked)
        {
            ShowDictionaryPopupWindow();
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
        foreach (var candidate in BuildDictionaryLookupCandidates(lookupWord))
        {
            if (_dictionaryLookupCache.TryGetValue(candidate, out var cached))
            {
                var cachedBest = PickBestDictionaryEntry(candidate, cached, context);
                if (cachedBest is not null)
                {
                    return cachedBest;
                }

                continue;
            }

            var entries = await _sefariaLibrary.LookupDictionaryEntriesAsync(candidate, cancellationToken);
            _dictionaryLookupCache[candidate] = entries;
            var best = PickBestDictionaryEntry(candidate, entries, context);

            if (best is not null)
            {
                return best;
            }
        }

        return null;
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
        var candidates = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);

        static void AddCandidate(HashSet<string> candidateSet, List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (candidateSet.Add(trimmed))
            {
                list.Add(trimmed);
            }
        }

        AddCandidate(set, candidates, lookupWord);

        var normalized = NormalizeDictionaryHebrewWord(lookupWord);
        AddCandidate(set, candidates, normalized);

        var withoutPrefixes = normalized;
        for (var removed = 0; removed < 2; removed++)
        {
            if (withoutPrefixes.Length <= 2 || !HebrewPrefixLetters.Contains(withoutPrefixes[0]))
            {
                break;
            }

            withoutPrefixes = withoutPrefixes[1..];
            AddCandidate(set, candidates, withoutPrefixes);
        }

        return candidates;
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

    private static string NormalizeDictionaryHebrewWord(string value)
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

            builder.Append(NormalizeDictionaryFinalHebrewLetter(character));
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Trim();
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
        _dictionaryPrimaryGloss = string.Empty;
        _dictionaryStatusText = "Right-click a word in the reader and choose Dictionary.";
        _dictionaryLookupCts.Cancel();
        _dictionaryLookupCts.Dispose();
        _dictionaryLookupCts = new CancellationTokenSource();
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
            ? _dictionaryStatusText
            : "Right-click a word in the reader and choose Dictionary.";

        _dictionaryPopupWindow?.UpdateEntry(displayWord, _dictionaryCurrentReference, _dictionaryPrimaryGloss, status);

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

        if (_dictionarySidebarPrimaryGloss is not null)
        {
            _dictionarySidebarPrimaryGloss.Text = _dictionaryPrimaryGloss;
            _dictionarySidebarPrimaryGloss.IsVisible = !string.IsNullOrWhiteSpace(_dictionaryPrimaryGloss);
        }

        if (_dictionarySidebarCard is not null)
        {
            _dictionarySidebarCard.IsVisible = _isDictionaryDocked && hasContent;
        }

        if (_dictionarySidebarSection is not null)
        {
            _dictionarySidebarSection.IsVisible = _isDictionaryDocked;
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
            _dictionaryPrimaryGloss,
            _dictionaryStatusText);
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
