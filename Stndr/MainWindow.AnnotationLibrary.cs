using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stndr;

public partial class MainWindow
{
    private const string BookmarksTabTitle = "Bookmarks";
    private const string HighlightsTabTitle = "Highlights";
    private const string NotesTabTitle = "Notes";
    private string _globalBookmarkSearch = string.Empty;
    private string _globalHighlightSearch = string.Empty;
    private string _globalNoteSearch = string.Empty;
    private AnnotationLibrarySortColumn _globalBookmarkSort = AnnotationLibrarySortColumn.Book;
    private AnnotationLibrarySortColumn _globalHighlightSort = AnnotationLibrarySortColumn.Book;
    private AnnotationLibrarySortColumn _globalNoteSort = AnnotationLibrarySortColumn.Book;
    private bool _globalBookmarkSortDescending;
    private bool _globalHighlightSortDescending;
    private bool _globalNoteSortDescending;

    private void OpenBookmarksClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenOrSelectTab(BookmarksTabTitle);

    private void OpenHighlightsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenOrSelectTab(HighlightsTabTitle);

    private void OpenNotesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenOrSelectTab(NotesTabTitle);

    private Control CreateAnnotationLibraryView(ReaderAnnotationKind kind)
    {
        EnsureAnnotationsLoaded();
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(20)
        };
        var heading = new StackPanel { Spacing = 4 };
        var title = new TextBlock
        {
            Text = AnnotationLibraryTitle(kind),
            FontSize = 24,
            FontWeight = FontWeight.SemiBold
        };
        var summary = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#667085")) };
        heading.Children.Add(title);
        heading.Children.Add(summary);
        root.Children.Add(heading);

        var search = new TextBox
        {
            PlaceholderText = $"Search all {AnnotationLibraryPlural(kind)}",
            Text = GetGlobalAnnotationSearch(kind),
            Margin = new Thickness(0, 14, 0, 10),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(search);
        Grid.SetRow(search, 1);

        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var rows = new StackPanel { Spacing = 0 };
        var header = CreateAnnotationLibraryHeader(kind, RefreshRows);
        table.Children.Add(header);
        var scroller = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        table.Children.Add(scroller);
        Grid.SetRow(scroller, 1);
        root.Children.Add(table);
        Grid.SetRow(table, 2);

        search.TextChanged += (_, _) =>
        {
            SetGlobalAnnotationSearch(kind, search.Text ?? string.Empty);
            RefreshRows();
        };

        RefreshRows();
        return root;

        void RefreshRows()
        {
            rows.Children.Clear();
            var query = search.Text?.Trim() ?? string.Empty;
            IEnumerable<ReaderAnnotation> annotations = _annotationService.Annotations
                .Where(item => item.Kind == kind);
            if (!string.IsNullOrWhiteSpace(query))
            {
                annotations = annotations.Where(item => AnnotationMatchesSearch(item, query));
            }

            var sortColumn = GetGlobalAnnotationSort(kind);
            var descending = GetGlobalAnnotationSortDescending(kind);
            annotations = SortAnnotationLibrary(annotations, sortColumn, descending);
            var materialized = annotations.ToList();
            var total = _annotationService.Annotations.Count(item => item.Kind == kind);
            summary.Text = string.IsNullOrWhiteSpace(query)
                ? $"{total:N0} {(total == 1 ? AnnotationLibrarySingular(kind) : AnnotationLibraryPlural(kind))}"
                : $"{materialized.Count:N0} of {total:N0} shown";

            if (materialized.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(query)
                        ? $"No {AnnotationLibraryPlural(kind)} yet."
                        : "No matching items.",
                    Margin = new Thickness(12, 18),
                    Foreground = new SolidColorBrush(Color.Parse("#667085"))
                });
                return;
            }

            foreach (var annotation in materialized)
            {
                rows.Children.Add(CreateAnnotationLibraryRow(annotation));
            }
        }
    }

    private Control CreateAnnotationLibraryHeader(ReaderAnnotationKind kind, Action refresh)
    {
        var grid = CreateAnnotationLibraryGrid();
        grid.Background = new SolidColorBrush(Color.Parse("#F2F4F7"));
        grid.Children.Add(CreateAnnotationSortHeader("Book", AnnotationLibrarySortColumn.Book, 0, kind, refresh));
        grid.Children.Add(CreateAnnotationSortHeader("Reference", AnnotationLibrarySortColumn.Reference, 1, kind, refresh));
        grid.Children.Add(CreateAnnotationSortHeader("Date", AnnotationLibrarySortColumn.Date, 2, kind, refresh));
        var content = new TextBlock
        {
            Text = kind switch
            {
                ReaderAnnotationKind.Bookmark => "Bookmarked text",
                ReaderAnnotationKind.Highlight => "Highlighted text",
                _ => "Note"
            },
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(content);
        Grid.SetColumn(content, 3);
        var actions = new TextBlock
        {
            Text = "Actions",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 4);
        return grid;
    }

    private Button CreateAnnotationSortHeader(
        string label,
        AnnotationLibrarySortColumn column,
        int gridColumn,
        ReaderAnnotationKind kind,
        Action refresh)
    {
        var currentColumn = GetGlobalAnnotationSort(kind);
        var descending = GetGlobalAnnotationSortDescending(kind);
        var button = new Button
        {
            Content = currentColumn == column ? $"{label} {(descending ? "▼" : "▲")}" : label,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(10, 8)
        };
        button.Click += (_, _) =>
        {
            var nextDescending = currentColumn == column
                ? !descending
                : column == AnnotationLibrarySortColumn.Date;
            SetGlobalAnnotationSort(kind, column, nextDescending);
            RefreshOpenAnnotationLibraryTab(kind);
        };
        Grid.SetColumn(button, gridColumn);
        return button;
    }

    private Control CreateAnnotationLibraryRow(ReaderAnnotation annotation)
    {
        var grid = CreateAnnotationLibraryGrid();
        grid.MinHeight = 58;
        grid.Children.Add(CreateAnnotationCell(annotation.WorkTitle, 0, FontWeight.SemiBold));
        grid.Children.Add(CreateAnnotationCell(GetAnnotationListReference(annotation), 1));
        grid.Children.Add(CreateAnnotationCell(annotation.CreatedAtUtc.ToLocalTime().ToString("g"), 2));
        var contentText = annotation.Kind == ReaderAnnotationKind.Note
            ? annotation.Note
            : string.IsNullOrWhiteSpace(annotation.SelectedText) ? "Whole passage" : annotation.SelectedText;
        if (annotation.Kind == ReaderAnnotationKind.Highlight)
        {
            contentText = $"{annotation.HighlightColor} · {contentText}";
        }
        grid.Children.Add(CreateAnnotationCell(contentText, 3));

        var open = new Button { Content = "Open", Padding = new Thickness(9, 4) };
        open.Click += (_, _) => OpenReaderAnnotation(annotation);
        var delete = new Button { Content = "Delete", Padding = new Thickness(9, 4) };
        delete.Click += (_, _) => RemoveReaderAnnotation(annotation.Id);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { open }
        };
        if (annotation.Kind == ReaderAnnotationKind.Note)
        {
            var edit = new Button { Content = "Edit", Padding = new Thickness(9, 4) };
            edit.Click += async (_, _) => await EditReaderNoteAsync(annotation);
            actions.Children.Add(edit);
        }
        actions.Children.Add(delete);
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 4);

        var row = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(annotation.Kind == ReaderAnnotationKind.Highlight
                ? GetHighlightColorHex(annotation.HighlightColor)
                : "#EAECF0")),
            BorderThickness = annotation.Kind == ReaderAnnotationKind.Highlight
                ? new Thickness(4, 0, 0, 1)
                : new Thickness(0, 0, 0, 1),
            Child = grid
        };
        row.DoubleTapped += (_, e) =>
        {
            OpenReaderAnnotation(annotation);
            e.Handled = true;
        };
        return row;
    }

    private static string AnnotationLibraryTitle(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => BookmarksTabTitle,
        ReaderAnnotationKind.Highlight => HighlightsTabTitle,
        _ => NotesTabTitle
    };

    private static string AnnotationLibrarySingular(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => "bookmark",
        ReaderAnnotationKind.Highlight => "highlight",
        _ => "note"
    };

    private static string AnnotationLibraryPlural(ReaderAnnotationKind kind) =>
        $"{AnnotationLibrarySingular(kind)}s";

    private string GetGlobalAnnotationSearch(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _globalBookmarkSearch,
        ReaderAnnotationKind.Highlight => _globalHighlightSearch,
        _ => _globalNoteSearch
    };

    private void SetGlobalAnnotationSearch(ReaderAnnotationKind kind, string value)
    {
        if (kind == ReaderAnnotationKind.Bookmark) _globalBookmarkSearch = value;
        else if (kind == ReaderAnnotationKind.Highlight) _globalHighlightSearch = value;
        else _globalNoteSearch = value;
    }

    private AnnotationLibrarySortColumn GetGlobalAnnotationSort(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _globalBookmarkSort,
        ReaderAnnotationKind.Highlight => _globalHighlightSort,
        _ => _globalNoteSort
    };

    private bool GetGlobalAnnotationSortDescending(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _globalBookmarkSortDescending,
        ReaderAnnotationKind.Highlight => _globalHighlightSortDescending,
        _ => _globalNoteSortDescending
    };

    private void SetGlobalAnnotationSort(
        ReaderAnnotationKind kind,
        AnnotationLibrarySortColumn column,
        bool descending)
    {
        if (kind == ReaderAnnotationKind.Bookmark)
        {
            _globalBookmarkSort = column;
            _globalBookmarkSortDescending = descending;
        }
        else if (kind == ReaderAnnotationKind.Highlight)
        {
            _globalHighlightSort = column;
            _globalHighlightSortDescending = descending;
        }
        else
        {
            _globalNoteSort = column;
            _globalNoteSortDescending = descending;
        }
    }

    private static string GetHighlightColorHex(ReaderHighlightColor color) => color switch
    {
        ReaderHighlightColor.Green => "#B7EB8F",
        ReaderHighlightColor.Blue => "#91D5FF",
        ReaderHighlightColor.Pink => "#FFADD2",
        ReaderHighlightColor.Purple => "#D3ADF7",
        _ => "#FFE58F"
    };

    private string GetAnnotationListReference(ReaderAnnotation annotation)
    {
        var hebrew = GetAnnotationHebrewReference(annotation);
        return string.IsNullOrWhiteSpace(hebrew)
            ? annotation.DisplayReference
            : $"{annotation.DisplayReference} · {hebrew}";
    }

    private string GetAnnotationHebrewReference(ReaderAnnotation annotation)
    {
        var book = _sefariaLibrary.GetInstalledBookByKey(annotation.BookKey) ??
            _sefariaLibrary.GetInstalledVersionsForTitle(annotation.WorkTitle).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(book?.HebrewTitle))
        {
            return string.Empty;
        }

        return FormatAnnotationReference(
            FormatAnnotationHebrewReference(annotation.StartReference, annotation.WorkTitle, book.HebrewTitle),
            FormatAnnotationHebrewReference(annotation.EndReference, annotation.WorkTitle, book.HebrewTitle));
    }

    internal static string FormatAnnotationHebrewReference(
        string reference,
        string englishWorkTitle,
        string hebrewWorkTitle)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(hebrewWorkTitle))
        {
            return string.Empty;
        }

        var location = reference.Trim();
        if (!string.IsNullOrWhiteSpace(englishWorkTitle) &&
            location.StartsWith(englishWorkTitle, StringComparison.OrdinalIgnoreCase))
        {
            location = location[englishWorkTitle.Length..].TrimStart(' ', ',');
        }

        location = Regex.Replace(location, @"\d+", match =>
            int.TryParse(match.Value, out var number) ? ToHebrewNumber(number) : match.Value);
        location = Regex.Replace(location, @"(?<=[\u05d0-\u05ea])a(?=\b|:)", " א", RegexOptions.IgnoreCase);
        location = Regex.Replace(location, @"(?<=[\u05d0-\u05ea])b(?=\b|:)", " ב", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(location)
            ? hebrewWorkTitle.Trim()
            : $"{hebrewWorkTitle.Trim()} {location}";
    }

    private static Grid CreateAnnotationLibraryGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("2*,1.35*,1.35*,3*,Auto"),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBlock CreateAnnotationCell(string text, int column, FontWeight? weight = null)
    {
        var cell = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 52,
            Margin = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = weight ?? FontWeight.Normal
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static IEnumerable<ReaderAnnotation> SortAnnotationLibrary(
        IEnumerable<ReaderAnnotation> annotations,
        AnnotationLibrarySortColumn column,
        bool descending)
    {
        Func<ReaderAnnotation, object> selector = column switch
        {
            AnnotationLibrarySortColumn.Reference => item => $"{item.WorkTitle}\u0000{NaturalAnnotationReferenceKey(item.StartReference)}",
            AnnotationLibrarySortColumn.Date => item => item.CreatedAtUtc,
            _ => item => item.WorkTitle
        };
        return descending
            ? annotations.OrderByDescending(selector).ThenByDescending(item => item.CreatedAtUtc)
            : annotations.OrderBy(selector).ThenBy(item => item.CreatedAtUtc);
    }

    internal static string NaturalAnnotationReferenceKey(string reference) =>
        Regex.Replace(reference ?? string.Empty, @"\d+", match =>
            long.TryParse(match.Value, out var number) ? number.ToString("D12") : match.Value);

    private void RefreshOpenAnnotationLibraryTabs()
    {
        RefreshOpenAnnotationLibraryTab(ReaderAnnotationKind.Bookmark);
        RefreshOpenAnnotationLibraryTab(ReaderAnnotationKind.Highlight);
        RefreshOpenAnnotationLibraryTab(ReaderAnnotationKind.Note);
    }

    private void RefreshOpenAnnotationLibraryTab(ReaderAnnotationKind kind)
    {
        if (_tabs is null) return;
        var title = AnnotationLibraryTitle(kind);
        var tab = _tabs.FirstOrDefault(item => string.Equals(item.Tag as string, title, StringComparison.Ordinal));
        if (tab is not null) ReplaceTabContent(tab, CreateAnnotationLibraryView(kind));
    }

    private enum AnnotationLibrarySortColumn
    {
        Book,
        Reference,
        Date
    }
}
