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
            Text = $"Data folder: {_sefariaLibrary.DataFolder}",
            TextWrapping = TextWrapping.Wrap
        };
        _libraryVersionBox = new ComboBox
        {
            MinWidth = 320,
            IsEnabled = false
        };
        _libraryVersionBox.SelectionChanged += OnLibraryVersionChanged;

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
                    new TextBlock
                    {
                        Text = "Version",
                        FontWeight = FontWeight.SemiBold
                    },
                    _libraryVersionBox,
                    _libraryProgress,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            _libraryDownloadButton,
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
        UpdateLibraryDetails();

        try
        {
            _sefariaRoot = await _sefariaLibrary.LoadLibraryAsync(CancellationToken.None);
            _libraryTree.ItemsSource = _sefariaRoot.Contents
                .OrderBy(n => n.Order)
                .Select(CreateLibraryTreeItem)
                .ToList();
            _libraryStatus.Text = $"Loaded from {_sefariaLibrary.IndexFilePath}";
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
        var horizontalOffset = _libraryTreeScrollViewer?.Offset.X ?? 0;
        var item = e.AddedItems.Count > 0 ? e.AddedItems[0] as TreeViewItem : null;
        _selectedSefariaBook = item?.DataContext as SefariaBookNode;
        UpdateLibraryDetails();
        RestoreLibraryTreeHorizontalOffset(horizontalOffset);

        if (_selectedSefariaBook is not null)
        {
            await EnsureVersionsLoadedAsync(_selectedSefariaBook);
            UpdateSelectedBookDownloadedState();
            UpdateLibraryDetails();
            RestoreLibraryTreeHorizontalOffset(horizontalOffset);
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
            book.Versions = await _sefariaLibrary.GetAvailableVersionsAsync(book.Title, CancellationToken.None);
            book.SelectedVersion = book.Versions.FirstOrDefault();
            book.IsVersionsLoaded = true;
        }
        catch (Exception ex)
        {
            book.Versions = new List<SefariaVersionOption>();
            book.IsVersionsLoaded = true;
            if (_libraryStatus is not null)
            {
                _libraryStatus.Text = $"Could not load versions for {book.Title}: {ex.Message}";
            }
        }
        finally
        {
            book.IsLoadingVersions = false;
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
            var progress = new Progress<double>(percent =>
            {
                book.DownloadProgress = percent;
                if (_libraryProgress is not null)
                {
                    _libraryProgress.Value = percent;
                }
                SetLibraryStatus($"Downloading {book.Title}: {percent:0}%");
            });

            await _sefariaLibrary.DownloadBookAsync(book, progress, _sefariaDownloadCts.Token);
            book.IsDownloaded = true;
            book.DownloadProgress = 100;
            RefreshInstalledBooksTree();
            RefreshOpenReaderTabForBook(book);
            SetLibraryStatus($"Downloaded {book.Title} to {_sefariaLibrary.GetExistingDownloadPath(book)}");
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
            _libraryVersionBox is null ||
            _libraryProgress is null ||
            _libraryDownloadButton is null ||
            _libraryCancelButton is null)
        {
            return;
        }

        if (_selectedSefariaBook is null)
        {
            _libraryTitle.Text = "Select a book";
            _libraryHebrewTitle.Text = string.Empty;
            _libraryDescription.Text = "Choose a book from the library tree to see its description.";
            _libraryVersionBox.ItemsSource = null;
            _libraryVersionBox.IsEnabled = false;
            _libraryProgress.IsVisible = false;
            _libraryDownloadButton.Content = "Download";
            _libraryDownloadButton.IsEnabled = false;
            _libraryCancelButton.IsEnabled = false;
            return;
        }

        _libraryTitle.Text = FormatTitle(_selectedSefariaBook.Title, _selectedSefariaBook.HebrewTitle);
        _libraryHebrewTitle.Text = string.Empty;
        _libraryDescription.Text = GetBookDescription(_selectedSefariaBook);
        _libraryVersionBox.ItemsSource = _selectedSefariaBook.Versions;
        _libraryVersionBox.SelectedItem = _selectedSefariaBook.SelectedVersion;
        _libraryVersionBox.IsEnabled = _selectedSefariaBook.Versions.Count > 0 && !_isSefariaDownloading;

        _libraryProgress.Value = _selectedSefariaBook.DownloadProgress;
        _libraryProgress.IsVisible = _selectedSefariaBook.IsDownloading;

        _libraryDownloadButton.Content = _selectedSefariaBook.IsDownloaded ? "Delete" : "Download";
        _libraryDownloadButton.IsEnabled = !_isSefariaDownloading && !_selectedSefariaBook.IsLoadingVersions;
        _libraryCancelButton.IsEnabled = _selectedSefariaBook.IsDownloading;
    }

    private void SetLibraryStatus(string message)
    {
        if (_libraryStatus is not null)
        {
            _libraryStatus.Text = message;
        }
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
