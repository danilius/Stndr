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
}
