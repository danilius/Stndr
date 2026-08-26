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
}
