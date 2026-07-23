using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public async Task<IReadOnlyList<SefariaLexiconInfo>> GetOfflineLexiconsAsync(CancellationToken token = default)
    {
        if (!HasOfflineLibrary) return Array.Empty<SefariaLexiconInfo>();
        try
        {
            await using var connection = OpenOfflineConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT l.id,l.name,l.language,l.to_language,COUNT(e.id)
                FROM lexicons l LEFT JOIN lexicon_entries e ON e.lexicon_id=l.id
                GROUP BY l.id ORDER BY l.name COLLATE NOCASE
                """;
            var result = new List<SefariaLexiconInfo>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1) { return Array.Empty<SefariaLexiconInfo>(); }
    }

    public async Task<IReadOnlyList<SefariaDictionaryPrefix>> GetOfflineDictionaryPrefixesAsync(
        long lexiconId, string prefix, int depth, CancellationToken token = default)
    {
        if (!HasOfflineLibrary) return Array.Empty<SefariaDictionaryPrefix>();
        var key = SefariaOfflineLibraryImporter.NormalizeDictionaryKey(prefix ?? "", keepSpaces: false);
        var length = Math.Max(key.Length + 1, depth);
        try
        {
            await using var connection = OpenOfflineConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT substr(sort_key,1,$length),COUNT(*) FROM lexicon_entries
                WHERE lexicon_id=$lexicon AND sort_key LIKE $prefix||'%' AND length(sort_key)>=$length
                GROUP BY substr(sort_key,1,$length) ORDER BY sort_key LIMIT 250
                """;
            command.Parameters.AddWithValue("$length", length);
            command.Parameters.AddWithValue("$lexicon", lexiconId);
            command.Parameters.AddWithValue("$prefix", key);
            var result = new List<SefariaDictionaryPrefix>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1) { return Array.Empty<SefariaDictionaryPrefix>(); }
    }

    public async Task<IReadOnlyList<SefariaDictionaryEntry>> BrowseOfflineDictionaryAsync(
        long lexiconId, string prefix, int offset = 0, int limit = 100, CancellationToken token = default)
    {
        if (!HasOfflineLibrary) return Array.Empty<SefariaDictionaryEntry>();
        var key = SefariaOfflineLibraryImporter.NormalizeDictionaryKey(prefix ?? "", keepSpaces: false);
        await using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DictionaryEntrySelect + """
            WHERE e.lexicon_id=$lexicon AND e.sort_key LIKE $prefix||'%'
            ORDER BY e.sort_key,e.id LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$lexicon", lexiconId);
        command.Parameters.AddWithValue("$prefix", key);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 250));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        return await ReadOfflineDictionaryEntriesAsync(command, null, token);
    }

    public async Task<IReadOnlyList<SefariaDictionaryEntry>> SearchOfflineDictionaryAsync(
        string query, long? lexiconId = null, int limit = 100, CancellationToken token = default)
    {
        if (!HasOfflineLibrary || string.IsNullOrWhiteSpace(query)) return Array.Empty<SefariaDictionaryEntry>();
        var key = SefariaOfflineLibraryImporter.NormalizeDictionaryKey(query, keepSpaces: true);
        var ftsQuery = string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => "\"" + part.Replace("\"", "\"\"") + "\"*"));
        await using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DictionaryEntrySelect + """
            WHERE ($lexicon IS NULL OR e.lexicon_id=$lexicon) AND e.id IN (
                SELECT entry_id FROM lexicon_aliases WHERE alias_key=$key OR alias_key LIKE $key||'%'
                UNION SELECT wfe.entry_id FROM dictionary_word_forms wf
                    JOIN dictionary_word_form_entries wfe ON wfe.form_id=wf.id
                    WHERE wf.form_key=$key OR wf.consonantal_form=$key
                UNION SELECT rowid FROM lexicon_search WHERE lexicon_search MATCH $fts
            ) ORDER BY CASE WHEN e.headword_key=$key THEN 0 WHEN e.headword_key LIKE $key||'%' THEN 1 ELSE 2 END,
              e.sort_key,e.id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$lexicon", lexiconId is null ? DBNull.Value : lexiconId.Value);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$fts", ftsQuery);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 250));
        try { return await ReadOfflineDictionaryEntriesAsync(command, key, token); }
        catch (SqliteException) { return Array.Empty<SefariaDictionaryEntry>(); }
    }

    public async Task<SefariaDictionaryEntry?> GetOfflineDictionaryEntryAsync(long entryId, CancellationToken token = default)
    {
        if (!HasOfflineLibrary) return null;
        await using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DictionaryEntrySelect + " WHERE e.id=$id";
        command.Parameters.AddWithValue("$id", entryId);
        return (await ReadOfflineDictionaryEntriesAsync(command, null, token)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupOfflineDictionaryEntriesAsync(
        string word, string? lookupRef, int limit, CancellationToken token)
    {
        if (!HasOfflineLibrary) return Array.Empty<SefariaDictionaryEntry>();
        var key = SefariaOfflineLibraryImporter.NormalizeDictionaryKey(word, keepSpaces: true);
        try
        {
            await using var connection = OpenOfflineConnection();
            using var command = connection.CreateCommand();
            command.CommandText = DictionaryEntrySelect + """
                WHERE e.id IN (
                    SELECT entry_id FROM lexicon_aliases WHERE alias_key=$key
                    UNION SELECT wfe.entry_id FROM dictionary_word_forms wf
                        JOIN dictionary_word_form_entries wfe ON wfe.form_id=wf.id
                        WHERE wf.form_key=$key OR wf.consonantal_form=$key
                ) ORDER BY e.sort_key,e.id LIMIT $limit
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$limit", limit);
            return await ReadOfflineDictionaryEntriesAsync(command, key, token, lookupRef);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1) { return Array.Empty<SefariaDictionaryEntry>(); }
    }

    private const string DictionaryEntrySelect =
        "SELECT e.id,e.headword,e.transliteration,e.pronunciation,l.name,e.definition_text," +
        "e.strong_number,e.gk,e.twot,e.root,e.prev_hw,e.next_hw " +
        "FROM lexicon_entries e JOIN lexicons l ON l.id=e.lexicon_id ";

    private async Task<IReadOnlyList<SefariaDictionaryEntry>> ReadOfflineDictionaryEntriesAsync(
        SqliteCommand command, string? formKey, CancellationToken token, string? lookupRef = null)
    {
        var rows = new List<SefariaDictionaryEntry>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        while (await reader.ReadAsync(token))
            rows.Add(new SefariaDictionaryEntry
            {
                EntryId = reader.GetInt64(0), Headword = reader.GetString(1), Transliteration = reader.GetString(2),
                Pronunciation = reader.GetString(3), LexiconName = reader.GetString(4), Definition = reader.GetString(5),
                ContentText = reader.GetString(5), StrongNumber = reader.GetString(6), GkNumber = reader.GetString(7),
                TwotNumber = reader.GetString(8), Root = reader.GetString(9), PreviousHeadword = reader.GetString(10),
                NextHeadword = reader.GetString(11), IsOffline = true
            });
        if (string.IsNullOrWhiteSpace(formKey) || rows.Count == 0) return rows;

        var references = await LoadOfflineDictionaryReferencesAsync(command.Connection!, rows.Select(row => row.EntryId).ToArray(), formKey, token);
        return rows.Select(row => new SefariaDictionaryEntry
        {
            EntryId = row.EntryId, Headword = row.Headword, Transliteration = row.Transliteration,
            Pronunciation = row.Pronunciation, LexiconName = row.LexiconName, Definition = row.Definition,
            ContentText = row.ContentText, StrongNumber = row.StrongNumber, GkNumber = row.GkNumber,
            TwotNumber = row.TwotNumber, Root = row.Root, PreviousHeadword = row.PreviousHeadword,
            NextHeadword = row.NextHeadword, IsOffline = true,
            Refs = references.TryGetValue(row.EntryId, out var refs) ? OrderReferences(refs, lookupRef) : Array.Empty<string>()
        }).ToList();
    }

    private static async Task<Dictionary<long, List<string>>> LoadOfflineDictionaryReferencesAsync(
        SqliteConnection connection, long[] ids, string formKey, CancellationToken token)
    {
        var result = new Dictionary<long, List<string>>();
        if (ids.Length == 0) return result;
        using var command = connection.CreateCommand();
        var names = ids.Select((id, index) => { var name = "$id" + index; command.Parameters.AddWithValue(name, id); return name; });
        command.CommandText = $"""
            SELECT wfe.entry_id,wf.refs_json FROM dictionary_word_forms wf
            JOIN dictionary_word_form_entries wfe ON wfe.form_id=wf.id
            WHERE (wf.form_key=$key OR wf.consonantal_form=$key) AND wfe.entry_id IN ({string.Join(',', names)})
            """;
        command.Parameters.AddWithValue("$key", formKey);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var refs)) result[id] = refs = new();
            try
            {
                foreach (var reference in JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(reference) && !refs.Contains(reference, StringComparer.Ordinal)) refs.Add(reference);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static IReadOnlyList<string> OrderReferences(List<string> refs, string? lookupRef)
    {
        if (string.IsNullOrWhiteSpace(lookupRef)) return refs.Take(30).ToList();
        return refs.OrderByDescending(reference => reference.StartsWith(lookupRef, StringComparison.OrdinalIgnoreCase))
            .ThenBy(reference => reference, StringComparer.Ordinal).Take(30).ToList();
    }
}
