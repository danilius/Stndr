using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private const int AdvancedSearchResultLimit = 250;
    private const int AdvancedSearchVisibleScopeChipLimit = 4;
    private const string WordOrLettersTemplateId = "word-or-letters";
    private const string LettersInsideWordTemplateId = "letters-inside-word";
    private const string ProximityTemplateId = "proximity";
    private const string SameUnitTemplateId = "same-unit";
    private const string NearExcludingTemplateId = "near-excluding";
    private const string SefariaTextSearchTemplateId = "sefaria-text-search";
    private const string DictionarySearchTemplateId = "dictionary-search";
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Compiled);

    private static readonly List<AdvancedSearchTemplate> AdvancedSearchTemplates = new()
    {
        new(WordOrLettersTemplateId, "Find [word/letters] in [scope] matching [Exact/loose/spaces ignored]", true),
        new(LettersInsideWordTemplateId, "Find [letters] inside a word in [scope]", true),
        new(ProximityTemplateId, "Find [A] within [N] [words/letters] of [B] in [scope] matching [Exact/Loose]", true),
        new(SameUnitTemplateId, "Find [A] and [B] in the same [segment/chapter]", true),
        new(NearExcludingTemplateId, "Find [A] near [B], excluding [C]", true),
        new(SefariaTextSearchTemplateId, "Search Sefaria for [query] in [corpus] matching [Exact/Lemmatized/Nearby]", true),
        new(DictionarySearchTemplateId, "Search dictionaries for [query] in [dictionary] matching [Everything/Headwords/Entry text]", true)
    };

    private Control CreateAdvancedSearchView()
    {
        return CreateAdvancedSearchView(null);
    }

    private Control CreateAdvancedSearchView(SavedAdvancedSearch? savedSearch)
    {
        var templateBox = new ComboBox
        {
            MinWidth = 620,
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = AdvancedSearchTemplates,
            SelectedIndex = GetAdvancedSearchTemplateIndex(savedSearch?.Query?.TemplateId)
        };
        var formHost = new StackPanel { Spacing = 0 };
        var runButton = new Button
        {
            Content = "Run Search",
            MinWidth = 108,
            VerticalAlignment = VerticalAlignment.Center
        };
        var saveButton = new Button
        {
            Content = "Save Search",
            MinWidth = 108,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        var autosaveBox = new CheckBox
        {
            Content = "Autosave",
            IsChecked = _advancedSearchAutosave,
            VerticalAlignment = VerticalAlignment.Center
        };
        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            VerticalAlignment = VerticalAlignment.Center
        };
        var resultSummary = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 8)
        };
        var resultItems = new ObservableCollection<AdvancedSearchResult>();
        var resultList = new ListBox
        {
            ItemsSource = resultItems,
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<AdvancedSearchResult>((result, _) =>
                result is null ? new TextBlock() : CreateAdvancedSearchResultRow(result))
        };
        var resultEmptyState = new TextBlock
        {
            Margin = new Thickness(16),
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            IsHitTestVisible = false
        };
        var visibleResults = new List<AdvancedSearchResult>();
        var hasSearchRun = savedSearch is not null;
        var isSearchRunning = false;
        CancellationTokenSource? searchCts = null;
        Action? activeFlushPendingResults = null;
        var searchRunVersion = 0;
        TimeSpan? lastElapsed = null;
        AdvancedSearchQuery? currentQuery = savedSearch?.Query;
        var currentFields = new AdvancedSearchFormFields();
        var fieldValues = CreateAdvancedSearchFieldValues(savedSearch?.Query);

        void RefreshAdvancedSearchResultState()
        {
            resultList.IsVisible = resultItems.Count > 0;
            resultEmptyState.IsVisible = resultItems.Count == 0;
            resultEmptyState.Text = !hasSearchRun
                ? "Run a search to see matching texts or dictionary entries here."
                : "No results found. Try widening the scope or loosening the match mode.";
        }

        void SetAdvancedSearchResultItems(IReadOnlyList<AdvancedSearchResult> results)
        {
            resultItems.Clear();
            foreach (var result in results)
            {
                resultItems.Add(result);
            }

            RefreshAdvancedSearchResultState();
        }

        void RenderTemplateForm()
        {
            CaptureAdvancedSearchFieldValues(currentFields, fieldValues);
            formHost.Children.Clear();
            var template = templateBox.SelectedItem as AdvancedSearchTemplate ?? AdvancedSearchTemplates[0];
            currentFields = CreateAdvancedSearchFields(template, fieldValues);
            var line = CreateAdvancedSearchTemplateLine(template, currentFields);
            formHost.Children.Add(line);
            runButton.IsEnabled = template.IsImplemented;
            status.Text = template.IsImplemented ? string.Empty : "This template is sketched for now.";
        }

        templateBox.SelectionChanged += (_, _) => RenderTemplateForm();
        RenderTemplateForm();

        if (savedSearch is not null)
        {
            status.Text = $"{savedSearch.Results.Count} saved results from {savedSearch.CompletedAtUtc.ToLocalTime():g}";
            visibleResults = savedSearch.Results.ToList();
            SetAdvancedSearchResultItems(visibleResults);
            saveButton.IsEnabled = false;
        }

        autosaveBox.IsCheckedChanged += (_, _) =>
        {
            _advancedSearchAutosave = autosaveBox.IsChecked == true;
            SaveLayoutState();
        };

        saveButton.Click += (_, _) =>
        {
            if (currentQuery is null)
            {
                status.Text = "Run a search before saving.";
                return;
            }

            SaveCompletedAdvancedSearch(currentQuery, visibleResults);
            saveButton.IsEnabled = false;
            status.Text = $"{visibleResults.Count} results. Search saved.";
        };

        runButton.Click += async (_, _) =>
        {
            if (isSearchRunning)
            {
                var cts = searchCts;
                searchCts = null;
                cts?.Cancel();
                activeFlushPendingResults?.Invoke();
                var cancelledResultsCount = visibleResults.Count;
                activeFlushPendingResults = null;
                searchRunVersion++;
                isSearchRunning = false;
                runButton.Content = "Run Search";
                runButton.IsEnabled = true;
                status.Text = $"{cancelledResultsCount} results before search was cancelled.";
                resultSummary.Text = $"{cancelledResultsCount.ToString("N0", CultureInfo.InvariantCulture)} results before cancellation";
                saveButton.IsEnabled = visibleResults.Count > 0 && currentQuery is not null;
                return;
            }

            var template = templateBox.SelectedItem as AdvancedSearchTemplate ?? AdvancedSearchTemplates[0];
            if (!template.IsImplemented)
            {
                status.Text = "This template is sketched for now.";
                return;
            }

            if (!TryBuildAdvancedSearchQuery(template, currentFields, out var query, out var validationMessage))
            {
                status.Text = validationMessage;
                return;
            }

            searchCts?.Dispose();
            searchCts = new CancellationTokenSource();
            var runCts = searchCts;
            var cancellationToken = runCts.Token;
            var searchRunId = ++searchRunVersion;
            isSearchRunning = true;
            runButton.Content = "Cancel";
            saveButton.IsEnabled = false;
            status.Text = query.TemplateId switch
            {
                SefariaTextSearchTemplateId => "Searching Sefaria...",
                DictionarySearchTemplateId => "Searching installed dictionaries...",
                _ => "Searching installed books..."
            };
            hasSearchRun = true;
            currentQuery = query;
            lastElapsed = null;
            visibleResults.Clear();
            UpdateAdvancedSearchResultSummary(resultSummary, visibleResults.Count, lastElapsed, hasSearchRun, false);
            resultItems.Clear();
            RefreshAdvancedSearchResultState();
            var pendingResults = new ConcurrentQueue<AdvancedSearchResult>();
            var flushScheduled = 0;

            void FlushPendingResults()
            {
                Interlocked.Exchange(ref flushScheduled, 0);
                var flushedCount = 0;
                while (flushedCount < 25 && pendingResults.TryDequeue(out var result))
                {
                    visibleResults.Add(result);
                    resultItems.Add(result);
                    flushedCount++;
                }

                if (flushedCount == 0)
                {
                    return;
                }

                RefreshAdvancedSearchResultState();
                UpdateAdvancedSearchResultSummary(
                    resultSummary,
                    visibleResults.Count,
                    lastElapsed,
                    hasSearchRun,
                    false);

                if (!pendingResults.IsEmpty)
                {
                    SchedulePendingResultFlush();
                }
            }

            void SchedulePendingResultFlush()
            {
                if (Interlocked.Exchange(ref flushScheduled, 1) == 1)
                {
                    return;
                }

                Dispatcher.UIThread.Post(FlushPendingResults, DispatcherPriority.Background);
            }

            activeFlushPendingResults = FlushPendingResults;

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var results = query.TemplateId switch
                {
                    SefariaTextSearchTemplateId => await RunSefariaAdvancedSearchAsync(query, cancellationToken),
                    DictionarySearchTemplateId => await RunDictionaryAdvancedSearchAsync(query, cancellationToken),
                    _ => await Task.Run(() => RunInstalledAdvancedSearch(
                        query,
                        result =>
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                pendingResults.Enqueue(result);
                                SchedulePendingResultFlush();
                            }
                        },
                        cancellationToken), cancellationToken)
                };
                stopwatch.Stop();
                while (!pendingResults.IsEmpty)
                {
                    FlushPendingResults();
                }
                lastElapsed = stopwatch.Elapsed;
                cancellationToken.ThrowIfCancellationRequested();
                if (searchRunId != searchRunVersion)
                {
                    return;
                }

                currentQuery = query;
                visibleResults = results.ToList();
                SetAdvancedSearchResultItems(visibleResults);
                UpdateAdvancedSearchResultSummary(resultSummary, visibleResults.Count, lastElapsed, hasSearchRun, false);
                if (_advancedSearchAutosave)
                {
                    SaveCompletedAdvancedSearch(query, results);
                    saveButton.IsEnabled = false;
                    status.Text = $"{results.Count} results. Search saved.";
                }
                else
                {
                    saveButton.IsEnabled = true;
                    status.Text = $"{results.Count} results. Save when ready.";
                }
            }
            catch (OperationCanceledException)
            {
                while (!pendingResults.IsEmpty)
                {
                    FlushPendingResults();
                }
                if (searchRunId != searchRunVersion)
                {
                    return;
                }

                status.Text = $"{visibleResults.Count} results before search was cancelled.";
                resultSummary.Text = $"{visibleResults.Count.ToString("N0", CultureInfo.InvariantCulture)} results before cancellation";
                saveButton.IsEnabled = visibleResults.Count > 0 && currentQuery is not null;
            }
            catch (Exception ex)
            {
                if (searchRunId != searchRunVersion)
                {
                    return;
                }

                status.Text = $"Search failed: {ex.Message}";
                UpdateAdvancedSearchResultSummary(resultSummary, visibleResults.Count, lastElapsed, hasSearchRun, false);
            }
            finally
            {
                runCts.Dispose();
                if (ReferenceEquals(searchCts, runCts))
                {
                    searchCts = null;
                }

                if (searchRunId == searchRunVersion)
                {
                    activeFlushPendingResults = null;
                    isSearchRunning = false;
                    runButton.Content = "Run Search";
                    runButton.IsEnabled = template.IsImplemented;
                }
            }
        };

        var searchArea = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9FAFB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Advanced Search",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#101828")),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    },
                    templateBox,
                    formHost,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            runButton,
                            saveButton,
                            autosaveBox,
                            status
                        }
                    }
                }
            }
        };

        UpdateAdvancedSearchResultSummary(resultSummary, visibleResults.Count, lastElapsed, hasSearchRun, savedSearch is not null);

        var resultHeader = CreateAdvancedSearchGridHeader(resultSummary, () =>
        {
            _savedSearchTitlesHebrew = !_savedSearchTitlesHebrew;
            SetAdvancedSearchResultItems(visibleResults);
            SaveLayoutState();
        });
        var resultBody = new Grid
        {
            Children =
            {
                resultList,
                resultEmptyState
            }
        };
        RefreshAdvancedSearchResultState();
        var resultsArea = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                resultHeader,
                resultBody
            }
        };
        Grid.SetRow(resultBody, 1);

        return new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brushes.White,
            Children =
            {
                searchArea,
                resultsArea
            }
        }.WithChildRow(resultsArea, 1);
    }

    private static int GetAdvancedSearchTemplateIndex(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return 0;
        }

        var index = AdvancedSearchTemplates.FindIndex(template =>
            string.Equals(template.Id, templateId, StringComparison.Ordinal));
        return index < 0 ? 0 : index;
    }

    private static AdvancedSearchFieldValues CreateAdvancedSearchFieldValues(AdvancedSearchQuery? query)
    {
        if (query is null)
        {
            return new AdvancedSearchFieldValues();
        }

        return new AdvancedSearchFieldValues
        {
            TermA = query.TermA,
            TermB = query.TermB,
            TermC = query.TermC,
            Distance = query.Distance.ToString(CultureInfo.InvariantCulture),
            Unit = query.Unit,
            SameUnit = query.SameUnit,
            Scope = query.Scope,
            SelectedScopes = query.SelectedScopes
                .Select(scope => new AdvancedSearchScopeSelection
                {
                    Kind = scope.Kind,
                    Key = scope.Key,
                    Label = scope.Label,
                    HebrewLabel = scope.HebrewLabel
                })
                .ToList(),
            MatchMode = query.MatchMode,
            DictionaryLexiconId = query.DictionaryLexiconId
        };
    }

    private AdvancedSearchFormFields CreateAdvancedSearchFields(
        AdvancedSearchTemplate template,
        AdvancedSearchFieldValues values)
    {
        var selectedScopes = values.SelectedScopes
            .Select(scope => new AdvancedSearchScopeSelection
            {
                Kind = scope.Kind,
                Key = scope.Key,
                Label = scope.Label,
                HebrewLabel = scope.HebrewLabel
            })
            .ToList();
        var scopePanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var fields = new AdvancedSearchFormFields
        {
            HasControls = true,
            TemplateId = template.Id,
            SelectedScopes = selectedScopes,
            ScopePanel = scopePanel,
            TermABox = new TextBox { Width = 180, PlaceholderText = "word or letters", Text = values.TermA },
            TermBBox = new TextBox { Width = 180, PlaceholderText = "word or letters", Text = values.TermB },
            TermCBox = new TextBox { Width = 180, PlaceholderText = "excluded", Text = values.TermC },
            DistanceBox = new TextBox { Width = 56, Text = values.Distance },
            UnitBox = new ComboBox
            {
                Width = 110,
                ItemsSource = new[] { "words", "letters" },
                SelectedItem = values.Unit
            },
            SameUnitBox = new ComboBox
            {
                Width = 120,
                ItemsSource = new[] { "segment", "chapter" },
                SelectedItem = values.SameUnit
            },
            MatchBox = new ComboBox
            {
                Width = 120,
                ItemsSource = new[] { "Exact", "Loose", "Ignore spaces" },
                SelectedItem = values.MatchMode
            },
            DictionaryScopeBox = new ComboBox
            {
                Width = 280,
                ItemsSource = new[] { new DictionarySearchScopeOption(null, "All dictionaries") },
                SelectedIndex = 0,
                ItemTemplate = new FuncDataTemplate<DictionarySearchScopeOption>((option, _) =>
                    new TextBlock { Text = option?.Label ?? string.Empty })
            }
        };

        if (template.Id == SefariaTextSearchTemplateId)
        {
            fields.TermABox.PlaceholderText = "query";
            fields.MatchBox.ItemsSource = new[] { "Exact", "Lemmatized", "Nearby" };
            fields.MatchBox.SelectedItem = IsSefariaSearchMatchMode(values.MatchMode)
                ? values.MatchMode
                : "Lemmatized";
            fields.DistanceBox.Text = string.IsNullOrWhiteSpace(values.Distance) ? "10" : values.Distance;
            fields.DistanceBox.IsVisible = string.Equals(fields.MatchBox.SelectedItem as string, "Nearby", StringComparison.OrdinalIgnoreCase);
        }
        else if (template.Id == DictionarySearchTemplateId)
        {
            fields.TermABox.Width = 240;
            fields.TermABox.PlaceholderText = "headword or entry text";
            fields.MatchBox.Width = 140;
            fields.MatchBox.ItemsSource = new[] { "Everything", "Headwords", "Entry text" };
            fields.MatchBox.SelectedItem = values.MatchMode is "Everything" or "Headwords" or "Entry text"
                ? values.MatchMode
                : "Everything";
            _ = PopulateAdvancedSearchDictionaryScopesAsync(fields.DictionaryScopeBox, values.DictionaryLexiconId);
        }

        if (template.Id != DictionarySearchTemplateId)
        {
            RefreshAdvancedSearchScopePanel(fields);
        }

        if (template.Id == NearExcludingTemplateId)
        {
            fields.DistanceBox.Text = "10";
        }

        return fields;
    }

    private static void CaptureAdvancedSearchFieldValues(
        AdvancedSearchFormFields fields,
        AdvancedSearchFieldValues values)
    {
        if (!fields.HasControls)
        {
            return;
        }

        values.TermA = fields.TermABox.Text ?? string.Empty;
        values.TermB = fields.TermBBox.Text ?? string.Empty;
        values.TermC = fields.TermCBox.Text ?? string.Empty;
        values.Distance = string.IsNullOrWhiteSpace(fields.DistanceBox.Text) ? "10" : fields.DistanceBox.Text;
        values.Unit = fields.UnitBox.SelectedItem as string ?? values.Unit;
        values.SameUnit = fields.SameUnitBox.SelectedItem as string ?? values.SameUnit;
        values.SelectedScopes = fields.SelectedScopes
            .Select(scope => new AdvancedSearchScopeSelection
                {
                    Kind = scope.Kind,
                    Key = scope.Key,
                    Label = scope.Label,
                    HebrewLabel = scope.HebrewLabel
                })
                .ToList();
        values.MatchMode = fields.MatchBox.SelectedItem as string ?? values.MatchMode;
        values.DictionaryLexiconId =
            (fields.DictionaryScopeBox.SelectedItem as DictionarySearchScopeOption)?.LexiconId;
    }

    private async Task PopulateAdvancedSearchDictionaryScopesAsync(ComboBox scopeBox, long? selectedLexiconId)
    {
        var lexicons = _dictionaryLexicons.Count > 0
            ? _dictionaryLexicons
            : await _sefariaLibrary.GetOfflineLexiconsAsync();
        _dictionaryLexicons = lexicons;
        var options = new List<DictionarySearchScopeOption> { new(null, "All dictionaries") };
        options.AddRange(lexicons.Select(lexicon => new DictionarySearchScopeOption(lexicon.Id, lexicon.Name)));
        scopeBox.ItemsSource = options;
        scopeBox.SelectedItem = options.FirstOrDefault(option => option.LexiconId == selectedLexiconId) ?? options[0];
    }

    private void RefreshAdvancedSearchScopePanel(AdvancedSearchFormFields fields)
    {
        fields.ScopePanel.Children.Clear();
        if (fields.SelectedScopes.Count > 0)
        {
            foreach (var scope in fields.SelectedScopes.Take(AdvancedSearchVisibleScopeChipLimit))
            {
                AddAdvancedSearchControls(fields.ScopePanel, CreateAdvancedSearchScopeChip(
                    scope,
                    () =>
                    {
                        fields.SelectedScopes.Remove(scope);
                        RefreshAdvancedSearchScopePanel(fields);
                    }));
            }

            var remainingCount = fields.SelectedScopes.Count - AdvancedSearchVisibleScopeChipLimit;
            if (remainingCount > 0)
            {
                AddAdvancedSearchControls(fields.ScopePanel, CreateAdvancedSearchScopeSummaryChip(remainingCount));
            }

            AddAdvancedSearchControls(fields.ScopePanel, CreateAdvancedSearchClearScopeButton(() =>
            {
                fields.SelectedScopes.Clear();
                RefreshAdvancedSearchScopePanel(fields);
                SaveLayoutState();
            }));
        }
        else
        {
            var isSefariaSearch = fields.TemplateId == SefariaTextSearchTemplateId;
            AddAdvancedSearchControls(fields.ScopePanel, CreateAdvancedSearchScopeChip(
                new AdvancedSearchScopeSelection
                {
                    Kind = AdvancedSearchScopeKind.AllInstalled,
                    Key = isSefariaSearch ? "All Sefaria books" : "All local books",
                    Label = isSefariaSearch ? "All Sefaria books" : "All local books",
                    HebrewLabel = isSefariaSearch
                        ? "\u05db\u05dc \u05e1\u05e4\u05e8\u05d9\u05d0"
                        : "\u05e1\u05e4\u05e8\u05d9\u05dd \u05de\u05e7\u05d5\u05de\u05d9\u05d9\u05dd"
                },
                null));
        }

        var editScopeButton = new Button
        {
            Content = "Edit scope",
            MinWidth = 92,
            VerticalAlignment = VerticalAlignment.Center
        };
        editScopeButton.Click += async (_, _) =>
        {
            var selectedScopes = await ShowAdvancedSearchScopeDialogAsync(
                fields.SelectedScopes,
                fields.TemplateId == SefariaTextSearchTemplateId);
            if (selectedScopes is null)
            {
                return;
            }

            fields.SelectedScopes.Clear();
            fields.SelectedScopes.AddRange(selectedScopes);
            RefreshAdvancedSearchScopePanel(fields);
            SaveLayoutState();
        };

        AddAdvancedSearchControls(fields.ScopePanel, editScopeButton);
    }

    private Control CreateAdvancedSearchScopeChip(
        AdvancedSearchScopeSelection scope,
        Action? remove)
    {
        static TextBlock CreateLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#344054"))
            };
        }

        if (remove is null)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F2F4F7")),
                BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3),
                Child = CreateLabel(FormatAdvancedSearchScopeLabel(scope))
            };
        }

        var removeButton = new Button
        {
            Content = "x",
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            IsTabStop = false
        };
        removeButton.Click += (_, e) =>
        {
            e.Handled = true;
            remove();
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2F4F7")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 2, 3),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    CreateLabel(FormatAdvancedSearchScopeLabel(scope)),
                    removeButton
                }
            }
        };
    }

    private static Control CreateAdvancedSearchScopeSummaryChip(int remainingCount)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9FAFB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3),
            Child = new TextBlock
            {
                Text = $"+{remainingCount.ToString(CultureInfo.InvariantCulture)} more",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#667085"))
            }
        };
    }

    private static Button CreateAdvancedSearchClearScopeButton(Action clear)
    {
        var button = new Button
        {
            Content = "Clear scope",
            MinWidth = 92,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            clear();
        };
        return button;
    }

    private async Task<List<AdvancedSearchScopeSelection>?> ShowAdvancedSearchScopeDialogAsync(
        IReadOnlyList<AdvancedSearchScopeSelection> initialScopes,
        bool useSefariaLibraryScope)
    {
        // Start from the global title-language setting; the dialog's א/A can override for the session.
        _advancedSearchScopeTitleDisplay = _settings.InstalledBookTitleDisplay;

        var selected = initialScopes
            .Where(scope => scope.Kind != AdvancedSearchScopeKind.AllInstalled)
            .Select(scope => new AdvancedSearchScopeSelection
            {
                Kind = scope.Kind,
                Key = scope.Key,
                Label = scope.Label,
                HebrewLabel = scope.HebrewLabel
            })
            .ToList();
        var searchBox = new TextBox
        {
            PlaceholderText = useSefariaLibraryScope
                ? "Search Sefaria categories or books"
                : "Search local categories or books",
            Margin = new Thickness(0, 0, 0, 8),
            IsEnabled = false
        };
        var selectedPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var scopeTree = new TreeView();
        var statusText = new TextBlock
        {
            Text = "Loading catalogue…",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var titleButton = new Button
        {
            Content = GetAdvancedSearchScopeTitleDisplayButtonText(),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = false
        };
        var searchHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                searchBox,
                titleButton
            }
        };
        Grid.SetColumn(titleButton, 1);
        var scroll = new ScrollViewer
        {
            Height = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = scopeTree
        };
        var okButton = new Button { Content = "Apply", MinWidth = 80 };
        var allScopeButton = new Button
        {
            Content = useSefariaLibraryScope ? "All Sefaria Books" : "All Local Books",
            MinWidth = 128,
            IsEnabled = false
        };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 76 };
        var dialog = new Window
        {
            Title = useSefariaLibraryScope ? "Edit Sefaria Search Scope" : "Edit Local Search Scope",
            Width = 560,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        IReadOnlyList<AdvancedSearchScopeCatalogueNode> catalogueRoots =
            Array.Empty<AdvancedSearchScopeCatalogueNode>();
        var filterDebounceCts = new CancellationTokenSource();
        var filterGeneration = 0;
        var suppressScopeTreeEvents = false;

        void RefreshSelectedChips()
        {
            selectedPanel.Children.Clear();
            if (selected.Count == 0)
            {
                AddAdvancedSearchControls(selectedPanel, CreateAdvancedSearchScopeChip(
                    new AdvancedSearchScopeSelection
                    {
                        Kind = AdvancedSearchScopeKind.AllInstalled,
                        Key = useSefariaLibraryScope ? "All Sefaria books" : "All local books",
                        Label = useSefariaLibraryScope ? "All Sefaria books" : "All local books",
                        HebrewLabel = useSefariaLibraryScope
                            ? "\u05db\u05dc \u05e1\u05e4\u05e8\u05d9\u05d0"
                            : "\u05e1\u05e4\u05e8\u05d9\u05dd \u05de\u05e7\u05d5\u05de\u05d9\u05d9\u05dd"
                    },
                    null));
                return;
            }

            foreach (var scope in selected.ToList().Take(AdvancedSearchVisibleScopeChipLimit))
            {
                AddAdvancedSearchControls(selectedPanel, CreateAdvancedSearchScopeChip(scope, () =>
                {
                    selected.RemoveAll(candidate => candidate.Kind == scope.Kind &&
                        string.Equals(candidate.Key, scope.Key, StringComparison.OrdinalIgnoreCase));
                    RefreshSelectedChips();
                    _ = RefreshScopeTreeAsync(searchBox.Text ?? string.Empty, expandAllMatches: false);
                }));
            }

            var remainingCount = selected.Count - AdvancedSearchVisibleScopeChipLimit;
            if (remainingCount > 0)
            {
                AddAdvancedSearchControls(selectedPanel, CreateAdvancedSearchScopeSummaryChip(remainingCount));
            }
        }

        async Task RefreshScopeTreeAsync(string filter, bool expandAllMatches)
        {
            var generation = Interlocked.Increment(ref filterGeneration);
            IReadOnlyList<AdvancedSearchScopeCatalogueNode> roots;
            if (string.IsNullOrWhiteSpace(filter))
            {
                roots = catalogueRoots;
            }
            else
            {
                roots = await Task.Run(() => AdvancedSearchScopeCatalogue.Filter(catalogueRoots, filter));
            }

            if (generation != filterGeneration)
            {
                return;
            }

            // Never materialize the full control tree. Expand only the first level when filtering
            // so matches are visible without freezing on large catalogues.
            var expandFirstLevel = expandAllMatches || !string.IsNullOrWhiteSpace(filter);
            suppressScopeTreeEvents = true;
            try
            {
                scopeTree.ItemsSource = BuildLazyScopeTreeItems(
                    roots,
                    selected,
                    RefreshSelectedChips,
                    () => _ = RefreshScopeTreeAsync(searchBox.Text ?? string.Empty, expandAllMatches: false),
                    expandFirstLevel,
                    () => suppressScopeTreeEvents,
                    catalogueRoots);
            }
            finally
            {
                suppressScopeTreeEvents = false;
            }

            statusText.IsVisible = roots.Count == 0;
            statusText.Text = roots.Count == 0
                ? (string.IsNullOrWhiteSpace(filter) ? "No books available." : "No matching categories or books.")
                : string.Empty;
        }

        string? action = null;
        okButton.Click += (_, _) =>
        {
            action = "apply";
            dialog.Close();
        };
        allScopeButton.Click += async (_, _) =>
        {
            selected.Clear();
            RefreshSelectedChips();
            await RefreshScopeTreeAsync(searchBox.Text ?? string.Empty, expandAllMatches: false);
        };
        cancelButton.Click += (_, _) => dialog.Close();
        searchBox.TextChanged += async (_, _) =>
        {
            filterDebounceCts.Cancel();
            filterDebounceCts.Dispose();
            filterDebounceCts = new CancellationTokenSource();
            var token = filterDebounceCts.Token;
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                {
                    await RefreshScopeTreeAsync(searchBox.Text ?? string.Empty, expandAllMatches: false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        };
        titleButton.Click += async (_, e) =>
        {
            e.Handled = true;
            _advancedSearchScopeTitleDisplay = GetNextAdvancedSearchScopeTitleDisplay();
            titleButton.Content = GetAdvancedSearchScopeTitleDisplayButtonText();
            RefreshSelectedChips();
            await RefreshScopeTreeAsync(searchBox.Text ?? string.Empty, expandAllMatches: false);
            SaveLayoutState();
        };

        RefreshSelectedChips();

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(16),
            Children =
            {
                searchHeader,
                selectedPanel,
                statusText,
                scroll,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        allScopeButton,
                        okButton,
                        cancelButton
                    }
                }
            }
        };
        if (dialog.Content is Grid grid)
        {
            Grid.SetRow(selectedPanel, 1);
            Grid.SetRow(statusText, 2);
            Grid.SetRow(scroll, 3);
            Grid.SetRow(grid.Children[4], 4);
        }

        dialog.Opened += async (_, _) =>
        {
            try
            {
                await EnsureScopeCataloguesLoadedAsync();
                catalogueRoots = useSefariaLibraryScope
                    ? _sefariaScopeCatalogue ?? Array.Empty<AdvancedSearchScopeCatalogueNode>()
                    : _installedScopeCatalogue ?? Array.Empty<AdvancedSearchScopeCatalogueNode>();

                if (useSefariaLibraryScope && catalogueRoots.Count == 0 && !_sefariaLibrary.HasOfflineLibrary)
                {
                    statusText.Text = "Offline Sefaria library is not installed.";
                }
                else
                {
                    await RefreshScopeTreeAsync(string.Empty, expandAllMatches: false);
                    searchBox.IsEnabled = true;
                    titleButton.IsEnabled = true;
                    allScopeButton.IsEnabled = true;
                    statusText.IsVisible = catalogueRoots.Count == 0;
                    if (catalogueRoots.Count == 0)
                    {
                        statusText.Text = "No books available.";
                    }
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"Could not load catalogue: {ex.Message}";
                statusText.IsVisible = true;
            }
        };

        dialog.Closed += (_, _) =>
        {
            filterDebounceCts.Cancel();
            filterDebounceCts.Dispose();
        };

        await dialog.ShowDialog(this);
        return action == "apply" ? selected : null;
    }

    private List<TreeViewItem> BuildLazyScopeTreeItems(
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> roots,
        List<AdvancedSearchScopeSelection> selected,
        Action refreshSelectedChips,
        Action rebuildTree,
        bool expandFirstLevel,
        Func<bool> isSuppressingEvents,
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> fullCatalogueRoots)
    {
        var items = new List<TreeViewItem>(roots.Count);
        foreach (var node in roots)
        {
            items.Add(CreateLazyScopeTreeItem(
                node,
                selected,
                refreshSelectedChips,
                rebuildTree,
                expandFirstLevel,
                isSuppressingEvents,
                fullCatalogueRoots));
        }

        return items;
    }

    private TreeViewItem CreateLazyScopeTreeItem(
        AdvancedSearchScopeCatalogueNode node,
        List<AdvancedSearchScopeSelection> selected,
        Action refreshSelectedChips,
        Action rebuildTree,
        bool expandThisLevel,
        Func<bool> isSuppressingEvents,
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> fullCatalogueRoots)
    {
        var scope = ToAdvancedSearchScopeSelection(node);
        var allDescendantsSelected = node.HasChildren && AreAllScopeDescendantsSelected(node, selected);
        var selfSelected = selected.Any(candidate => candidate.Kind == scope.Kind &&
            string.Equals(candidate.Key, scope.Key, StringComparison.OrdinalIgnoreCase));
        // Two-state only: Avalonia three-state cycles true→null→false, and treating null as
        // checked made unticking impossible.
        var isDisplayedChecked = node.HasChildren ? allDescendantsSelected : selfSelected;

        var checkBox = new CheckBox
        {
            Content = FormatAdvancedSearchScopeLabel(scope),
            IsThreeState = false,
            IsChecked = isDisplayedChecked,
            VerticalAlignment = VerticalAlignment.Center
        };

        checkBox.IsCheckedChanged += (_, _) =>
        {
            if (isSuppressingEvents())
            {
                return;
            }

            var isChecked = checkBox.IsChecked == true;
            if (node.HasChildren)
            {
                ApplyScopeSelectionCascade(node, isChecked, selected);
                refreshSelectedChips();
                rebuildTree();
                return;
            }

            var existingIndex = selected.FindIndex(candidate => candidate.Kind == scope.Kind &&
                string.Equals(candidate.Key, scope.Key, StringComparison.OrdinalIgnoreCase));
            if (isChecked && existingIndex < 0)
            {
                selected.Add(scope);
            }
            else if (!isChecked && existingIndex >= 0)
            {
                selected.RemoveAt(existingIndex);
            }

            // Deselecting one mesechta must clear partially-selected ancestors (seder / Talmud).
            if (!isChecked)
            {
                PruneIncompleteParentSelections(fullCatalogueRoots, selected);
            }
            else
            {
                PromoteFullySelectedParents(fullCatalogueRoots, selected);
            }

            refreshSelectedChips();
            rebuildTree();
        };

        var expansionKey = GetAdvancedSearchScopeExpansionKey(scope);
        var rememberExpanded = _advancedSearchExpandedScopeKeys.Contains(expansionKey);
        var shouldExpand = (expandThisLevel || rememberExpanded) && node.HasChildren;
        var item = new TreeViewItem
        {
            Header = checkBox,
            IsExpanded = shouldExpand,
            DataContext = scope
        };

        if (!node.HasChildren)
        {
            return item;
        }

        var childrenLoaded = false;
        void LoadChildren()
        {
            if (childrenLoaded)
            {
                return;
            }

            childrenLoaded = true;
            // Children never auto-expand further; user expands level by level.
            item.ItemsSource = BuildLazyScopeTreeItems(
                node.Children,
                selected,
                refreshSelectedChips,
                rebuildTree,
                expandFirstLevel: false,
                isSuppressingEvents,
                fullCatalogueRoots);
        }

        if (shouldExpand)
        {
            LoadChildren();
        }
        else
        {
            // Placeholder so the expander chevron appears; real children load on expand.
            item.ItemsSource = new[] { new TreeViewItem { IsVisible = false, IsEnabled = false } };
        }

        item.PropertyChanged += (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty)
            {
                return;
            }

            var key = GetAdvancedSearchScopeExpansionKey(scope);
            if (item.IsExpanded)
            {
                _advancedSearchExpandedScopeKeys.Add(key);
                LoadChildren();
            }
            else
            {
                _advancedSearchExpandedScopeKeys.Remove(key);
            }

            SaveLayoutState();
        };

        return item;
    }

    private static void ApplyScopeSelectionCascade(
        AdvancedSearchScopeCatalogueNode node,
        bool isChecked,
        List<AdvancedSearchScopeSelection> selected)
    {
        var scope = ToAdvancedSearchScopeSelection(node);
        var existingIndex = selected.FindIndex(candidate => candidate.Kind == scope.Kind &&
            string.Equals(candidate.Key, scope.Key, StringComparison.OrdinalIgnoreCase));
        if (isChecked && existingIndex < 0)
        {
            selected.Add(scope);
        }
        else if (!isChecked && existingIndex >= 0)
        {
            selected.RemoveAt(existingIndex);
        }

        foreach (var child in node.Children)
        {
            ApplyScopeSelectionCascade(child, isChecked, selected);
        }
    }

    /// <summary>
    /// Removes category selections whose descendants are no longer fully selected
    /// (e.g. seder stays selected after one mesechta is unchecked).
    /// </summary>
    private static void PruneIncompleteParentSelections(
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> roots,
        List<AdvancedSearchScopeSelection> selected)
    {
        foreach (var root in roots)
        {
            PruneIncompleteParentSelectionsRecursive(root, selected);
        }
    }

    private static void PruneIncompleteParentSelectionsRecursive(
        AdvancedSearchScopeCatalogueNode node,
        List<AdvancedSearchScopeSelection> selected)
    {
        foreach (var child in node.Children)
        {
            PruneIncompleteParentSelectionsRecursive(child, selected);
        }

        if (!node.HasChildren)
        {
            return;
        }

        if (!AreAllScopeDescendantsSelected(node, selected))
        {
            selected.RemoveAll(candidate =>
                candidate.Kind == AdvancedSearchScopeKind.Category &&
                string.Equals(candidate.Key, node.Key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// When every child of a category is selected, also select the category itself.
    /// </summary>
    private static void PromoteFullySelectedParents(
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> roots,
        List<AdvancedSearchScopeSelection> selected)
    {
        foreach (var root in roots)
        {
            PromoteFullySelectedParentsRecursive(root, selected);
        }
    }

    private static void PromoteFullySelectedParentsRecursive(
        AdvancedSearchScopeCatalogueNode node,
        List<AdvancedSearchScopeSelection> selected)
    {
        foreach (var child in node.Children)
        {
            PromoteFullySelectedParentsRecursive(child, selected);
        }

        if (!node.HasChildren || !AreAllScopeDescendantsSelected(node, selected))
        {
            return;
        }

        var scope = ToAdvancedSearchScopeSelection(node);
        if (!selected.Any(candidate => candidate.Kind == scope.Kind &&
                string.Equals(candidate.Key, scope.Key, StringComparison.OrdinalIgnoreCase)))
        {
            selected.Add(scope);
        }
    }

    private static bool AreAllScopeDescendantsSelected(
        AdvancedSearchScopeCatalogueNode node,
        List<AdvancedSearchScopeSelection> selected)
    {
        if (!node.HasChildren)
        {
            return selected.Any(candidate =>
                string.Equals(candidate.Key, node.Key, StringComparison.OrdinalIgnoreCase));
        }

        return node.Children.All(child => AreAllScopeDescendantsSelected(child, selected));
    }

    private static string GetAdvancedSearchScopeExpansionKey(AdvancedSearchScopeSelection scope)
    {
        return $"{scope.Kind}:{scope.Key}";
    }

    private static AdvancedSearchScopeSelection ToAdvancedSearchScopeSelection(
        AdvancedSearchScopeCatalogueNode node)
    {
        return new AdvancedSearchScopeSelection
        {
            Kind = node.Kind == AdvancedSearchScopeCatalogueKind.Category
                ? AdvancedSearchScopeKind.Category
                : AdvancedSearchScopeKind.Work,
            Key = node.Key,
            Label = node.Label,
            HebrewLabel = node.HebrewLabel
        };
    }

    private string FormatAdvancedSearchScopeLabel(AdvancedSearchScopeSelection scope)
    {
        var english = string.IsNullOrWhiteSpace(scope.Label) ? scope.Key : scope.Label;
        var hebrew = string.IsNullOrWhiteSpace(scope.HebrewLabel)
            ? GetKnownCategoryHebrewTitle(english)
            : scope.HebrewLabel;
        return _advancedSearchScopeTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => string.IsNullOrWhiteSpace(hebrew) ? english : hebrew,
            InstalledBookTitleDisplay.English => english,
            _ => string.IsNullOrWhiteSpace(hebrew) ? english : $"{hebrew} / {english}"
        };
    }

    private string GetAdvancedSearchScopeTitleDisplayButtonText()
    {
        return _advancedSearchScopeTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => "\u05d0",
            InstalledBookTitleDisplay.English => "A",
            _ => "A/\u05d0"
        };
    }

    private InstalledBookTitleDisplay GetNextAdvancedSearchScopeTitleDisplay()
    {
        return _advancedSearchScopeTitleDisplay switch
        {
            InstalledBookTitleDisplay.English => InstalledBookTitleDisplay.Hebrew,
            InstalledBookTitleDisplay.Hebrew => InstalledBookTitleDisplay.Both,
            _ => InstalledBookTitleDisplay.English
        };
    }

    private static WrapPanel CreateAdvancedSearchTemplateLine(
        AdvancedSearchTemplate template,
        AdvancedSearchFormFields fields)
    {
        var line = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
        switch (template.Id)
        {
            case LettersInsideWordTemplateId:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Find"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("inside a word in"),
                    fields.ScopePanel);
                break;
            case WordOrLettersTemplateId:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Find"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.ScopePanel,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox);
                break;
            case SameUnitTemplateId:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Find"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("and"),
                    fields.TermBBox,
                    CreateAdvancedSearchPhrase("in the same"),
                    fields.SameUnitBox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.ScopePanel,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox);
                break;
            case NearExcludingTemplateId:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Find"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("within"),
                    fields.DistanceBox,
                    fields.UnitBox,
                    CreateAdvancedSearchPhrase("of"),
                    fields.TermBBox,
                    CreateAdvancedSearchPhrase("excluding"),
                    fields.TermCBox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.ScopePanel,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox);
                break;
            case SefariaTextSearchTemplateId:
                var distancePhrase = CreateAdvancedSearchPhrase("words");
                static void UpdateSefariaDistanceVisibility(AdvancedSearchFormFields fields, Control distancePhrase)
                {
                    var isNearby = string.Equals(fields.MatchBox.SelectedItem as string, "Nearby", StringComparison.OrdinalIgnoreCase);
                    distancePhrase.IsVisible = isNearby;
                    fields.DistanceBox.IsVisible = isNearby;
                }

                fields.MatchBox.SelectionChanged += (_, _) => UpdateSefariaDistanceVisibility(fields, distancePhrase);
                UpdateSefariaDistanceVisibility(fields, distancePhrase);
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Search Sefaria for"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.ScopePanel,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox,
                    CreateAdvancedSearchPhrase("within"),
                    fields.DistanceBox,
                    distancePhrase);
                break;
            case DictionarySearchTemplateId:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Search dictionaries for"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.DictionaryScopeBox,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox);
                break;
            default:
                AddAdvancedSearchControls(line,
                    CreateAdvancedSearchPhrase("Find"),
                    fields.TermABox,
                    CreateAdvancedSearchPhrase("within"),
                    fields.DistanceBox,
                    fields.UnitBox,
                    CreateAdvancedSearchPhrase("of"),
                    fields.TermBBox,
                    CreateAdvancedSearchPhrase("in"),
                    fields.ScopePanel,
                    CreateAdvancedSearchPhrase("matching"),
                    fields.MatchBox);
                break;
        }

        return line;
    }

    private static void AddAdvancedSearchControls(WrapPanel line, params Control[] controls)
    {
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 0, 8, 8);
            line.Children.Add(control);
        }
    }

    private static TextBlock CreateAdvancedSearchPhrase(string text)
    {
        return new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#344054"))
        };
    }

    private static bool TryBuildAdvancedSearchQuery(
        AdvancedSearchTemplate template,
        AdvancedSearchFormFields fields,
        out AdvancedSearchQuery query,
        out string validationMessage)
    {
        query = new AdvancedSearchQuery
        {
            TemplateId = template.Id,
            TermA = fields.TermABox.Text ?? string.Empty,
            TermB = fields.TermBBox.Text ?? string.Empty,
            TermC = fields.TermCBox.Text ?? string.Empty,
            Unit = fields.UnitBox.SelectedItem as string ?? "words",
            SameUnit = fields.SameUnitBox.SelectedItem as string ?? "segment",
            SelectedScopes = fields.SelectedScopes
                .Select(scope => new AdvancedSearchScopeSelection
                {
                    Kind = scope.Kind,
                    Key = scope.Key,
                    Label = scope.Label,
                    HebrewLabel = scope.HebrewLabel
                })
                .ToList(),
            MatchMode = fields.MatchBox.SelectedItem as string ?? "Exact"
        };
        if (template.Id == DictionarySearchTemplateId &&
            fields.DictionaryScopeBox.SelectedItem is DictionarySearchScopeOption dictionaryScope)
        {
            query.DictionaryLexiconId = dictionaryScope.LexiconId;
            query.Scope = dictionaryScope.Label;
        }
        else
        {
            query.Scope = FormatAdvancedSearchScopeSummary(query.SelectedScopes);
        }

        if (string.IsNullOrWhiteSpace(query.TermA))
        {
            validationMessage = "Enter the first search term.";
            return false;
        }

        if (template.Id is ProximityTemplateId or SameUnitTemplateId or NearExcludingTemplateId &&
            string.IsNullOrWhiteSpace(query.TermB))
        {
            validationMessage = "Enter the second search term.";
            return false;
        }

        if (template.Id == NearExcludingTemplateId && string.IsNullOrWhiteSpace(query.TermC))
        {
            validationMessage = "Enter the excluded term.";
            return false;
        }

        if (template.Id is ProximityTemplateId or NearExcludingTemplateId ||
            (template.Id == SefariaTextSearchTemplateId &&
             string.Equals(query.MatchMode, "Nearby", StringComparison.OrdinalIgnoreCase)))
        {
            if (!int.TryParse(fields.DistanceBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var distance) ||
                distance < 0)
            {
                validationMessage = template.Id == SefariaTextSearchTemplateId
                    ? "Enter a whole number for the nearby distance."
                    : "Enter a whole number for the distance.";
                return false;
            }

            query.Distance = distance;
        }

        validationMessage = string.Empty;
        return true;
    }

    private List<AdvancedSearchResult> RunInstalledAdvancedSearch(AdvancedSearchQuery query)
    {
        return RunInstalledAdvancedSearch(query, null, CancellationToken.None);
    }

    private List<AdvancedSearchResult> RunInstalledAdvancedSearch(
        AdvancedSearchQuery query,
        Action<AdvancedSearchResult>? onResult,
        CancellationToken cancellationToken)
    {
        var installedBooks = _sefariaLibrary.GetInstalledBooks();
        cancellationToken.ThrowIfCancellationRequested();
        var versionsByTitle = _sefariaLibrary.GetInstalledVersionsByTitle(installedBooks);
        cancellationToken.ThrowIfCancellationRequested();
        var books = installedBooks
            .Where(book => MatchesAdvancedSearchScope(book, query))
            .GroupBy(book => book.Title, StringComparer.Ordinal)
            .Select(group => SelectAdvancedSearchBookForTitle(group.Key, group.ToList(), versionsByTitle))
            .Where(book => book is not null)
            .Select(book => book!)
            .OrderBy(book => book.Categories.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<AdvancedSearchResult>();
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bool AddResult(ReaderTextUnit unit)
                {
                    var result = new AdvancedSearchResult
                    {
                        Reference = SefariaReferenceFormatting.BuildFullAnchorRef(book.Title, unit.Reference),
                        WorkTitle = book.Title,
                        VersionTitle = FormatReaderVersionTitle(book),
                        Source = "Installed",
                        Snippet = BuildAdvancedSearchSnippet(unit.Text),
                        BookKey = book.Key,
                        ReferenceWithinWork = NormalizeReaderUnitReference(unit.Reference),
                        MatchedTerms = BuildAdvancedSearchMatchedTerms(query)
                    };
                    results.Add(result);
                    onResult?.Invoke(result);
                    return results.Count >= AdvancedSearchResultLimit;
                }

                if (ShouldGroupAdvancedSearchByChapter(query))
                {
                    var units = _sefariaLibrary.ReadInstalledBookUnits(book, cancellationToken);
                    foreach (var unit in GetMatchingAdvancedSearchUnits(units, query, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (AddResult(unit))
                        {
                            return results;
                        }
                    }
                }
                else
                {
                    foreach (var unit in _sefariaLibrary.StreamInstalledBookUnits(book, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!AdvancedSearchUnitMatches(unit.Text, query))
                        {
                            continue;
                        }

                        if (AddResult(unit))
                        {
                            return results;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }
        }

        return results;
    }

    private async Task<List<AdvancedSearchResult>> RunSefariaAdvancedSearchAsync(
        AdvancedSearchQuery query,
        CancellationToken cancellationToken)
    {
        var searchResults = await _sefariaLibrary.SearchTextsAsync(
            BuildSefariaTextSearchRequest(query),
            cancellationToken);
        var workFilters = query.SelectedScopes
            .Where(scope => scope.Kind == AdvancedSearchScopeKind.Work)
            .Select(scope => scope.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();
        if (workFilters.Count > 0)
        {
            searchResults = searchResults
                .Where(result => workFilters.Any(work =>
                    string.Equals(result.WorkTitle, work, StringComparison.OrdinalIgnoreCase) ||
                    result.Reference.StartsWith(work + " ", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var results = new List<AdvancedSearchResult>();
        foreach (var searchResult in searchResults.Take(AdvancedSearchResultLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workTitle = FirstNonEmptyAdvancedSearchValue(
                searchResult.WorkTitle,
                ExtractAdvancedSearchReferenceTitle(searchResult.Reference));
            var installedBook = string.IsNullOrWhiteSpace(workTitle)
                ? null
                : _sefariaLibrary.GetInstalledVersionsForTitle(workTitle).FirstOrDefault();
            var referenceWithinWork = ExtractAdvancedSearchReferenceWithinWork(searchResult.Reference, workTitle);
            results.Add(new AdvancedSearchResult
            {
                Reference = searchResult.Reference,
                WorkTitle = workTitle,
                VersionTitle = installedBook is null ? "Not downloaded" : "Installed",
                Source = "Sefaria",
                Snippet = BuildAdvancedSearchSnippet(searchResult.Snippet),
                BookKey = installedBook?.Key ?? string.Empty,
                ReferenceWithinWork = NormalizeReaderUnitReference(referenceWithinWork),
                RemoteUrl = BuildSefariaReferenceUrl(searchResult.Reference),
                IsRemote = true,
                MatchedTerms = BuildAdvancedSearchMatchedTerms(query)
            });
        }

        return results;
    }

    private async Task<List<AdvancedSearchResult>> RunDictionaryAdvancedSearchAsync(
        AdvancedSearchQuery query,
        CancellationToken cancellationToken)
    {
        var mode = query.MatchMode switch
        {
            "Headwords" => SefariaDictionarySearchMode.Headwords,
            "Entry text" => SefariaDictionarySearchMode.EntryText,
            _ => SefariaDictionarySearchMode.Everything
        };
        var entries = await _sefariaLibrary.SearchOfflineDictionaryAsync(
            query.TermA,
            query.DictionaryLexiconId,
            AdvancedSearchResultLimit,
            cancellationToken,
            mode);
        return entries.Select(entry => new AdvancedSearchResult
        {
            Reference = entry.Headword,
            WorkTitle = entry.LexiconName,
            VersionTitle = entry.LexiconName,
            Source = "Dictionary",
            Snippet = BuildAdvancedSearchSnippet(
                NormalizeDictionaryText(entry.Definition),
                query.TermA),
            MatchedTerms = new List<string> { query.TermA },
            IsDictionaryEntry = true,
            DictionaryEntryId = entry.EntryId,
            DictionaryLexiconId = entry.LexiconId
        }).ToList();
    }

    private static SefariaTextSearchRequest BuildSefariaTextSearchRequest(AdvancedSearchQuery query)
    {
        var matchMode = query.MatchMode;
        var isExact = string.Equals(matchMode, "Exact", StringComparison.OrdinalIgnoreCase);
        var isNearby = string.Equals(matchMode, "Nearby", StringComparison.OrdinalIgnoreCase);
        return new SefariaTextSearchRequest
        {
            Query = query.TermA,
            Field = isExact ? "exact" : "naive_lemmatizer",
            Filters = BuildSefariaSearchFilters(query),
            Aggregations = new List<string> { "path" },
            Size = AdvancedSearchResultLimit,
            Slop = isNearby ? query.Distance : 0,
            SortFields = new List<string> { isExact ? "order" : "pagesheetrank" },
            SortMethod = isExact ? "sort" : "score"
        };
    }

    private static List<string> BuildSefariaSearchFilters(AdvancedSearchQuery query)
    {
        return query.SelectedScopes
            .Where(scope => scope.Kind == AdvancedSearchScopeKind.Category)
            .Select(scope => scope.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatAdvancedSearchScopeSummary(IReadOnlyList<AdvancedSearchScopeSelection> scopes)
    {
        if (scopes.Count == 0 || scopes.Any(scope => scope.Kind == AdvancedSearchScopeKind.AllInstalled))
        {
            return "All local books";
        }

        return string.Join(", ", scopes.Select(scope => scope.Label));
    }

    private static string FormatSefariaSearchScopeSummary(AdvancedSearchQuery query)
    {
        if (query.SelectedScopes.Count == 0 ||
            query.SelectedScopes.Any(scope => scope.Kind == AdvancedSearchScopeKind.AllInstalled))
        {
            return "Sefaria library";
        }

        return string.Join(", ", query.SelectedScopes.Select(scope => scope.Label));
    }

    private static bool ShouldGroupAdvancedSearchByChapter(AdvancedSearchQuery query)
    {
        return query.TemplateId == SameUnitTemplateId &&
            string.Equals(query.SameUnit, "chapter", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ReaderTextUnit> GetMatchingAdvancedSearchUnits(
        IEnumerable<ReaderTextUnit> units,
        AdvancedSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (ShouldGroupAdvancedSearchByChapter(query))
        {
            var chapters = new Dictionary<string, List<ReaderTextUnit>>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chapterKey = GetReferencePart(unit.Reference, 0);
                if (!chapters.TryGetValue(chapterKey, out var chapterUnits))
                {
                    chapterUnits = new List<ReaderTextUnit>();
                    chapters[chapterKey] = chapterUnits;
                }

                chapterUnits.Add(unit);
            }

            foreach (var chapterUnits in chapters.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!AdvancedSearchTextContains(chapterUnits.Select(unit => unit.Text), query.TermA, query.MatchMode, cancellationToken) ||
                    !AdvancedSearchTextContains(chapterUnits.Select(unit => unit.Text), query.TermB, query.MatchMode, cancellationToken))
                {
                    continue;
                }

                foreach (var unit in chapterUnits)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (AdvancedSearchTextContains(unit.Text, query.TermA, query.MatchMode) ||
                        AdvancedSearchTextContains(unit.Text, query.TermB, query.MatchMode))
                    {
                        yield return unit;
                    }
                }
            }

            yield break;
        }

        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AdvancedSearchUnitMatches(unit.Text, query))
            {
                yield return unit;
            }
        }
    }

    private static bool MatchesAdvancedSearchScope(InstalledSefariaBook book, AdvancedSearchQuery query)
    {
        var scopes = query.SelectedScopes;
        if (scopes.Count == 0 || scopes.Any(scope => scope.Kind == AdvancedSearchScopeKind.AllInstalled))
        {
            return true;
        }

        return scopes.Any(scope => scope.Kind switch
        {
            AdvancedSearchScopeKind.Work =>
                string.Equals(book.Title, scope.Key, StringComparison.OrdinalIgnoreCase),
            AdvancedSearchScopeKind.Category =>
                MatchesAdvancedSearchCategoryScope(book, scope.Key),
            _ => true
        });
    }

    private static bool MatchesAdvancedSearchCategoryScope(InstalledSefariaBook book, string scopeKey)
    {
        var scopePath = SplitAdvancedSearchScopePath(scopeKey);
        if (scopePath.Count > 1)
        {
            if (IsMishnahSederScope(scopePath))
            {
                return IsBaseMishnahTractateInSeder(book, scopePath[^1]);
            }

            if (IsTalmudSederScope(scopePath))
            {
                return IsBaseTalmudTractateInSeder(book, scopePath[^1]);
            }

            return BookCategoryPathStartsWith(book, scopePath);
        }

        if (IsSederCategoryName(scopeKey))
        {
            return IsBaseMishnahTractateInSeder(book, scopeKey) ||
                IsBaseTalmudTractateInSeder(book, scopeKey);
        }

        return book.Categories.Any(category =>
            string.Equals(category, scopeKey, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(scopeKey, "Halakhah", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(category, "Halacha", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> SplitAdvancedSearchScopePath(string scopeKey)
    {
        return scopeKey
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool IsSederCategoryName(string scopeKey)
    {
        return scopeKey.StartsWith("Seder ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMishnahSederScope(IReadOnlyList<string> scopePath)
    {
        return scopePath.Count >= 2 &&
            string.Equals(scopePath[0], "Mishnah", StringComparison.OrdinalIgnoreCase) &&
            IsSederCategoryName(scopePath[^1]);
    }

    private static bool IsTalmudSederScope(IReadOnlyList<string> scopePath)
    {
        return scopePath.Count >= 2 &&
            string.Equals(scopePath[0], "Talmud", StringComparison.OrdinalIgnoreCase) &&
            IsSederCategoryName(scopePath[^1]);
    }

    private static bool BookCategoryPathStartsWith(InstalledSefariaBook book, IReadOnlyList<string> scopePath)
    {
        if (book.Categories.Count < scopePath.Count)
        {
            return false;
        }

        for (var i = 0; i < scopePath.Count; i++)
        {
            if (!string.Equals(book.Categories[i], scopePath[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBaseMishnahTractateInSeder(InstalledSefariaBook book, string seder)
    {
        return book.Categories.Count >= 2 &&
            string.Equals(book.Categories[0], "Mishnah", StringComparison.OrdinalIgnoreCase) &&
            book.Categories.Any(category => string.Equals(category, seder, StringComparison.OrdinalIgnoreCase)) &&
            !book.Title.Contains(" on ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBaseTalmudTractateInSeder(InstalledSefariaBook book, string seder)
    {
        return book.Categories.Count >= 2 &&
            string.Equals(book.Categories[0], "Talmud", StringComparison.OrdinalIgnoreCase) &&
            book.Categories.Any(category => string.Equals(category, seder, StringComparison.OrdinalIgnoreCase)) &&
            !book.Title.Contains(" on ", StringComparison.OrdinalIgnoreCase);
    }

    private InstalledSefariaBook? SelectAdvancedSearchBookForTitle(
        string title,
        List<InstalledSefariaBook> scopedBooks,
        IReadOnlyDictionary<string, List<InstalledSefariaBook>> versionsByTitle)
    {
        var versions = versionsByTitle.TryGetValue(title, out var titleVersions)
            ? titleVersions
                .Where(version => scopedBooks.Any(scoped => string.Equals(scoped.Key, version.Key, StringComparison.Ordinal)))
                .ToList()
            : new List<InstalledSefariaBook>();
        if (versions.Count == 0)
        {
            versions = scopedBooks
                .OrderByDescending(SefariaLibraryService.IsHebrew)
                .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var hebrewTexts = versions.Where(SefariaLibraryService.IsHebrew).ToList();
        return GetSavedHebrewText(title, hebrewTexts) ??
            hebrewTexts.FirstOrDefault() ??
            versions.FirstOrDefault();
    }

    private static bool AdvancedSearchUnitMatches(string text, AdvancedSearchQuery query)
    {
        return query.TemplateId switch
        {
            WordOrLettersTemplateId => AdvancedSearchTextContains(text, query.TermA, query.MatchMode),
            LettersInsideWordTemplateId => AdvancedSearchLettersInsideWord(text, query.TermA),
            SameUnitTemplateId => AdvancedSearchTextContains(text, query.TermA, query.MatchMode) &&
                AdvancedSearchTextContains(text, query.TermB, query.MatchMode),
            NearExcludingTemplateId => AdvancedSearchProximityMatches(text, query) &&
                !AdvancedSearchTextContains(text, query.TermC, query.MatchMode),
            _ => AdvancedSearchProximityMatches(text, query)
        };
    }

    private static bool AdvancedSearchProximityMatches(string text, AdvancedSearchQuery query)
    {
        var normalizedText = NormalizeSearchText(text, keepSpaces: true);
        var termA = NormalizeSearchText(query.TermA, keepSpaces: true);
        var termB = NormalizeSearchText(query.TermB, keepSpaces: true);
        if (string.IsNullOrWhiteSpace(termA))
        {
            return false;
        }

        if (IsSpaceInsensitiveMatchMode(query.MatchMode))
        {
            normalizedText = RemoveSearchSpaces(normalizedText);
            termA = RemoveSearchSpaces(termA);
            termB = RemoveSearchSpaces(termB);
        }

        if (string.IsNullOrWhiteSpace(termB))
        {
            return normalizedText.Contains(termA, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(query.Unit, "letters", StringComparison.OrdinalIgnoreCase))
        {
            var compactText = RemoveSearchSpaces(normalizedText);
            var compactA = RemoveSearchSpaces(termA);
            var compactB = RemoveSearchSpaces(termB);
            var firstA = compactText.IndexOf(compactA, StringComparison.OrdinalIgnoreCase);
            var firstB = compactText.IndexOf(compactB, StringComparison.OrdinalIgnoreCase);
            return firstA >= 0 &&
                firstB >= 0 &&
                Math.Abs(firstA - firstB) <= query.Distance + Math.Max(compactA.Length, compactB.Length);
        }

        var words = normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aIndexes = FindWordIndexes(words, termA);
        var bIndexes = FindWordIndexes(words, termB);
        return aIndexes.Any(a => bIndexes.Any(b => Math.Abs(a - b) <= query.Distance));
    }

    private static bool AdvancedSearchTextContains(IEnumerable<string> texts, string term, string matchMode)
    {
        return AdvancedSearchTextContains(texts, term, matchMode, CancellationToken.None);
    }

    private static bool AdvancedSearchTextContains(
        IEnumerable<string> texts,
        string term,
        string matchMode,
        CancellationToken cancellationToken)
    {
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AdvancedSearchTextContains(text, term, matchMode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AdvancedSearchTextContains(string text, string term, string matchMode)
    {
        var normalizedText = NormalizeSearchText(text, keepSpaces: true);
        var normalizedTerm = NormalizeSearchText(term, keepSpaces: true);
        if (string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return false;
        }

        if (IsSpaceInsensitiveMatchMode(matchMode))
        {
            normalizedText = RemoveSearchSpaces(normalizedText);
            normalizedTerm = RemoveSearchSpaces(normalizedTerm);
        }

        return normalizedText.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AdvancedSearchLettersInsideWord(string text, string term)
    {
        var normalizedTerm = RemoveSearchSpaces(NormalizeSearchText(term, keepSpaces: true));
        if (string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return false;
        }

        return GetNormalizedSearchWords(text)
            .Any(word => word.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSpaceInsensitiveMatchMode(string matchMode)
    {
        return string.Equals(matchMode, "Loose", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(matchMode, "Ignore spaces", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSefariaSearchMatchMode(string matchMode)
    {
        return string.Equals(matchMode, "Exact", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(matchMode, "Lemmatized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(matchMode, "Nearby", StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> FindWordIndexes(IReadOnlyList<string> words, string term)
    {
        var indexes = new List<int>();
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    private static string NormalizeSearchText(string text, bool keepSpaces)
    {
        text = WebUtility.HtmlDecode(HtmlTagRegex.Replace(text ?? string.Empty, " "));
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (ShouldSuppressHebrewMarkForWeb(character, HebrewMarksMode.TextOnly))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(NormalizeFinalHebrewLetter(character));
                continue;
            }

            if (keepSpaces && char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
        }

        return Regex.Replace(builder.ToString(), " +", " ").Trim();
    }

    private static IEnumerable<string> GetNormalizedSearchWords(string text)
    {
        text = WebUtility.HtmlDecode(HtmlTagRegex.Replace(text ?? string.Empty, " "));
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (ShouldSuppressHebrewMarkForWeb(character, HebrewMarksMode.TextOnly))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(NormalizeFinalHebrewLetter(character));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static char NormalizeFinalHebrewLetter(char character)
    {
        return character switch
        {
            '\u05da' => '\u05db',
            '\u05dd' => '\u05de',
            '\u05df' => '\u05e0',
            '\u05e3' => '\u05e4',
            '\u05e5' => '\u05e6',
            _ => character
        };
    }

    private static string RemoveSearchSpaces(string value)
    {
        return value.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string BuildAdvancedSearchSnippet(string text, string? focusTerm = null)
    {
        var cleaned = WebUtility.HtmlDecode(HtmlTagRegex.Replace(text ?? string.Empty, " "));
        cleaned = ApplyHebrewMarksModeForWeb(cleaned, HebrewMarksMode.TextOnly);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length <= 220)
        {
            return cleaned;
        }

        var focusIndex = FindAdvancedSearchSnippetFocus(cleaned, focusTerm);
        if (focusIndex < 0)
        {
            return cleaned[..220] + "...";
        }

        var start = Math.Max(0, focusIndex - 80);
        if (start > 0)
        {
            var nextSpace = cleaned.IndexOf(' ', start);
            if (nextSpace >= 0 && nextSpace < focusIndex)
            {
                start = nextSpace + 1;
            }
        }

        var length = Math.Min(220, cleaned.Length - start);
        var end = start + length;
        if (end < cleaned.Length)
        {
            var previousSpace = cleaned.LastIndexOf(' ', end - 1, length);
            if (previousSpace > start)
            {
                end = previousSpace;
            }
        }

        return (start > 0 ? "..." : string.Empty) +
            cleaned[start..end] +
            (end < cleaned.Length ? "..." : string.Empty);
    }

    private static int FindAdvancedSearchSnippetFocus(string text, string? focusTerm)
    {
        if (string.IsNullOrWhiteSpace(focusTerm))
        {
            return -1;
        }

        var normalizedFocus = ApplyHebrewMarksModeForWeb(
            WebUtility.HtmlDecode(focusTerm),
            HebrewMarksMode.TextOnly);
        return normalizedFocus
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
    }

    private static string FirstNonEmptyAdvancedSearchValue(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string ExtractAdvancedSearchReferenceTitle(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var endIndex = parts.Length;
        while (endIndex > 0 && parts[endIndex - 1].Any(char.IsDigit))
        {
            endIndex--;
        }

        return endIndex <= 0 ? reference.Trim() : string.Join(' ', parts, 0, endIndex);
    }

    private static string ExtractAdvancedSearchReferenceWithinWork(string reference, string workTitle)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(workTitle) &&
            reference.StartsWith(workTitle, StringComparison.OrdinalIgnoreCase))
        {
            return reference[workTitle.Length..].Trim().TrimStart(',');
        }

        return reference;
    }

    private static string BuildSefariaReferenceUrl(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return "https://www.sefaria.org";
        }

        return $"https://www.sefaria.org/{Uri.EscapeDataString(reference.Replace(' ', '_'))}";
    }

    private static List<string> BuildAdvancedSearchMatchedTerms(AdvancedSearchQuery query)
    {
        return new[] { query.TermA, query.TermB, query.TermC }
            .Where((_, index) =>
                (query.TemplateId != NearExcludingTemplateId || index < 2) &&
                (query.TemplateId != WordOrLettersTemplateId || index == 0) &&
                (query.TemplateId != LettersInsideWordTemplateId || index == 0) &&
                (query.TemplateId != SefariaTextSearchTemplateId || index == 0))
            .Select(term => NormalizeSearchText(term, keepSpaces: true))
            .Select(term => query.TemplateId == LettersInsideWordTemplateId ? RemoveSearchSpaces(term) : term)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ToList();
    }

    private static string BuildAdvancedSearchSummary(AdvancedSearchQuery query)
    {
        return query.TemplateId switch
        {
            DictionarySearchTemplateId =>
                $"Search dictionaries for {query.TermA} in {query.Scope} matching {query.MatchMode}",
            SefariaTextSearchTemplateId =>
                $"Search Sefaria for {query.TermA} in {FormatSefariaSearchScopeSummary(query)} matching {query.MatchMode}",
            WordOrLettersTemplateId =>
                $"Find {query.TermA} in {query.Scope} matching {query.MatchMode}",
            LettersInsideWordTemplateId =>
                $"Find {query.TermA} inside a word in {query.Scope}",
            SameUnitTemplateId =>
                $"Find {query.TermA} and {query.TermB} in the same {query.SameUnit} in {query.Scope} matching {query.MatchMode}",
            NearExcludingTemplateId =>
                $"Find {query.TermA} within {query.Distance.ToString(CultureInfo.InvariantCulture)} {query.Unit} of {query.TermB}, excluding {query.TermC}, in {query.Scope} matching {query.MatchMode}",
            _ =>
                $"Find {query.TermA} within {query.Distance.ToString(CultureInfo.InvariantCulture)} {query.Unit} of {query.TermB} in {query.Scope} matching {query.MatchMode}"
        };
    }

    private static string BuildAdvancedSearchName(AdvancedSearchQuery query, int count)
    {
        return $"{BuildAdvancedSearchSummary(query)} ({count})";
    }

    private void SaveCompletedAdvancedSearch(AdvancedSearchQuery query, IReadOnlyList<AdvancedSearchResult> results)
    {
        var saved = new SavedAdvancedSearch
        {
            Name = BuildAdvancedSearchName(query, results.Count),
            QuerySummary = BuildAdvancedSearchSummary(query),
            Query = query,
            CompletedAtUtc = DateTime.UtcNow,
            Results = results.ToList()
        };
        _savedAdvancedSearches.Insert(0, saved);
        RefreshSavedSearchesList();
        SaveLayoutState();
    }

    private static void UpdateAdvancedSearchResultSummary(
        TextBlock summary,
        int count,
        TimeSpan? elapsed,
        bool hasSearchRun,
        bool isSavedSearch)
    {
        if (isSavedSearch)
        {
            summary.Text = $"{count.ToString("N0", CultureInfo.InvariantCulture)} saved results";
            return;
        }

        if (!hasSearchRun)
        {
            summary.Text = "Not run yet";
            return;
        }

        if (elapsed is null)
        {
            summary.Text = count > 0
                ? $"Searching... {count.ToString("N0", CultureInfo.InvariantCulture)} results"
                : "Searching...";
            return;
        }

        summary.Text = $"{count.ToString("N0", CultureInfo.InvariantCulture)} results in {FormatAdvancedSearchElapsed(elapsed.Value)}";
    }

    private static string FormatAdvancedSearchElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{elapsed.TotalMilliseconds:0} ms";
    }

    private Control CreateAdvancedSearchGridHeader(TextBlock resultSummary, Action toggleTitleLanguage)
    {
        var languageButton = new Button
        {
            Content = _savedSearchTitlesHebrew ? "\u05d0" : "A",
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        languageButton.Click += (_, e) =>
        {
            e.Handled = true;
            toggleTitleLanguage();
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,160,90,*,Auto,Auto"),
            Background = new SolidColorBrush(Color.Parse("#F2F4F7")),
            MinHeight = 34,
            Children =
            {
                CreateAdvancedSearchHeaderCell("Reference"),
                CreateAdvancedSearchHeaderCell("Version"),
                CreateAdvancedSearchHeaderCell("Source"),
                CreateAdvancedSearchHeaderCell("Snippet"),
                resultSummary,
                languageButton
            }
        };
        Grid.SetColumn(grid.Children[1], 1);
        Grid.SetColumn(grid.Children[2], 2);
        Grid.SetColumn(grid.Children[3], 3);
        Grid.SetColumn(resultSummary, 4);
        Grid.SetColumn(languageButton, 5);
        return grid;
    }

    private static TextBlock CreateAdvancedSearchHeaderCell(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#344054")),
            Margin = new Thickness(10, 8)
        };
    }

    private Control CreateAdvancedSearchResultRow(AdvancedSearchResult result)
    {
        if (result.IsDictionaryEntry)
        {
            return CreateAdvancedDictionarySearchResultRow(result);
        }

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("220,160,90,*"),
                MinHeight = 52,
                Children =
                {
                    CreateAdvancedSearchResultCell(FormatAdvancedSearchResultReference(result), true),
                    CreateAdvancedSearchResultCell(result.VersionTitle, false),
                    CreateAdvancedSearchResultCell(result.Source, false),
                    CreateAdvancedSearchSnippetCell(result)
                }
            }
        };

        if (button.Content is Grid grid)
        {
            Grid.SetColumn(grid.Children[1], 1);
            Grid.SetColumn(grid.Children[2], 2);
            Grid.SetColumn(grid.Children[3], 3);
        }

        button.Click += (_, _) => OpenAdvancedSearchResult(result);
        return button;
    }

    private Control CreateAdvancedDictionarySearchResultRow(AdvancedSearchResult result)
    {
        var openButton = new Button
        {
            Content = "Open in Dictionary",
            MinWidth = 132,
            Margin = new Thickness(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        openButton.Click += (_, e) =>
        {
            e.Handled = true;
            OpenAdvancedSearchResult(result);
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,160,90,*,Auto"),
            MinHeight = 58,
            Children =
            {
                CreateAdvancedSearchResultCell(result.Reference, true),
                CreateAdvancedSearchResultCell(result.VersionTitle, false),
                CreateAdvancedSearchResultCell(result.Source, false),
                CreateAdvancedSearchSnippetCell(result),
                openButton
            }
        };
        for (var column = 1; column < grid.Children.Count; column++)
        {
            Grid.SetColumn(grid.Children[column], column);
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private string FormatAdvancedSearchResultReference(AdvancedSearchResult result)
    {
        if (!_savedSearchTitlesHebrew)
        {
            return result.Reference;
        }

        var versions = _sefariaLibrary.GetInstalledVersionsForTitle(result.WorkTitle);
        var hebrewTitle = versions
            .Select(version => version.HebrewTitle)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        if (string.IsNullOrWhiteSpace(hebrewTitle))
        {
            return result.Reference;
        }

        var referenceWithinWork = string.IsNullOrWhiteSpace(result.ReferenceWithinWork)
            ? string.Empty
            : SefariaReferenceFormatting.NormalizeUnitPathToRelative(result.ReferenceWithinWork);
        return string.IsNullOrWhiteSpace(referenceWithinWork)
            ? hebrewTitle
            : SefariaReferenceFormatting.UsesCommaAfterTitle(referenceWithinWork)
                ? $"{hebrewTitle}, {referenceWithinWork}"
                : $"{hebrewTitle} {referenceWithinWork}";
    }

    private static TextBlock CreateAdvancedSearchResultCell(string text, bool isReference)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = isReference ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.Parse(isReference ? "#175CD3" : "#101828")),
            Margin = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateAdvancedSearchSnippetCell(AdvancedSearchResult result)
    {
        var textBlock = CreateAdvancedSearchResultCell(string.Empty, false);
        AppendHighlightedSnippetRuns(textBlock, result.Snippet, result.MatchedTerms);
        return textBlock;
    }

    private static void AppendHighlightedSnippetRuns(TextBlock textBlock, string text, IReadOnlyList<string> terms)
    {
        var inlines = textBlock.Inlines ?? new InlineCollection();
        inlines.Clear();

        var spans = FindAdvancedSearchHighlightSpans(text, terms);
        if (string.IsNullOrEmpty(text) || spans.Count == 0)
        {
            inlines.Add(new Run(text));
            textBlock.Inlines = inlines;
            return;
        }

        var position = 0;
        foreach (var span in spans)
        {
            if (span.Start > position)
            {
                inlines.Add(new Run(text[position..span.Start]));
            }

            inlines.Add(new Run(text.Substring(span.Start, span.Length))
            {
                FontWeight = FontWeight.Bold
            });
            position = span.Start + span.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run(text[position..]));
        }

        textBlock.Inlines = inlines;
    }

    private static List<(int Start, int Length)> FindAdvancedSearchHighlightSpans(
        string text,
        IReadOnlyList<string> terms)
    {
        var normalizedBuilder = new StringBuilder(text.Length);
        var originalIndexes = new List<int>(text.Length);
        var previousWasSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (ShouldSuppressHebrewMarkForWeb(character, HebrewMarksMode.TextOnly))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                normalizedBuilder.Append(NormalizeFinalHebrewLetter(character));
                originalIndexes.Add(i);
                previousWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character) && !previousWasSpace)
            {
                normalizedBuilder.Append(' ');
                originalIndexes.Add(i);
                previousWasSpace = true;
            }
        }

        var normalized = normalizedBuilder.ToString().Trim();
        var spans = new List<(int Start, int Length)>();
        foreach (var term in terms
            .Select(term => NormalizeSearchText(term, keepSpaces: true))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length))
        {
            AddAdvancedSearchHighlightSpans(normalized, originalIndexes, term, spans);
            var compactTerm = RemoveSearchSpaces(term);
            if (!string.Equals(compactTerm, term, StringComparison.Ordinal))
            {
                AddAdvancedSearchHighlightSpans(RemoveSearchSpaces(normalized), originalIndexes, compactTerm, spans);
            }
        }

        return spans.OrderBy(span => span.Start).ToList();
    }

    private static void AddAdvancedSearchHighlightSpans(
        string normalized,
        IReadOnlyList<int> originalIndexes,
        string term,
        List<(int Start, int Length)> spans)
    {
        var searchStart = 0;
        while (searchStart < normalized.Length)
        {
            var index = normalized.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            if (index < originalIndexes.Count && index + term.Length - 1 < originalIndexes.Count)
            {
                var originalStart = originalIndexes[index];
                var originalEnd = originalIndexes[index + term.Length - 1] + 1;
                if (!spans.Any(span => originalStart < span.Start + span.Length && originalEnd > span.Start))
                {
                    spans.Add((originalStart, originalEnd - originalStart));
                }
            }

            searchStart = index + Math.Max(1, term.Length);
        }
    }

    private async void OpenAdvancedSearchResult(AdvancedSearchResult result)
    {
        if (result.IsDictionaryEntry)
        {
            await OpenAdvancedDictionarySearchResultAsync(result);
            return;
        }

        var book = _sefariaLibrary.GetInstalledBookByKey(result.BookKey) ??
            _sefariaLibrary.GetInstalledVersionsForTitle(result.WorkTitle).FirstOrDefault();
        if (book is null)
        {
            await ShowAdvancedSearchRemoteResultDialogAsync(result);
            return;
        }

        OpenInstalledBook(book);
        var pair = _openReaderTabs.FirstOrDefault(candidate =>
            string.Equals(candidate.Value.WorkTitle, book.Title, StringComparison.Ordinal));
        if (pair.Key is null)
        {
            return;
        }

        pair.Value.SearchHighlightReferenceWithinWork = result.ReferenceWithinWork;
        pair.Value.SearchHighlightTerms = result.MatchedTerms.ToList();
        pair.Value.PendingExactReferenceWithinWork = result.ReferenceWithinWork;
        RenderReaderContent(pair.Value);
        Dispatcher.UIThread.Post(
            () => ScrollReaderStateToReference(pair.Value, result.ReferenceWithinWork),
            DispatcherPriority.Background);
    }

    private async Task OpenAdvancedDictionarySearchResultAsync(AdvancedSearchResult result)
    {
        if (result.DictionaryEntryId <= 0)
        {
            return;
        }

        OpenOrSelectTab(DictionaryTabTitle);
        if (_dictionaryLexicons.Count == 0)
        {
            _dictionaryLexicons = await _sefariaLibrary.GetOfflineLexiconsAsync();
        }

        var entry = await _sefariaLibrary.GetOfflineDictionaryEntryAsync(result.DictionaryEntryId);
        if (entry is null)
        {
            return;
        }

        await OpenDictionaryEntryInBookAsync(entry);
        UpdateReaderTools();
    }

    private async Task ShowAdvancedSearchRemoteResultDialogAsync(AdvancedSearchResult result)
    {
        var workTitle = FirstNonEmptyAdvancedSearchValue(
            result.WorkTitle,
            ExtractAdvancedSearchReferenceTitle(result.Reference));
        if (string.IsNullOrWhiteSpace(workTitle))
        {
            OpenAdvancedSearchPreviewTab(result);
            return;
        }

        var installed = _sefariaLibrary.GetInstalledVersionsForTitle(workTitle);
        if (installed.Count > 0)
        {
            result.BookKey = installed[0].Key;
            result.VersionTitle = "Installed";
            OpenAdvancedSearchResult(result);
            return;
        }

        var previewButton = new Button { Content = "Preview Passage", MinWidth = 118, IsDefault = true };
        var closeButton = new Button { Content = "Close", MinWidth = 76, IsCancel = true };
        var dialog = new Window
        {
            Title = "Search result",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        previewButton.Click += (_, _) =>
        {
            OpenAdvancedSearchPreviewTab(result);
            dialog.Close();
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = result.Reference,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#101828"))
                },
                new TextBlock
                {
                    Text = $"“{workTitle}” is not available in the offline library. You can preview this passage, " +
                           "or update the library from Settings if this title should be included.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#344054"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { previewButton, closeButton }
                }
            }
        };

        await dialog.ShowDialog(this);
    }

    private static TextBlock CreateAdvancedSearchDialogLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 4, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#344054"))
        };
    }

    private void OpenAdvancedSearchPreviewTab(AdvancedSearchResult result)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(result.Reference)
            ? "Sefaria Preview"
            : $"Preview: {result.Reference}";
        var tab = CreateTab(title, CreateAdvancedSearchPreviewLoadingView(result.Reference));
        tab.Tag = title;
        _tabs.Add(tab);
        _centerTabs.SelectedItem = tab;
        _ = LoadAdvancedSearchPreviewTabAsync(tab, result);
    }

    private async Task LoadAdvancedSearchPreviewTabAsync(TabItem tab, AdvancedSearchResult result)
    {
        try
        {
            var preview = await _sefariaLibrary.GetLinkPreviewAsync(
                new SefariaLinkItem
                {
                    Ref = result.Reference,
                    SourceRef = result.Reference,
                    IndexTitle = result.WorkTitle
                },
                CancellationToken.None);
            ReplaceTabContent(tab, CreateAdvancedSearchPreviewView(result, preview));
        }
        catch (Exception ex)
        {
            ReplaceTabContent(tab, CreateAdvancedSearchPreviewView(result, null, ex.Message));
        }
    }

    private Control CreateAdvancedSearchPreviewLoadingView(string reference)
    {
        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = reference,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Loading Sefaria preview...",
                        Foreground = new SolidColorBrush(Color.Parse("#667085")),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
    }

    private Control CreateAdvancedSearchPreviewView(
        AdvancedSearchResult result,
        SefariaLinkPreview? preview,
        string error = "")
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        panel.Children.Add(new TextBlock
        {
            Text = result.Reference,
            FontWeight = FontWeight.SemiBold,
            FontSize = Math.Max(15, GetSelectedUiFontSize() + 1),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#101828"))
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Remote preview from Sefaria",
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap
        });

        if (preview is not null)
        {
            var english = BuildAdvancedSearchSnippet(preview.EnglishText);
            var hebrew = BuildAdvancedSearchSnippet(preview.HebrewText);
            if (!string.IsNullOrWhiteSpace(english))
            {
                panel.Children.Add(CreateAdvancedSearchPreviewTextBlock(english, result.MatchedTerms, FlowDirection.LeftToRight));
            }

            if (!string.IsNullOrWhiteSpace(hebrew))
            {
                panel.Children.Add(CreateAdvancedSearchPreviewTextBlock(hebrew, result.MatchedTerms, FlowDirection.RightToLeft));
            }
        }

        if (preview is null || (!string.IsNullOrWhiteSpace(result.Snippet) && panel.Children.Count <= 2))
        {
            panel.Children.Add(CreateAdvancedSearchPreviewTextBlock(
                result.Snippet,
                result.MatchedTerms,
                FlowDirection.LeftToRight));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Preview loaded from search snippet only: {error}",
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var downloadButton = new Button
        {
            Content = "Download Work",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        downloadButton.Click += async (_, _) => await ShowAdvancedSearchRemoteResultDialogAsync(result);
        panel.Children.Add(downloadButton);

        return new ScrollViewer
        {
            Background = Brushes.White,
            Padding = new Thickness(22),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    private static TextBlock CreateAdvancedSearchPreviewTextBlock(
        string text,
        IReadOnlyList<string> terms,
        FlowDirection flowDirection)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = flowDirection,
            FontSize = 16,
            LineHeight = 24,
            Foreground = new SolidColorBrush(Color.Parse("#101828"))
        };
        AppendHighlightedSnippetRuns(textBlock, text, terms);
        return textBlock;
    }

    private void OnSavedSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
        {
            return;
        }

        var savedSearch = e.AddedItems[0] switch
        {
            SavedAdvancedSearch direct => direct,
            ListBoxItem { DataContext: SavedAdvancedSearch fromItem } => fromItem,
            _ => null
        };
        if (savedSearch is null)
        {
            return;
        }

        OpenAdvancedSearchTabForSavedSearch(savedSearch);
        if (sender is ListBox listBox)
        {
            listBox.SelectedItem = null;
        }
    }

    private void OpenAdvancedSearchTabForSavedSearch(SavedAdvancedSearch savedSearch)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, AdvancedSearchTabTitle, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = CreateTab(AdvancedSearchTabTitle, CreateAdvancedSearchView(savedSearch));
            _tabs.Add(existing);
        }
        else
        {
            ReplaceTabContent(existing, CreateAdvancedSearchView(savedSearch));
        }

        _centerTabs.SelectedItem = existing;
    }

    private void RefreshSavedSearchesList()
    {
        if (_savedSearchesList is null)
        {
            return;
        }

        var items = new List<ListBoxItem>();
        var pinned = _savedAdvancedSearches
            .Where(search => search.IsPinned)
            .ToList();
        var unpinned = _savedAdvancedSearches
            .Where(search => !search.IsPinned)
            .ToList();

        if (pinned.Count > 0)
        {
            items.Add(CreateSavedSearchSectionHeader("Pinned"));
            items.AddRange(pinned.Select(CreateSavedSearchListItem));
        }

        if (unpinned.Count > 0)
        {
            if (pinned.Count > 0)
            {
                items.Add(CreateSavedSearchSectionHeader("Saved"));
            }

            items.AddRange(unpinned.Select(CreateSavedSearchListItem));
        }

        _savedSearchesList.ItemsSource = null;
        _savedSearchesList.ItemsSource = items;
    }

    private static ListBoxItem CreateSavedSearchSectionHeader(string text)
    {
        return new ListBoxItem
        {
            IsEnabled = false,
            Padding = new Thickness(6, 10, 6, 4),
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#667085"))
            }
        };
    }

    private ListBoxItem CreateSavedSearchListItem(SavedAdvancedSearch savedSearch)
    {
        var title = new TextBlock
        {
            Text = savedSearch.ToString(),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#101828"))
        };
        var meta = new TextBlock
        {
            Text = $"{savedSearch.Results.Count} results",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#667085"))
        };
        var closeButton = new Button
        {
            Content = "x",
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#98A2B3")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false
        };
        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            DeleteSavedAdvancedSearch(savedSearch);
        };
        var pinButton = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(6, 0, 4, 0),
            Opacity = savedSearch.IsPinned ? 0.85 : 0.32,
            MinWidth = 22,
            MinHeight = 20,
            Child = new TextBlock
            {
                Text = "\ud83d\udccc",
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        pinButton.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(pinButton).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            ToggleSavedAdvancedSearchPinned(savedSearch);
        };

        var textStack = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                title,
                meta
            }
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Children =
            {
                textStack,
                pinButton,
                closeButton
            }
        };
        Grid.SetColumn(pinButton, 1);
        Grid.SetColumn(closeButton, 2);

        var item = new ListBoxItem
        {
            DataContext = savedSearch,
            Padding = new Thickness(6),
            Content = row
        };

        var pinItem = new MenuItem { Header = savedSearch.IsPinned ? "Unpin" : "Pin" };
        pinItem.Click += (_, _) => ToggleSavedAdvancedSearchPinned(savedSearch);
        var rerunItem = new MenuItem { Header = "Rerun" };
        rerunItem.Click += async (_, _) => await RerunSavedAdvancedSearchAsync(savedSearch);
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += async (_, _) => await RenameSavedAdvancedSearchAsync(savedSearch);
        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => DeleteSavedAdvancedSearch(savedSearch);
        item.ContextMenu = new ContextMenu
        {
            Items =
            {
                pinItem,
                rerunItem,
                renameItem,
                deleteItem
            }
        };

        return item;
    }

    private void ToggleSavedAdvancedSearchPinned(SavedAdvancedSearch savedSearch)
    {
        savedSearch.IsPinned = !savedSearch.IsPinned;
        RefreshSavedSearchesList();
        SaveLayoutState();
    }

    private async Task RerunSavedAdvancedSearchAsync(SavedAdvancedSearch savedSearch)
    {
        if (savedSearch.Query is null)
        {
            OpenAdvancedSearchTabForSavedSearch(savedSearch);
            return;
        }

        var results = savedSearch.Query.TemplateId switch
        {
            SefariaTextSearchTemplateId => await RunSefariaAdvancedSearchAsync(savedSearch.Query, CancellationToken.None),
            DictionarySearchTemplateId => await RunDictionaryAdvancedSearchAsync(savedSearch.Query, CancellationToken.None),
            _ => await Task.Run(() => RunInstalledAdvancedSearch(savedSearch.Query))
        };
        savedSearch.CompletedAtUtc = DateTime.UtcNow;
        savedSearch.Results = results;
        savedSearch.Name = BuildAdvancedSearchName(savedSearch.Query, results.Count);
        savedSearch.QuerySummary = BuildAdvancedSearchSummary(savedSearch.Query);
        RefreshSavedSearchesList();
        SaveLayoutState();
        OpenAdvancedSearchTabForSavedSearch(savedSearch);
    }

    private async Task RenameSavedAdvancedSearchAsync(SavedAdvancedSearch savedSearch)
    {
        var name = await ShowAdvancedSearchRenameDialogAsync(savedSearch.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        savedSearch.Name = name.Trim();
        RefreshSavedSearchesList();
        SaveLayoutState();
    }

    private void DeleteSavedAdvancedSearch(SavedAdvancedSearch savedSearch)
    {
        _savedAdvancedSearches.Remove(savedSearch);
        RefreshSavedSearchesList();
        SaveLayoutState();
    }

    private async Task<string?> ShowAdvancedSearchRenameDialogAsync(string currentName)
    {
        var nameBox = new TextBox
        {
            Text = currentName,
            MinWidth = 420
        };
        var okButton = new Button { Content = "Rename", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 76 };
        var dialog = new Window
        {
            Title = "Rename Saved Search",
            Width = 520,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                Margin = new Thickness(16),
                Children =
                {
                    nameBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            okButton,
                            cancelButton
                        }
                    }
                }
            }
        };
        if (dialog.Content is Grid grid)
        {
            Grid.SetRow(grid.Children[1], 1);
        }

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = nameBox.Text;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }

    private sealed class AdvancedSearchQuery
    {
        public string TemplateId { get; set; } = ProximityTemplateId;
        public string TermA { get; set; } = string.Empty;
        public string TermB { get; set; } = string.Empty;
        public string TermC { get; set; } = string.Empty;
        public int Distance { get; set; }
        public string Unit { get; set; } = "words";
        public string SameUnit { get; set; } = "segment";
        public string Scope { get; set; } = "Installed books";
        public long? DictionaryLexiconId { get; set; }
        public List<AdvancedSearchScopeSelection> SelectedScopes { get; set; } = new();
        public string MatchMode { get; set; } = "Exact";
    }

    private sealed record AdvancedSearchTemplate(string Id, string TemplateText, bool IsImplemented)
    {
        public override string ToString()
        {
            return TemplateText;
        }
    }

    private sealed class AdvancedSearchFormFields
    {
        public bool HasControls { get; init; }
        public string TemplateId { get; init; } = string.Empty;
        public TextBox TermABox { get; init; } = new();
        public TextBox TermBBox { get; init; } = new();
        public TextBox TermCBox { get; init; } = new();
        public TextBox DistanceBox { get; init; } = new();
        public ComboBox UnitBox { get; init; } = new();
        public ComboBox SameUnitBox { get; init; } = new();
        public WrapPanel ScopePanel { get; init; } = new();
        public List<AdvancedSearchScopeSelection> SelectedScopes { get; init; } = new();
        public ComboBox MatchBox { get; init; } = new();
        public ComboBox DictionaryScopeBox { get; init; } = new();
    }

    private sealed class AdvancedSearchFieldValues
    {
        public string TermA { get; set; } = string.Empty;
        public string TermB { get; set; } = string.Empty;
        public string TermC { get; set; } = string.Empty;
        public string Distance { get; set; } = "10";
        public string Unit { get; set; } = "words";
        public string SameUnit { get; set; } = "segment";
        public string Scope { get; set; } = "Installed books";
        public long? DictionaryLexiconId { get; set; }
        public List<AdvancedSearchScopeSelection> SelectedScopes { get; set; } = new();
        public string MatchMode { get; set; } = "Exact";
    }

    private sealed class AdvancedSearchScopeSelection
    {
        public AdvancedSearchScopeKind Kind { get; set; } = AdvancedSearchScopeKind.AllInstalled;
        public string Key { get; set; } = "Installed books";
        public string Label { get; set; } = "Installed books";
        public string HebrewLabel { get; set; } = string.Empty;

        public override string ToString()
        {
            return Label;
        }
    }

    private enum AdvancedSearchScopeKind
    {
        AllInstalled,
        Category,
        Work
    }
}

internal static class AdvancedSearchGridExtensions
{
    public static Grid WithChildRow(this Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        return grid;
    }
}
