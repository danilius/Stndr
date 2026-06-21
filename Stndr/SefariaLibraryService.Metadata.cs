using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;
using LiteDatabase = LiteDB.LiteDatabase;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private const string VersionMetadataCollectionName = "version_metadata";

    private sealed class VersionMetadataCacheEntry
    {
        public string Title { get; set; } = string.Empty;
        public List<SefariaVersionOption> Versions { get; set; } = new();
        public DateTime UpdatedAtUtc { get; set; }
    }

    public bool TryGetCachedAvailableVersions(string title, out List<SefariaVersionOption> versions)
    {
        if (!System.IO.File.Exists(VersionMetadataDbPath))
        {
            versions = new List<SefariaVersionOption>();
            return false;
        }

        using var db = new LiteDatabase(VersionMetadataDbPath);
        var collection = db.GetCollection<VersionMetadataCacheEntry>(VersionMetadataCollectionName);
        var entry = collection.FindOne(Query.EQ(nameof(VersionMetadataCacheEntry.Title), title));
        if (entry is null)
        {
            versions = new List<SefariaVersionOption>();
            return false;
        }

        versions = entry.Versions.Select(version => new SefariaVersionOption
        {
            LanguageCode = version.LanguageCode,
            LanguageFamilyName = version.LanguageFamilyName,
            VersionTitle = version.VersionTitle,
            DisplayText = version.DisplayText
        }).ToList();
        return true;
    }
}
