using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stndr;

public abstract class SefariaNode : INotifyPropertyChanged
{
    [JsonPropertyName("enShortDesc")]
    public string? EnShortDesc { get; set; }

    [JsonPropertyName("heShortDesc")]
    public string? HeShortDesc { get; set; }

    [JsonPropertyName("order")]
    public float Order { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class SefariaCategoryNode : SefariaNode
{
    [JsonPropertyName("contents")]
    public ObservableCollection<SefariaNode> Contents { get; set; } = new();

    [JsonPropertyName("enDesc")]
    public string? EnDesc { get; set; }

    [JsonPropertyName("heDesc")]
    public string? HeDesc { get; set; }

    [JsonPropertyName("heCategory")]
    public string? HebrewCategory { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Category) ? "Category" : Category;
}

public sealed class SefariaBookNode : SefariaNode
{
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("primary_category")]
    public string? PrimaryCategory { get; set; }

    [JsonPropertyName("heTitle")]
    public string? HebrewTitle { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("corpus")]
    public string? Corpus { get; set; }

    private List<SefariaVersionOption> _versions = new();
    private SefariaVersionOption? _selectedVersion;
    private bool _isVersionsLoaded;
    private bool _isLoadingVersions;
    private bool _isDownloaded;
    private bool _isDownloading;
    private double _downloadProgress;

    [JsonIgnore]
    public List<SefariaVersionOption> Versions
    {
        get => _versions;
        set
        {
            _versions = value ?? new List<SefariaVersionOption>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMultipleVersions));
        }
    }

    [JsonIgnore]
    public bool HasMultipleVersions => Versions.Count > 1;

    [JsonIgnore]
    public SefariaVersionOption? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Equals(_selectedVersion, value))
            {
                _selectedVersion = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsVersionsLoaded
    {
        get => _isVersionsLoaded;
        set
        {
            if (_isVersionsLoaded != value)
            {
                _isVersionsLoaded = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        set
        {
            if (_isLoadingVersions != value)
            {
                _isLoadingVersions = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            if (_isDownloaded != value)
            {
                _isDownloaded = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading != value)
            {
                _isDownloading = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (Math.Abs(_downloadProgress - value) > 0.01)
            {
                _downloadProgress = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
}

public sealed class SefariaVersionOption
{
    public string? LanguageFamilyName { get; set; }
    public string LanguageCode { get; set; } = "en";
    public string VersionTitle { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;

    public string DownloadSegment => $"{LanguageCode} - {VersionTitle}";

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(DisplayText) ? DownloadSegment : DisplayText;
    }
}

public sealed class SefariaIndexJsonNode
{
    [JsonPropertyName("enShortDesc")]
    public string? EnShortDesc { get; set; }

    [JsonPropertyName("heShortDesc")]
    public string? HeShortDesc { get; set; }

    [JsonPropertyName("order")]
    public float Order { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("primary_category")]
    public string? PrimaryCategory { get; set; }

    [JsonPropertyName("heTitle")]
    public string? HebrewTitle { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("corpus")]
    public string? Corpus { get; set; }

    [JsonPropertyName("contents")]
    public List<SefariaIndexJsonNode>? Contents { get; set; }

    [JsonPropertyName("enDesc")]
    public string? EnDesc { get; set; }

    [JsonPropertyName("heDesc")]
    public string? HeDesc { get; set; }

    [JsonPropertyName("heCategory")]
    public string? HebrewCategory { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

public sealed class InstalledSefariaBook
{
    public string Title { get; set; } = string.Empty;
    public string? HebrewTitle { get; set; }
    public List<string> Categories { get; set; } = new();
    public List<string?> HebrewCategories { get; set; } = new();
    public List<float> CategoryOrders { get; set; } = new();
    public float Order { get; set; }
    public string LanguageCode { get; set; } = "en";
    public string VersionTitle { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileLength { get; set; }
    public DateTime FileLastWriteTimeUtc { get; set; }
    public double LastScrollOffset { get; set; }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;

    [JsonIgnore]
    public string DisplayVersion => string.IsNullOrWhiteSpace(VersionTitle)
        ? LanguageCode
        : $"{VersionTitle} ({LanguageCode})";

    [JsonIgnore]
    public string Key => $"{Title}|{LanguageCode}|{VersionTitle}";
}

public sealed class InstalledSefariaCategory
{
    public string Title { get; set; } = string.Empty;
    public string? HebrewTitle { get; set; }
    public string CategoryPath { get; set; } = string.Empty;
    public bool IsBookTitle { get; set; }
    public float Order { get; set; }
    public ObservableCollection<object> Children { get; } = new();
}

public enum InstalledBookTitleDisplay
{
    Hebrew,
    English,
    Both
}

public enum ReaderDisplayMode
{
    PrimaryOnly,
    TranslationBelow,
    SideBySide,
    TranslationSideBySide
}

public enum CommentaryLanguage
{
    English,
    Hebrew
}

public enum CommentarySortMode
{
    English,
    Hebrew,
    Custom
}

public enum HebrewMarksMode
{
    TextOnly,
    Nikkud,
    NikkudAndCantillation
}

public sealed class AppSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DataStorageFolder { get; set; } = string.Empty;
    public InstalledBookTitleDisplay InstalledBookTitleDisplay { get; set; } = InstalledBookTitleDisplay.Both;
    public Dictionary<string, string> SelectedHebrewTextsByBook { get; set; } = new();
    public Dictionary<string, string> SelectedTranslationsByBook { get; set; } = new();
    public Dictionary<string, ReaderDisplayMode> ReaderDisplayModesByBook { get; set; } = new();
    public Dictionary<string, ReaderLinksPreferences> ReaderLinksPreferencesByBook { get; set; } = new();
    public Dictionary<string, List<string>> PinnedCommentarySourceKeysByBook { get; set; } = new();
    public Dictionary<string, ReaderCommentarySortPreferences> CommentarySortPreferencesByBook { get; set; } = new();
    public List<string> PinnedCommentarySourceKeys { get; set; } = new();
    public string HebrewReaderFontFamily { get; set; } = string.Empty;
    public string EnglishReaderFontFamily { get; set; } = string.Empty;
    public string UiFontFamily { get; set; } = string.Empty;
    public string HebrewDisplayFontFamily { get; set; } = string.Empty;
    public double HebrewReaderFontSize { get; set; } = 15;
    public double EnglishReaderFontSize { get; set; } = 15;
    public double HebrewCommentaryFontSize { get; set; } = 15;
    public double EnglishCommentaryFontSize { get; set; } = 15;
    public double UiFontSize { get; set; } = 13;
    public double HebrewDisplayFontSize { get; set; } = 18;
    public double SingleLanguageReaderColumnLetters { get; set; } = 80;
    public double DualLanguageReaderColumnLetters { get; set; } = 120;
}

public sealed class ReaderLinksPreferences
{
    public bool IsExpanded { get; set; }
    public List<string> SelectedCategories { get; set; } = new();
}

public sealed class ReaderCommentarySortPreferences
{
    public CommentarySortMode SortMode { get; set; } = CommentarySortMode.English;
    public List<string> CustomOrder { get; set; } = new();
}

public sealed record ReaderTextUnit(
    string Reference,
    string Text,
    string ChapterTitle = "",
    string HebrewChapterTitle = "");

public sealed record ReaderNavigationPage(
    string Page,
    string ChapterTitle,
    string HebrewChapterTitle);

public sealed class SefariaCommentaryItem
{
    public string Ref { get; set; } = string.Empty;
    public string AnchorRef { get; set; } = string.Empty;
    public string IndexTitle { get; set; } = string.Empty;
    public string CollectiveTitleEnglish { get; set; } = string.Empty;
    public string CollectiveTitleHebrew { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string HebrewText { get; set; } = string.Empty;
    public string VersionTitle { get; set; } = string.Empty;
    public string HebrewVersionTitle { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public string HebrewLicense { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(CollectiveTitleEnglish)
        ? IndexTitle
        : CollectiveTitleEnglish;

    [JsonIgnore]
    public string HebrewDisplayTitle => string.IsNullOrWhiteSpace(CollectiveTitleHebrew)
        ? DisplayTitle
        : CollectiveTitleHebrew;
}

public sealed class CachedSefariaCommentarySet
{
    public string Id { get; set; } = string.Empty;
    public string AnchorRef { get; set; } = string.Empty;
    public DateTime RetrievedAtUtc { get; set; }
    public List<SefariaCommentaryItem> Items { get; set; } = new();
}

public sealed class SefariaLinkItem
{
    public string Ref { get; set; } = string.Empty;
    public string AnchorRef { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string SourceHeRef { get; set; } = string.Empty;
    public string IndexTitle { get; set; } = string.Empty;
    public string CollectiveTitleEnglish { get; set; } = string.Empty;
    public string CollectiveTitleHebrew { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool SourceHasEnglish { get; set; }
    public int AnchorVerse { get; set; }

    [JsonIgnore]
    public string DisplayTitle => FirstNonEmptyDisplayValue(
        CollectiveTitleEnglish,
        IndexTitle,
        ExtractReferenceTitle(DisplayReference));

    [JsonIgnore]
    public string HebrewDisplayTitle => string.IsNullOrWhiteSpace(CollectiveTitleHebrew)
        ? DisplayTitle
        : CollectiveTitleHebrew;

    [JsonIgnore]
    public string DisplayReference => string.IsNullOrWhiteSpace(SourceRef)
        ? Ref
        : SourceRef;

    private static string FirstNonEmptyDisplayValue(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ExtractReferenceTitle(string fullReference)
    {
        if (string.IsNullOrWhiteSpace(fullReference))
        {
            return string.Empty;
        }

        var parts = fullReference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var endIndex = parts.Length;
        while (endIndex > 0 && ContainsDigit(parts[endIndex - 1]))
        {
            endIndex--;
        }

        return endIndex == parts.Length
            ? fullReference.Trim()
            : string.Join(' ', parts, 0, endIndex);
    }

    private static bool ContainsDigit(string value)
    {
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class SefariaLinkPreview
{
    public string Reference { get; set; } = string.Empty;
    public string WorkTitle { get; set; } = string.Empty;
    public string WorkHebrewTitle { get; set; } = string.Empty;
    public string ReferenceWithinWork { get; set; } = string.Empty;
    public string EnglishText { get; set; } = string.Empty;
    public string HebrewText { get; set; } = string.Empty;
    public bool IsFromInstalledBook { get; set; }
    public bool IsExcerptOnly { get; set; }
    public List<InstalledSefariaBook> Versions { get; set; } = new();
    public CachedSefariaLinkPreview? CachedPreview { get; set; }
}

public sealed class CachedSefariaLinkPreview
{
    public string Id { get; set; } = string.Empty;
    public string FullReference { get; set; } = string.Empty;
    public string WorkTitle { get; set; } = string.Empty;
    public string WorkHebrewTitle { get; set; } = string.Empty;
    public string ReferenceWithinWork { get; set; } = string.Empty;
    public string EnglishText { get; set; } = string.Empty;
    public string HebrewText { get; set; } = string.Empty;
    public string EnglishVersionTitle { get; set; } = string.Empty;
    public string HebrewVersionTitle { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = new();
    public DateTime RetrievedAtUtc { get; set; }
}

public sealed class CachedSefariaLinkSet
{
    public string Id { get; set; } = string.Empty;
    public string AnchorRef { get; set; } = string.Empty;
    public DateTime RetrievedAtUtc { get; set; }
    public List<SefariaLinkItem> Items { get; set; } = new();
}

public sealed class BookSchema
{
    public string Title { get; set; } = string.Empty;
    public string HeTitle { get; set; } = string.Empty;
    public int Depth { get; set; }
    public List<string> SectionNames { get; set; } = new();
    public List<string> HeSectionNames { get; set; } = new();
    public Dictionary<string, List<SchemaAltNode>> AltStructures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static BookSchema? Load(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var schema = new BookSchema
            {
                Title = GetString(root, "title"),
                HeTitle = GetString(root, "heTitle"),
                Depth = GetInt(root, "schema", "depth"),
            };

            if (root.TryGetProperty("sectionNames", out var sn))
            {
                foreach (var item in sn.EnumerateArray())
                    schema.SectionNames.Add(item.GetString() ?? "");
            }
            if (root.TryGetProperty("heSectionNames", out var hsn))
            {
                foreach (var item in hsn.EnumerateArray())
                    schema.HeSectionNames.Add(item.GetString() ?? "");
            }

            if (root.TryGetProperty("alts", out var alts))
            {
                foreach (var altProp in alts.EnumerateObject())
                {
                    var nodes = new List<SchemaAltNode>();
                    if (altProp.Value.TryGetProperty("nodes", out var nodesEl))
                    {
                        foreach (var n in nodesEl.EnumerateArray())
                        {
                            nodes.Add(new SchemaAltNode
                            {
                                Title = GetString(n, "title"),
                                HeTitle = GetString(n, "heTitle"),
                                WholeRef = GetString(n, "wholeRef"),
                                NumericEquivalent = GetInt(n, "numeric_equivalent"),
                            });
                        }
                    }
                    schema.AltStructures[altProp.Name] = nodes;
                }
            }

            return schema;
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement el, params string[] path)
    {
        var current = el;
        foreach (var p in path)
        {
            if (!current.TryGetProperty(p, out current)) return string.Empty;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
    }

    private static int GetInt(JsonElement el, params string[] path)
    {
        var current = el;
        foreach (var p in path)
        {
            if (!current.TryGetProperty(p, out current)) return 0;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var v) ? v : 0;
    }
}

public sealed class SchemaAltNode
{
    public string Title { get; set; } = string.Empty;
    public string HeTitle { get; set; } = string.Empty;
    public string WholeRef { get; set; } = string.Empty;
    public int NumericEquivalent { get; set; }
}
