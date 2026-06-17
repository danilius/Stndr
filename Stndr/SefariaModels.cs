using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

public enum HebrewMarksMode
{
    TextOnly,
    Nikkud,
    NikkudAndCantillation
}

public enum HebrewReaderFont
{
    SefariaSerif,
    NotoSans,
    WindowsHebrew
}

public enum EnglishReaderFont
{
    SystemSans,
    HumanistSans,
    Serif
}

public enum UiFont
{
    Inter,
    SegoeUi,
    NirmalaUi
}

public sealed class AppSettings
{
    public InstalledBookTitleDisplay InstalledBookTitleDisplay { get; set; } = InstalledBookTitleDisplay.Both;
    public Dictionary<string, string> SelectedHebrewTextsByBook { get; set; } = new();
    public Dictionary<string, string> SelectedTranslationsByBook { get; set; } = new();
    public Dictionary<string, ReaderDisplayMode> ReaderDisplayModesByBook { get; set; } = new();
    public List<string> PinnedCommentarySourceKeys { get; set; } = new();
    public HebrewReaderFont HebrewReaderFont { get; set; } = HebrewReaderFont.SefariaSerif;
    public EnglishReaderFont EnglishReaderFont { get; set; } = EnglishReaderFont.SystemSans;
    public UiFont UiFont { get; set; } = UiFont.Inter;
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
