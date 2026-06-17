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
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsCommentariesExpanded { get; set; } = true;
        public bool IsTextsExpanded { get; set; } = true;
        public string SelectedCommentaryRef { get; set; } = string.Empty;
        public bool IsCommentaryContentOpen { get; set; }
        public string SelectedCommentarySourceKey { get; set; } = AllCommentariesSelectionKey;
        public string SelectedCommentarySourceTitleEnglish { get; set; } = string.Empty;
        public string SelectedCommentarySourceTitleHebrew { get; set; } = string.Empty;
        public CommentaryLanguage CommentaryLanguage { get; set; } = CommentaryLanguage.English;
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
        public bool IsNavigationExpanded { get; set; } = true;
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsCommentariesExpanded { get; set; } = true;
        public bool IsTextsExpanded { get; set; } = true;
        public ReaderDisplayRow? SelectedReaderRow { get; set; }
        public string SelectedCommentaryRef { get; set; } = string.Empty;
        public bool IsCommentaryContentOpen { get; set; }
        public string SelectedCommentarySourceKey { get; set; } = AllCommentariesSelectionKey;
        public string SelectedCommentarySourceTitleEnglish { get; set; } = string.Empty;
        public string SelectedCommentarySourceTitleHebrew { get; set; } = string.Empty;
        public HashSet<string> PinnedCommentarySourceKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SefariaCommentaryItem> Commentaries { get; set; } = new();
        public bool IsCommentaryLoading { get; set; }
        public string CommentaryError { get; set; } = string.Empty;
        public CommentaryLanguage CommentaryLanguage { get; set; } = CommentaryLanguage.English;
        public CancellationTokenSource? CommentaryLoadCts { get; set; }
        public CancellationTokenSource? ReadingPositionSaveCts { get; set; }
        public ListBox? ReaderList { get; set; }
        public Flyout? DisplayFlyout { get; set; }
        public bool IsReaderScrollTrackingAttached { get; set; }
        public TextBlock? TitleBlock { get; set; }
        public TextBlock? ChapterBlock { get; set; }
        public TextBlock? VersionBlock { get; set; }
        public string CurrentChapterKey { get; set; } = string.Empty;
    }

    private sealed record ReaderDisplayRow(
        ReaderTextUnit? Primary,
        ReaderTextUnit? Translation,
        bool IsChapterHeading,
        string ChapterKey,
        string ChapterHeading);

    private sealed record ReaderNavigationItem(string Label, ReaderDisplayRow Row, string ChapterTitle);

    private sealed record CommentarySourceGroup(
        string Key,
        string EnglishTitle,
        string HebrewTitle,
        List<SefariaCommentaryItem> Items);

    private sealed class ReaderNavigationChapter
    {
        public ReaderNavigationChapter(string title)
        {
            Title = title;
        }

        public string Title { get; }
        public List<ReaderNavigationItem> Items { get; } = new();
    }

    private sealed record FontOption(string FamilyName)
    {
        public override string ToString()
        {
            return FamilyName;
        }
    }
}
