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
    private void InitializeNavigationItems()
    {
        if (_installedBooksTree is null)
        {
            return;
        }

        _installedBooksTree.ItemsSource = null;
    }

    private void LoadLayoutState()
    {
        var state = ReadState();

        if (state is null)
        {
            EnsureDefaultTabs();
            RefreshSavedSearchesList();
            ApplyLeftPanelState(false, DefaultExpandedPanelWidth);
            ApplyRightPanelState(false, DefaultExpandedPanelWidth);
            return;
        }

        _savedAdvancedSearches.Clear();
        foreach (var savedSearch in state.SavedAdvancedSearches)
        {
            _savedAdvancedSearches.Add(savedSearch);
        }
        _savedSearchTitlesHebrew = state.SavedSearchTitlesHebrew;
        _advancedSearchAutosave = state.AdvancedSearchAutosave;
        _advancedSearchScopeTitleDisplay = state.AdvancedSearchScopeTitleDisplay;
        _advancedSearchExpandedScopeKeys.Clear();
        foreach (var key in state.AdvancedSearchExpandedScopeKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            _advancedSearchExpandedScopeKeys.Add(key);
        }
        _isDictionaryDocked = state.DictionaryDocked;
        _dictionaryCurrentWord = state.DictionaryCurrentWord ?? string.Empty;
        _dictionaryCurrentReference = state.DictionaryCurrentReference ?? string.Empty;
        _dictionaryPopupLeft = state.DictionaryPopupLeft;
        _dictionaryPopupTop = state.DictionaryPopupTop;
        RefreshSavedSearchesList();
        RefreshDictionarySurface();
        ConstrainDictionaryPopupPosition();

        _leftExpandedWidth = Math.Max(CollapsedPanelWidth, state.LeftExpandedWidth);
        _rightExpandedWidth = Math.Max(CollapsedPanelWidth, state.RightExpandedWidth);

        ApplyLeftPanelState(state.LeftCollapsed, _leftExpandedWidth);
        ApplyRightPanelState(state.RightCollapsed, _rightExpandedWidth);

        ApplyTabsFromState(state);

        if (_isDictionaryDocked)
        {
            UpdateReaderTools();
            RefreshDictionarySurface();
        }
    }

    private void ApplyTabsFromState(LayoutState state)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        ClearTabContents();
        _openReaderTabs.Clear();
        _tabs.Clear();

        var savedTabs = state.Tabs.Count > 0
            ? state.Tabs
            : state.OpenTabs
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new SavedTabState { Kind = SavedTabKind.Utility, Title = t })
                .ToList();

        if (savedTabs.Count == 0)
        {
            EnsureDefaultTabs();
            return;
        }

        var selectedIndex = savedTabs.Count == 0
            ? -1
            : Math.Clamp(state.SelectedTabIndex, 0, savedTabs.Count - 1);
        for (var i = 0; i < savedTabs.Count; i++)
        {
            var savedTab = savedTabs[i];
            try
            {
                if (savedTab.Kind == SavedTabKind.Reader)
                {
                    RestoreReaderTab(
                        savedTab,
                        selectAfterOpen: i == selectedIndex,
                        renderImmediately: true);
                }
                else if (IsKnownUtilityTabTitle(savedTab.Title))
                {
                    _tabs.Add(CreateTab(savedTab.Title));
                }
            }
            catch
            {
                // Skip malformed or obsolete saved tabs so one bad entry cannot block startup.
            }
        }

        _centerTabs.SelectedIndex = _tabs.Count == 0
            ? -1
            : Math.Clamp(state.SelectedTabIndex, 0, _tabs.Count - 1);
    }

    private void EnsureDefaultTabs()
    {
        // The main workspace is allowed to start empty.
    }

    private TabItem CreateTab(string title, Control? content = null)
    {
        var tab = new TabItem();

        var tabIcon = new TextBlock
        {
            Text = "\u25c9",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#3F4A56")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#2F3843")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var closeButton = new Button
        {
            Content = new TextBlock
            {
                Text = "\u00d7",
                FontSize = 13,
                LineHeight = 13,
                Foreground = new SolidColorBrush(Color.Parse("#2F3843")),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false
        };

        closeButton.Classes.Add("tab-close-button");
        closeButton.Click += (_, _) => CloseTab(tab);

        var separator = new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#7FA3EC")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerLayout = new Grid
        {
            Width = 236,
            Height = 34,
            ColumnDefinitions = new ColumnDefinitions("18,*,24,10"),
            Children =
            {
                tabIcon,
                titleBlock,
                closeButton,
                separator
            }
        };

        Grid.SetColumn(titleBlock, 1);
        Grid.SetColumn(closeButton, 2);
        Grid.SetColumn(separator, 3);

        var header = new Border
        {
            Padding = new Thickness(10, 0, 6, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = headerLayout
        };

        DragDrop.SetAllowDrop(header, true);

        header.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(tab.Tag as string ?? string.Empty));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        };

        DragDrop.AddDragOverHandler(header, (_, e) =>
        {
            if (e.DataTransfer.Contains(DataFormat.Text))
            {
                e.DragEffects = DragDropEffects.Move;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        });

        DragDrop.AddDropHandler(header, (_, e) =>
        {
            if (_tabs is null)
            {
                return;
            }

            var sourceTitle = e.DataTransfer.TryGetValue(DataFormat.Text);
            if (string.IsNullOrWhiteSpace(sourceTitle))
            {
                return;
            }

            var source = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, sourceTitle, StringComparison.Ordinal));

            if (source is null || source == tab)
            {
                return;
            }

            var sourceIndex = _tabs.IndexOf(source);
            var targetIndex = _tabs.IndexOf(tab);

            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }

            _tabs.Move(sourceIndex, targetIndex);
            if (_centerTabs is not null)
            {
                _centerTabs.SelectedItem = source;
            }
        });

        tab.Header = header;

        tab.Tag = title;
        RegisterTabContent(tab, content ?? CreateTabContent(title));

        return tab;
    }

    private void RegisterTabContent(TabItem tab, Control content)
    {
        _tabContents[tab] = content;
        content.IsVisible = ReferenceEquals(tab, _centerTabs?.SelectedItem);
        _centerTabContentHost?.Children.Add(content);
    }

    private void ReplaceTabContent(TabItem tab, Control content)
    {
        RemoveTabContent(tab);
        RegisterTabContent(tab, content);
        UpdateSelectedTabContentVisibility();
    }

    private void RemoveTabContent(TabItem tab)
    {
        if (!_tabContents.Remove(tab, out var content))
        {
            return;
        }

        _centerTabContentHost?.Children.Remove(content);
    }

    private void ClearTabContents()
    {
        _tabContents.Clear();
        _centerTabContentHost?.Children.Clear();
    }

    private void UpdateSelectedTabContentVisibility()
    {
        var selectedTab = _centerTabs?.SelectedItem as TabItem;
        foreach (var (tab, content) in _tabContents)
        {
            content.IsVisible = ReferenceEquals(tab, selectedTab);
        }
    }

    private Control CreateTabContent(string title)
    {
        if (string.Equals(title, LibraryManagerTabTitle, StringComparison.Ordinal))
        {
            return CreateLibraryManagerView();
        }

        if (string.Equals(title, SettingsTabTitle, StringComparison.Ordinal))
        {
            return CreateSettingsView();
        }

        if (string.Equals(title, AdvancedSearchTabTitle, StringComparison.Ordinal))
        {
            return CreateAdvancedSearchView();
        }

        if (string.Equals(title, DictionaryTabTitle, StringComparison.Ordinal))
        {
            return CreateDictionaryView();
        }

        throw new InvalidOperationException($"Unknown utility tab: {title}");
    }

    private void OpenLibraryManagerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(LibraryManagerTabTitle);
    }

    private void OpenSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(SettingsTabTitle);
    }

    private void ToggleInstalledBooksSearchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleSearchBox(_leftPanelSearchBox);
        if (_leftPanelSearchBox?.IsVisible == false && _leftPanelSearchSuggestionsContainer is not null)
            _leftPanelSearchSuggestionsContainer.IsVisible = false;
    }

    private void ToggleLibraryManagerSearchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ToggleSearchBox(_libraryManagerSearchBox);
        if (_libraryManagerSearchBox?.IsVisible == false && _librarySearchSuggestionsContainer is not null)
            _librarySearchSuggestionsContainer.IsVisible = false;
    }

    private static void ToggleSearchBox(TextBox? searchBox)
    {
        if (searchBox is null)
        {
            return;
        }

        searchBox.IsVisible = !searchBox.IsVisible;
        if (searchBox.IsVisible)
        {
            searchBox.Focus();
            searchBox.SelectAll();
            return;
        }

        searchBox.Text = string.Empty;
    }

    private void OpenAdvancedSearchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(AdvancedSearchTabTitle);
    }

    private void OpenDictionaryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(DictionaryTabTitle);
    }

    private void OpenOrSelectTab(string title)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, title, StringComparison.Ordinal));
        if (existing is not null)
        {
            _centerTabs.SelectedItem = existing;
            return;
        }

        var tab = CreateTab(title);
        _tabs.Add(tab);
        _centerTabs.SelectedItem = tab;
    }

    /// <summary>
    /// Ctrl+PageDown / Ctrl+PageUp cycle open center tabs (same convention as browsers and many editors).
    /// </summary>
    private bool TryHandleCenterTabShortcut(KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Alt) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return false;
        }

        var direction = e.Key switch
        {
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0
        };
        if (direction == 0)
        {
            return false;
        }

        return SelectAdjacentCenterTab(direction);
    }

    private bool SelectAdjacentCenterTab(int direction)
    {
        if (_tabs is null || _centerTabs is null || _tabs.Count < 2)
        {
            return false;
        }

        var currentIndex = _centerTabs.SelectedItem is TabItem selected
            ? _tabs.IndexOf(selected)
            : _centerTabs.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + direction) % _tabs.Count;
        if (nextIndex < 0)
        {
            nextIndex += _tabs.Count;
        }

        if (nextIndex == currentIndex)
        {
            return false;
        }

        _centerTabs.SelectedItem = _tabs[nextIndex];
        return true;
    }

    private void CloseTab(TabItem tab)
    {
        if (_tabs is null)
        {
            return;
        }

        SaveReaderTabPosition(tab);
        _openReaderTabs.Remove(tab);
        RemoveTabContent(tab);
        _tabs.Remove(tab);
        if (_centerTabs is not null)
        {
            _centerTabs.SelectedIndex = _tabs.Count == 0 ? -1 : Math.Max(0, _tabs.Count - 1);
        }
        UpdateTabHeaderStates();
    }

    private void SaveReaderTabPosition(TabItem tab)
    {
        if (!_openReaderTabs.TryGetValue(tab, out var readerTab))
        {
            return;
        }

        if (readerTab.ReaderList?.Scroll is not null)
        {
            _sefariaLibrary.SaveReadingPosition(readerTab.Primary, readerTab.ReaderList.Scroll.Offset.Y);
            return;
        }

        _sefariaLibrary.SaveReadingPosition(readerTab.Primary, readerTab.ReaderWebScrollOffset);
    }

    private void ApplyLeftPanelState(bool collapsed, double expandedWidth)
    {
        if (_leftColumn is null ||
            _leftSplitter is null ||
            _leftPanelBody is null ||
            _leftPanelTitle is null ||
            _leftPanelSearchButton is null ||
            _leftPanelSearchBox is null)
        {
            return;
        }

        _leftCollapsed = collapsed;
        _leftExpandedWidth = Math.Max(CollapsedPanelWidth, expandedWidth);

        _leftColumn.MinWidth = CollapsedPanelWidth;
        _leftColumn.Width = new GridLength(_leftCollapsed ? CollapsedPanelWidth : _leftExpandedWidth, GridUnitType.Pixel);
        _leftSplitter.IsVisible = !_leftCollapsed;

        _leftPanelTitle.IsVisible = !_leftCollapsed;
        _leftPanelSearchButton.IsVisible = !_leftCollapsed;
        if (_leftCollapsed)
        {
            _leftPanelSearchBox.IsVisible = false;
            _leftPanelSearchBox.Text = string.Empty;
        }
        _leftPanelBody.IsVisible = !_leftCollapsed;
    }

    private void ApplyRightPanelState(bool collapsed, double expandedWidth)
    {
        if (_rightColumn is null || _rightSplitter is null || _rightPanelBody is null || _rightPanelTitle is null)
        {
            return;
        }

        _rightCollapsed = collapsed;
        _rightExpandedWidth = Math.Max(CollapsedPanelWidth, expandedWidth);

        _rightColumn.MinWidth = CollapsedPanelWidth;
        _rightColumn.Width = new GridLength(_rightCollapsed ? CollapsedPanelWidth : _rightExpandedWidth, GridUnitType.Pixel);
        _rightSplitter.IsVisible = !_rightCollapsed;

        _rightPanelTitle.IsVisible = !_rightCollapsed;
        _rightPanelBody.IsVisible = !_rightCollapsed;
    }

    private void ToggleLeftPanel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_leftColumn is null)
        {
            return;
        }

        if (!_leftCollapsed)
        {
            _leftExpandedWidth = Math.Max(CollapsedPanelWidth, _leftColumn.Width.Value);
        }

        ApplyLeftPanelState(!_leftCollapsed, _leftExpandedWidth);
    }

    private void ToggleRightPanel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_rightColumn is null)
        {
            return;
        }

        if (!_rightCollapsed)
        {
            _rightExpandedWidth = Math.Max(CollapsedPanelWidth, _rightColumn.Width.Value);
        }

        ApplyRightPanelState(!_rightCollapsed, _rightExpandedWidth);
    }

    private void SaveLayoutState()
    {
        if (!_hasLoadedLayoutState)
        {
            return;
        }

        if (_leftColumn is null || _rightColumn is null || _tabs is null || _centerTabs is null)
        {
            return;
        }

        foreach (var tab in _openReaderTabs.Keys.ToList())
        {
            SaveReaderTabPosition(tab);
        }

        if (!_leftCollapsed)
        {
            _leftExpandedWidth = Math.Max(CollapsedPanelWidth, _leftColumn.Width.Value);
        }

        if (!_rightCollapsed)
        {
            _rightExpandedWidth = Math.Max(CollapsedPanelWidth, _rightColumn.Width.Value);
        }

        var state = new LayoutState
        {
            LeftCollapsed = _leftCollapsed,
            RightCollapsed = _rightCollapsed,
            LeftExpandedWidth = _leftExpandedWidth,
            RightExpandedWidth = _rightExpandedWidth,
            Tabs = _tabs
                .Select(CreateSavedTabState)
                .Where(savedTab => savedTab is not null)
                .Select(savedTab => savedTab!)
            .ToList(),
            SavedAdvancedSearches = _savedAdvancedSearches.ToList(),
            SavedSearchTitlesHebrew = _savedSearchTitlesHebrew,
            AdvancedSearchAutosave = _advancedSearchAutosave,
            AdvancedSearchScopeTitleDisplay = _advancedSearchScopeTitleDisplay,
            AdvancedSearchExpandedScopeKeys = _advancedSearchExpandedScopeKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            DictionaryDocked = _isDictionaryDocked,
            DictionaryCurrentWord = _dictionaryCurrentWord,
            DictionaryCurrentReference = _dictionaryCurrentReference,
            DictionaryPopupLeft = _dictionaryPopupLeft,
            DictionaryPopupTop = _dictionaryPopupTop,
            SelectedTabIndex = _tabs.Count == 0 ? -1 : Math.Clamp(_centerTabs.SelectedIndex, 0, _tabs.Count - 1)
        };
        state.OpenTabs = state.Tabs
            .Where(tab => tab.Kind == SavedTabKind.Utility && !string.IsNullOrWhiteSpace(tab.Title))
            .Select(tab => tab.Title)
            .ToList();

        var folder = GetStateFolder();
        Directory.CreateDirectory(folder);
        var filePath = GetStateFilePath();
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    private static LayoutState? ReadState()
    {
        var filePath = GetStateFilePath();
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<LayoutState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStateFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Stndr");
    }

    private static string GetStateFilePath()
    {
        return Path.Combine(GetStateFolder(), "layout-state.json");
    }

    private SavedTabState? CreateSavedTabState(TabItem tab)
    {
        if (_openReaderTabs.TryGetValue(tab, out var readerState))
        {
            return new SavedTabState
            {
                Kind = SavedTabKind.Reader,
                Title = tab.Tag as string ?? readerState.WorkTitle,
                WorkTitle = readerState.WorkTitle,
                PrimaryKey = readerState.Primary.Key,
                SelectedTranslationKey = readerState.SelectedTranslation?.Key,
                DisplayMode = readerState.DisplayMode,
                HebrewMarksMode = readerState.HebrewMarksMode,
                IsNavigationExpanded = readerState.IsNavigationExpanded,
                ExpandedNavigationTopicKeys = readerState.ExpandedNavigationTopics
                    .Where(pair => pair.Value)
                    .Select(pair => pair.Key)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList(),
                NavigationJumpQuery = readerState.NavigationJumpQuery,
                NavigationTopicsAllExpanded = readerState.NavigationTopicsAllExpanded,
                IsDisplayExpanded = readerState.IsDisplayExpanded,
                IsSedrotExpanded = readerState.IsSedrotExpanded,
                IsCommentariesExpanded = readerState.IsCommentariesExpanded,
                IsLinksExpanded = readerState.IsLinksExpanded,
                IsLicensesExpanded = readerState.IsLicensesExpanded,
                IsTextsExpanded = readerState.IsTextsExpanded,
                ShowAliyot = readerState.ShowAliyot,
                IsSedraContentOpen = readerState.IsSedraContentOpen,
                SelectedSedraKey = readerState.SelectedSedraKey,
                SelectedCommentaryRef = readerState.SelectedCommentaryRef,
                SelectedLinkCategories = readerState.SelectedLinkCategories
                    .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                IsCommentaryContentOpen = readerState.IsCommentaryContentOpen,
                SelectedCommentarySourceKey = readerState.SelectedCommentarySourceKey,
                SelectedCommentarySourceTitleEnglish = readerState.SelectedCommentarySourceTitleEnglish,
                SelectedCommentarySourceTitleHebrew = readerState.SelectedCommentarySourceTitleHebrew,
                CommentaryLanguage = readerState.CommentaryLanguage,
                IsCommentarySplitOpen = readerState.IsCommentarySplitOpen,
                ScrollOffset = readerState.ReaderWebView is not null
                    ? readerState.ReaderWebScrollOffset
                    : readerState.ReaderList?.Scroll?.Offset.Y ?? readerState.ReaderWebScrollOffset
            };
        }

        var title = tab.Tag as string ?? "Tab";
        if (!IsKnownUtilityTabTitle(title))
        {
            return null;
        }

        return new SavedTabState
        {
            Kind = SavedTabKind.Utility,
            Title = title
        };
    }

    private static bool IsKnownUtilityTabTitle(string? title)
    {
        return string.Equals(title, LibraryManagerTabTitle, StringComparison.Ordinal) ||
            string.Equals(title, AdvancedSearchTabTitle, StringComparison.Ordinal) ||
            string.Equals(title, DictionaryTabTitle, StringComparison.Ordinal) ||
            string.Equals(title, SettingsTabTitle, StringComparison.Ordinal);
    }
}
