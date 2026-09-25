using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private readonly ReaderAnnotationService _annotationService = new();
    private string _annotationStorageRoot = string.Empty;
    private ReaderAnnotationVisibility _annotationVisibility = ReaderAnnotationVisibility.All;
    private int _readerToolsTabIndex;
    private int _bookmarkSortIndex;
    private int _highlightSortIndex;
    private int _noteSortIndex;
    private string _bookmarkSearch = string.Empty;
    private string _highlightSearch = string.Empty;
    private string _noteSearch = string.Empty;

    private void EnsureAnnotationsLoaded()
    {
        var root = _settings.DataStorageFolder ?? string.Empty;
        if (string.Equals(root, _annotationStorageRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _annotationStorageRoot = root;
        _annotationService.SetStorageRootFolder(string.IsNullOrWhiteSpace(root) ? null : root);
        _annotationVisibility = _annotationService.Visibility;
    }

    private bool TryHandleReaderAnnotationShortcut(KeyEventArgs e)
    {
        if (e.Key != Key.D || e.KeyModifiers != KeyModifiers.Control ||
            e.Source is TextBox || _centerTabs?.SelectedItem is not TabItem selectedTab ||
            !_openReaderTabs.TryGetValue(selectedTab, out var state))
        {
            return false;
        }

        AddReaderAnnotation(state, ReaderAnnotationKind.Bookmark, CreateSelectedRowAnchor(state));
        return true;
    }

    private ReaderAnnotationAnchor CreateSelectedRowAnchor(ReaderTabState state)
    {
        var row = state.SelectedReaderRow ?? state.ReaderRows.FirstOrDefault(item => !item.IsChapterHeading);
        if (row is null)
        {
            return new ReaderAnnotationAnchor();
        }

        var reference = GetReaderRowWebReference(state, row);
        return new ReaderAnnotationAnchor
        {
            StartReference = reference,
            EndReference = reference,
            Segments = new List<ReaderAnnotationSegment>
            {
                new()
                {
                    Reference = reference,
                    Language = SefariaLibraryService.IsHebrew(state.Primary) ? "he" : "en",
                    Text = string.Empty
                }
            }
        };
    }

    private static ReaderAnnotationAnchor ReadAnnotationAnchor(JsonElement root)
    {
        var anchor = new ReaderAnnotationAnchor
        {
            StartReference = ReadString(root, "startRef", ReadString(root, "ref")),
            EndReference = ReadString(root, "endRef"),
            SelectedText = ReadString(root, "text"),
            AnnotationId = ReadString(root, "annotationId")
        };
        if (string.IsNullOrWhiteSpace(anchor.EndReference))
        {
            anchor.EndReference = anchor.StartReference;
        }

        if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segments.EnumerateArray())
            {
                var reference = ReadString(segment, "ref");
                if (string.IsNullOrWhiteSpace(reference)) continue;
                anchor.Segments.Add(new ReaderAnnotationSegment
                {
                    Reference = reference,
                    Language = ReadString(segment, "language"),
                    Text = ReadString(segment, "text")
                });
            }
        }

        return anchor;
    }

    private static string ReadString(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private void AddReaderAnnotation(
        ReaderTabState state,
        ReaderAnnotationKind kind,
        ReaderAnnotationAnchor anchor,
        ReaderHighlightColor color = ReaderHighlightColor.Yellow,
        string note = "")
    {
        if (string.IsNullOrWhiteSpace(anchor.StartReference))
        {
            anchor = CreateSelectedRowAnchor(state);
        }
        if (string.IsNullOrWhiteSpace(anchor.StartReference)) return;

        EnsureAnnotationsLoaded();
        var annotation = new ReaderAnnotation
        {
            Kind = kind,
            WorkTitle = state.WorkTitle,
            BookKey = state.Primary.Key,
            StartReference = anchor.StartReference,
            EndReference = string.IsNullOrWhiteSpace(anchor.EndReference) ? anchor.StartReference : anchor.EndReference,
            SelectedText = CollapseAnnotationWhitespace(anchor.SelectedText),
            Note = note.Trim(),
            HighlightColor = color,
            Segments = anchor.Segments
        };
        if (annotation.Segments.Count == 0)
        {
            annotation.Segments.Add(new ReaderAnnotationSegment
            {
                Reference = annotation.StartReference,
                Text = annotation.SelectedText
            });
        }

        var duplicate = _annotationService.FindDuplicate(annotation);
        if (duplicate is not null)
        {
            if (kind != ReaderAnnotationKind.Highlight || duplicate.HighlightColor == color) return;
            _annotationService.Remove(duplicate.Id);
        }
        _annotationService.Add(annotation);
        RefreshAnnotationPresentation(state.WorkTitle);
    }

    private async Task AddReaderNoteAsync(ReaderTabState state, ReaderAnnotationAnchor anchor)
    {
        var note = await ShowReaderNoteDialogAsync(anchor, string.Empty);
        if (note is null) return;
        AddReaderAnnotation(state, ReaderAnnotationKind.Note, anchor, note: note);
    }

    private async Task EditReaderNoteAsync(ReaderAnnotation annotation)
    {
        var anchor = new ReaderAnnotationAnchor
        {
            StartReference = annotation.StartReference,
            EndReference = annotation.EndReference,
            SelectedText = annotation.SelectedText,
            Segments = annotation.Segments
        };
        var note = await ShowReaderNoteDialogAsync(anchor, annotation.Note);
        if (note is null) return;
        if (_annotationService.UpdateNote(annotation.Id, note))
        {
            RefreshAnnotationPresentation(annotation.WorkTitle);
        }
    }

    private async Task<string?> ShowReaderNoteDialogAsync(ReaderAnnotationAnchor anchor, string existingNote)
    {
        var editor = new TextBox
        {
            Text = existingNote,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var save = new Button
        {
            Content = "Save note",
            IsDefault = true,
            MinWidth = 92,
            IsEnabled = !string.IsNullOrWhiteSpace(existingNote)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76 };
        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(existingNote) ? "Add note" : "Edit note",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        editor.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(editor.Text);
        save.Click += (_, _) => dialog.Close(editor.Text?.Trim() ?? string.Empty);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = FormatAnnotationReference(anchor.StartReference, anchor.EndReference), FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = CollapseAnnotationWhitespace(anchor.SelectedText),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 90,
                    Foreground = new SolidColorBrush(Color.Parse("#475467")),
                    IsVisible = !string.IsNullOrWhiteSpace(anchor.SelectedText)
                },
                editor,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save }
                }
            }
        };
        dialog.Opened += (_, _) => editor.Focus();
        return await dialog.ShowDialog<string?>(this);
    }

    private void RemoveReaderAnnotation(string id)
    {
        EnsureAnnotationsLoaded();
        var annotation = _annotationService.Annotations.FirstOrDefault(item => item.Id == id);
        if (annotation is null || !_annotationService.Remove(id)) return;
        RefreshAnnotationPresentation(annotation.WorkTitle);
    }

    private void RemoveHighlightAtAnchor(ReaderTabState state, ReaderAnnotationAnchor anchor)
    {
        EnsureAnnotationsLoaded();
        var highlight = !string.IsNullOrWhiteSpace(anchor.AnnotationId)
            ? _annotationService.Annotations.FirstOrDefault(item =>
                item.Kind == ReaderAnnotationKind.Highlight && item.Id == anchor.AnnotationId)
            : _annotationService.Annotations.FirstOrDefault(item =>
                item.Kind == ReaderAnnotationKind.Highlight &&
                string.Equals(item.WorkTitle, state.WorkTitle, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.StartReference, anchor.StartReference, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(anchor.SelectedText) ||
                 string.Equals(CollapseAnnotationWhitespace(item.SelectedText), CollapseAnnotationWhitespace(anchor.SelectedText), StringComparison.Ordinal)));
        if (highlight is not null) RemoveReaderAnnotation(highlight.Id);
    }

    private void RefreshAnnotationPresentation(string workTitle)
    {
        foreach (var state in _openReaderTabs.Values.Where(item =>
                     string.Equals(item.WorkTitle, workTitle, StringComparison.OrdinalIgnoreCase)))
        {
            RenderReaderWebView(state);
        }
        UpdateReaderTools();
        RefreshOpenAnnotationLibraryTabs();
    }

    private string BuildReaderAnnotationJson(ReaderTabState state)
    {
        EnsureAnnotationsLoaded();
        var visible = _annotationService.Annotations
            .Where(item => string.Equals(item.WorkTitle, state.WorkTitle, StringComparison.OrdinalIgnoreCase))
            .Where(item => IsAnnotationKindVisible(item.Kind))
            .Select(item => new
            {
                id = item.Id,
                kind = item.Kind.ToString().ToLowerInvariant(),
                startRef = item.StartReference,
                endRef = item.EndReference,
                text = item.SelectedText,
                note = item.Note,
                color = item.HighlightColor.ToString().ToLowerInvariant(),
                segments = item.Segments.Select(segment => new
                {
                    @ref = segment.Reference,
                    language = segment.Language,
                    text = segment.Text
                })
            });
        return JsonSerializer.Serialize(visible).Replace("</", "<\\/", StringComparison.Ordinal);
    }

    private bool IsAnnotationKindVisible(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _annotationVisibility.HasFlag(ReaderAnnotationVisibility.Bookmarks),
        ReaderAnnotationKind.Highlight => _annotationVisibility.HasFlag(ReaderAnnotationVisibility.Highlights),
        ReaderAnnotationKind.Note => _annotationVisibility.HasFlag(ReaderAnnotationVisibility.Notes),
        _ => false
    };

    private Control CreateAnnotationVisibilityToggle(ReaderTabState state, ReaderAnnotationKind kind, string label)
    {
        var flag = kind switch
        {
            ReaderAnnotationKind.Bookmark => ReaderAnnotationVisibility.Bookmarks,
            ReaderAnnotationKind.Highlight => ReaderAnnotationVisibility.Highlights,
            _ => ReaderAnnotationVisibility.Notes
        };
        var checkBox = new CheckBox
        {
            Content = label,
            IsChecked = _annotationVisibility.HasFlag(flag),
            Margin = new Thickness(0, 2)
        };
        checkBox.IsCheckedChanged += (_, _) =>
        {
            _annotationVisibility = checkBox.IsChecked == true
                ? _annotationVisibility | flag
                : _annotationVisibility & ~flag;
            _annotationService.SetVisibility(_annotationVisibility);
            RefreshAnnotationPresentation(state.WorkTitle);
            RefreshReaderDisplayFlyout(state);
        };
        return checkBox;
    }

    private void WrapReaderToolsInAnnotationTabs(ReaderTabState state)
    {
        if (_rightPanelBody is null) return;
        var tools = _rightPanelBody.Children.ToList();
        _rightPanelBody.Children.Clear();
        var toolsPanel = new StackPanel { Spacing = 8 };
        foreach (var control in tools) toolsPanel.Children.Add(control);

        var tabs = new TabControl
        {
            SelectedIndex = Math.Clamp(_readerToolsTabIndex, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                new TabItem { Header = "Tools", Content = toolsPanel },
                new TabItem { Header = "Bookmarks", Content = CreateAnnotationIndex(state, ReaderAnnotationKind.Bookmark) },
                new TabItem { Header = "Highlights", Content = CreateAnnotationIndex(state, ReaderAnnotationKind.Highlight) },
                new TabItem { Header = "Notes", Content = CreateAnnotationIndex(state, ReaderAnnotationKind.Note) }
            }
        };
        tabs.SelectionChanged += (_, _) => _readerToolsTabIndex = tabs.SelectedIndex;
        _rightPanelBody.Children.Add(tabs);
    }

    private Control CreateAnnotationIndex(ReaderTabState state, ReaderAnnotationKind kind)
    {
        EnsureAnnotationsLoaded();
        var searchValue = GetAnnotationIndexSearch(kind);
        var sortIndex = GetAnnotationIndexSort(kind);
        var search = new TextBox { PlaceholderText = $"Search {kind.ToString().ToLowerInvariant()}s", Text = searchValue };
        var sort = new ComboBox
        {
            ItemsSource = new[] { "Newest first", "Reference order", "Oldest first" },
            SelectedIndex = Math.Clamp(sortIndex, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var results = new StackPanel { Spacing = 8 };

        void RefreshResults()
        {
            results.Children.Clear();
            var query = search.Text?.Trim() ?? string.Empty;
            var items = _annotationService.Annotations.Where(item =>
                item.Kind == kind &&
                string.Equals(item.WorkTitle, state.WorkTitle, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(item => AnnotationMatchesSearch(item, query));
            }
            items = sort.SelectedIndex switch
            {
                1 => items.OrderBy(item => NaturalAnnotationReferenceKey(item.StartReference), StringComparer.OrdinalIgnoreCase),
                2 => items.OrderBy(item => item.CreatedAtUtc),
                _ => items.OrderByDescending(item => item.CreatedAtUtc)
            };
            var materialized = items.Take(500).ToList();
            if (materialized.Count == 0)
            {
                results.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(query)
                        ? $"No {kind.ToString().ToLowerInvariant()}s in this view."
                        : "No matching items.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#667085"))
                });
                return;
            }
            foreach (var annotation in materialized) results.Children.Add(CreateAnnotationIndexCard(annotation));
        }

        search.TextChanged += (_, _) =>
        {
            SetAnnotationIndexSearch(kind, search.Text ?? string.Empty);
            RefreshResults();
        };
        sort.SelectionChanged += (_, _) =>
        {
            SetAnnotationIndexSort(kind, sort.SelectedIndex);
            RefreshResults();
        };
        RefreshResults();
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { search, sort, results }
        };
    }

    private string GetAnnotationIndexSearch(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _bookmarkSearch,
        ReaderAnnotationKind.Highlight => _highlightSearch,
        _ => _noteSearch
    };

    private void SetAnnotationIndexSearch(ReaderAnnotationKind kind, string value)
    {
        if (kind == ReaderAnnotationKind.Bookmark) _bookmarkSearch = value;
        else if (kind == ReaderAnnotationKind.Highlight) _highlightSearch = value;
        else _noteSearch = value;
    }

    private int GetAnnotationIndexSort(ReaderAnnotationKind kind) => kind switch
    {
        ReaderAnnotationKind.Bookmark => _bookmarkSortIndex,
        ReaderAnnotationKind.Highlight => _highlightSortIndex,
        _ => _noteSortIndex
    };

    private void SetAnnotationIndexSort(ReaderAnnotationKind kind, int value)
    {
        if (kind == ReaderAnnotationKind.Bookmark) _bookmarkSortIndex = value;
        else if (kind == ReaderAnnotationKind.Highlight) _highlightSortIndex = value;
        else _noteSortIndex = value;
    }

    private Control CreateAnnotationIndexCard(ReaderAnnotation annotation)
    {
        var open = new Button { Content = "Open", Padding = new Thickness(6, 2) };
        var delete = new Button { Content = "Delete", Padding = new Thickness(6, 2) };
        open.Click += (_, _) => OpenReaderAnnotation(annotation);
        delete.Click += (_, _) => RemoveReaderAnnotation(annotation.Id);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { open } };
        if (annotation.Kind == ReaderAnnotationKind.Note)
        {
            var edit = new Button { Content = "Edit", Padding = new Thickness(6, 2) };
            edit.Click += async (_, _) => await EditReaderNoteAsync(annotation);
            actions.Children.Add(edit);
        }
        actions.Children.Add(delete);
        var isHighlight = annotation.Kind == ReaderAnnotationKind.Highlight;
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse(isHighlight
                ? GetHighlightColorHex(annotation.HighlightColor)
                : "#D0D5DD")),
            BorderThickness = isHighlight ? new Thickness(4, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{GetAnnotationListReference(annotation)} · {annotation.CreatedAtUtc.ToLocalTime():g}",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 34,
                        Foreground = new SolidColorBrush(Color.Parse("#475467"))
                    },
                    new TextBlock
                    {
                        Text = annotation.Kind == ReaderAnnotationKind.Note ? annotation.Note : annotation.SelectedText,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 54,
                        IsVisible = annotation.Kind == ReaderAnnotationKind.Note || !string.IsNullOrWhiteSpace(annotation.SelectedText)
                    },
                    actions
                }
            }
        };
        card.DoubleTapped += (_, e) =>
        {
            OpenReaderAnnotation(annotation);
            e.Handled = true;
        };
        return card;
    }

    private void OpenReaderAnnotation(ReaderAnnotation annotation)
    {
        var book = _sefariaLibrary.GetInstalledBookByKey(annotation.BookKey) ??
            _sefariaLibrary.GetInstalledVersionsForTitle(annotation.WorkTitle).FirstOrDefault();
        if (book is null) return;
        OpenInstalledBook(book);
        var pair = _openReaderTabs.FirstOrDefault(candidate =>
            string.Equals(candidate.Value.WorkTitle, annotation.WorkTitle, StringComparison.OrdinalIgnoreCase));
        if (pair.Key is null) return;
        pair.Value.SearchHighlightReferenceWithinWork = string.Empty;
        pair.Value.SearchHighlightTerms.Clear();
        pair.Value.PendingExactReferenceWithinWork = annotation.StartReference;
        RenderReaderContent(pair.Value);
        Dispatcher.UIThread.Post(
            () => ScrollReaderStateToReference(pair.Value, annotation.StartReference),
            DispatcherPriority.Background);
    }

    private bool AnnotationMatchesSearch(ReaderAnnotation annotation, string query) =>
        annotation.WorkTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        annotation.DisplayReference.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        GetAnnotationHebrewReference(annotation).Contains(query, StringComparison.OrdinalIgnoreCase) ||
        annotation.SelectedText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        annotation.Note.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        annotation.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string FormatAnnotationReference(string start, string end) =>
        string.IsNullOrWhiteSpace(end) || string.Equals(start, end, StringComparison.Ordinal)
            ? start
            : $"{start}–{end}";

    private static string CollapseAnnotationWhitespace(string value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class ReaderAnnotationAnchor
    {
        public string StartReference { get; set; } = string.Empty;
        public string EndReference { get; set; } = string.Empty;
        public string SelectedText { get; set; } = string.Empty;
        public string AnnotationId { get; set; } = string.Empty;
        public List<ReaderAnnotationSegment> Segments { get; set; } = new();
    }
}
