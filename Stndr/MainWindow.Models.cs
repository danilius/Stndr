using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Stndr;


public partial class MainWindow
{
    private sealed class LayoutState
    {
        public bool LeftCollapsed { get; set; }
        public bool RightCollapsed { get; set; }
        public double LeftExpandedWidth { get; set; } = DefaultExpandedPanelWidth;
        public double RightExpandedWidth { get; set; } = DefaultExpandedPanelWidth;
        public List<SavedTabState> Tabs { get; set; } = new();
        public List<string> OpenTabs { get; set; } = new();
        public List<SavedAdvancedSearch> SavedAdvancedSearches { get; set; } = new();
        public bool SavedSearchTitlesHebrew { get; set; }
        public bool AdvancedSearchAutosave { get; set; }
        public InstalledBookTitleDisplay AdvancedSearchScopeTitleDisplay { get; set; } = InstalledBookTitleDisplay.Both;
        public List<string> AdvancedSearchExpandedScopeKeys { get; set; } = new();
        public bool DictionaryDocked { get; set; }
        public string DictionaryCurrentWord { get; set; } = string.Empty;
        public string DictionaryCurrentReference { get; set; } = string.Empty;
        public double DictionaryPopupLeft { get; set; } = 360;
        public double DictionaryPopupTop { get; set; } = 140;
        public int SelectedTabIndex { get; set; }
    }

    private enum SavedTabKind
    {
        Utility,
        Reader
    }

    private sealed class SavedTabState
    {
        public SavedTabKind Kind { get; set; } = SavedTabKind.Utility;
        public string Title { get; set; } = string.Empty;
        public string WorkTitle { get; set; } = string.Empty;
        public string? PrimaryKey { get; set; }
        public string? SelectedTranslationKey { get; set; }
        public ReaderDisplayMode DisplayMode { get; set; } = ReaderDisplayMode.PrimaryOnly;
        public HebrewMarksMode HebrewMarksMode { get; set; } = HebrewMarksMode.NikkudAndCantillation;
        public bool IsNavigationExpanded { get; set; } = true;
        public List<string> ExpandedNavigationTopicKeys { get; set; } = new();
        public string NavigationJumpQuery { get; set; } = string.Empty;
        public bool NavigationTopicsAllExpanded { get; set; }
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsSedrotExpanded { get; set; } = true;
        public bool IsCommentariesExpanded { get; set; } = true;
        public bool IsLinksExpanded { get; set; }
        public bool IsTextsExpanded { get; set; } = true;
        public bool ShowAliyot { get; set; }
        public bool IsSedraContentOpen { get; set; }
        public string SelectedSedraKey { get; set; } = string.Empty;
        public string SelectedCommentaryRef { get; set; } = string.Empty;
        public List<string> SelectedLinkCategories { get; set; } = new();
        public bool IsCommentaryContentOpen { get; set; }
        public string SelectedCommentarySourceKey { get; set; } = AllCommentariesSelectionKey;
        public string SelectedCommentarySourceTitleEnglish { get; set; } = string.Empty;
        public string SelectedCommentarySourceTitleHebrew { get; set; } = string.Empty;
        public CommentaryLanguage CommentaryLanguage { get; set; } = CommentaryLanguage.English;
        public bool IsCommentarySplitOpen { get; set; }
        public double ScrollOffset { get; set; }
    }

    private sealed class ReaderTabState
    {
        public string WorkTitle { get; set; } = string.Empty;
        public InstalledSefariaBook Primary { get; set; } = new();
        public List<InstalledSefariaBook> Versions { get; set; } = new();
        public List<InstalledSefariaBook> HebrewTexts { get; set; } = new();
        public List<InstalledSefariaBook> Translations { get; set; } = new();
        public InstalledSefariaBook? SelectedTranslation { get; set; }
        public ReaderDisplayMode DisplayMode { get; set; }
        public HebrewMarksMode HebrewMarksMode { get; set; } = HebrewMarksMode.NikkudAndCantillation;
        public bool HasTalmudNavigation { get; set; }
        public List<ReaderDisplayRow> ReaderRows { get; set; } = new();
        public List<ReaderNavigationItem> NavigationItems { get; set; } = new();
        public List<ReaderNavigationChapter> NavigationChapters { get; set; } = new();
        public Dictionary<string, bool> ExpandedNavigationTopics { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Expander> NavigationTopicExpanders { get; } = new(StringComparer.Ordinal);
        public string NavigationJumpQuery { get; set; } = string.Empty;
        public bool NavigationTopicsAllExpanded { get; set; }
        public string ActiveNavigationTopicKey { get; set; } = string.Empty;
        public BookSchema? Schema { get; set; }
        public bool IsNavigationExpanded { get; set; } = true;
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsSedrotExpanded { get; set; } = true;
        public bool IsCommentariesExpanded { get; set; } = true;
        public bool IsLinksExpanded { get; set; }
        public bool IsTextsExpanded { get; set; } = true;
        public bool ShowAliyot { get; set; }
        public bool IsSedraContentOpen { get; set; }
        public string SelectedSedraKey { get; set; } = string.Empty;
        public ReaderDisplayRow? SelectedReaderRow { get; set; }
        public string SelectedCommentaryRef { get; set; } = string.Empty;
        public string SelectedLinksRef { get; set; } = string.Empty;
        public string LoadedLinksRef { get; set; } = string.Empty;
        public string ExpandedLinkPreviewRef { get; set; } = string.Empty;
        public bool IsCommentaryContentOpen { get; set; }
        public string SelectedCommentarySourceKey { get; set; } = AllCommentariesSelectionKey;
        public string SelectedCommentarySourceTitleEnglish { get; set; } = string.Empty;
        public string SelectedCommentarySourceTitleHebrew { get; set; } = string.Empty;
        public HashSet<string> PinnedCommentarySourceKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public CommentarySortMode CommentarySortMode { get; set; } = CommentarySortMode.English;
        public List<string> CommentaryCustomOrder { get; set; } = new();
        public Flyout? CommentarySortFlyout { get; set; }
        public HashSet<string> SelectedLinkCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExpandedLinkCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasInitializedLinkCategorySelection { get; set; }
        public bool IsLinkCategoryMoreExpanded { get; set; }
        public List<SefariaCommentaryItem> Commentaries { get; set; } = new();
        public List<SefariaLinkItem> Links { get; set; } = new();
        public SefariaLinkItem? ActiveLinkPreviewItem { get; set; }
        public SefariaLinkPreview? ActiveLinkPreview { get; set; }
        public bool IsCommentaryLoading { get; set; }
        public bool IsLinksLoading { get; set; }
        public bool IsLinkPreviewLoading { get; set; }
        public bool IsLinkWorkDownloadLoading { get; set; }
        public bool IsLinkSourceTabLoading { get; set; }
        public bool IsLinkSplitOpen { get; set; }
        public bool IsCommentarySplitOpen { get; set; }
        public string CommentaryError { get; set; } = string.Empty;
        public string LinksError { get; set; } = string.Empty;
        public string LinkPreviewError { get; set; } = string.Empty;
        public CommentaryLanguage CommentaryLanguage { get; set; } = CommentaryLanguage.English;
        public CancellationTokenSource? CommentaryLoadCts { get; set; }
        public CancellationTokenSource? LinksLoadCts { get; set; }
        public CancellationTokenSource? LinkPreviewLoadCts { get; set; }
        public CancellationTokenSource? ReadingPositionSaveCts { get; set; }
        public ListBox? ReaderList { get; set; }
        public NativeWebView? ReaderWebView { get; set; }
        public double ReaderWebScrollOffset { get; set; }
        public bool HasAppliedInitialWebScroll { get; set; }
        public bool IsApplyingWebScrollRestore { get; set; }
        public string PendingExactReferenceWithinWork { get; set; } = string.Empty;
        public string SearchHighlightReferenceWithinWork { get; set; } = string.Empty;
        public List<string> SearchHighlightTerms { get; set; } = new();
        public Flyout? DisplayFlyout { get; set; }
        public bool IsReaderScrollTrackingAttached { get; set; }
        public TextBlock? TitleBlock { get; set; }
        public TextBlock? ChapterBlock { get; set; }
        public TextBlock? VersionBlock { get; set; }
        public ColumnDefinition? LinkSplitSplitterColumn { get; set; }
        public ColumnDefinition? LinkSplitContentColumn { get; set; }
        public GridSplitter? LinkSplitSplitter { get; set; }
        public Border? LinkSplitBorder { get; set; }
        public ContentControl? LinkSplitContentHost { get; set; }
        public string CurrentChapterKey { get; set; } = string.Empty;
    }

    private sealed record ReaderDisplayRow(
        ReaderTextUnit? Primary,
        ReaderTextUnit? Translation,
        bool IsChapterHeading,
        string ChapterKey,
        string ChapterHeading);

    private sealed record ReaderNavigationItem(string Label, ReaderDisplayRow Row, string ChapterTitle);

    private sealed record TorahSedra(
        string Key,
        string BookTitle,
        string EnglishTitle,
        string HebrewTitle,
        string WholeRef,
        IReadOnlyList<TorahAliyah> Aliyot,
        bool IsCombined = false);

    private sealed record TorahAliyah(int Number, string Ref);

    private sealed record TorahVerseLocation(int Chapter, int Verse) : IComparable<TorahVerseLocation>
    {
        public int CompareTo(TorahVerseLocation? other)
        {
            if (other is null)
            {
                return 1;
            }

            var chapterComparison = Chapter.CompareTo(other.Chapter);
            return chapterComparison != 0 ? chapterComparison : Verse.CompareTo(other.Verse);
        }
    }

    private sealed record CommentarySourceGroup(
        string Key,
        string EnglishTitle,
        string HebrewTitle,
        List<SefariaCommentaryItem> Items);

    private sealed record LinkCategoryGroup(
        string Category,
        List<SefariaLinkItem> Items);

    private sealed class ReaderNavigationChapter
    {
        public ReaderNavigationChapter(string title, string key)
        {
            Title = title;
            Key = key;
        }

        public string Key { get; }
        public string Title { get; }
        public string RangeLabel { get; set; } = string.Empty;
        public List<ReaderNavigationItem> Items { get; } = new();
    }

    private sealed record FontOption(string FamilyName)
    {
        public override string ToString()
        {
            return FamilyName;
        }
    }

    private sealed class AdvancedSearchResult
    {
        public string Reference { get; set; } = string.Empty;
        public string WorkTitle { get; set; } = string.Empty;
        public string VersionTitle { get; set; } = string.Empty;
        public string Source { get; set; } = "Installed";
        public string Snippet { get; set; } = string.Empty;
        public string BookKey { get; set; } = string.Empty;
        public string ReferenceWithinWork { get; set; } = string.Empty;
        public string RemoteUrl { get; set; } = string.Empty;
        public bool IsRemote { get; set; }
        public List<string> MatchedTerms { get; set; } = new();
    }

    private sealed class SavedAdvancedSearch
    {
        public string Name { get; set; } = string.Empty;
        public string QuerySummary { get; set; } = string.Empty;
        public AdvancedSearchQuery? Query { get; set; }
        public bool IsPinned { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public List<AdvancedSearchResult> Results { get; set; } = new();

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? QuerySummary : Name;
        }
    }

    private sealed class CommentarySectionReorderContext
    {
        public ReaderTabState ReaderState { get; init; } = null!;
        public StackPanel Section { get; init; } = null!;
        public Border SectionChrome { get; init; } = null!;
        public Border InsertionLine { get; init; } = null!;
        public List<string> SectionKeys { get; init; } = new();
    }

    private sealed class CommentaryReorderDragState
    {
        public CommentarySectionReorderContext Context { get; init; } = null!;
        public string SourceKey { get; init; } = string.Empty;
        public int SourceIndex { get; init; }
        public Control DraggedWrapper { get; init; } = null!;
        public Control Grip { get; init; } = null!;
        public double StartPointerY { get; init; }
        public int InsertIndex { get; set; }
    }
}
