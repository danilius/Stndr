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
            Text = $"Data folder: {_sefariaLibrary.StorageRootFolder}",
            TextWrapping = TextWrapping.Wrap
        };
        _libraryBookVersionLabel = new TextBlock
        {
            Text = "Version",
            FontWeight = FontWeight.SemiBold
        };
        _libraryVersionBox = new ComboBox
        {
            MinWidth = 320,
            IsEnabled = false
        };
        _libraryVersionBox.SelectionChanged += OnLibraryVersionChanged;
        _libraryCategoryHebrewVersionBox = new ComboBox
        {
            MinWidth = 320,
            IsEnabled = false
        };
        _libraryCategoryHebrewVersionBox.SelectionChanged += (_, _) => UpdateLibraryDetails();
        _libraryCategoryEnglishVersionBox = new ComboBox
        {
            MinWidth = 320,
            IsEnabled = false
        };
        _libraryCategoryEnglishVersionBox.SelectionChanged += (_, _) => UpdateLibraryDetails();
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
                new TextBlock
                {
                    Text = "Translation",
                    FontWeight = FontWeight.SemiBold
                },
                _libraryCategoryEnglishVersionBox
            }
        };

        _libraryProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            IsVisible = false
        };
        _libraryDownloadButton = new Button
        {
            Content = "Download",
            IsEnabled = false,
            MinWidth = 100
        };
        _libraryDownloadButton.Click += async (_, _) => await DownloadOrDeleteSelectedBookAsync();
        _libraryDeleteHebrewButton = new Button
        {
            Content = "Delete Hebrew",
            IsEnabled = false,
            MinWidth = 120,
            IsVisible = false
        };
        _libraryDeleteHebrewButton.Click += async (_, _) => await DeleteSelectedCategoryHebrewAsync();
        _libraryDeleteTranslationButton = new Button
        {
            Content = "Delete translation",
            IsEnabled = false,
            MinWidth = 140,
            IsVisible = false
        };
        _libraryDeleteTranslationButton.Click += async (_, _) => await DeleteSelectedCategoryTranslationAsync();

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
                    _libraryBookVersionLabel,
                    _libraryVersionBox,
                    _libraryCategoryVersionPanel,
                    _libraryProgress,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            _libraryDownloadButton,
                            _libraryDeleteHebrewButton,
                            _libraryDeleteTranslationButton,
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

        _libraryStatus.Text = "Loading Sefaria index...";
        _libraryTree.ItemsSource = null;
        _selectedSefariaBook = null;
        _selectedSefariaCategory = null;
        UpdateLibraryDetails();

        try
        {
            _sefariaRoot = await _sefariaLibrary.LoadLibraryAsync(CancellationToken.None);
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
            var horizontalOffset = _libraryTreeScrollViewer?.Offset.X ?? 0;
            var item = e.AddedItems.Count > 0 ? e.AddedItems[0] as TreeViewItem : null;
            item ??= _selectedLibraryTreeItem;
            _selectedSefariaBook = item?.DataContext as SefariaBookNode;
            _selectedSefariaCategory = item?.DataContext as SefariaCategoryNode;
            UpdateLibraryDetails();
            RestoreLibraryTreeHorizontalOffset(horizontalOffset);

            if (_selectedSefariaBook is not null)
            {
                _cachedCategoryProgress = null;
                CancelCategoryInstallProgressRefresh();
                _ = EnsureVersionsLoadedAsync(_selectedSefariaBook);
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateSelectedBookDownloadedState();
                    UpdateLibraryDetails();
                    RestoreLibraryTreeHorizontalOffset(horizontalOffset);
                }, DispatcherPriority.Background);
                return;
            }

            if (_selectedSefariaCategory is not null && item is not null)
            {
                CancelCategoryInstallProgressRefresh();
                _cachedCategoryProgress = null;
                _ = EnsureCategoryVersionChoicesLoadedAsync(_selectedSefariaCategory, item);
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateLibraryDetails();
                    RestoreLibraryTreeHorizontalOffset(horizontalOffset);
                }, DispatcherPriority.Background);
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

    private Task EnsureVersionsLoadedAsync(SefariaBookNode book)
    {
        if (book.IsVersionsLoaded || book.IsLoadingVersions)
        {
            return Task.CompletedTask;
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
                book.SelectedVersion = book.Versions.FirstOrDefault();
                book.IsVersionsLoaded = true;
                book.IsLoadingVersions = false;
                return Task.CompletedTask;
            }
            book.Versions = new List<SefariaVersionOption>();
            book.SelectedVersion = null;
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

        return Task.CompletedTask;
    }

    private Task EnsureCategoryVersionChoicesLoadedAsync(SefariaCategoryNode category, TreeViewItem item)
    {
        try
        {
            if (_libraryCategoryHebrewVersionBox is null || _libraryCategoryEnglishVersionBox is null)
            {
                return Task.CompletedTask;
            }

            if (!TryCreateCategoryBulkTarget(category, item, out var target))
            {
                _libraryCategoryHebrewVersionBox.ItemsSource = null;
                _libraryCategoryEnglishVersionBox.ItemsSource = null;
                _libraryCategoryHebrewVersionBox.IsEnabled = false;
                _libraryCategoryEnglishVersionBox.IsEnabled = false;
                return Task.CompletedTask;
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
                return Task.CompletedTask;
            }

            if (!_sefariaLibrary.TryGetCachedAvailableVersions(representativeBook.Title, out var representativeVersions))
            {
                representativeVersions = new List<SefariaVersionOption>();
            }

            representativeBook.Versions = representativeVersions;
            _libraryCategoryHebrewVersionBox.ItemsSource = BuildCategoryVersionChoices(new List<SefariaBookNode> { representativeBook }, true, "Default Hebrew (category)");
            _libraryCategoryEnglishVersionBox.ItemsSource = BuildCategoryVersionChoices(new List<SefariaBookNode> { representativeBook }, false, "Default translation (category)");
            _libraryCategoryHebrewVersionBox.SelectedIndex = 0;
            _libraryCategoryEnglishVersionBox.SelectedIndex = 0;
            _libraryCategoryHebrewVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryCategoryEnglishVersionBox.IsEnabled = !_isSefariaDownloading;
            ScheduleCategoryInstallProgressRefresh(target);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = $"Failed to load category versions: {ex.Message}";
            }
            return Task.CompletedTask;
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

        if (string.IsNullOrWhiteSpace(version.LanguageFamilyName))
        {
            return true;
        }

        return string.Equals(version.LanguageFamilyName, "hebrew", StringComparison.OrdinalIgnoreCase);
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
        if (_selectedSefariaBook is null || _libraryVersionBox?.SelectedItem is not SefariaVersionOption version)
        {
            return;
        }

        _selectedSefariaBook.SelectedVersion = version;
        UpdateSelectedBookDownloadedState();
        UpdateLibraryDetails();
    }

    private async Task DownloadOrDeleteSelectedBookAsync()
    {
        if (_selectedSefariaBook is null)
        {
            if (_selectedSefariaCategory is not null &&
                _selectedLibraryTreeItem is not null &&
                TryCreateCategoryBulkTarget(_selectedSefariaCategory, _selectedLibraryTreeItem, out var categoryTarget))
            {
                if (_cachedCategoryProgress is not null && _cachedCategoryProgress.IsSelectedPairFullyInstalled)
                {
                    await DeleteCategoryAsync(categoryTarget);
                }
                else
                {
                    await DownloadCategoryAsync(categoryTarget);
                }
            }
            return;
        }

        await EnsureVersionsLoadedAsync(_selectedSefariaBook);
        UpdateSelectedBookDownloadedState();

        if (_selectedSefariaBook.IsDownloaded)
        {
            try
            {
                CloseReaderTabsForBook(_selectedSefariaBook);
                _sefariaLibrary.DeleteBook(_selectedSefariaBook);
                _selectedSefariaBook.IsDownloaded = false;
                _selectedSefariaBook.DownloadProgress = 0;
                RefreshInstalledBooksTree();
                UpdateLibraryDetails();
            }
            catch (Exception ex)
            {
                SetLibraryStatus($"Failed to delete {_selectedSefariaBook.Title}: {ex.Message}");
            }

            return;
        }

        if (_isSefariaDownloading)
        {
            SetLibraryStatus("A download is already in progress.");
            return;
        }

        _isSefariaDownloading = true;
        _selectedSefariaBook.IsDownloading = true;
        _selectedSefariaBook.DownloadProgress = 0;
        _sefariaDownloadCts = new CancellationTokenSource();
        UpdateLibraryDetails();

        try
        {
            var book = _selectedSefariaBook;
            var requestedVersion = book.SelectedVersion;

            await DownloadBookVersionAsync(book, requestedVersion, _sefariaDownloadCts.Token);
            book.IsDownloaded = true;
            book.DownloadProgress = 100;

            var downloadedDefaultHebrew = await DownloadDefaultHebrewFallbackIfNeededAsync(book, requestedVersion, _sefariaDownloadCts.Token);

            RefreshInstalledBooksTree();
            RefreshOpenReaderTabForBook(book);
            SetLibraryStatus(downloadedDefaultHebrew
                ? $"Downloaded {book.Title} and the default Hebrew text."
                : $"Downloaded {book.Title} to {_sefariaLibrary.GetExistingDownloadPath(book)}");
        }
        catch (OperationCanceledException)
        {
            _selectedSefariaBook.DownloadProgress = 0;
            SetLibraryStatus($"Cancelled download of {_selectedSefariaBook.Title}.");
        }
        catch (Exception ex)
        {
            _selectedSefariaBook.DownloadProgress = 0;
            SetLibraryStatus($"Download failed: {ex.Message}");
        }
        finally
        {
            _isSefariaDownloading = false;
            _selectedSefariaBook.IsDownloading = false;
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

    private async Task<bool> DownloadDefaultHebrewFallbackIfNeededAsync(
        SefariaBookNode book,
        SefariaVersionOption? requestedVersion,
        CancellationToken cancellationToken)
    {
        if (!IsEnglishVersion(requestedVersion))
        {
            return false;
        }

        var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
        if (installedVersions.Any(SefariaLibraryService.IsHebrew))
        {
            return false;
        }

        var defaultHebrewVersion = book.Versions.FirstOrDefault(IsHebrewVersion);
        if (defaultHebrewVersion is null)
        {
            return false;
        }

        await DownloadBookVersionAsync(book, defaultHebrewVersion, cancellationToken);
        return true;
    }

    private static bool IsEnglishVersion(SefariaVersionOption? version)
    {
        return string.Equals(version?.LanguageCode, "en", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHebrewVersion(SefariaVersionOption version)
    {
        return string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadCategoryAsync(CategoryBulkTarget target)
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

                if (!hasHebrew)
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

                if (!hasTranslation)
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

    private void UpdateSelectedBookDownloadedState()
    {
        if (_selectedSefariaBook is null)
        {
            return;
        }

        _selectedSefariaBook.IsDownloaded = _sefariaLibrary.IsBookDownloaded(_selectedSefariaBook);
    }

    private void UpdateLibraryDetails()
    {
        if (_libraryTitle is null ||
            _libraryHebrewTitle is null ||
            _libraryDescription is null ||
            _libraryBookVersionLabel is null ||
            _libraryVersionBox is null ||
            _libraryCategoryVersionPanel is null ||
            _libraryCategoryHebrewVersionBox is null ||
            _libraryCategoryEnglishVersionBox is null ||
            _libraryProgress is null ||
            _libraryDownloadButton is null ||
            _libraryDeleteHebrewButton is null ||
            _libraryDeleteTranslationButton is null ||
            _libraryCancelButton is null)
        {
            return;
        }

        if (_selectedSefariaBook is not null)
        {
            _libraryTitle.Text = FormatTitle(_selectedSefariaBook.Title, _selectedSefariaBook.HebrewTitle);
            _libraryHebrewTitle.Text = string.Empty;
            _libraryDescription.Text = GetBookDescription(_selectedSefariaBook);
            _libraryBookVersionLabel.IsVisible = true;
            _libraryVersionBox.IsVisible = true;
            _libraryCategoryVersionPanel.IsVisible = false;
            _libraryVersionBox.ItemsSource = _selectedSefariaBook.Versions;
            _libraryVersionBox.SelectedItem = _selectedSefariaBook.SelectedVersion;
            _libraryVersionBox.IsEnabled = _selectedSefariaBook.Versions.Count > 0 && !_isSefariaDownloading;

            _libraryProgress.Value = _selectedSefariaBook.DownloadProgress;
            _libraryProgress.IsVisible = _selectedSefariaBook.IsDownloading;

            _libraryDownloadButton.Content = _selectedSefariaBook.IsDownloaded ? "Delete" : "Download";
            _libraryDownloadButton.IsEnabled = !_isSefariaDownloading && !_selectedSefariaBook.IsLoadingVersions;
            _libraryDeleteHebrewButton.IsVisible = false;
            _libraryDeleteHebrewButton.IsEnabled = false;
            _libraryDeleteTranslationButton.IsVisible = false;
            _libraryDeleteTranslationButton.IsEnabled = false;
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
            _libraryBookVersionLabel.IsVisible = false;
            _libraryVersionBox.IsVisible = false;
            _libraryCategoryVersionPanel.IsVisible = true;
            _libraryCategoryHebrewVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryCategoryEnglishVersionBox.IsEnabled = !_isSefariaDownloading;
            _libraryProgress.IsVisible = _isSefariaDownloading;
            _libraryDownloadButton.Content = installProgress is not null && installProgress.IsSelectedPairFullyInstalled ? "Delete" : "Download all";
            _libraryDownloadButton.IsEnabled = !_isSefariaDownloading && categoryTarget.Books.Count > 0;
            _libraryDeleteHebrewButton.IsVisible = true;
            _libraryDeleteHebrewButton.IsEnabled = !_isSefariaDownloading && installProgress is not null && installProgress.HebrewInstalledBooks > 0;
            _libraryDeleteTranslationButton.IsVisible = true;
            _libraryDeleteTranslationButton.IsEnabled = !_isSefariaDownloading && installProgress is not null && installProgress.TranslationInstalledBooks > 0;
            _libraryCancelButton.IsEnabled = _isSefariaDownloading;
            return;
        }

        _libraryTitle.Text = "Select a book";
        _libraryHebrewTitle.Text = string.Empty;
        _libraryDescription.Text = "Choose a book from the library tree to see its description.";
        _libraryBookVersionLabel.IsVisible = true;
        _libraryVersionBox.IsVisible = true;
        _libraryCategoryVersionPanel.IsVisible = false;
        _libraryVersionBox.ItemsSource = null;
        _libraryVersionBox.IsEnabled = false;
        _libraryProgress.IsVisible = false;
        _libraryDownloadButton.Content = "Download";
        _libraryDownloadButton.IsEnabled = false;
        _libraryDeleteHebrewButton.IsVisible = false;
        _libraryDeleteHebrewButton.IsEnabled = false;
        _libraryDeleteTranslationButton.IsVisible = false;
        _libraryDeleteTranslationButton.IsEnabled = false;
        _libraryCancelButton.IsEnabled = false;
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
