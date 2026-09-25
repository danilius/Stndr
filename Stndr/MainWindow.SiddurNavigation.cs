using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stndr;

public partial class MainWindow
{
    private Control CreateSiddurNavigationTools(ReaderTabState state)
    {
        state.NavigationTopicExpanders.Clear();
        state.SiddurNavigationButtons.Clear();
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        var search = new TextBox
        {
            PlaceholderText = "Find a prayer…",
            Text = state.NavigationJumpQuery,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var tree = new StackPanel { Spacing = 2 };
        foreach (var node in state.SiddurNavigation)
            tree.Children.Add(CreateSiddurTreeBranch(state, node));
        var results = new StackPanel { Spacing = 2 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var expand in new[] { true, false })
        {
            var button = new Button { Content = expand ? "Expand all" : "Collapse all", Padding = new Thickness(6, 2) };
            button.Click += (_, _) =>
            {
                state.NavigationTopicsAllExpanded = expand;
                foreach (var (key, expander) in state.NavigationTopicExpanders)
                {
                    state.ExpandedNavigationTopics[key] = expand;
                    expander.IsExpanded = expand;
                }
                SaveLayoutState();
            };
            actions.Children.Add(button);
        }
        void Filter()
        {
            state.NavigationJumpQuery = search.Text ?? "";
            var searching = !string.IsNullOrWhiteSpace(state.NavigationJumpQuery);
            tree.IsVisible = !searching;
            actions.IsVisible = !searching;
            results.IsVisible = searching;
            results.Children.Clear();
            if (!searching) return;
            foreach (var root in state.SiddurNavigation)
                AddSiddurSearchResults(state, root, new(), results);
            if (results.Children.Count == 0)
                results.Children.Add(new TextBlock { Text = "No matching prayers.", TextWrapping = TextWrapping.Wrap });
        }
        search.TextChanged += (_, _) => { Filter(); SaveLayoutState(); };
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { search.Text = ""; e.Handled = true; }
            if (e.Key == Key.Enter && results.Children.OfType<Button>().FirstOrDefault()?.Tag is SiddurNavigationNode node)
            {
                JumpToSiddurNode(state, node);
                e.Handled = true;
            }
        };
        panel.Children.Add(search);
        panel.Children.Add(actions);
        panel.Children.Add(tree);
        panel.Children.Add(results);
        Filter();
        SyncSiddurNavigation(state, state.CurrentChapterKey);
        return panel;
    }

    private Control CreateSiddurTreeBranch(ReaderTabState state, SiddurNavigationNode node)
    {
        var button = CreateSiddurTitleButton(state, node);
        state.SiddurNavigationButtons[node.Part.Key] = button;
        if (node.Children.Count == 0)
        {
            button.Margin = new Thickness(22, 0, 0, 0);
            return button;
        }
        var children = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Spacing = 2 };
        foreach (var child in node.Children) children.Children.Add(CreateSiddurTreeBranch(state, child));
        var expander = new Expander
        {
            Header = button,
            Content = children,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = state.NavigationTopicsAllExpanded || state.ExpandedNavigationTopics.GetValueOrDefault(node.Part.Key)
        };
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty) return;
            state.ExpandedNavigationTopics[node.Part.Key] = expander.IsExpanded;
            if (!expander.IsExpanded) state.NavigationTopicsAllExpanded = false;
            SaveLayoutState();
        };
        state.NavigationTopicExpanders[node.Part.Key] = expander;
        return expander;
    }

    private Button CreateSiddurTitleButton(ReaderTabState state, SiddurNavigationNode node, string? label = null)
    {
        var title = FormatChapterTitleParts(node.Part.Title, node.Part.HebrewTitle);
        var button = new Button
        {
            Content = new TextBlock { Text = label ?? title, TextWrapping = TextWrapping.Wrap },
            Tag = node,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        ToolTip.SetTip(button, "Go to " + title);
        button.Click += (_, e) => { JumpToSiddurNode(state, node); e.Handled = true; };
        return button;
    }

    private void JumpToSiddurNode(ReaderTabState state, SiddurNavigationNode node)
    {
        var item = state.NavigationItems.FirstOrDefault(item => item.Row.ChapterKey == node.TargetKey);
        if (item is null) return;
        UpdateReaderChapterHeader(state, item.Row);
        ScrollReaderRowToTop(state, item.Row);
        SaveLayoutState();
    }

    private void AddSiddurSearchResults(ReaderTabState state, SiddurNavigationNode node,
        List<string> ancestors, StackPanel results)
    {
        var title = FormatChapterTitleParts(node.Part.Title, node.Part.HebrewTitle);
        if (node.Matches(state.NavigationJumpQuery))
        {
            var label = title + (ancestors.Count > 0 ? "\n" + string.Join(" › ", ancestors) : "");
            results.Children.Add(CreateSiddurTitleButton(state, node, label));
        }
        var path = new List<string>(ancestors) { title };
        foreach (var child in node.Children) AddSiddurSearchResults(state, child, path, results);
    }

    private void SyncSiddurNavigation(ReaderTabState state, string chapterKey)
    {
        var item = state.NavigationItems.FirstOrDefault(item => item.Row.ChapterKey == chapterKey);
        if (item?.NavigationPath is not { } path) return;
        var changed = state.ActiveNavigationTopicKey != chapterKey;
        state.ActiveNavigationTopicKey = chapterKey;
        var keys = path.Select(part => part.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, button) in state.SiddurNavigationButtons)
        {
            var active = key == path[^1].Key;
            button.Background = active ? new SolidColorBrush(Color.Parse("#E8EEF8")) : Brushes.Transparent;
            button.FontWeight = keys.Contains(key) ? FontWeight.SemiBold : FontWeight.Normal;
        }
        if (!changed) return;
        foreach (var part in path.SkipLast(1))
        {
            state.ExpandedNavigationTopics[part.Key] = true;
            if (state.NavigationTopicExpanders.TryGetValue(part.Key, out var expander)) expander.IsExpanded = true;
        }
    }
}

