using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stndr;

public partial class MainWindow
{
    private void InitializeThemeShell()
    {
        _isDictionaryDocked = true;
        if (_rightColumn is not null) _rightColumn.Width = new GridLength(UiTheme.Metric("RightPanelWidth"));
        _rightExpandedWidth = UiTheme.Metric("RightPanelWidth");
    }

    private bool TryHandleGlobalSearchShortcut(KeyEventArgs e)
    {
        if (e.Key != Key.K || e.KeyModifiers != KeyModifiers.Control) return false;
        this.FindControl<TextBox>("GlobalSearchBox")?.Focus();
        return true;
    }

    private void GlobalSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input || string.IsNullOrWhiteSpace(input.Text)) return;
        var search = new SavedAdvancedSearch
        {
            Query = new AdvancedSearchQuery { TemplateId = WordOrLettersTemplateId, TermA = input.Text.Trim(), Scope = "Installed books" }
        };
        var tab = _tabs?.FirstOrDefault(t => string.Equals(t.Tag as string, AdvancedSearchTabTitle, StringComparison.Ordinal));
        if (tab is null)
        {
            tab = CreateTab(AdvancedSearchTabTitle, CreateAdvancedSearchView(search, runImmediately: true));
            _tabs?.Add(tab);
        }
        else ReplaceTabContent(tab, CreateAdvancedSearchView(search, runImmediately: true));
        if (_centerTabs is not null) _centerTabs.SelectedItem = tab;
        e.Handled = true;
    }

    private Control CreateLibraryTreeHeader(object node)
    {
        var category = node as InstalledSefariaCategory;
        var name = category?.Title ?? (node as InstalledSefariaBook)?.Title ?? "";
        var icon = category?.IsBookTitle == true ? "dot" : name switch
        {
            "Tanakh" or "Torah" or "Bavli" => "scroll",
            "Talmud" or "Reference" => "note",
            "Tosefta" => "scroll",
            "Halakhah" => "scales",
            "Responsa" => "comment",
            "Jewish Thought" => "light",
            "Musar" => "leaf",
            _ => "book"
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                new ThemeIcon { Kind = icon, Width = 19, Height = 19, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    [UiTheme.RoleProperty] = "LibraryItem",
                    Text = category is not null ? FormatTitle(category.Title, category.HebrewTitle) : (node as InstalledSefariaBook)?.DisplayVersion,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private Control CreateDockedDictionarySearchBox()
    {
        var box = new TextBox { PlaceholderText = "Look up a word…", MinWidth = 0 };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(box.Text)) return;
            ShowDictionaryEntry(box.Text.Trim(), null);
            e.Handled = true;
        };
        return box;
    }

    private Control CreateThemeSettingsPanel()
    {
        var status = new TextBlock { [UiTheme.RoleProperty] = "Status", TextWrapping = TextWrapping.Wrap,
            Text = UiTheme.LoadError ?? "Edit the theme file to change colours, typography, icons, and layout measurements." };
        var path = new TextBox { Text = UiTheme.FilePath ?? "Choose a data folder to save a custom theme.", IsReadOnly = true };
        var reload = new Button { Content = "Reload theme" };
        reload.Click += (_, _) =>
        {
            if (!UiTheme.Reload()) { status.Text = UiTheme.LoadError; return; }
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                foreach (var window in desktop.Windows) UiTheme.ApplyTree(window);
            else UiTheme.ApplyTree(this);
            ApplyLeftPanelState(_leftCollapsed, UiTheme.Metric("LeftPanelWidth"));
            ApplyRightPanelState(_rightCollapsed, UiTheme.Metric("RightPanelWidth"));
            RefreshOpenReaderTabs();
            UpdateReaderTools();
            status.Text = $"Applied {UiTheme.Current.Name}.";
        };
        var open = new Button { Content = "Open theme file", IsEnabled = UiTheme.FilePath is not null };
        open.Click += (_, _) =>
        {
            if (UiTheme.FilePath is { } file && File.Exists(file))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        };
        return new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { [UiTheme.RoleProperty] = "SectionHeading", Text = "Theme" },
            path, status,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, reload } }
        }};
    }

    private static Control IconHeader(string icon, object content)
    {
        var label = content as Control ?? new TextBlock { [UiTheme.RoleProperty] = "ExpanderHeader", Text = content.ToString() };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
        grid.Children.Add(new ThemeIcon { Kind = icon, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(label);
        Grid.SetColumn(label, 1);
        return grid;
    }

    private Button ReaderHeaderButton(string icon, string tip, Action action)
    {
        var button = new Button { Content = new ThemeIcon { Kind = icon, Width = 18, Height = 18 }, Width = 36, Height = 36,
            Padding = new Thickness(7), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private Button CreateReaderOverflowButton(ReaderTabState state)
    {
        var button = ReaderHeaderButton("more", "Reader tools", () => { });
        button.Flyout = new Avalonia.Controls.Flyout { Content = CreateReaderToolsExpandCollapseButton(state) };
        return button;
    }

    private void StepReaderPage(ReaderTabState state, int delta)
    {
        var index = state.NavigationItems.FindIndex(item => item.Row.ChapterKey == state.CurrentChapterKey);
        var next = index + delta;
        if (next >= 0 && next < state.NavigationItems.Count)
            ScrollReaderToExactReference(state, state.NavigationItems[next].Row.ChapterKey);
    }
}
