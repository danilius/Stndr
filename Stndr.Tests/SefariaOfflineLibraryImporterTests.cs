using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using Xunit;

namespace Stndr.Tests;

public sealed class SefariaOfflineLibraryImporterTests
{
    [Fact]
    public void Node_coverage_records_only_non_empty_top_level_complex_sections()
    {
        var content = new BsonDocument
        {
            ["Orach Chaim"] = new BsonDocument
            {
                ["default"] = new BsonArray
                {
                    new BsonArray { "first", "", "third" }
                }
            },
            ["Yoreh Deah"] = new BsonDocument
            {
                ["default"] = new BsonArray()
            },
            ["Choshen Mishpat"] = new BsonDocument
            {
                ["default"] = new BsonArray { new BsonArray { "text" } }
            }
        };

        var coverage = SefariaOfflineLibraryImporter.ExtractNodeCoverage(content);

        Assert.Equal(2, coverage.Count);
        Assert.Equal(2, coverage["Orach Chaim"].Segments);
        Assert.Equal(10, coverage["Orach Chaim"].Characters);
        Assert.Equal(1, coverage["Choshen Mishpat"].Segments);
        Assert.DoesNotContain("Yoreh Deah", coverage.Keys);
    }

    [Fact]
    public void Node_coverage_is_empty_for_simple_array_versions()
    {
        var coverage = SefariaOfflineLibraryImporter.ExtractNodeCoverage(
            new BsonArray { new BsonArray { "text" } });

        Assert.Empty(coverage);
    }

    [Fact]
    public async Task Import_succeeds_when_dependent_collections_precede_their_catalogues()
    {
        // Mirrors the 2026-10-22 dump, where lexicon_entry precedes lexicon and texts precede index.
        var folder = Path.Combine(Path.GetTempPath(), "Stndr-importer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var archive = Path.Combine(folder, "dump.tar.gz");
            WriteArchive(archive,
                ("word_form", [new BsonDocument
                {
                    ["form"] = "אב",
                    ["lookups"] = new BsonArray { new BsonDocument { ["parent_lexicon"] = "BDB", ["headword"] = "אב" } }
                }]),
                ("lexicon_entry", [new BsonDocument { ["parent_lexicon"] = "BDB", ["headword"] = "אב" }]),
                ("lexicon", [new BsonDocument { ["name"] = "BDB", ["language"] = "heb.biblical" }]),
                ("texts", [new BsonDocument
                {
                    ["title"] = "Genesis", ["language"] = "he", ["versionTitle"] = "V",
                    ["chapter"] = new BsonArray { new BsonArray { "בראשית" } }
                }]),
                ("links", []),
                ("index", [new BsonDocument { ["title"] = "Genesis", ["categories"] = new BsonArray { "Tanakh" } }]));

            var database = Path.Combine(folder, "library.db");
            var result = await new SefariaOfflineLibraryImporter().ImportAsync(archive, database);

            Assert.Equal(1, result.Works);
            Assert.Equal(1, result.Lexicons);
            await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            Assert.Equal("heb.biblical", await ScalarAsync(connection, "SELECT language FROM lexicons"));
            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM lexicon_entries e JOIN lexicons l ON l.id=e.lexicon_id"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM dictionary_word_form_entries"));
            Assert.Equal("[\"Tanakh\"]", await ScalarAsync(connection,
                "SELECT w.categories_json FROM versions v JOIN works w ON w.id=v.work_id"));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static void WriteArchive(string path, params (string Collection, BsonDocument[] Documents)[] collections)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);
        foreach (var (collection, documents) in collections)
        {
            var data = new MemoryStream();
            foreach (var document in documents) data.Write(document.ToBson());
            data.Position = 0;
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"dump/sefaria/{collection}.bson") { DataStream = data });
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
