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

    // Cancels the previous search's debounce + API call when the user types a new character.
    private CancellationTokenSource _installedBooksSearchCts = new();

    /// <summary>
    /// Loads the offline library tree for Advanced Search scope and similar catalogue uses.
    /// Replaces the old Library Manager catalogue load.
    /// </summary>
    private async Task LoadSefariaLibraryAsync()
    {
        try
        {
            if (!_sefariaLibrary.IsConfigured || !_sefariaLibrary.HasOfflineLibrary)
            {
                _sefariaRoot = null;
                InvalidateScopeCatalogues();
                return;
            }

            _sefariaRoot = await _sefariaLibrary.LoadLibraryAsync(CancellationToken.None);
            await BuildSefariaScopeCatalogueFromRootAsync(_sefariaRoot);
            // A legacy offline install may have downloaded its canonical TOC during the
            // load above. Rebuild the visible tree so those newly available ranks apply.
            RefreshInstalledBooksTree();
        }
        catch
        {
            _sefariaRoot = null;
        }
    }

    private void InvalidateScopeCatalogues()
    {
        Interlocked.Increment(ref _scopeCatalogueGeneration);
        _sefariaScopeCatalogue = null;
        _installedScopeCatalogue = null;
        _sefariaScopeCatalogueKey = null;
        _installedScopeCatalogueKey = null;
        _scopeCatalogueLoadTask = null;
    }

    private string GetOfflineLibraryCacheKey()
    {
        if (!_sefariaLibrary.HasOfflineLibrary)
        {
            return string.Empty;
        }

        try
        {
            var path = _sefariaLibrary.OfflineLibraryDatabasePath;
            var info = new FileInfo(path);
            return info.Exists
                ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetInstalledBooksCacheKey()
    {
        try
        {
            var path = _sefariaLibrary.InstalledBooksFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "installed:empty";
            }

            var info = new FileInfo(path);
            return $"installed|{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "installed:unknown";
        }
    }

    /// <summary>
    /// Ensures Sefaria + installed Advanced Search scope catalogues are built off the UI thread.
    /// Safe to call repeatedly; concurrent callers share one in-flight task.
    /// </summary>
    private Task EnsureScopeCataloguesLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_sefariaScopeCatalogue is not null && _installedScopeCatalogue is not null)
        {
            var sefariaKey = GetOfflineLibraryCacheKey();
            var installedKey = GetInstalledBooksCacheKey();
            if (string.Equals(_sefariaScopeCatalogueKey, sefariaKey, StringComparison.Ordinal) &&
                string.Equals(_installedScopeCatalogueKey, installedKey, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }
        }

        if (_scopeCatalogueLoadTask is { IsCompleted: false })
        {
            return _scopeCatalogueLoadTask;
        }

        _scopeCatalogueLoadTask = LoadScopeCataloguesCoreAsync(cancellationToken);
        return _scopeCatalogueLoadTask;
    }

    private async Task LoadScopeCataloguesCoreAsync(CancellationToken cancellationToken)
    {
        var generation = _scopeCatalogueGeneration;
        var sefariaKey = GetOfflineLibraryCacheKey();
        var installedKey = GetInstalledBooksCacheKey();

        if (_libraryLoadTask is not null)
        {
            try
            {
                await _libraryLoadTask;
            }
            catch
            {
                // LoadSefariaLibraryAsync already swallows; continue with best effort.
            }
        }
        else if (_sefariaLibrary.IsConfigured && _sefariaLibrary.HasOfflineLibrary && _sefariaRoot is null)
        {
            _libraryLoadTask = LoadSefariaLibraryAsync();
            try
            {
                await _libraryLoadTask;
            }
            catch
            {
            }
        }

        if (generation != _scopeCatalogueGeneration)
        {
            return;
        }

        if (_sefariaRoot is not null &&
            !string.Equals(_sefariaScopeCatalogueKey, sefariaKey, StringComparison.Ordinal))
        {
            await BuildSefariaScopeCatalogueFromRootAsync(_sefariaRoot);
        }

        if (!string.Equals(_installedScopeCatalogueKey, installedKey, StringComparison.Ordinal))
        {
            try
            {
                var installedRoots = await Task.Run(() => _sefariaLibrary.BuildInstalledTree(), cancellationToken);
                if (generation != _scopeCatalogueGeneration)
                {
                    return;
                }

                var catalogue = await Task.Run(
                    () => AdvancedSearchScopeCatalogue.FromInstalledRoots(installedRoots),
                    cancellationToken);
                if (generation != _scopeCatalogueGeneration)
                {
                    return;
                }

                _installedScopeCatalogue = catalogue;
                _installedScopeCatalogueKey = installedKey;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _installedScopeCatalogue ??= Array.Empty<AdvancedSearchScopeCatalogueNode>();
                _installedScopeCatalogueKey = installedKey;
            }
        }
    }

    private async Task BuildSefariaScopeCatalogueFromRootAsync(SefariaCategoryNode root)
    {
        var generation = _scopeCatalogueGeneration;
        var key = GetOfflineLibraryCacheKey();
        var catalogue = await Task.Run(() => AdvancedSearchScopeCatalogue.FromSefariaRoot(root));
        if (generation != _scopeCatalogueGeneration)
        {
            return;
        }

        _sefariaScopeCatalogue = catalogue;
        _sefariaScopeCatalogueKey = key;
    }

    private void RefreshInstalledBooksTree()
    {
        _ = RefreshInstalledBooksTreeAsync();
    }

    private async Task RefreshInstalledBooksTreeAsync()
    {
        if (_installedBooksTree is null)
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
            if (_installedBooksTree is null || generation != _installedBooksTreeRefreshGeneration)
            {
                return;
            }

            _installedBooksTree.ItemsSource = roots
                .Cast<object>()
                .Select(CreateInstalledBookTreeItem)
                .ToList();
        });
    }

    private async Task ReconcileInstalledBooksAfterStartupAsync()
    {
        try
        {
            var result = await _sefariaLibrary.ReconcileInstalledBooksAsync();
            if (result.Added == 0 && result.Refreshed == 0 && result.Removed == 0)
            {
                return;
            }

            InvalidateScopeCatalogues();
            await RefreshInstalledBooksTreeAsync();
            _ = EnsureScopeCataloguesLoadedAsync();
        }
        catch
        {
            // Startup reconciliation is best-effort; normal download/delete paths keep the manifest current.
        }
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
        var hebrew = string.IsNullOrWhiteSpace(hebrewTitle)
            ? GetKnownCategoryHebrewTitle(english)
            : hebrewTitle;

        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => string.IsNullOrWhiteSpace(hebrew)
                ? english
                : hebrew,
            InstalledBookTitleDisplay.English => english,
            _ => string.IsNullOrWhiteSpace(hebrew)
                ? english
                : $"{hebrew} / {english}"
        };
    }

    /// <summary>
    /// Offline dump categories often lack Hebrew labels. Map common Sefaria category names
    /// so the title-language setting still applies in the library tree.
    /// </summary>
    private static string? GetKnownCategoryHebrewTitle(string? englishTitle)
    {
        if (string.IsNullOrWhiteSpace(englishTitle))
        {
            return null;
        }

        return englishTitle switch
        {
            "Tanakh" => "תנ״ך",
            "Torah" => "תורה",
            "Prophets" => "נביאים",
            "Writings" => "כתובים",
            "Targum" => "תרגום",
            "Rishonim on Tanakh" => "ראשונים על תנ״ך",
            "Acharonim on Tanakh" => "אחרונים על תנ״ך",
            "Modern Commentary on Tanakh" => "פרשנות מודרנית על תנ״ך",
            "Mishnah" => "משנה",
            "Talmud" => "תלמוד",
            "Bavli" => "בבלי",
            "Yerushalmi" => "ירושלמי",
            "Seder Zeraim" => "סדר זרעים",
            "Seder Moed" => "סדר מועד",
            "Seder Nashim" => "סדר נשים",
            "Seder Nezikin" => "סדר נזיקין",
            "Seder Kodashim" => "סדר קדשים",
            "Seder Tahorot" => "סדר טהרות",
            "Guides" => "מבואות",
            "Tosefta" => "תוספתא",
            "Midrash" => "מדרש",
            "Halakhah" => "הלכה",
            "Responsa" => "שו״ת",
            "Liturgy" => "תפילה",
            "Jewish Thought" => "מחשבת ישראל",
            "Musar" => "מוסר",
            "Chasidut" => "חסידות",
            "Kabbalah" => "קבלה",
            "Reference" => "עיון",
            "Second Temple" => "בית שני",
            "Rishonim" => "ראשונים",
            "Acharonim" => "אחרונים",
            "Commentary" => "פירוש",
            _ => null
        };
    }

    private async void OnInstalledBooksSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_leftPanelSearchSuggestionsContainer is null || _leftPanelSearchSuggestions is null)
            return;

        var query = _leftPanelSearchBox?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _leftPanelSearchSuggestionsContainer.IsVisible = false;
            return;
        }

        // Cancel the previous search (debounce + in-flight API call) and start a new one
        _installedBooksSearchCts.Cancel();
        _installedBooksSearchCts.Dispose();
        _installedBooksSearchCts = new CancellationTokenSource();
        var cts = _installedBooksSearchCts;

        // Show local matches immediately — no network round-trip needed
        var localMatches = FindInstalledCategoriesByTitleOrHebrew(query);
        ShowInstalledBookSuggestions(localMatches, cts);

        // Debounce: avoid a network call on every keystroke
        try { await Task.Delay(350, cts.Token); }
        catch (OperationCanceledException) { return; }

        // Ask Sefaria to resolve the query via transliteration (e.g. "bereshit" → "Genesis")
        IReadOnlyList<string> transliterationKeys;
        try { transliterationKeys = await _sefariaLibrary.FetchTransliterationMatchKeysAsync(query, cts.Token); }
        catch (OperationCanceledException) { return; }

        if (transliterationKeys.Count == 0 || cts.IsCancellationRequested)
            return;

        // Find books matched via transliteration that aren't already in the local results
        var alreadyShown = new HashSet<string>(
            localMatches.Select(c => c.Title),
            StringComparer.OrdinalIgnoreCase);

        var transliterationKeySet = new HashSet<string>(transliterationKeys, StringComparer.OrdinalIgnoreCase);

        var extraMatches = EnumerateInstalledCategoriesFromTree()
            .Where(c => !alreadyShown.Contains(c.Title) && transliterationKeySet.Contains(c.Title))
            .ToList();

        if (extraMatches.Count == 0 || cts.IsCancellationRequested)
            return;

        var merged = localMatches.Concat(extraMatches).Take(10).ToList();
        ShowInstalledBookSuggestions(merged, cts);
    }

    // Returns installed books/categories whose English or Hebrew title contains the query.
    private List<InstalledSefariaCategory> FindInstalledCategoriesByTitleOrHebrew(string query)
    {
        return EnumerateInstalledCategoriesFromTree()
            .Where(c =>
                SearchTextMatcher.Matches(query, c.Title, c.HebrewTitle))
            .Take(10)
            .ToList();
    }

    // Updates the suggestion dropdown, guarding against stale results from a cancelled search.
    private void ShowInstalledBookSuggestions(IReadOnlyList<InstalledSefariaCategory> matches, CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested ||
            _leftPanelSearchSuggestions is null ||
            _leftPanelSearchSuggestionsContainer is null)
        {
            return;
        }

        _leftPanelSearchSuggestions.ItemsSource = matches;
        _leftPanelSearchSuggestionsContainer.IsVisible = matches.Count > 0;
    }

    private void OnInstalledBooksSearchSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;

        switch (e.AddedItems[0])
        {
            case InstalledSefariaBook book:
                OpenInstalledBook(book);
                HighlightInstalledNodeInTree(book);
                break;
            case InstalledSefariaCategory { IsBookTitle: true } bookTitle:
                var firstVersion = _sefariaLibrary.GetInstalledVersionsForTitle(bookTitle.Title).FirstOrDefault();
                if (firstVersion is not null)
                    OpenInstalledBook(firstVersion);
                HighlightInstalledNodeInTree(bookTitle);
                break;
            case InstalledSefariaCategory category:
                HighlightInstalledNodeInTree(category);
                break;
            default:
                return;
        }

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

    private IEnumerable<InstalledSefariaCategory> EnumerateInstalledCategoriesFromTree()
    {
        if (_installedBooksTree?.ItemsSource is not IEnumerable<TreeViewItem> roots)
            yield break;
        foreach (var cat in EnumerateCategoriesFromTreeItems(roots))
            yield return cat;
    }

    private static IEnumerable<InstalledSefariaCategory> EnumerateCategoriesFromTreeItems(
        IEnumerable<TreeViewItem> items)
    {
        foreach (var item in items)
        {
            if (item.DataContext is InstalledSefariaCategory category)
            {
                yield return category;
                if (item.ItemsSource is IEnumerable<TreeViewItem> children)
                    foreach (var child in EnumerateCategoriesFromTreeItems(children))
                        yield return child;
            }
        }
    }

    private void HighlightInstalledNodeInTree(object targetNode)
    {
        if (_installedBooksTree?.ItemsSource is not IEnumerable<TreeViewItem> roots)
            return;
        FindAndSelectInstalledNodeItem(roots, targetNode);
    }

    private static bool FindAndSelectInstalledNodeItem(
        IEnumerable<TreeViewItem> items, object targetNode)
    {
        foreach (var item in items)
        {
            var matches = targetNode switch
            {
                InstalledSefariaBook book => item.DataContext is InstalledSefariaBook b &&
                                             string.Equals(b.Key, book.Key, StringComparison.Ordinal),
                _ => ReferenceEquals(item.DataContext, targetNode)
            };

            if (matches)
            {
                item.IsSelected = true;
                return true;
            }

            if (item.ItemsSource is IEnumerable<TreeViewItem> children &&
                FindAndSelectInstalledNodeItem(children, targetNode))
            {
                item.IsExpanded = true;
                return true;
            }
        }
        return false;
    }
}
