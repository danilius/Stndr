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
    private int _installedBooksTreeRefreshGeneration;

    private void RefreshInstalledBooksTree()
    {
        _ = RefreshInstalledBooksTreeAsync();
    }

    private async Task RefreshInstalledBooksTreeAsync()
    {
        if (_leftPanelBody is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _installedBooksTreeRefreshGeneration);
        ObservableCollection<object> roots;
        try
        {
            roots = await Task.Run(() => _sefariaLibrary.BuildInstalledTree());
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_leftPanelBody is null || generation != _installedBooksTreeRefreshGeneration)
            {
                return;
            }

            _leftPanelBody.ItemsSource = roots
                .Cast<object>()
                .Select(CreateInstalledBookTreeItem)
                .ToList();
        });
    }

    private TreeViewItem CreateInstalledBookTreeItem(object node)
    {
        var item = new TreeViewItem
        {
            Header = node switch
            {
                InstalledSefariaCategory installedCategory => FormatTitle(installedCategory.Title, installedCategory.HebrewTitle),
                InstalledSefariaBook book => book.DisplayVersion,
                _ => "Item"
            },
            DataContext = node
        };

        item.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) =>
            {
                if (e.Source is not Visual source ||
                    !ReferenceEquals(source.FindAncestorOfType<TreeViewItem>(true), item) ||
                    !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                if (node is InstalledSefariaCategory { IsBookTitle: true } bookTitleCategory)
                {
                    if (_sefariaLibrary.GetInstalledVersionsForTitle(bookTitleCategory.Title).FirstOrDefault() is { } firstVersion)
                    {
                        OpenInstalledBook(firstVersion);
                    }
                }
                else if (node is InstalledSefariaCategory)
                {
                    item.IsExpanded = !item.IsExpanded;
                }
                else if (node is InstalledSefariaBook book)
                {
                    OpenInstalledBook(book);
                }

                e.Handled = true;
            },
            RoutingStrategies.Tunnel,
            true);

        if (node is InstalledSefariaCategory { IsBookTitle: false } category)
        {
            item.ItemsSource = category.Children
                .Cast<object>()
                .Select(CreateInstalledBookTreeItem)
                .ToList();
        }

        return item;
    }

    private string FormatTitle(string? englishTitle, string? hebrewTitle)
    {
        var english = string.IsNullOrWhiteSpace(englishTitle) ? "Untitled" : englishTitle;

        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => string.IsNullOrWhiteSpace(hebrewTitle)
                ? english
                : hebrewTitle,
            InstalledBookTitleDisplay.English => english,
            _ => string.IsNullOrWhiteSpace(hebrewTitle)
                ? english
                : $"{hebrewTitle} / {english}"
        };
    }

    private void OnInstalledBooksSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_leftPanelSearchSuggestionsContainer is null || _leftPanelSearchSuggestions is null)
            return;

        var query = _leftPanelSearchBox?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _leftPanelSearchSuggestionsContainer.IsVisible = false;
            return;
        }

        var matches = _sefariaLibrary.GetInstalledBooks()
            .Where(b =>
                b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (b.HebrewTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(10)
            .ToList();

        _leftPanelSearchSuggestions.ItemTemplate = new FuncDataTemplate<InstalledSefariaBook>((book, _) =>
            new TextBlock
            {
                Text = FormatTitle(book.Title, book.HebrewTitle),
                Padding = new Thickness(6, 4)
            });
        _leftPanelSearchSuggestions.ItemsSource = matches;
        _leftPanelSearchSuggestionsContainer.IsVisible = matches.Count > 0;
    }

    private void OnInstalledBooksSearchSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not InstalledSefariaBook book)
            return;

        OpenInstalledBook(book);

        if (_leftPanelSearchSuggestionsContainer is not null)
            _leftPanelSearchSuggestionsContainer.IsVisible = false;
        if (_leftPanelSearchBox is not null)
            _leftPanelSearchBox.Text = string.Empty;
        if (_leftPanelSearchSuggestions is not null)
            _leftPanelSearchSuggestions.SelectedItem = null;
    }

    private async void OnInstalledBooksSearchLostFocus(object? sender, RoutedEventArgs e)
    {
        // Small delay so a click on a suggestion registers before we hide
        await Task.Delay(150);
        if (_leftPanelSearchSuggestionsContainer is not null)
            _leftPanelSearchSuggestionsContainer.IsVisible = false;
    }
}
