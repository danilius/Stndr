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
    private void OnInstalledBookSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 &&
            e.AddedItems[0] is TreeViewItem { DataContext: InstalledSefariaCategory { IsBookTitle: true } bookCategory } &&
            _sefariaLibrary.GetInstalledVersionsForTitle(bookCategory.Title).FirstOrDefault() is { } book)
        {
            OpenInstalledBook(book);
        }
    }

    private void OpenInstalledBook(InstalledSefariaBook book)
    {
        OpenInstalledBook(book, null);
    }

    private void OpenInstalledBook(
        InstalledSefariaBook book,
        SavedTabState? savedState,
        bool selectAfterOpen = true,
        bool renderImmediately = true)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
        if (installedVersions.Count == 0)
        {
            return;
        }

        var existing = _openReaderTabs.FirstOrDefault(pair => string.Equals(pair.Value.WorkTitle, book.Title, StringComparison.Ordinal));
        if (existing.Key is not null)
        {
            if (SefariaLibraryService.IsHebrew(book))
            {
                var selectedHebrewText = installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal));
                if (selectedHebrewText is not null)
                {
                    existing.Value.Primary = selectedHebrewText;
                    NormalizeHebrewMarksMode(existing.Value);
                    SaveSelectedHebrewText(existing.Value);
                    RenderReaderContent(existing.Value);
                    UpdateReaderTools();
                }
            }
            else
            {
                existing.Value.SelectedTranslation = installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal));
                SaveSelectedTranslation(existing.Value);
                RenderReaderContent(existing.Value);
                UpdateReaderTools();
            }

            if (selectAfterOpen)
            {
                _centerTabs.SelectedItem = existing.Key;
            }
            return;
        }

        var hebrewTexts = installedVersions.Where(SefariaLibraryService.IsHebrew).ToList();
        var primary = GetSavedHebrewText(book.Title, hebrewTexts)
            ?? hebrewTexts.FirstOrDefault()
            ?? installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal))
            ?? installedVersions[0];
        var translations = installedVersions.Where(version => !SefariaLibraryService.IsHebrew(version)).ToList();
        var selectedTranslation = !SefariaLibraryService.IsHebrew(book)
            ? translations.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal))
            : GetSavedTranslation(book.Title, translations);
        selectedTranslation ??= translations.FirstOrDefault();

        var state = new ReaderTabState
        {
            WorkTitle = book.Title,
            Primary = primary,
            Versions = installedVersions,
            HebrewTexts = hebrewTexts,
            Translations = translations,
            SelectedTranslation = selectedTranslation,
            PinnedCommentarySourceKeys = new HashSet<string>(_settings.PinnedCommentarySourceKeys, StringComparer.OrdinalIgnoreCase),
            DisplayMode = GetSavedDisplayMode(book.Title, selectedTranslation is not null)
        };

        ApplySavedReaderState(state, savedState);
        SaveSelectedHebrewText(state);
        SaveSelectedTranslation(state);

        var reader = CreateReaderView(state, out var readerList, out var titleBlock, out var versionBlock);
        state.ReaderList = readerList;
        state.TitleBlock = titleBlock;
        state.ChapterBlock = reader.Tag as TextBlock;
        state.VersionBlock = versionBlock;

        var tab = CreateTab(FormatTitle(primary.Title, primary.HebrewTitle), reader);
        tab.Tag = primary.Title;
        _openReaderTabs[tab] = state;
        _tabs.Add(tab);
        if (selectAfterOpen)
        {
            _centerTabs.SelectedItem = tab;
        }

        state.ReaderWebScrollOffset = savedState?.ScrollOffset ?? state.Primary.LastScrollOffset;

        if (renderImmediately)
        {
            RenderReaderContent(state);
        }
        UpdateReaderTools();
    }

    private void RestoreReaderTab(
        SavedTabState savedTab,
        bool selectAfterOpen = true,
        bool renderImmediately = true)
    {
        if (string.IsNullOrWhiteSpace(savedTab.WorkTitle))
        {
            return;
        }

        var versions = _sefariaLibrary.GetInstalledVersionsForTitle(savedTab.WorkTitle);
        if (versions.Count == 0)
        {
            return;
        }

        var selectedBook = versions.FirstOrDefault(version => string.Equals(version.Key, savedTab.PrimaryKey, StringComparison.Ordinal))
            ?? versions.FirstOrDefault(version => string.Equals(version.Key, savedTab.SelectedTranslationKey, StringComparison.Ordinal))
            ?? versions[0];
        OpenInstalledBook(selectedBook, savedTab, selectAfterOpen, renderImmediately);
    }

    private static void ApplySavedReaderState(ReaderTabState state, SavedTabState? savedState)
    {
        if (savedState is null)
        {
            NormalizeHebrewMarksMode(state);
            return;
        }

        state.Primary = state.HebrewTexts.FirstOrDefault(version => string.Equals(version.Key, savedState.PrimaryKey, StringComparison.Ordinal))
            ?? state.Primary;
        state.SelectedTranslation = state.Translations.FirstOrDefault(version => string.Equals(version.Key, savedState.SelectedTranslationKey, StringComparison.Ordinal))
            ?? state.SelectedTranslation;
        state.DisplayMode = savedState.DisplayMode;
        state.HebrewMarksMode = savedState.HebrewMarksMode;
        NormalizeHebrewMarksMode(state);

        state.IsNavigationExpanded = savedState.IsNavigationExpanded;
        state.IsDisplayExpanded = savedState.IsDisplayExpanded;
        state.IsSedrotExpanded = savedState.IsSedrotExpanded;
        state.IsCommentariesExpanded = savedState.IsCommentariesExpanded;
        state.IsTextsExpanded = savedState.IsTextsExpanded;
        state.ShowAliyot = savedState.ShowAliyot;
        state.IsSedraContentOpen = savedState.IsSedraContentOpen;
        state.SelectedSedraKey = savedState.SelectedSedraKey;
        state.SelectedCommentaryRef = savedState.SelectedCommentaryRef;
        state.IsCommentaryContentOpen = savedState.IsCommentaryContentOpen;
        state.SelectedCommentarySourceKey = string.IsNullOrWhiteSpace(savedState.SelectedCommentarySourceKey)
            ? AllCommentariesSelectionKey
            : savedState.SelectedCommentarySourceKey;
        state.SelectedCommentarySourceTitleEnglish = savedState.SelectedCommentarySourceTitleEnglish;
        state.SelectedCommentarySourceTitleHebrew = savedState.SelectedCommentarySourceTitleHebrew;
        state.CommentaryLanguage = savedState.CommentaryLanguage;
    }

    private InstalledSefariaBook? GetSavedTranslation(string workTitle, List<InstalledSefariaBook> translations)
    {
        if (!_settings.SelectedTranslationsByBook.TryGetValue(workTitle, out var selectedKey))
        {
            return null;
        }

        return translations.FirstOrDefault(version => string.Equals(version.Key, selectedKey, StringComparison.Ordinal));
    }

    private InstalledSefariaBook? GetSavedHebrewText(string workTitle, List<InstalledSefariaBook> hebrewTexts)
    {
        if (!_settings.SelectedHebrewTextsByBook.TryGetValue(workTitle, out var selectedKey))
        {
            return null;
        }

        return hebrewTexts.FirstOrDefault(version => string.Equals(version.Key, selectedKey, StringComparison.Ordinal));
    }

    private ReaderDisplayMode GetSavedDisplayMode(string workTitle, bool hasTranslation)
    {
        if (_settings.ReaderDisplayModesByBook.TryGetValue(workTitle, out var mode))
        {
            return mode;
        }

        return hasTranslation ? ReaderDisplayMode.TranslationBelow : ReaderDisplayMode.PrimaryOnly;
    }

    private static bool SupportsCantillation(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Tanakh", StringComparison.OrdinalIgnoreCase));
    }

    private static void NormalizeHebrewMarksMode(ReaderTabState state)
    {
        if (!SupportsCantillation(state.Primary) && state.HebrewMarksMode == HebrewMarksMode.NikkudAndCantillation)
        {
            state.HebrewMarksMode = HebrewMarksMode.Nikkud;
        }
    }

    private static string FormatReaderVersionTitle(InstalledSefariaBook book)
    {
        return string.IsNullOrWhiteSpace(book.VersionTitle)
            ? book.LanguageCode
            : book.VersionTitle;
    }

    private Control CreateReaderView(
        ReaderTabState state,
        out ListBox? readerList,
        out TextBlock titleBlock,
        out TextBlock versionBlock)
    {
        titleBlock = new TextBlock
        {
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        versionBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var chapterBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#344054")),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var header = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(64, 24, 64, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                titleBlock,
                chapterBlock,
                versionBlock
            }
        };

        var headerArea = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                header,
                CreateReaderDisplaySettingsButton(state)
            }
        };
        Grid.SetColumn(headerArea.Children[1], 1);

        var headerChrome = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #14000000"),
            Child = headerArea
        };

        // WebView reader spike:
        // The previous visible reader was a virtualized ListBox of ReaderTextView controls. It is intentionally
        // no longer attached here because cross-paragraph selection, links, bookmarks, and document-level hit
        // testing are much cleaner in one browser document. The old Avalonia reader factory remains below as a
        // fallback/reference while this experiment proves out the WebView path.
        readerList = null;
        var webView = CreateReaderWebView(state);
        state.ReaderWebView = webView;

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brushes.White,
            Children =
            {
                headerChrome,
                webView
            }
        };
        layout.Tag = chapterBlock;
        Grid.SetRow(webView, 1);

        return layout;
    }

    private ListBox CreateAvaloniaReaderListFallback(ReaderTabState state)
    {
        // Kept as a fallback/reference during the WebView reader spike. This is the current native-Avalonia
        // implementation that renders each verse/paragraph as its own ReaderTextView, which is exactly what
        // prevents native text selection from spanning multiple verses/paragraphs.
        var readerList = new ListBox
        {
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(32, 0, 96, 24),
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
            {
                Orientation = Orientation.Vertical,
                CacheLength = 2
            }),
            ItemTemplate = new FuncDataTemplate<ReaderDisplayRow>((unit, _) => unit is null
                ? new Control()
                : CreateReaderDisplayRow(state, unit),
                true)
        };
        readerList.Classes.Add("reader-list");
        readerList.KeyDown += (_, e) => HandleReaderPageKeyDown(state, e);
        var currentReaderList = readerList;
        currentReaderList.AttachedToVisualTree += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => AttachReaderScrollTracking(state, currentReaderList), DispatcherPriority.Loaded);
        };
        readerList.SelectionChanged += (_, e) =>
        {
            var selectedRow = currentReaderList.SelectedItem as ReaderDisplayRow;
            OnReaderParagraphSelectionChanged(state, selectedRow);
            e.Handled = true;
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(readerList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalSnapPointsType(readerList, SnapPointsType.None);
        return readerList;
    }

    private void RenderReaderContent(ReaderTabState state)
    {
        if (state.TitleBlock is null || state.VersionBlock is null)
        {
            return;
        }

        var primaryIsHebrew = SefariaLibraryService.IsHebrew(state.Primary);
        ApplyReaderTitle(state.TitleBlock, state.Primary.Title, state.Primary.HebrewTitle);
        state.TitleBlock.TextAlignment = TextAlignment.Center;
        state.TitleBlock.FlowDirection = FlowDirection.LeftToRight;
        state.VersionBlock.Text = FormatReaderVersionTitle(state.Primary);
        state.VersionBlock.TextAlignment = TextAlignment.Center;
        state.VersionBlock.FlowDirection = primaryIsHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        var primaryUnits = _sefariaLibrary.ReadInstalledBookUnits(state.Primary);
        var translationUnits = state.SelectedTranslation is null
            ? new List<ReaderTextUnit>()
            : _sefariaLibrary.ReadInstalledBookUnits(state.SelectedTranslation);
        var navigationPages = BuildReaderNavigationPages(state);
        var isTalmudNavigation = navigationPages.Count > 0;
        var chapterTitlesByPage = navigationPages
            .GroupBy(page => page.Page, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => FormatChapterTitle(group.First()), StringComparer.Ordinal);

        var showTranslation = state.SelectedTranslation is not null && state.DisplayMode != ReaderDisplayMode.PrimaryOnly;
        var rowUnits = PairReaderUnitsByReference(primaryUnits, showTranslation ? translationUnits : new List<ReaderTextUnit>());
        var items = new List<ReaderDisplayRow>(rowUnits.Count);
        var pageRows = new Dictionary<string, ReaderDisplayRow>(StringComparer.Ordinal);
        var currentPage = string.Empty;
        foreach (var (primary, translation) in rowUnits)
        {
            var reference = primary?.Reference ?? translation?.Reference ?? string.Empty;
            var page = GetReferencePart(reference, 0);
            var chapterTitle = chapterTitlesByPage.TryGetValue(page, out var navigationChapterTitle)
                ? navigationChapterTitle
                : FormatChapterTitle(primary, translation);
            if (!string.IsNullOrWhiteSpace(page) && !string.Equals(page, currentPage, StringComparison.Ordinal))
            {
                currentPage = page;
                var heading = new ReaderDisplayRow(
                    Primary: null,
                    Translation: null,
                    IsChapterHeading: true,
                    ChapterKey: page,
                    ChapterHeading: FormatChapterHeading(page, chapterTitle));
                items.Add(heading);
                pageRows[page] = heading;
            }

            items.Add(new ReaderDisplayRow(
                Primary: primary,
                Translation: translation,
                IsChapterHeading: false,
                ChapterKey: page,
                ChapterHeading: string.Empty));
        }

        state.NavigationItems = isTalmudNavigation
            ? navigationPages
                .Where(page => pageRows.ContainsKey(page.Page))
                .Select(page => new ReaderNavigationItem(
                    FormatNavigationChapterLabel(page.Page),
                    pageRows[page.Page],
                    FormatChapterTitle(page)))
                .ToList()
            : pageRows
                .Select(pair => new ReaderNavigationItem(
                    FormatNavigationChapterLabel(pair.Key),
                    pair.Value,
                    pair.Value.ChapterHeading))
                .ToList();
        state.HasTalmudNavigation = isTalmudNavigation;
        state.NavigationChapters = BuildReaderNavigationChapters(state.NavigationItems);
        state.ReaderRows = items;
        if (state.ReaderList is not null)
        {
            state.ReaderList.ItemsSource = items;
        }

        RenderReaderWebView(state);
        UpdateReaderChapterHeader(state, state.NavigationItems.FirstOrDefault()?.Row);
        RestoreSavedCommentarySelection(state);
    }

    private static List<(ReaderTextUnit? Primary, ReaderTextUnit? Translation)> PairReaderUnitsByReference(
        IReadOnlyList<ReaderTextUnit> primaryUnits,
        IReadOnlyList<ReaderTextUnit> translationUnits)
    {
        if (translationUnits.Count == 0)
        {
            return primaryUnits
                .Select(unit => ((ReaderTextUnit?)unit, (ReaderTextUnit?)null))
                .ToList();
        }

        var translationsByReference = translationUnits
            .GroupBy(unit => NormalizeReaderUnitReference(unit.Reference), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var usedTranslationReferences = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(ReaderTextUnit? Primary, ReaderTextUnit? Translation)>(
            Math.Max(primaryUnits.Count, translationUnits.Count));

        foreach (var primary in primaryUnits)
        {
            var reference = NormalizeReaderUnitReference(primary.Reference);
            translationsByReference.TryGetValue(reference, out var translation);
            if (translation is not null)
            {
                usedTranslationReferences.Add(reference);
            }

            rows.Add((primary, translation));
        }

        foreach (var translation in translationUnits)
        {
            var reference = NormalizeReaderUnitReference(translation.Reference);
            if (!usedTranslationReferences.Contains(reference) &&
                !primaryUnits.Any(primary => string.Equals(
                    NormalizeReaderUnitReference(primary.Reference),
                    reference,
                    StringComparison.Ordinal)))
            {
                rows.Add((null, translation));
            }
        }

        return rows;
    }

    private static string NormalizeReaderUnitReference(string reference)
    {
        return string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : reference.Trim().Replace(':', '.');
    }

    private void AttachReaderScrollTracking(ReaderTabState state, ListBox readerList)
    {
        if (state.IsReaderScrollTrackingAttached)
        {
            return;
        }

        var scrollViewer = readerList.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            Dispatcher.UIThread.Post(() => AttachReaderScrollTracking(state, readerList), DispatcherPriority.Background);
            return;
        }

        state.IsReaderScrollTrackingAttached = true;
        scrollViewer.ScrollChanged += (_, _) =>
        {
            UpdateReaderChapterHeaderFromScroll(state);
            ScheduleReadingPositionSave(state, scrollViewer.Offset.Y);
        };
        UpdateReaderChapterHeaderFromScroll(state);
    }

    private void ScheduleReadingPositionSave(ReaderTabState state, double verticalOffset)
    {
        state.Primary.LastScrollOffset = verticalOffset;
        state.ReadingPositionSaveCts?.Cancel();
        state.ReadingPositionSaveCts = new CancellationTokenSource();
        var token = state.ReadingPositionSaveCts.Token;
        Task.Delay(500, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                _sefariaLibrary.SaveReadingPosition(state.Primary, verticalOffset);
            }
        }, TaskScheduler.Default);
    }

    private void OnReaderParagraphSelectionChanged(ReaderTabState state, ReaderDisplayRow? row)
    {
        state.CommentaryLoadCts?.Cancel();
        state.CommentaryLoadCts = null;

        state.SelectedReaderRow = row is { IsChapterHeading: false } ? row : null;
        UpdateReaderChapterHeader(state, row);
        state.SelectedCommentaryRef = state.SelectedReaderRow is null
            ? string.Empty
            : BuildSefariaAnchorRef(state, state.SelectedReaderRow, preferTranslation: false);
        state.Commentaries = new List<SefariaCommentaryItem>();
        state.CommentaryError = string.Empty;
        state.IsCommentaryLoading = false;

        if (string.IsNullOrWhiteSpace(state.SelectedCommentaryRef))
        {
            UpdateReaderTools();
            return;
        }

        state.IsCommentaryLoading = true;
        UpdateReaderTools();

        var cts = new CancellationTokenSource();
        state.CommentaryLoadCts = cts;
        _ = LoadCommentariesForSelectionAsync(state, state.SelectedCommentaryRef, cts);
    }

    private void RestoreSavedCommentarySelection(ReaderTabState state)
    {
        if (state.SelectedReaderRow is not null ||
            string.IsNullOrWhiteSpace(state.SelectedCommentaryRef))
        {
            return;
        }

        var savedRef = state.SelectedCommentaryRef;
        var row = state.ReaderRows
            .Where(candidate => !candidate.IsChapterHeading)
            .FirstOrDefault(candidate =>
                string.Equals(BuildSefariaAnchorRef(state, candidate, preferTranslation: false), savedRef, StringComparison.Ordinal) ||
                string.Equals(BuildSefariaAnchorRef(state, candidate, preferTranslation: true), savedRef, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        state.SelectedReaderRow = row;
        state.SelectedCommentaryRef = savedRef;
        state.IsCommentaryLoading = true;
        UpdateReaderChapterHeader(state, row);

        var cts = new CancellationTokenSource();
        state.CommentaryLoadCts = cts;
        _ = LoadCommentariesForSelectionAsync(state, savedRef, cts);
    }

    private async Task LoadCommentariesForSelectionAsync(
        ReaderTabState state,
        string anchorRef,
        CancellationTokenSource cts)
    {
        var requestedAnchorRef = anchorRef;
        var appliedResults = false;
        try
        {
            var commentaries = await _sefariaLibrary.GetCommentariesAsync(anchorRef, cts.Token);
            if (commentaries.Count == 0 && state.SelectedReaderRow is not null)
            {
                var fallbackRef = BuildSefariaAnchorRef(state, state.SelectedReaderRow, preferTranslation: true);
                if (!string.IsNullOrWhiteSpace(fallbackRef) &&
                    !string.Equals(fallbackRef, anchorRef, StringComparison.Ordinal))
                {
                    commentaries = await _sefariaLibrary.GetCommentariesAsync(fallbackRef, cts.Token);
                    if (commentaries.Count > 0)
                    {
                        anchorRef = fallbackRef;
                    }
                }
            }

            if (cts.IsCancellationRequested ||
                !ReferenceEquals(state.CommentaryLoadCts, cts) ||
                !string.Equals(state.SelectedCommentaryRef, requestedAnchorRef, StringComparison.Ordinal))
            {
                return;
            }

            state.SelectedCommentaryRef = anchorRef;
            state.Commentaries = commentaries;
            state.CommentaryError = string.Empty;
            appliedResults = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            if (IsActiveCommentaryLoad(state, requestedAnchorRef, cts))
            {
                state.CommentaryError = "Commentaries could not be loaded.";
            }
        }
        finally
        {
            if (IsActiveCommentaryLoad(state, requestedAnchorRef, cts) ||
                (appliedResults && !cts.IsCancellationRequested && ReferenceEquals(state.CommentaryLoadCts, cts)))
            {
                state.IsCommentaryLoading = false;
                state.CommentaryLoadCts = null;
                UpdateReaderTools();
            }

            cts.Dispose();
        }
    }

    private static bool IsActiveCommentaryLoad(
        ReaderTabState state,
        string requestedAnchorRef,
        CancellationTokenSource cts)
    {
        return !cts.IsCancellationRequested &&
            ReferenceEquals(state.CommentaryLoadCts, cts) &&
            string.Equals(state.SelectedCommentaryRef, requestedAnchorRef, StringComparison.Ordinal);
    }

    private void HandleReaderPageKeyDown(ReaderTabState state, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0
        };
        if (direction == 0 || state.NavigationItems.Count == 0)
        {
            return;
        }

        var currentKey = FirstNonEmpty(state.SelectedReaderRow?.ChapterKey, state.CurrentChapterKey);
        var currentIndex = state.NavigationItems.FindIndex(item =>
            string.Equals(item.Row.ChapterKey, currentKey, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            currentIndex = direction > 0 ? -1 : state.NavigationItems.Count;
        }

        var nextIndex = Math.Clamp(currentIndex + direction, 0, state.NavigationItems.Count - 1);
        var nextItem = state.NavigationItems[nextIndex];
        ScrollReaderRowToTop(state, nextItem.Row);
        UpdateReaderChapterHeader(state, nextItem.Row);
        e.Handled = true;
    }

    private void UpdateReaderChapterHeader(ReaderTabState state, ReaderDisplayRow? row)
    {
        if (state.ChapterBlock is null)
        {
            return;
        }

        var chapterKey = row?.ChapterKey ?? state.CurrentChapterKey;
        if (string.IsNullOrWhiteSpace(chapterKey))
        {
            state.ChapterBlock.Text = string.Empty;
            return;
        }

        var navigationItem = state.NavigationItems.FirstOrDefault(item =>
            string.Equals(item.Row.ChapterKey, chapterKey, StringComparison.Ordinal));
        var chapterText = row?.IsChapterHeading == true
            ? row.ChapterHeading
            : FirstNonEmpty(row?.ChapterHeading, navigationItem?.Row.ChapterHeading, navigationItem?.ChapterTitle);
        if (string.IsNullOrWhiteSpace(chapterText))
        {
            chapterText = FormatChapterHeading(chapterKey, string.Empty);
        }

        state.CurrentChapterKey = chapterKey;
        state.ChapterBlock.Text = chapterText;
    }

    private void UpdateReaderChapterHeaderFromScroll(ReaderTabState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state.ReaderList is null || state.ReaderRows.Count == 0)
            {
                return;
            }

            ReaderDisplayRow? bestRow = null;
            var bestDistance = double.PositiveInfinity;

            foreach (var row in state.ReaderRows)
            {
                if (state.ReaderList.ContainerFromItem(row) is not Control container ||
                    container.TranslatePoint(new Point(0, 0), state.ReaderList) is not { } point)
                {
                    continue;
                }

                var bottom = point.Y + container.Bounds.Height;
                if (bottom < 0 || point.Y > state.ReaderList.Bounds.Height)
                {
                    continue;
                }

                var distance = point.Y <= 0
                    ? Math.Abs(point.Y) * 0.1
                    : point.Y;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestRow = row;
                }
            }

            UpdateReaderChapterHeader(state, bestRow);
        }, DispatcherPriority.Loaded);
    }

    private static string BuildSefariaAnchorRef(
        ReaderTabState state,
        ReaderDisplayRow row,
        bool preferTranslation)
    {
        var unit = preferTranslation
            ? row.Translation ?? row.Primary
            : row.Primary ?? row.Translation;
        if (unit is null)
        {
            return string.Empty;
        }

        var reference = NormalizeReaderReferenceForSefaria(unit.Reference);
        return string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : $"{state.Primary.Title} {reference}";
    }

    private static string NormalizeReaderReferenceForSefaria(string reference)
    {
        reference = reference.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        return reference.Replace('.', ':');
    }

    private void ApplyReaderTitle(TextBlock titleBlock, string? englishTitle, string? hebrewTitle)
    {
        titleBlock.Text = null;
        var inlines = titleBlock.Inlines ?? new InlineCollection();
        inlines.Clear();

        var english = string.IsNullOrWhiteSpace(englishTitle) ? "Untitled" : englishTitle;
        var hasHebrew = !string.IsNullOrWhiteSpace(hebrewTitle);

        switch (_settings.InstalledBookTitleDisplay)
        {
            case InstalledBookTitleDisplay.Hebrew:
                AddReaderTitleRun(inlines, hasHebrew ? hebrewTitle! : english, hasHebrew);
                break;

            case InstalledBookTitleDisplay.English:
                AddReaderTitleRun(inlines, english, false);
                break;

            default:
                if (hasHebrew)
                {
                    AddReaderTitleRun(inlines, hebrewTitle!, true);
                    AddReaderTitleRun(inlines, " / ", false);
                }

                AddReaderTitleRun(inlines, english, false);
                break;
        }

        titleBlock.Inlines = inlines;
    }

    private void AddReaderTitleRun(InlineCollection inlines, string text, bool isHebrew)
    {
        inlines.Add(new Run(text)
        {
            FontFamily = isHebrew
                ? new FontFamily(GetSelectedHebrewFontFamily())
                : new FontFamily(GetSelectedEnglishFontFamily())
        });
    }

    private static List<ReaderNavigationChapter> BuildReaderNavigationChapters(List<ReaderNavigationItem> navigationItems)
    {
        var chapters = new List<ReaderNavigationChapter>();
        foreach (var item in navigationItems)
        {
            var current = chapters.LastOrDefault();
            if (current is null || !string.Equals(current.Title, item.ChapterTitle, StringComparison.Ordinal))
            {
                current = new ReaderNavigationChapter(item.ChapterTitle);
                chapters.Add(current);
            }

            current.Items.Add(item);
        }

        return chapters;
    }

    private List<ReaderNavigationPage> BuildReaderNavigationPages(ReaderTabState state)
    {
        var navigationPages = _sefariaLibrary.ReadInstalledBookNavigationPages(state.Primary);
        if (navigationPages.Count > 0)
        {
            return navigationPages;
        }

        if (state.SelectedTranslation is not null)
        {
            navigationPages = _sefariaLibrary.ReadInstalledBookNavigationPages(state.SelectedTranslation);
            if (navigationPages.Count > 0)
            {
                return navigationPages;
            }
        }

        return new List<ReaderNavigationPage>();
    }

    private Control CreateReaderDisplayRow(ReaderTabState state, ReaderDisplayRow row)
    {
        if (row.IsChapterHeading)
        {
            return CreateChapterHeading(row.ChapterHeading);
        }

        return CreateAliyahDisplayRow(
            state,
            row,
            CreateReaderUnit(state, row.Primary, row.Translation));
    }

    private static Control CreateChapterHeading(string heading)
    {
        return new TextBlock
        {
            Text = heading,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#344054")),
            Margin = new Thickness(0, 18, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private string FormatChapterHeading(string chapter, string chapterTitle)
    {
        if (!string.IsNullOrWhiteSpace(chapterTitle))
        {
            return $"{chapterTitle} - {chapter}";
        }

        if (!int.TryParse(chapter, out var number))
        {
            return chapter;
        }

        var english = $"Chapter {number}";
        var hebrew = $"\u05e4\u05e8\u05e7 {ToHebrewNumber(number)}";
        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => hebrew,
            InstalledBookTitleDisplay.English => english,
            _ => $"{hebrew} / {english}"
        };
    }

    private string FormatNavigationChapterLabel(string chapter)
    {
        if (_settings.InstalledBookTitleDisplay != InstalledBookTitleDisplay.Hebrew)
        {
            return chapter;
        }

        if (!int.TryParse(chapter, out var number))
        {
            var numericPrefixLength = 0;
            while (numericPrefixLength < chapter.Length && char.IsDigit(chapter[numericPrefixLength]))
            {
                numericPrefixLength++;
            }

            if (numericPrefixLength == 0 ||
                !int.TryParse(chapter[..numericPrefixLength], out number))
            {
                return chapter;
            }

            var suffix = FormatHebrewNavigationSuffix(chapter[numericPrefixLength..]);
            return string.IsNullOrWhiteSpace(suffix)
                ? ToHebrewNumber(number)
                : $"{ToHebrewNumber(number)} {suffix}";
        }

        return ToHebrewNumber(number);
    }

    private static string FormatHebrewNavigationSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        return suffix.Trim().ToLowerInvariant() switch
        {
            "a" => "\u05d0",
            "b" => "\u05d1",
            _ => suffix.Trim()
        };
    }

    private string FormatChapterTitle(ReaderTextUnit? primary, ReaderTextUnit? translation)
    {
        var english = FirstNonEmpty(primary?.ChapterTitle, translation?.ChapterTitle);
        var hebrew = FirstNonEmpty(primary?.HebrewChapterTitle, translation?.HebrewChapterTitle);
        return FormatChapterTitleParts(english, hebrew);
    }

    private string FormatChapterTitle(ReaderNavigationPage page)
    {
        return FormatChapterTitleParts(page.ChapterTitle, page.HebrewChapterTitle);
    }

    private string FormatChapterTitleParts(string? english, string? hebrew)
    {
        english = string.IsNullOrWhiteSpace(english) ? string.Empty : english;
        hebrew = string.IsNullOrWhiteSpace(hebrew) ? string.Empty : hebrew;
        if (string.IsNullOrWhiteSpace(english) && string.IsNullOrWhiteSpace(hebrew))
        {
            return string.Empty;
        }

        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => string.IsNullOrWhiteSpace(hebrew) ? english : hebrew,
            InstalledBookTitleDisplay.English => string.IsNullOrWhiteSpace(english) ? hebrew : english,
            _ => string.IsNullOrWhiteSpace(hebrew)
                ? english
                : string.IsNullOrWhiteSpace(english)
                    ? hebrew
                    : $"{hebrew} / {english}"
        };
    }

    private Control CreateAliyahDisplayRow(ReaderTabState state, ReaderDisplayRow row, Control content)
    {
        if (!state.ShowAliyot || GetSelectedTorahSedra(state) is not { } sedra)
        {
            return content;
        }

        var reference = FirstNonEmpty(row.Primary?.Reference, row.Translation?.Reference);
        if (string.IsNullOrWhiteSpace(reference) ||
            GetAliyahForReaderReference(sedra, reference) is not { } aliyah)
        {
            return content;
        }

        var shadedContent = new Border
        {
            Background = GetAliyahBrush(aliyah.Number),
            CornerRadius = new CornerRadius(4),
            Child = content
        };

        if (!IsAliyahStart(aliyah, reference))
        {
            return shadedContent;
        }

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = FormatAliyahHeading(aliyah.Number),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#344054")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                },
                shadedContent
            }
        };
    }

    private string FormatAliyahHeading(int aliyahNumber)
    {
        var english = $"Aliyah {aliyahNumber}";
        var hebrew = $"\u05e2\u05dc\u05d9\u05d9\u05d4 {ToHebrewNumber(aliyahNumber)}";
        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => hebrew,
            InstalledBookTitleDisplay.English => english,
            _ => $"{hebrew} / {english}"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatSegmentLabel(ReaderTextUnit? unit, bool useHebrew)
    {
        var value = GetReferencePart(unit?.Reference ?? string.Empty, -1);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return useHebrew && int.TryParse(value, out var number)
            ? ToHebrewNumber(number)
            : value;
    }

    private static string GetReferencePart(string reference, int index)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var resolvedIndex = index < 0 ? parts.Length + index : index;
        return resolvedIndex >= 0 && resolvedIndex < parts.Length ? parts[resolvedIndex] : string.Empty;
    }

    private static string ToHebrewNumber(int number)
    {
        if (number <= 0)
        {
            return number.ToString();
        }

        var builder = new StringBuilder();
        var hundreds = new[] { "", "\u05e7", "\u05e8", "\u05e9", "\u05ea" };
        while (number >= 400)
        {
            builder.Append('\u05ea');
            number -= 400;
        }

        if (number >= 100)
        {
            var count = Math.Min(4, number / 100);
            builder.Append(hundreds[count]);
            number -= count * 100;
        }

        if (number == 15)
        {
            builder.Append("\u05d8\u05d5");
            return builder.ToString();
        }

        if (number == 16)
        {
            builder.Append("\u05d8\u05d6");
            return builder.ToString();
        }

        var tens = new[] { "", "\u05d9", "\u05db", "\u05dc", "\u05de", "\u05e0", "\u05e1", "\u05e2", "\u05e4", "\u05e6" };
        var ones = new[] { "", "\u05d0", "\u05d1", "\u05d2", "\u05d3", "\u05d4", "\u05d5", "\u05d6", "\u05d7", "\u05d8" };
        if (number >= 10)
        {
            builder.Append(tens[number / 10]);
            number %= 10;
        }

        builder.Append(ones[number]);
        return builder.ToString();
    }

    private Control CreateReaderUnit(ReaderTabState state, ReaderTextUnit? primary, ReaderTextUnit? translation)
    {
        var primaryIsHebrew = SefariaLibraryService.IsHebrew(state.Primary);
        var primaryBlock = CreateReaderTextBlock(
            primary?.Text ?? string.Empty,
            primaryIsHebrew,
            state.HebrewMarksMode);
        var primaryLabelText = FormatSegmentLabel(primary, primaryIsHebrew);
        var primaryLabel = CreateSegmentLabel(
            primaryLabelText,
            HorizontalAlignment.Left);
        if (translation is null || state.DisplayMode == ReaderDisplayMode.PrimaryOnly)
        {
            if (!primaryIsHebrew)
            {
                return CreateReaderWidthContainer(
                    CreateLabeledTextRow(primaryBlock, null, primaryLabel),
                    GetSingleLanguageReaderColumnLetters(),
                    GetSelectedEnglishFontSize());
            }

            return CreateReaderWidthContainer(
                CreateLabeledTextRow(primaryBlock, null, primaryLabel),
                GetSingleLanguageReaderColumnLetters(),
                GetSelectedHebrewFontSize());
        }

        var translationBlock = CreateReaderTextBlock(
            translation.Text,
            false,
            state.HebrewMarksMode);
        var translationLabelText = FormatSegmentLabel(translation, false);
        var translationLabel = CreateSegmentLabel(translationLabelText, HorizontalAlignment.Right);
        if (state.DisplayMode is ReaderDisplayMode.SideBySide or ReaderDisplayMode.TranslationSideBySide)
        {
            primaryBlock = new Border
            {
                Padding = new Thickness(0, 0, 8, 0),
                Child = primaryBlock
            };
            translationBlock = new Border
            {
                Padding = new Thickness(8, 0, 0, 0),
                Child = translationBlock
            };
            var centerLabel = CreateSegmentLabel(
                FirstNonEmpty(primaryLabelText, translationLabelText),
                HorizontalAlignment.Center);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,44,*"),
                ColumnSpacing = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    primaryBlock,
                    centerLabel,
                    translationBlock,
                }
            };
            if (state.DisplayMode == ReaderDisplayMode.TranslationSideBySide)
            {
                Grid.SetColumn(translationBlock, 0);
                Grid.SetColumn(centerLabel, 1);
                Grid.SetColumn(primaryBlock, 2);
            }
            else
            {
                Grid.SetColumn(centerLabel, 1);
                Grid.SetColumn(translationBlock, 2);
            }

            return CreateReaderWidthContainer(
                grid,
                GetDualLanguageReaderColumnLetters(),
                Math.Max(GetSelectedHebrewFontSize(), GetSelectedEnglishFontSize()));
        }

        return CreateReaderWidthContainer(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateLabeledTextRow(primaryBlock, primaryLabel, null),
                CreateLabeledTextRow(translationBlock, null, translationLabel)
            }
        },
        GetDualLanguageReaderColumnLetters(),
        Math.Max(GetSelectedHebrewFontSize(), GetSelectedEnglishFontSize()));
    }

    private static Control CreateReaderWidthContainer(Control content, double letterCount, double fontSize)
    {
        var width = EstimateReaderWidth(letterCount, fontSize);
        return new Border
        {
            Width = width,
            MaxWidth = width,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = content
        };
    }

    private static double EstimateReaderWidth(double letterCount, double fontSize)
    {
        return letterCount * fontSize * AverageReaderCharacterWidthFactor;
    }

    private static TextBlock CreateSegmentLabel(string label, HorizontalAlignment alignment)
    {
        return new TextBlock
        {
            Text = label,
            Width = ReaderSegmentLabelWidth,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#98A2B3")),
            HorizontalAlignment = alignment,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 11, 0, 0)
        };
    }

    private static Control CreateLabeledTextRow(Control text, Control? leftLabel, Control? rightLabel)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("32,*,32"),
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                text
            }
        };

        Grid.SetColumn(text, 1);
        if (leftLabel is not null)
        {
            grid.Children.Add(leftLabel);
            Grid.SetColumn(leftLabel, 0);
        }

        if (rightLabel is not null)
        {
            grid.Children.Add(rightLabel);
            Grid.SetColumn(rightLabel, 2);
        }

        return grid;
    }

    private Control CreateReaderTextBlock(
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode,
        double? overrideFontSize = null)
    {
        var fontSize = overrideFontSize ?? (isHebrew ? GetSelectedHebrewFontSize() : GetSelectedEnglishFontSize());
        return new ReaderTextView
        {
            SourceText = text,
            IsHebrew = isHebrew,
            HebrewMarksMode = hebrewMarksMode,
            FontSize = fontSize,
            FontFamily = isHebrew
                ? new FontFamily(GetSelectedHebrewFontFamily())
                : new FontFamily(GetSelectedEnglishFontFamily()),
            EmbeddedHebrewFontFamily = new FontFamily(GetSelectedHebrewFontFamily()),
            LineHeight = fontSize + 19,
            Padding = isHebrew
                ? new Thickness(16, 8, 24, 8)
                : new Thickness(16, 8, 16, 8)
        };
    }

    private void CloseReaderTabsForBook(SefariaBookNode book)
    {
        if (_tabs is null)
        {
            return;
        }

        var title = book.Title;
        var matchingTabs = _openReaderTabs
            .Where(pair => string.Equals(pair.Value.WorkTitle, title, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var tab in matchingTabs)
        {
            CloseTab(tab);
        }
    }

    private void RefreshOpenReaderTabForBook(SefariaBookNode book)
    {
        var readerTab = _openReaderTabs.FirstOrDefault(pair => string.Equals(pair.Value.WorkTitle, book.Title, StringComparison.Ordinal));
        if (readerTab.Key is null)
        {
            return;
        }

        var state = readerTab.Value;
        var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
        state.Versions = installedVersions;
        state.HebrewTexts = installedVersions.Where(SefariaLibraryService.IsHebrew).ToList();
        state.Translations = installedVersions.Where(version => !SefariaLibraryService.IsHebrew(version)).ToList();

        var downloadedHebrewText = book.SelectedVersion is null
            ? null
            : state.HebrewTexts.FirstOrDefault(version =>
                string.Equals(version.Title, book.Title, StringComparison.Ordinal) &&
                string.Equals(version.LanguageCode, book.SelectedVersion.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.VersionTitle, book.SelectedVersion.VersionTitle, StringComparison.Ordinal));

        state.Primary = state.HebrewTexts.FirstOrDefault(version => string.Equals(version.Key, state.Primary.Key, StringComparison.Ordinal))
            ?? GetSavedHebrewText(book.Title, state.HebrewTexts)
            ?? downloadedHebrewText
            ?? state.HebrewTexts.FirstOrDefault()
            ?? installedVersions.FirstOrDefault(version => string.Equals(version.Key, state.Primary.Key, StringComparison.Ordinal))
            ?? installedVersions.FirstOrDefault()
            ?? state.Primary;
        NormalizeHebrewMarksMode(state);
        SaveSelectedHebrewText(state);

        var downloadedVersion = book.SelectedVersion is null
            ? null
            : state.Translations.FirstOrDefault(version =>
                string.Equals(version.Title, book.Title, StringComparison.Ordinal) &&
                string.Equals(version.LanguageCode, book.SelectedVersion.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.VersionTitle, book.SelectedVersion.VersionTitle, StringComparison.Ordinal));

        state.SelectedTranslation = downloadedVersion
            ?? GetSavedTranslation(book.Title, state.Translations)
            ?? state.SelectedTranslation;

        if (state.SelectedTranslation is not null && state.DisplayMode == ReaderDisplayMode.PrimaryOnly)
        {
            state.DisplayMode = GetSavedDisplayMode(book.Title, true);
        }

        RenderReaderContent(state);

        if (ReferenceEquals(_centerTabs?.SelectedItem, readerTab.Key))
        {
            UpdateReaderTools();
        }
    }
}
