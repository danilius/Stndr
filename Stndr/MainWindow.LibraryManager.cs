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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Stndr;


public partial class MainWindow
{
    private const double LibraryVersionDropdownWidth = 400;

    private sealed record CategoryBulkTarget(
        string ActionTitle,
        string Description,
        List<SefariaBookNode> Books);

    private sealed class CategoryVersionChoice
    {
        public string LanguageCode { get; init; } = string.Empty;
        public string VersionTitle { get; init; } = string.Empty;
        public string DisplayText { get; init; } = string.Empty;
        public bool IsAutomatic { get; init; }

        public override string ToString()
        {
            return DisplayText;
        }
    }

    private sealed class SingleBookVersionChoice
    {
        public SefariaVersionOption? Version { get; init; }
        public string DisplayText { get; init; } = string.Empty;
        public bool IsNone { get; init; }

        public override string ToString()
        {
            return DisplayText;
        }
    }

    private static void ApplyVersionDropdownItemTooltips(ComboBox comboBox)
    {
        var itemStyle = new Style(selector => selector.OfType<ComboBoxItem>());
        itemStyle.Setters.Add(new Setter(ToolTip.TipProperty, new Avalonia.Data.Binding(".")));
        itemStyle.Setters.Add(new Setter(Layoutable.MaxWidthProperty, LibraryVersionDropdownWidth));
        comboBox.Styles.Add(itemStyle);

        var textStyle = new Style(selector => selector.OfType<ComboBoxItem>().Descendant().OfType<TextBlock>());
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        comboBox.Styles.Add(textStyle);
    }

    private static void SetComboBoxSelectionToolTip(ComboBox? comboBox)
    {
        if (comboBox is null)
        {
            return;
        }

        var selectedText = comboBox.SelectedItem switch
        {
            SingleBookVersionChoice singleChoice => singleChoice.DisplayText,
            CategoryVersionChoice categoryChoice => categoryChoice.DisplayText,
            _ => comboBox.SelectedItem?.ToString() ?? string.Empty
        };

        ToolTip.SetTip(comboBox, string.IsNullOrWhiteSpace(selectedText) ? null : selectedText);
    }

    private Control CreateLibraryManagerView()
    {
        _libraryTree = new TreeView
        {
            Margin = new Thickness(8, 0, 8, 8)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_libraryTree, ScrollBarVisibility.Auto);
        ScrollViewer.SetBringIntoViewOnFocusChange(_libraryTree, false);
        _libraryTree.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true);
        _libraryTree.AttachedToVisualTree += (_, _) =>
        {
            _libraryTreeScrollViewer = _libraryTree.FindDescendantOfType<ScrollViewer>();
            if (_libraryTreeScrollViewer is not null)
            {
                _libraryTreeScrollViewer.BringIntoViewOnFocusChange = false;
            }
        };
        _libraryTree.SelectionChanged += OnLibraryTreeSelectionChanged;

        var leftPane = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new TextBlock
                {
                    Text = "Sefaria Library",
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center
                },
                _libraryTree
            }
        };
        Grid.SetRow(_libraryTree, 1);

        _libraryTitle = new TextBlock
        {
            Text = "Select a book",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        };
        _libraryHebrewTitle = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#667085"))
        };
        _libraryDescription = new TextBlock
        {
            Text = "Choose a book from the library tree to see its description.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _libraryStatus = new TextBlock
        {
            Text = _sefariaLibrary.IsConfigured
                ? $"Data folder: {_sefariaLibrary.StorageRootFolder}"
                : "No data folder set. Choose one in the Settings tab to download and manage texts.",
            TextWrapping = TextWrapping.Wrap
        };
        _libraryBookVersionLabel = new TextBlock
        {
            Text = "Hebrew text",
            FontWeight = FontWeight.SemiBold
        };
        _libraryVersionBox = new ComboBox
        {
            Width = LibraryVersionDropdownWidth,
            MaxWidth = LibraryVersionDropdownWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false
        };
        ApplyVersionDropdownItemTooltips(_libraryVersionBox);
        _libraryVersionBox.SelectionChanged += OnLibraryVersionChanged;
        _libraryTranslationVersionBox = new ComboBox
        {
            Width = LibraryVersionDropdownWidth,
            MaxWidth = LibraryVersionDropdownWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false
        };
        ApplyVersionDropdownItemTooltips(_libraryTranslationVersionBox);
        _libraryTranslationVersionBox.SelectionChanged += OnLibraryTranslationVersionChanged;
        _librarySingleHebrewActionButton = new Button
        {
            Content = "Download",
            IsEnabled = false,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _librarySingleHebrewActionButton.Click += async (_, _) => await DownloadOrDeleteSelectedHebrewAsync();
        _librarySingleTranslationActionButton = new Button
        {
            Content = "Download",
            IsEnabled = false,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _librarySingleTranslationActionButton.Click += async (_, _) => await DownloadOrDeleteSelectedTranslationAsync();
        _libraryBookVersionPanel = new StackPanel
        {
            Spacing = 8,
            IsVisible = true,
            Children =
            {
                _libraryBookVersionLabel,
                _libraryVersionBox,
                _librarySingleHebrewActionButton,
                new TextBlock
                {
                    Text = "Translation",
                    FontWeight = FontWeight.SemiBold
                },
                _libraryTranslationVersionBox,
                _librarySingleTranslationActionButton
            }
        };
        _libraryCategoryHebrewVersionBox = new ComboBox
        {
            Width = LibraryVersionDropdownWidth,
            MaxWidth = LibraryVersionDropdownWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false
        };
        ApplyVersionDropdownItemTooltips(_libraryCategoryHebrewVersionBox);
        _libraryCategoryHebrewVersionBox.SelectionChanged += OnLibraryCategoryVersionChanged;
        _libraryCategoryEnglishVersionBox = new ComboBox
        {
            Width = LibraryVersionDropdownWidth,
            MaxWidth = LibraryVersionDropdownWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false
        };
        ApplyVersionDropdownItemTooltips(_libraryCategoryEnglishVersionBox);
        _libraryCategoryEnglishVersionBox.SelectionChanged += OnLibraryCategoryVersionChanged;
        _libraryCategoryHebrewActionButton = new Button
        {
            Content = "Download",
            IsEnabled = false,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _libraryCategoryHebrewActionButton.Click += async (_, _) => await DownloadOrDeleteSelectedHebrewAsync();
        _libraryCategoryTranslationActionButton = new Button
        {
            Content = "Download",
            IsEnabled = false,
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _libraryCategoryTranslationActionButton.Click += async (_, _) => await DownloadOrDeleteSelectedTranslationAsync();
        _libraryCategoryVersionPanel = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                new TextBlock
                {
                    Text = "Hebrew version",
                    FontWeight = FontWeight.SemiBold
                },
                _libraryCategoryHebrewVersionBox,
                _libraryCategoryHebrewActionButton,
                new TextBlock
                {
                    Text = "Translation",
                    FontWeight = FontWeight.SemiBold
                },
                _libraryCategoryEnglishVersionBox,
                _libraryCategoryTranslationActionButton
            }
        };

        _libraryProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            IsVisible = false
        };

        _libraryCancelButton = new Button
        {
            Content = "Cancel",
            IsEnabled = false,
            MinWidth = 90
        };
        _libraryCancelButton.Click += (_, _) => _sefariaDownloadCts?.Cancel();

        var detailsPane = new Border
        {
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#22000000")),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _libraryTitle,
                    _libraryHebrewTitle,
                    new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.Parse("#E4E7EC")),
                        Margin = new Thickness(0, 2, 0, 0)
                    },
                    _libraryDescription,
                    _libraryBookVersionPanel,
                    _libraryCategoryVersionPanel,
                    _libraryProgress,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            _libraryCancelButton
                        }
                    }
                }
            }
        };

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("360,5,*"),
            Background = Brushes.White,
            Children =
            {
                leftPane,
                new GridSplitter
                {
                    Width = 5,
                    Background = Brushes.Transparent,
                    ResizeDirection = GridResizeDirection.Columns,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext
                },
                detailsPane
            }
        };
        Grid.SetColumn(layout.Children[1], 1);
        Grid.SetColumn(detailsPane, 2);

        _ = LoadSefariaLibraryAsync();

        return layout;
    }

    private async Task LoadSefariaLibraryAsync()
    {
        if (_libraryTree is null || _libraryStatus is null)
        {
            return;
        }

        if (!_sefariaLibrary.IsConfigured)
        {
            _libraryTree.ItemsSource = null;
            _selectedSefariaBook = null;
            _selectedSefariaCategory = null;
            UpdateLibraryDetails();
            _libraryStatus.Text = "No data folder set. Choose one in the Settings tab to download and manage texts.";
            return;
        }

        _libraryStatus.Text = "Loading Sefaria index...";
        _libraryTree.ItemsSource = null;
        _selectedSefariaBook = null;
        _selectedSefariaCategory = null;
        UpdateLibraryDetails();

        try
        {
            _sefariaRoot = await _sefariaLibrary.LoadLibraryAsync(CancellationToken.None);
            _libraryStatus.Text = "Loading Sefaria versions...";
            await PopulateLibraryBookVersionsAsync(_sefariaRoot);
            _libraryTree.ItemsSource = _sefariaRoot.Contents
                .OrderBy(n => n.Order)
                .Select(CreateLibraryTreeItem)
                .ToList();
            _libraryStatus.Text = $"Loaded from {_sefariaLibrary.StorageRootFolder}";
        }
        catch (Exception ex)
        {
            _libraryStatus.Text = $"Failed to load Sefaria index: {ex.Message}";
        }
    }

    private async Task PopulateLibraryBookVersionsAsync(SefariaCategoryNode root)
    {
        var versionsLookup = await _sefariaLibrary.GetAllAvailableVersionsLookupAsync(CancellationToken.None);
        foreach (var book in EnumerateBooks(root))
        {
            if (versionsLookup.TryGetValue(book.Title, out var versions))
            {
                book.Versions = versions;
            }
            else
            {
                book.Versions = new List<SefariaVersionOption>();
            }

            book.SelectedVersion = book.Versions.FirstOrDefault(IsHebrewCategoryVersion) ?? book.Versions.FirstOrDefault();
            book.IsVersionsLoaded = true;
            book.IsLoadingVersions = false;
        }
    }

    private TreeViewItem CreateLibraryTreeItem(SefariaNode node)
    {
        var item = new TreeViewItem
        {
            Header = CreateLibraryTreeHeader(node),
            DataContext = node,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        item.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true);

        item.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (e.Source is not Visual source ||
                    !ReferenceEquals(source.FindAncestorOfType<TreeViewItem>(true), item))
                {
                    return;
                }

                if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                var horizontalOffset = _libraryTreeScrollViewer?.Offset.X ?? 0;
                SelectLibraryTreeItem(item);
                if (node is SefariaCategoryNode)
                {
                    item.IsExpanded = !item.IsExpanded;
                }

                e.Handled = true;
                RestoreLibraryTreeHorizontalOffset(horizontalOffset);
            },
            RoutingStrategies.Tunnel,
            true);

        if (node is SefariaCategoryNode category)
        {
            item.ItemsSource = category.Contents
                .OrderBy(n => n.Order)
                .Select(CreateLibraryTreeItem)
                .ToList();
        }

        return item;
    }

    private void RefreshLibraryManagerHeaders()
    {
        if (_libraryTree is null || _sefariaRoot is null)
        {
            return;
        }

        _libraryTree.ItemsSource = _sefariaRoot.Contents
            .OrderBy(n => n.Order)
            .Select(CreateLibraryTreeItem)
            .ToList();
    }

    private void SelectLibraryTreeItem(TreeViewItem item)
    {
        if (!ReferenceEquals(_selectedLibraryTreeItem, item))
        {
            if (_selectedLibraryTreeItem is not null)
            {
                _selectedLibraryTreeItem.IsSelected = false;
            }

            _selectedLibraryTreeItem = item;
        }

        item.IsSelected = true;
    }

    private Control CreateLibraryTreeHeader(SefariaNode node)
    {
        var title = node switch
        {
            SefariaCategoryNode category => FormatTitle(category.DisplayTitle, category.HebrewCategory),
            SefariaBookNode book => FormatTitle(book.DisplayTitle, book.HebrewTitle),
            _ => "Item"
        };

        return new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 3),
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private async void OnLibraryTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var selectionVersion = Interlocked.Increment(ref _librarySelectionVersion);
            var horizontalOffset = _libraryTreeScrollViewer?.Offset.X ?? 0;
            var item = e.AddedItems.Count > 0 ? e.AddedItems[0] as TreeViewItem : null;
            item ??= _selectedLibraryTreeItem;
            _selectedSefariaBook = item?.DataContext as SefariaBookNode;
            _selectedSefariaCategory = item?.DataContext as SefariaCategoryNode;
            UpdateLibraryDetails();
            RestoreLibraryTreeHorizontalOffset(horizontalOffset);

            if (_selectedSefariaBook is not null)
            {
                var selectedBook = _selectedSefariaBook;
                _cachedCategoryProgress = null;
                CancelCategoryInstallProgressRefresh();
                await EnsureVersionsLoadedAsync(selectedBook);
                if (selectionVersion != Volatile.Read(ref _librarySelectionVersion) ||
                    !ReferenceEquals(_selectedSefariaBook, selectedBook))
                {
                    return;
                }
                UpdateSelectedBookDownloadedState();
                UpdateLibraryDetails();
                RestoreLibraryTreeHorizontalOffset(horizontalOffset);
                return;
            }

            if (_selectedSefariaCategory is not null && item is not null)
            {
                var selectedCategory = _selectedSefariaCategory;
                CancelCategoryInstallProgressRefresh();
                _cachedCategoryProgress = null;
                await EnsureCategoryVersionChoicesLoadedAsync(selectedCategory, item);
                if (selectionVersion != Volatile.Read(ref _librarySelectionVersion) ||
                    !ReferenceEquals(_selectedSefariaCategory, selectedCategory))
                {
                    return;
                }
                UpdateLibraryDetails();
                RestoreLibraryTreeHorizontalOffset(horizontalOffset);
            }
        }
        catch (Exception ex)
        {
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = $"Failed to load selection: {ex.Message}";
            }
        }
    }

    private void RestoreLibraryTreeHorizontalOffset(double horizontalOffset)
    {
        _ = RestoreLibraryTreeHorizontalOffsetAsync(horizontalOffset);
    }

    private async Task RestoreLibraryTreeHorizontalOffsetAsync(double horizontalOffset)
    {
        for (var i = 0; i < 5; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_libraryTreeScrollViewer is null)
                {
                    return;
                }

                var offset = _libraryTreeScrollViewer.Offset;
                _libraryTreeScrollViewer.Offset = new Vector(horizontalOffset, offset.Y);
            });

            await Task.Delay(25);
        }
    }

    private async Task EnsureVersionsLoadedAsync(SefariaBookNode book)
    {
        if (book.IsVersionsLoaded || book.IsLoadingVersions)
        {
            return;
        }

        book.IsLoadingVersions = true;
        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = $"Loading versions for {book.Title}...";
        }

        try
        {
            if (_sefariaLibrary.TryGetCachedAvailableVersions(book.Title, out var cachedVersions))
            {
                book.Versions = cachedVersions;
            }
            else
            {
                book.Versions = await _sefariaLibrary.GetAvailableVersionsAsync(book.Title, CancellationToken.None);
            }
            book.SelectedVersion = book.Versions.FirstOrDefault();
            book.IsVersionsLoaded = true;
            book.IsLoadingVersions = false;
        }
        catch (Exception ex)
        {
            book.IsLoadingVersions = false;
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = $"Could not load versions for {book.Title}: {ex.Message}";
            }
        }
    }

    private async Task EnsureCategoryVersionChoicesLoadedAsync(SefariaCategoryNode category, TreeViewItem item)
    {
        try
        {
            if (_libraryCategoryHebrewVersionBox is null || _libraryCategoryEnglishVersionBox is null)
            {
                return;
            }

            if (!TryCreateCategoryBulkTarget(category, item, out var target))
            {
                _libraryCategoryHebrewVersionBox.ItemsSource = null;
                _libraryCategoryEnglishVersionBox.ItemsSource = null;
                _libraryCategoryHebrewVersionBox.IsEnabled = false;
                _libraryCategoryEnglishVersionBox.IsEnabled = false;
                return;
            }

            var representativeBook = target.Books
                .OrderBy(book => book.Order)
                .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (representativeBook is null)
            {
                _libraryCategoryHebrewVersionBox.ItemsSource = null;
                _libraryCategoryEnglishVersionBox.ItemsSource = null;
                _libraryCategoryHebrewVersionBox.IsEnabled = false;
                _libraryCategoryEnglishVersionBox.IsEnabled = false;
                return;
            }

            if (!representativeBook.IsVersionsLoaded)
            {
                var representativeVersions = await _sefariaLibrary.GetAvailableVersionsAsync(representativeBook.Title, CancellationToken.None);
                representativeBook.Versions = representativeVersions;
                representativeBook.IsVersionsLoaded = true;
            }
            _libraryCategoryHebrewVersionBox.ItemsSource = BuildCategoryVersionChoices(new List<SefariaBookNode> { representativeBook }, true, "Default Hebrew (category)");
            _libraryCategoryEnglishVersionBox.ItemsSource = BuildCategoryVersionChoices(new List<SefariaBookNode> { representativeBook }, false, "Default translation (category)");
            _libraryCategoryHebrewVersionBox.SelectedIndex = 0;
            _libraryCategoryEnglishVersionBox.SelectedIndex = 0;
            _libraryCategoryHebrewVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryCategoryEnglishVersionBox.IsEnabled = !_isSefariaDownloading;
            ScheduleCategoryInstallProgressRefresh(target);
        }
        catch (Exception ex)
        {
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = $"Failed to load category versions: {ex.Message}";
            }
        }
    }

    private static List<CategoryVersionChoice> BuildCategoryVersionChoices(
        List<SefariaBookNode> books,
        bool hebrewChoices,
        string defaultLabel)
    {
        var choices = new List<CategoryVersionChoice>
        {
            new()
            {
                LanguageCode = hebrewChoices ? "he" : "translation",
                VersionTitle = string.Empty,
                DisplayText = defaultLabel,
                IsAutomatic = true
            }
        };

        var titles = books
            .SelectMany(book => book.Versions)
            .Where(version => hebrewChoices
                ? IsHebrewCategoryVersion(version)
                : !IsHebrewCategoryVersion(version))
            .Select(version => version.VersionTitle)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(title => title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        choices.AddRange(titles.Select(title => new CategoryVersionChoice
        {
            LanguageCode = hebrewChoices ? "he" : "translation",
            VersionTitle = title,
            DisplayText = title
        }));

        return choices;
    }

    private static bool IsHebrewCategoryVersion(SefariaVersionOption version)
    {
        if (!string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LooksLikeTranslationVersionTitle(version.VersionTitle))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(version.LanguageFamilyName))
        {
            return true;
        }

        return string.Equals(version.LanguageFamilyName, "hebrew", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTranslationVersionTitle(string? versionTitle)
    {
        if (string.IsNullOrWhiteSpace(versionTitle))
        {
            return false;
        }

        var normalized = versionTitle.ToLowerInvariant();
        if (normalized.Contains("translation") ||
            normalized.Contains("translated") ||
            normalized.Contains("trans.") ||
            normalized.Contains(" trans "))
        {
            return true;
        }

        var start = normalized.LastIndexOf("[", StringComparison.Ordinal);
        var end = normalized.LastIndexOf("]", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var code = normalized[(start + 1)..end].Trim();
            if (!string.IsNullOrWhiteSpace(code) &&
                !string.Equals(code, "he", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> GetCategoryPath(TreeViewItem item)
    {
        var path = new List<string>();
        var current = item;
        while (current is not null)
        {
            if (current.DataContext is SefariaCategoryNode category &&
                !string.IsNullOrWhiteSpace(category.DisplayTitle))
            {
                path.Add(category.DisplayTitle);
            }

            current = current.FindAncestorOfType<TreeViewItem>();
        }

        path.Reverse();
        return path;
    }

    private static bool TryCreateCategoryBulkTarget(
        SefariaCategoryNode category,
        TreeViewItem item,
        out CategoryBulkTarget target)
    {
        var path = GetCategoryPath(item);
        target = new CategoryBulkTarget(string.Empty, string.Empty, new List<SefariaBookNode>());

        if (path.Count == 0)
        {
            return false;
        }

        var topLevel = NormalizeCategoryKey(path[0]);
        var selected = NormalizeCategoryKey(category.DisplayTitle);

        if (topLevel == "tanakh" && IsTanakhMajorCategory(selected) && EnumerateBooks(category).Any())
        {
            target = CreateBulkTarget(category);
            return target.Books.Count > 0;
        }

        if ((topLevel == "mishnah" || topLevel == "talmud") && path.Count >= 2 && EnumerateBooks(category).Any())
        {
            target = CreateBulkTarget(category);
            return target.Books.Count > 0;
        }

        if ((topLevel == "halakhah" || topLevel == "halacha") && IsMajorHalachicWork(selected))
        {
            target = CreateBulkTarget(category);
            return target.Books.Count > 0;
        }

        return false;
    }

    private static bool HasDirectBookChildren(SefariaCategoryNode category)
    {
        return category.Contents.Any(node => node is SefariaBookNode);
    }

    private static bool IsTanakhMajorCategory(string key)
    {
        return key is "torah" or "prophets" or "writings" or "neviim" or "ketuvim";
    }

    private static bool IsMajorHalachicWork(string key)
    {
        return key.Contains("mishneh torah", StringComparison.Ordinal) ||
            key.Contains("rambam", StringComparison.Ordinal) ||
            key.Contains("shulchan aruch", StringComparison.Ordinal) ||
            key.Contains("shulchan arukh", StringComparison.Ordinal) ||
            key.Contains("tur", StringComparison.Ordinal);
    }

    private static string NormalizeCategoryKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = new string(value
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            .ToArray());
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static CategoryBulkTarget CreateBulkTarget(SefariaCategoryNode category)
    {
        var books = EnumerateBooks(category).ToList();
        return new CategoryBulkTarget(
            $"Download all {category.DisplayTitle}",
            $"Download Hebrew and translation texts for all books in {category.DisplayTitle}. Already-installed versions are skipped.",
            books);
    }

    private static IEnumerable<SefariaBookNode> EnumerateBooks(SefariaCategoryNode category)
    {
        foreach (var node in category.Contents)
        {
            if (node is SefariaBookNode book)
            {
                yield return book;
                continue;
            }

            if (node is SefariaCategoryNode childCategory)
            {
                foreach (var descendant in EnumerateBooks(childCategory))
                {
                    yield return descendant;
                }
            }
        }
    }

    private void OnLibraryVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibraryVersionChangeEvents)
        {
            return;
        }

        if (_selectedSefariaBook is null ||
            _libraryVersionBox?.SelectedItem is not SingleBookVersionChoice choice ||
            choice.Version is null)
        {
            return;
        }

        _selectedSefariaBook.SelectedVersion = choice.Version;
        UpdateSelectedBookDownloadedState();
        UpdateLibraryDetails();
    }

    private void OnLibraryTranslationVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibraryVersionChangeEvents)
        {
            return;
        }

        if (_selectedSefariaBook is null)
        {
            return;
        }

        UpdateSelectedBookDownloadedState();
        UpdateLibraryDetails();
    }

    private async Task DownloadOrDeleteSelectedHebrewAsync()
    {
        if (_selectedSefariaBook is not null)
        {
            await DownloadOrDeleteSelectedSingleBookLanguageAsync(downloadHebrew: true);
            return;
        }

        if (_selectedSefariaCategory is null ||
            _selectedLibraryTreeItem is null ||
            !TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var categoryTarget))
        {
            return;
        }

        var installProgress = _cachedCategoryProgress ?? GetSelectedCategoryInstallProgress(
            categoryTarget,
            _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice,
            _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice);
        var hebrewFullyInstalled = installProgress.TotalBooks > 0 && installProgress.HebrewInstalledBooks == installProgress.TotalBooks;
        if (hebrewFullyInstalled)
        {
            await DeleteSelectedCategoryHebrewAsync();
        }
        else
        {
            await DownloadCategoryAsync(categoryTarget, includeHebrew: true, includeTranslation: false);
        }
    }

    private async Task DownloadOrDeleteSelectedTranslationAsync()
    {
        if (_selectedSefariaBook is not null)
        {
            await DownloadOrDeleteSelectedSingleBookLanguageAsync(downloadHebrew: false);
            return;
        }

        if (_selectedSefariaCategory is null ||
            _selectedLibraryTreeItem is null ||
            !TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var categoryTarget))
        {
            return;
        }

        var installProgress = _cachedCategoryProgress ?? GetSelectedCategoryInstallProgress(
            categoryTarget,
            _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice,
            _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice);
        var translationFullyInstalled = installProgress.TotalBooks > 0 && installProgress.TranslationInstalledBooks == installProgress.TotalBooks;
        if (translationFullyInstalled)
        {
            await DeleteSelectedCategoryTranslationAsync();
        }
        else
        {
            await DownloadCategoryAsync(categoryTarget, includeHebrew: false, includeTranslation: true);
        }
    }

    private async Task DownloadOrDeleteSelectedSingleBookLanguageAsync(bool downloadHebrew)
    {
        if (_selectedSefariaBook is null)
        {
            return;
        }

        await EnsureVersionsLoadedAsync(_selectedSefariaBook);
        var book = _selectedSefariaBook;
        var selectedVersion = downloadHebrew ? GetSelectedSingleBookHebrewVersion(book) : GetSelectedSingleBookTranslationVersion();
        if (selectedVersion is null)
        {
            SetLibraryStatus(downloadHebrew
                ? $"No Hebrew version is available for {book.Title}."
                : $"No translation is selected for {book.Title}.");
            return;
        }

        if (_isSefariaDownloading)
        {
            SetLibraryStatus("A download is already in progress.");
            return;
        }

        var isInstalled = HasInstalledExactVersion(book.Title, selectedVersion);
        if (isInstalled)
        {
            try
            {
                CloseReaderTabsForBook(book);
                _sefariaLibrary.DeleteBook(CreateBookVersionTarget(book, selectedVersion));
                book.IsDownloaded = IsSelectedSingleBookPairInstalled(book);
                RefreshInstalledBooksTree();
                RefreshOpenReaderTabForBook(book);
                SetLibraryStatus($"{(downloadHebrew ? "Hebrew" : "Translation")} deleted for {book.Title}.");
            }
            catch (Exception ex)
            {
                SetLibraryStatus($"Failed to delete {book.Title}: {ex.Message}");
            }

            UpdateLibraryDetails();
            return;
        }

        _isSefariaDownloading = true;
        book.IsDownloading = true;
        book.DownloadProgress = 0;
        _sefariaDownloadCts = new CancellationTokenSource();
        UpdateLibraryDetails();

        try
        {
            SetLibraryStatus($"Downloading {book.Title}: {(downloadHebrew ? "Hebrew" : "Translation")}");
            await DownloadBookVersionAsync(book, selectedVersion, _sefariaDownloadCts.Token);
            book.IsDownloaded = IsSelectedSingleBookPairInstalled(book);
            book.DownloadProgress = 100;
            RefreshInstalledBooksTree();
            RefreshOpenReaderTabForBook(book);
            SetLibraryStatus($"{(downloadHebrew ? "Hebrew" : "Translation")} downloaded for {book.Title}.");
        }
        catch (OperationCanceledException)
        {
            book.DownloadProgress = 0;
            SetLibraryStatus($"Cancelled download of {book.Title}.");
        }
        catch (Exception ex)
        {
            book.DownloadProgress = 0;
            SetLibraryStatus($"Download failed: {ex.Message}");
        }
        finally
        {
            _isSefariaDownloading = false;
            book.IsDownloading = false;
            _sefariaDownloadCts?.Dispose();
            _sefariaDownloadCts = null;
            UpdateSelectedBookDownloadedState();
            UpdateLibraryDetails();
        }
    }

    private async Task DownloadBookVersionAsync(SefariaBookNode book, SefariaVersionOption? version, CancellationToken cancellationToken)
    {
        var previousVersion = book.SelectedVersion;
        book.SelectedVersion = version;

        try
        {
            var progress = new Progress<double>(percent =>
            {
                book.DownloadProgress = percent;
                if (_libraryProgress is not null)
                {
                    _libraryProgress.Value = percent;
                }
                SetLibraryStatus($"Downloading {book.Title}: {percent:0}%");
            });

            await _sefariaLibrary.DownloadBookAsync(book, progress, cancellationToken);
        }
        finally
        {
            book.SelectedVersion = previousVersion;
        }
    }

    private async Task DownloadCategoryAsync(CategoryBulkTarget target, bool includeHebrew, bool includeTranslation)
    {
        if (_isSefariaDownloading)
        {
            SetLibraryStatus("A download is already in progress.");
            return;
        }

        _isSefariaDownloading = true;
        _sefariaDownloadCts = new CancellationTokenSource();
        UpdateLibraryDetails();

        var downloadedBooks = 0;
        var skippedBooks = 0;
        var unavailableBooks = 0;
        var failedBooks = 0;
        var refreshedBooks = new List<SefariaBookNode>();

        try
        {
            var hebrewChoice = _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice;
            var englishChoice = _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice;
            var orderedBooks = target.Books
                .OrderBy(book => book.Order)
                .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < orderedBooks.Count; index++)
            {
                var book = orderedBooks[index];
                var downloadedForBook = false;
                var unavailableForBook = false;

                await EnsureVersionsLoadedAsync(book);
                var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
                var hasHebrew = HasInstalledMatchingSelection(installedVersions, true, hebrewChoice);
                var hasTranslation = HasInstalledMatchingSelection(installedVersions, false, englishChoice);

                if (includeHebrew && !hasHebrew)
                {
                    var hebrewVersion = SelectBulkVersion(book, true, hebrewChoice);
                    if (hebrewVersion is null)
                    {
                        unavailableForBook = true;
                    }
                    else
                    {
                        SetLibraryStatus($"Downloading {index + 1}/{orderedBooks.Count}: {book.Title} (Hebrew)");
                        await DownloadBookVersionAsync(book, hebrewVersion, _sefariaDownloadCts.Token);
                        downloadedForBook = true;
                    }
                }

                if (includeTranslation && !hasTranslation)
                {
                    var englishVersion = SelectBulkVersion(book, false, englishChoice);
                    if (englishVersion is null)
                    {
                        unavailableForBook = true;
                    }
                    else
                    {
                        SetLibraryStatus($"Downloading {index + 1}/{orderedBooks.Count}: {book.Title} (Translation)");
                        await DownloadBookVersionAsync(book, englishVersion, _sefariaDownloadCts.Token);
                        downloadedForBook = true;
                    }
                }

                if (downloadedForBook)
                {
                    downloadedBooks++;
                    refreshedBooks.Add(book);
                }
                else if (unavailableForBook)
                {
                    unavailableBooks++;
                }
                else
                {
                    skippedBooks++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetLibraryStatus("Cancelled category download.");
            return;
        }
        catch (Exception ex)
        {
            failedBooks++;
            SetLibraryStatus($"Category download failed: {ex.Message}");
        }
        finally
        {
            _isSefariaDownloading = false;
            _sefariaDownloadCts?.Dispose();
            _sefariaDownloadCts = null;
            RefreshInstalledBooksTree();
            foreach (var book in refreshedBooks)
            {
                RefreshOpenReaderTabForBook(book);
            }
            UpdateLibraryDetails();
        }

        SetLibraryStatus(
            $"Category download complete. Downloaded: {downloadedBooks}, skipped: {skippedBooks}, unavailable: {unavailableBooks}, failed: {failedBooks}.");
    }

    private async Task DeleteCategoryAsync(CategoryBulkTarget target)
    {
        var selectedHebrew = _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice;
        var selectedTranslation = _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice;
        var deleteLabel = $"{GetSelectionLabel(true, selectedHebrew)} + {GetSelectionLabel(false, selectedTranslation)}";
        var confirmed = await ConfirmCategoryDeleteAsync(
            "Delete selected category versions",
            $"Delete {deleteLabel} for every book in {target.ActionTitle.Replace("Download all ", string.Empty, StringComparison.OrdinalIgnoreCase)}?");
        if (!confirmed)
        {
            return;
        }

        await DeleteCategorySelectionsAsync(target, selectedHebrew, selectedTranslation, $"Deleted selected versions ({deleteLabel})");
    }

    private async Task DeleteSelectedCategoryHebrewAsync()
    {
        if (_selectedSefariaCategory is null ||
            _selectedLibraryTreeItem is null ||
            !TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var target))
        {
            return;
        }

        var selectedHebrew = _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice;
        var confirmed = await ConfirmCategoryDeleteAsync(
            "Delete Hebrew texts",
            $"Delete {GetSelectionLabel(true, selectedHebrew)} for every book in {target.ActionTitle.Replace("Download all ", string.Empty, StringComparison.OrdinalIgnoreCase)}?");
        if (!confirmed)
        {
            return;
        }

        await DeleteCategorySelectionsAsync(target, selectedHebrew, null, "Deleted Hebrew texts");
    }

    private async Task DeleteSelectedCategoryTranslationAsync()
    {
        if (_selectedSefariaCategory is null ||
            _selectedLibraryTreeItem is null ||
            !TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var target))
        {
            return;
        }

        var selectedTranslation = _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice;
        var confirmed = await ConfirmCategoryDeleteAsync(
            "Delete translations",
            $"Delete {GetSelectionLabel(false, selectedTranslation)} for every book in {target.ActionTitle.Replace("Download all ", string.Empty, StringComparison.OrdinalIgnoreCase)}?");
        if (!confirmed)
        {
            return;
        }

        await DeleteCategorySelectionsAsync(target, null, selectedTranslation, "Deleted translations");
    }

    private async Task DeleteCategorySelectionsAsync(
        CategoryBulkTarget target,
        CategoryVersionChoice? hebrewSelection,
        CategoryVersionChoice? translationSelection,
        string completionPrefix)
    {
        if (_isSefariaDownloading)
        {
            SetLibraryStatus("A delete is already in progress.");
            return;
        }

        _isSefariaDownloading = true;
        _sefariaDownloadCts = new CancellationTokenSource();
        UpdateLibraryDetails();

        var deletedBooks = 0;
        var skippedBooks = 0;
        var failedBooks = 0;

        try
        {
            var orderedBooks = target.Books
                .OrderBy(book => book.Order)
                .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < orderedBooks.Count; index++)
            {
                _sefariaDownloadCts.Token.ThrowIfCancellationRequested();

                var book = orderedBooks[index];
                var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title).ToList();
                var versionsToDelete = installedVersions
                    .Where(version =>
                        (hebrewSelection is not null && IsInstalledSelectionMatch(version, true, hebrewSelection)) ||
                        (translationSelection is not null && IsInstalledSelectionMatch(version, false, translationSelection)))
                    .ToList();
                if (versionsToDelete.Count == 0)
                {
                    skippedBooks++;
                    continue;
                }

                SetLibraryStatus($"Deleting {index + 1}/{orderedBooks.Count}: {book.Title}");
                CloseReaderTabsForBook(book);

                foreach (var installedVersion in versionsToDelete)
                {
                    var bookToDelete = new SefariaBookNode
                    {
                        Title = book.Title,
                        HebrewTitle = book.HebrewTitle,
                        Categories = book.Categories,
                        SelectedVersion = new SefariaVersionOption
                        {
                            LanguageCode = installedVersion.LanguageCode,
                            VersionTitle = installedVersion.VersionTitle
                        }
                    };

                    _sefariaLibrary.DeleteBook(bookToDelete);
                }

                deletedBooks++;
            }
        }
        catch (OperationCanceledException)
        {
            SetLibraryStatus("Cancelled category delete.");
            return;
        }
        catch (Exception ex)
        {
            failedBooks++;
            SetLibraryStatus($"Category delete failed: {ex.Message}");
        }
        finally
        {
            _isSefariaDownloading = false;
            _sefariaDownloadCts?.Dispose();
            _sefariaDownloadCts = null;
            RefreshInstalledBooksTree();
            UpdateLibraryDetails();
        }

        SetLibraryStatus($"{completionPrefix}. Deleted: {deletedBooks}, skipped: {skippedBooks}, failed: {failedBooks}.");
    }

    private static string GetSelectionLabel(bool hebrewSelection, CategoryVersionChoice? selection)
    {
        if (selection is null || selection.IsAutomatic || string.IsNullOrWhiteSpace(selection.VersionTitle))
        {
            return hebrewSelection ? "default Hebrew" : "default translation";
        }

        return selection.VersionTitle;
    }

    private bool CategoryHasInstalledContent(CategoryBulkTarget target)
    {
        var selectedHebrew = _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice;
        var selectedTranslation = _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice;
        return GetSelectedCategoryInstallProgress(target, selectedHebrew, selectedTranslation).IsSelectedPairFullyInstalled;
    }

    private sealed record CategorySelectionProgress(
        int TotalBooks,
        int SelectedPairInstalledBooks,
        int HebrewInstalledBooks,
        int TranslationInstalledBooks)
    {
        public bool IsSelectedPairFullyInstalled => TotalBooks > 0 && SelectedPairInstalledBooks == TotalBooks;
    }

    private CategorySelectionProgress GetSelectedCategoryInstallProgress(
        CategoryBulkTarget target,
        CategoryVersionChoice? selectedHebrew,
        CategoryVersionChoice? selectedTranslation)
    {
        var selectedPairInstalledBooks = 0;
        var hebrewInstalledBooks = 0;
        var translationInstalledBooks = 0;
        var totalBooks = target.Books.Count;

        foreach (var book in target.Books)
        {
            var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
            var hasHebrew = HasInstalledMatchingSelection(installedVersions, true, selectedHebrew);
            var hasTranslation = HasInstalledMatchingSelection(installedVersions, false, selectedTranslation);

            if (hasHebrew && hasTranslation)
            {
                selectedPairInstalledBooks++;
            }
            if (hasHebrew)
            {
                hebrewInstalledBooks++;
            }
            if (hasTranslation)
            {
                translationInstalledBooks++;
            }
        }

        return new CategorySelectionProgress(totalBooks, selectedPairInstalledBooks, hebrewInstalledBooks, translationInstalledBooks);
    }

    private static bool HasInstalledMatchingSelection(
        IReadOnlyCollection<InstalledSefariaBook> installedVersions,
        bool hebrewSelection,
        CategoryVersionChoice? selectedChoice)
    {
        return installedVersions.Any(version => IsInstalledSelectionMatch(version, hebrewSelection, selectedChoice));
    }

    private static bool IsInstalledSelectionMatch(
        InstalledSefariaBook version,
        bool hebrewSelection,
        CategoryVersionChoice? selectedChoice)
    {
        var isHebrewInstalled = SefariaLibraryService.IsHebrew(version);
        if (hebrewSelection != isHebrewInstalled)
        {
            return false;
        }

        if (selectedChoice is null || selectedChoice.IsAutomatic || string.IsNullOrWhiteSpace(selectedChoice.VersionTitle))
        {
            return true;
        }

        return string.Equals(version.VersionTitle, selectedChoice.VersionTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static SefariaVersionOption? SelectBulkVersion(
        SefariaBookNode book,
        bool hebrewVersion,
        CategoryVersionChoice? selectedChoice)
    {
        var matchingVersions = book.Versions
            .Where(version => hebrewVersion
                ? IsHebrewCategoryVersion(version)
                : !IsHebrewCategoryVersion(version))
            .ToList();
        if (matchingVersions.Count == 0)
        {
            return null;
        }

        if (selectedChoice is null || selectedChoice.IsAutomatic || string.IsNullOrWhiteSpace(selectedChoice.VersionTitle))
        {
            return matchingVersions.FirstOrDefault();
        }

        return matchingVersions.FirstOrDefault(version =>
                   string.Equals(version.VersionTitle, selectedChoice.VersionTitle, StringComparison.OrdinalIgnoreCase)) ??
               matchingVersions.FirstOrDefault();
    }

    private static List<SingleBookVersionChoice> BuildSingleBookVersionChoices(
        IEnumerable<SefariaVersionOption> versions,
        bool hebrewChoices,
        bool includeNoneOption)
    {
        var choices = new List<SingleBookVersionChoice>();
        choices.AddRange(versions
            .Where(version => hebrewChoices ? IsHebrewCategoryVersion(version) : !IsHebrewCategoryVersion(version))
            .Select(version => new SingleBookVersionChoice
            {
                Version = version,
                DisplayText = string.IsNullOrWhiteSpace(version.DisplayText) ? version.VersionTitle : version.DisplayText
            }));

        if (includeNoneOption)
        {
            choices.Add(new SingleBookVersionChoice
            {
                Version = null,
                IsNone = true,
                DisplayText = "No translation"
            });
        }

        return choices;
    }

    private SefariaVersionOption? GetSelectedSingleBookHebrewVersion(SefariaBookNode book)
    {
        if (_libraryVersionBox?.SelectedItem is SingleBookVersionChoice { Version: not null } selected)
        {
            return selected.Version;
        }

        return book.Versions.FirstOrDefault(IsHebrewCategoryVersion) ?? book.Versions.FirstOrDefault();
    }

    private SefariaVersionOption? GetSelectedSingleBookTranslationVersion()
    {
        if (_libraryTranslationVersionBox?.SelectedItem is not SingleBookVersionChoice selected)
        {
            if (_selectedSefariaBook is null)
            {
                return null;
            }

            return _selectedSefariaBook.Versions.FirstOrDefault(version => !IsHebrewCategoryVersion(version));
        }

        if (selected.IsNone)
        {
            return null;
        }

        return selected.Version;
    }

    private bool HasInstalledExactVersion(string title, SefariaVersionOption version)
    {
        return _sefariaLibrary.GetInstalledVersionSummariesForTitle(title).Any(installed =>
            string.Equals(installed.LanguageCode, version.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(installed.VersionTitle, version.VersionTitle, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSelectedSingleBookPairInstalled(SefariaBookNode book)
    {
        var selectedHebrew = GetSelectedSingleBookHebrewVersion(book);
        var selectedTranslation = GetSelectedSingleBookTranslationVersion();
        var hasHebrew = selectedHebrew is null || HasInstalledExactVersion(book.Title, selectedHebrew);
        var hasTranslation = selectedTranslation is null || HasInstalledExactVersion(book.Title, selectedTranslation);
        return hasHebrew && hasTranslation;
    }

    private static SefariaBookNode CreateBookVersionTarget(SefariaBookNode source, SefariaVersionOption version)
    {
        return new SefariaBookNode
        {
            Title = source.Title,
            HebrewTitle = source.HebrewTitle,
            Categories = source.Categories,
            PrimaryCategory = source.PrimaryCategory,
            SelectedVersion = version
        };
    }

    private void UpdateSelectedBookDownloadedState()
    {
        if (_selectedSefariaBook is null)
        {
            return;
        }

        _selectedSefariaBook.IsDownloaded = IsSelectedSingleBookPairInstalled(_selectedSefariaBook);
    }

    private void PopulateSingleBookVersionBoxes(SefariaBookNode book)
    {
        if (_libraryVersionBox is null || _libraryTranslationVersionBox is null)
        {
            return;
        }

        var hebrewChoices = BuildSingleBookVersionChoices(book.Versions, true, false);
        if (hebrewChoices.Count == 0)
        {
            hebrewChoices = book.Versions
                .Select(version => new SingleBookVersionChoice
                {
                    Version = version,
                    DisplayText = string.IsNullOrWhiteSpace(version.DisplayText) ? version.VersionTitle : version.DisplayText
                })
                .ToList();
        }

        var translationChoices = BuildSingleBookVersionChoices(book.Versions, false, true);

        _suppressLibraryVersionChangeEvents = true;
        try
        {
            _libraryVersionBox.ItemsSource = hebrewChoices;
            _libraryTranslationVersionBox.ItemsSource = translationChoices;
            _libraryVersionBox.SelectedItem = hebrewChoices.FirstOrDefault(choice => choice.Version is not null);
            _libraryTranslationVersionBox.SelectedItem = translationChoices.FirstOrDefault(choice => choice.Version is not null)
                ?? translationChoices.FirstOrDefault();
        }
        finally
        {
            _suppressLibraryVersionChangeEvents = false;
        }

        book.SelectedVersion = (_libraryVersionBox.SelectedItem as SingleBookVersionChoice)?.Version ?? book.SelectedVersion;
        _libraryVersionBoxesBook = book;
    }

    private void UpdateLibraryDetails()
    {
        if (_libraryTitle is null ||
            _libraryHebrewTitle is null ||
            _libraryDescription is null ||
            _libraryBookVersionLabel is null ||
            _libraryBookVersionPanel is null ||
            _libraryVersionBox is null ||
            _libraryTranslationVersionBox is null ||
            _libraryCategoryVersionPanel is null ||
            _libraryCategoryHebrewVersionBox is null ||
            _libraryCategoryEnglishVersionBox is null ||
            _libraryProgress is null ||
            _librarySingleHebrewActionButton is null ||
            _librarySingleTranslationActionButton is null ||
            _libraryCategoryHebrewActionButton is null ||
            _libraryCategoryTranslationActionButton is null ||
            _libraryCancelButton is null)
        {
            return;
        }

        if (_selectedSefariaBook is not null)
        {
            _libraryTitle.Text = FormatTitle(_selectedSefariaBook.Title, _selectedSefariaBook.HebrewTitle);
            _libraryHebrewTitle.Text = string.Empty;
            _libraryDescription.Text = GetBookDescription(_selectedSefariaBook);
            _libraryBookVersionPanel.IsVisible = true;
            _libraryCategoryVersionPanel.IsVisible = false;

            if (!ReferenceEquals(_libraryVersionBoxesBook, _selectedSefariaBook))
            {
                PopulateSingleBookVersionBoxes(_selectedSefariaBook);
            }

            var hebrewChoiceCount = (_libraryVersionBox.ItemsSource as IReadOnlyCollection<SingleBookVersionChoice>)?.Count ?? 0;
            var translationChoiceCount = (_libraryTranslationVersionBox.ItemsSource as IReadOnlyCollection<SingleBookVersionChoice>)?.Count ?? 0;

            SetComboBoxSelectionToolTip(_libraryVersionBox);
            SetComboBoxSelectionToolTip(_libraryTranslationVersionBox);

            _libraryVersionBox.IsEnabled = hebrewChoiceCount > 0 && !_isSefariaDownloading;
            _libraryTranslationVersionBox.IsEnabled = translationChoiceCount > 0 && !_isSefariaDownloading;

            _libraryProgress.Value = _selectedSefariaBook.DownloadProgress;
            _libraryProgress.IsVisible = _selectedSefariaBook.IsDownloading;

            var selectedHebrewVersion = GetSelectedSingleBookHebrewVersion(_selectedSefariaBook);
            var selectedTranslationVersion = GetSelectedSingleBookTranslationVersion();
            var hasHebrewInstalled = selectedHebrewVersion is not null && HasInstalledExactVersion(_selectedSefariaBook.Title, selectedHebrewVersion);
            var hasTranslationInstalled = selectedTranslationVersion is not null && HasInstalledExactVersion(_selectedSefariaBook.Title, selectedTranslationVersion);
            _librarySingleHebrewActionButton.Content = hasHebrewInstalled ? "Delete" : "Download";
            _librarySingleHebrewActionButton.IsEnabled = !_isSefariaDownloading && !_selectedSefariaBook.IsLoadingVersions && selectedHebrewVersion is not null;
            _librarySingleTranslationActionButton.Content = hasTranslationInstalled ? "Delete" : "Download";
            _librarySingleTranslationActionButton.IsEnabled = !_isSefariaDownloading && !_selectedSefariaBook.IsLoadingVersions && selectedTranslationVersion is not null;
            _libraryCancelButton.IsEnabled = _selectedSefariaBook.IsDownloading;
            return;
        }

        if (_selectedSefariaCategory is not null &&
            _selectedLibraryTreeItem is not null &&
            TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var categoryTarget))
        {
            var installProgress = _cachedCategoryProgress;
            _libraryTitle.Text = categoryTarget.ActionTitle;
            _libraryHebrewTitle.Text = string.Empty;
            _libraryDescription.Text = installProgress is null
                ? $"{categoryTarget.Description}{Environment.NewLine}Installed (selected pair): checking..."
                : $"{categoryTarget.Description}{Environment.NewLine}Installed (selected pair): {installProgress.SelectedPairInstalledBooks}/{installProgress.TotalBooks} books";
            _libraryBookVersionPanel.IsVisible = false;
            _libraryCategoryVersionPanel.IsVisible = true;
            _libraryCategoryHebrewVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryCategoryEnglishVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryProgress.IsVisible = _isSefariaDownloading;
            var hebrewFullyInstalled = installProgress is not null && installProgress.TotalBooks > 0 && installProgress.HebrewInstalledBooks == installProgress.TotalBooks;
            var translationFullyInstalled = installProgress is not null && installProgress.TotalBooks > 0 && installProgress.TranslationInstalledBooks == installProgress.TotalBooks;
            _libraryCategoryHebrewActionButton.Content = hebrewFullyInstalled ? "Delete" : "Download";
            _libraryCategoryHebrewActionButton.IsEnabled = !_isSefariaDownloading && categoryTarget.Books.Count > 0;
            _libraryCategoryTranslationActionButton.Content = translationFullyInstalled ? "Delete" : "Download";
            _libraryCategoryTranslationActionButton.IsEnabled = !_isSefariaDownloading && categoryTarget.Books.Count > 0;
            _libraryCancelButton.IsEnabled = _isSefariaDownloading;
            SetComboBoxSelectionToolTip(_libraryCategoryHebrewVersionBox);
            SetComboBoxSelectionToolTip(_libraryCategoryEnglishVersionBox);
            return;
        }

        _libraryTitle.Text = "Select a book";
        _libraryHebrewTitle.Text = string.Empty;
        _libraryDescription.Text = "Choose a book from the library tree to see its description.";
        _libraryBookVersionPanel.IsVisible = true;
        _libraryCategoryVersionPanel.IsVisible = false;
        _libraryVersionBox.ItemsSource = null;
        _libraryTranslationVersionBox.ItemsSource = null;
        _libraryVersionBoxesBook = null;
        _libraryVersionBox.IsEnabled = false;
        _libraryTranslationVersionBox.IsEnabled = false;
        _libraryProgress.IsVisible = false;
        _librarySingleHebrewActionButton.Content = "Download";
        _librarySingleHebrewActionButton.IsEnabled = false;
        _librarySingleTranslationActionButton.Content = "Download";
        _librarySingleTranslationActionButton.IsEnabled = false;
        _libraryCancelButton.IsEnabled = false;
        SetComboBoxSelectionToolTip(_libraryVersionBox);
        SetComboBoxSelectionToolTip(_libraryTranslationVersionBox);
    }

    private void SetLibraryStatus(string message)
    {
        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = message;
        }
    }

    private void CancelCategoryInstallProgressRefresh()
    {
        if (_categoryInstallProgressCts is null)
        {
            return;
        }

        _categoryInstallProgressCts.Cancel();
        _categoryInstallProgressCts.Dispose();
        _categoryInstallProgressCts = null;
    }

    private void OnLibraryCategoryVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectedSefariaCategory is null ||
            _selectedLibraryTreeItem is null ||
            !TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var target))
        {
            UpdateLibraryDetails();
            return;
        }

        _cachedCategoryProgress = null;
        UpdateLibraryDetails();
        ScheduleCategoryInstallProgressRefresh(target);
    }

    private void ScheduleCategoryInstallProgressRefresh(CategoryBulkTarget target)
    {
        if (_selectedSefariaCategory is null)
        {
            return;
        }

        CancelCategoryInstallProgressRefresh();
        var cts = new CancellationTokenSource();
        _categoryInstallProgressCts = cts;
        _ = RefreshCategoryInstallProgressAsync(target, cts);
    }

    private async Task RefreshCategoryInstallProgressAsync(CategoryBulkTarget target, CancellationTokenSource cts)
    {
        try
        {
            CategoryVersionChoice? selectedHebrew = null;
            CategoryVersionChoice? selectedTranslation = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                selectedHebrew = _libraryCategoryHebrewVersionBox?.SelectedItem as CategoryVersionChoice;
                selectedTranslation = _libraryCategoryEnglishVersionBox?.SelectedItem as CategoryVersionChoice;
            });

            var progress = await Task.Run(
                () => GetSelectedCategoryInstallProgress(target, selectedHebrew, selectedTranslation),
                cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_categoryInstallProgressCts, cts))
                {
                    return;
                }

                _cachedCategoryProgress = progress;
                UpdateLibraryDetails();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_categoryInstallProgressCts, cts) && _libraryStatus is not null)
                {
                    _libraryStatus.Text = $"Could not load category status: {ex.Message}";
                }
            });
        }
    }

    private async Task<bool> ConfirmCategoryDeleteAsync(string title, string message)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button
                            {
                                Content = "Cancel",
                                MinWidth = 90
                            },
                            new Button
                            {
                                Content = "Delete",
                                MinWidth = 90
                            }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children;
        var cancelButton = (Button)buttons[0];
        var deleteButton = (Button)buttons[1];
        cancelButton.Click += (_, _) => dialog.Close();
        deleteButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };

        await dialog.ShowDialog(this);
        return confirmed;
    }

    private static string GetBookDescription(SefariaBookNode book)
    {
        if (!string.IsNullOrWhiteSpace(book.EnShortDesc))
        {
            return book.EnShortDesc;
        }

        if (!string.IsNullOrWhiteSpace(book.HeShortDesc))
        {
            return book.HeShortDesc;
        }

        return "No description is available for this book.";
    }
}
