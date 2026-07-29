using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TalmudLane.Query;

namespace Stndr;

/// <summary>
/// Interim integration of the Hebrew Dictionary Concordance (M9) into the reader's Dictionary lookup.
/// For refs the concordance covers (currently Bava Metzia / Bava Kamma / Berakhot ch.1), a right-click
/// lookup is served by the concordance's ranked, confidence-scored senses instead of the Sefaria
/// dictionary; anything it does not cover falls through to the original path. Gated by
/// <see cref="AppSettings.UseConcordanceForDictionary"/> so it is fully reversible.
///
/// This is deliberately minimal: concordance results are mapped onto the existing
/// <see cref="SefariaDictionaryEntry"/> UI. A richer confidence/tier presentation comes later, as does
/// the exact (ref, position) path once the reader emits a clicked word's position.
/// </summary>
public partial class MainWindow
{
    // Machine-local fallback so the feature works on this dev box without hand-editing settings.json.
    private const string ConcordanceDevDefaultPath =
        @"F:\Git Repos\Hebrew Dictionary Concordance\data\db\talmud_lane.sqlite";

    private IConcordanceLookup? _concordanceLookup;
    private bool _concordanceInitAttempted;

    private bool ConcordanceEnabled =>
        _settings.UseConcordanceForDictionary && GetConcordanceLookup() is not null;

    private IConcordanceLookup? GetConcordanceLookup()
    {
        if (_concordanceInitAttempted)
        {
            return _concordanceLookup;
        }

        _concordanceInitAttempted = true;
        var path = ResolveConcordancePath();
        if (path is null)
        {
            return null;
        }

        try
        {
            _concordanceLookup = new ConcordanceLookup(path);
        }
        catch
        {
            // A missing/locked artifact must never break the dictionary; just stay inactive.
            _concordanceLookup = null;
        }

        return _concordanceLookup;
    }

    private string? ResolveConcordancePath()
    {
        var configured = _settings.ConcordanceDatabasePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return File.Exists(ConcordanceDevDefaultPath) ? ConcordanceDevDefaultPath : null;
    }

    /// <summary>
    /// Concordance-first routing used in place of the Sefaria-only lookup. Tries the concordance for a
    /// covered ref; if it yields nothing, falls back to the original Sefaria dictionary path.
    /// </summary>
    private async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupDictionaryEntriesRoutedAsync(
        string lookupWord, string reference, CancellationToken cancellationToken)
    {
        if (ConcordanceEnabled && !string.IsNullOrWhiteSpace(reference))
        {
            var concordanceEntries = await LookupConcordanceEntriesAsync(reference, lookupWord, cancellationToken);
            if (concordanceEntries.Count > 0)
            {
                return concordanceEntries;
            }
        }

        return await LookupDictionaryEntriesWithFallbacksAsync(lookupWord, reference, cancellationToken);
    }

    private async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupConcordanceEntriesAsync(
        string reference, string surface, CancellationToken cancellationToken)
    {
        var lookup = GetConcordanceLookup();
        if (lookup is null)
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        LookupResult? result;
        try
        {
            result = await lookup.LookupByWordAsync(reference, surface, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        if (result is null || result.Readings.Count == 0)
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        return MapConcordanceResult(result);
    }

    private static IReadOnlyList<SefariaDictionaryEntry> MapConcordanceResult(LookupResult result)
    {
        var entries = new List<SefariaDictionaryEntry>();
        // Each ranked reading (skeleton) expands to every dictionary lemma sharing it; the reading's
        // rank/tier/confidence labels each of its entries.
        foreach (var reading in result.Readings)
        {
            var label = $"Concordance · #{reading.Rank} · {reading.Tier} · {reading.Score:P0}";
            foreach (var entry in reading.Entries)
            {
                var glosses = entry.Senses
                    .Select(sense => sense.ModernEnglish ?? sense.OriginalGloss)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!.Trim())
                    .ToList();
                var definition = glosses.Count switch
                {
                    0 => "(no gloss)",
                    1 => glosses[0],
                    _ => string.Join("\n", glosses.Select((text, index) => $"{index + 1}. {text}")),
                };

                var refs = entry.Senses
                    .SelectMany(sense => sense.Links)
                    .Select(link => link.Reference)
                    .Where(reference => !string.IsNullOrWhiteSpace(reference))
                    .Distinct(StringComparer.Ordinal)
                    .Take(30)
                    .ToList();

                entries.Add(new SefariaDictionaryEntry
                {
                    EntryId = entry.LemmaId,
                    Headword = entry.LemmaForm,
                    LexiconName = label,
                    Definition = definition,
                    ContentText = definition,
                    Refs = refs,
                    IsOffline = true,
                });
            }
        }

        return entries;
    }
}
