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

public partial class MainWindow : Window
{
    private const double CollapsedPanelWidth = 40;
    private const double DefaultExpandedPanelWidth = 280;
    private const double DefaultReaderFontSize = 15;
    private const double MinReaderFontSize = 11;
    private const double MaxReaderFontSize = 28;
    private const double DefaultUiFontSize = 13;
    private const double MinUiFontSize = 10;
    private const double MaxUiFontSize = 22;
    private const string LibraryManagerTabTitle = "Library Manager";
    private const string SettingsTabTitle = "Settings";
    private const string AllCommentariesSelectionKey = "__all_commentaries__";

    private ColumnDefinition? _leftColumn;
    private ColumnDefinition? _rightColumn;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private TreeView? _leftPanelBody;
    private TextBlock? _leftPanelTitle;
    private TextBlock? _rightPanelTitle;
    private StackPanel? _rightPanelBody;
    private TabControl? _centerTabs;
    private TreeView? _libraryTree;
    private ScrollViewer? _libraryTreeScrollViewer;
    private TreeViewItem? _selectedLibraryTreeItem;
    private TextBlock? _libraryTitle;
    private TextBlock? _libraryHebrewTitle;
    private TextBlock? _libraryDescription;
    private TextBlock? _libraryStatus;
    private ComboBox? _libraryVersionBox;
    private ProgressBar? _libraryProgress;
    private Button? _libraryDownloadButton;
    private Button? _libraryCancelButton;

    private ObservableCollection<TabItem>? _tabs;
    private readonly SefariaLibraryService _sefariaLibrary = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly Dictionary<TabItem, ReaderTabState> _openReaderTabs = new();
    private AppSettings _settings = new();

    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private bool _isSefariaDownloading;
    private double _leftExpandedWidth = DefaultExpandedPanelWidth;
    private double _rightExpandedWidth = DefaultExpandedPanelWidth;
    private SefariaCategoryNode? _sefariaRoot;
    private SefariaBookNode? _selectedSefariaBook;
    private CancellationTokenSource? _sefariaDownloadCts;
    private List<FontOption>? _allFontOptions;
    private List<FontOption>? _hebrewFontOptions;

    public MainWindow()
    {
        InitializeComponent();

        var rootGrid = this.FindControl<Grid>("LayoutRoot");
        if (rootGrid is not null)
        {
            _leftColumn = rootGrid.ColumnDefinitions[0];
            _rightColumn = rootGrid.ColumnDefinitions[4];
        }
        _leftSplitter = this.FindControl<GridSplitter>("LeftSplitter");
        _rightSplitter = this.FindControl<GridSplitter>("RightSplitter");
        _leftPanelBody = this.FindControl<TreeView>("LeftPanelBody");
        _leftPanelTitle = this.FindControl<TextBlock>("LeftPanelTitle");
        _rightPanelTitle = this.FindControl<TextBlock>("RightPanelTitle");
        _rightPanelBody = this.FindControl<StackPanel>("RightPanelBody");
        _centerTabs = this.FindControl<TabControl>("CenterTabs");

        _tabs = new ObservableCollection<TabItem>();
        _centerTabs.ItemsSource = _tabs;
        _centerTabs.SelectionChanged += (_, _) =>
        {
            UpdateTabHeaderStates();
            UpdateReaderTools();
        };

        _settings = _settingsService.Load();
        ApplyUiFontSetting();
        InitializeNavigationItems();
        LoadLayoutState();
        RefreshInstalledBooksTree();
        UpdateTabHeaderStates();

        this.Closing += (_, _) => SaveLayoutState();
        this.Closed += (_, _) => SaveLayoutState();
    }

    private void InitializeNavigationItems()
    {
        if (_leftPanelBody is null)
        {
            return;
        }

        _leftPanelBody.ItemsSource = null;
    }

    private void LoadLayoutState()
    {
        var state = ReadState();

        if (state is null)
        {
            EnsureDefaultTabs();
            ApplyLeftPanelState(false, DefaultExpandedPanelWidth);
            ApplyRightPanelState(false, DefaultExpandedPanelWidth);
            return;
        }

        _leftExpandedWidth = Math.Max(CollapsedPanelWidth, state.LeftExpandedWidth);
        _rightExpandedWidth = Math.Max(CollapsedPanelWidth, state.RightExpandedWidth);

        ApplyLeftPanelState(state.LeftCollapsed, _leftExpandedWidth);
        ApplyRightPanelState(state.RightCollapsed, _rightExpandedWidth);

        ApplyTabsFromState(state);
    }

    private void ApplyTabsFromState(LayoutState state)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        _tabs.Clear();

        var savedTabs = state.Tabs.Count > 0
            ? state.Tabs
            : state.OpenTabs
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new SavedTabState { Kind = SavedTabKind.Utility, Title = t })
                .ToList();

        if (savedTabs.Count == 0)
        {
            EnsureDefaultTabs();
            return;
        }

        foreach (var savedTab in savedTabs)
        {
            if (savedTab.Kind == SavedTabKind.Reader)
            {
                RestoreReaderTab(savedTab);
            }
            else if (!string.IsNullOrWhiteSpace(savedTab.Title))
            {
                _tabs.Add(CreateTab(savedTab.Title));
            }
        }

        _centerTabs.SelectedIndex = _tabs.Count == 0
            ? -1
            : Math.Clamp(state.SelectedTabIndex, 0, _tabs.Count - 1);
    }

    private void EnsureDefaultTabs()
    {
        // The main workspace is allowed to start empty.
    }

    private TabItem CreateTab(string title, Control? content = null)
    {
        var tab = new TabItem
        {
            Content = content ?? CreateTabContent(title)
        };

        var tabIcon = new TextBlock
        {
            Text = "\u25c9",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#3F4A56")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#2F3843")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var closeButton = new Button
        {
            Content = new TextBlock
            {
                Text = "\u00d7",
                FontSize = 13,
                LineHeight = 13,
                Foreground = new SolidColorBrush(Color.Parse("#2F3843")),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false
        };

        closeButton.Classes.Add("tab-close-button");
        closeButton.Click += (_, _) => CloseTab(tab);

        var separator = new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#7FA3EC")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerLayout = new Grid
        {
            Width = 236,
            Height = 34,
            ColumnDefinitions = new ColumnDefinitions("18,*,24,10"),
            Children =
            {
                tabIcon,
                titleBlock,
                closeButton,
                separator
            }
        };

        Grid.SetColumn(titleBlock, 1);
        Grid.SetColumn(closeButton, 2);
        Grid.SetColumn(separator, 3);

        var header = new Border
        {
            Padding = new Thickness(10, 0, 6, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = headerLayout
        };

        DragDrop.SetAllowDrop(header, true);

        header.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(tab.Tag as string ?? string.Empty));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        };

        DragDrop.AddDragOverHandler(header, (_, e) =>
        {
            if (e.DataTransfer.Contains(DataFormat.Text))
            {
                e.DragEffects = DragDropEffects.Move;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        });

        DragDrop.AddDropHandler(header, (_, e) =>
        {
            if (_tabs is null)
            {
                return;
            }

            var sourceTitle = e.DataTransfer.TryGetValue(DataFormat.Text);
            if (string.IsNullOrWhiteSpace(sourceTitle))
            {
                return;
            }

            var source = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, sourceTitle, StringComparison.Ordinal));

            if (source is null || source == tab)
            {
                return;
            }

            var sourceIndex = _tabs.IndexOf(source);
            var targetIndex = _tabs.IndexOf(tab);

            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }

            _tabs.Move(sourceIndex, targetIndex);
            if (_centerTabs is not null)
            {
                _centerTabs.SelectedItem = source;
            }
        });

        tab.Header = header;

        tab.Tag = title;

        return tab;
    }

    private Control CreateTabContent(string title)
    {
        if (string.Equals(title, LibraryManagerTabTitle, StringComparison.Ordinal))
        {
            return CreateLibraryManagerView();
        }

        if (string.Equals(title, SettingsTabTitle, StringComparison.Ordinal))
        {
            return CreateSettingsView();
        }

        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(12),
            Child = new TextBlock { Text = $"{title} content placeholder" }
        };
    }

    private void OpenLibraryManagerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(LibraryManagerTabTitle);
    }

    private void OpenSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenOrSelectTab(SettingsTabTitle);
    }

    private void OpenOrSelectTab(string title)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, title, StringComparison.Ordinal));
        if (existing is not null)
        {
            _centerTabs.SelectedItem = existing;
            return;
        }

        var tab = CreateTab(title);
        _tabs.Add(tab);
        _centerTabs.SelectedItem = tab;
    }

    private void RefreshInstalledBooksTree()
    {
        if (_leftPanelBody is null)
        {
            return;
        }

        var roots = _sefariaLibrary.BuildInstalledTree();
        _leftPanelBody.ItemsSource = roots
            .Cast<object>()
            .Select(CreateInstalledBookTreeItem)
            .ToList();
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

    private Control CreateSettingsView()
    {
        var hebrewOption = CreateTitleDisplayOption("Hebrew", InstalledBookTitleDisplay.Hebrew);
        var englishOption = CreateTitleDisplayOption("English", InstalledBookTitleDisplay.English);
        var bothOption = CreateTitleDisplayOption("Hebrew / English", InstalledBookTitleDisplay.Both);
        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Settings",
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Installed library title display",
                        FontWeight = FontWeight.SemiBold
                    },
                    hebrewOption,
                    englishOption,
                    bothOption,
                    CreateFontSettingRow(
                        "Display font",
                        GetAllFontOptions(),
                        GetSelectedUiFontFamily(),
                        GetSelectedUiFontSize(),
                        MinUiFontSize,
                        MaxUiFontSize,
                        family =>
                        {
                            _settings.UiFontFamily = family;
                            _settingsService.Save(_settings);
                            ApplyUiFontSetting();
                        },
                        size =>
                        {
                            _settings.UiFontSize = size;
                            _settingsService.Save(_settings);
                            ApplyUiFontSetting();
                        }),
                    CreateFontSettingRow(
                        "Hebrew reader font",
                        GetHebrewFontOptions(),
                        GetSelectedHebrewFontFamily(),
                        GetSelectedHebrewFontSize(),
                        MinReaderFontSize,
                        MaxReaderFontSize,
                        family =>
                        {
                            _settings.HebrewReaderFontFamily = family;
                            _settingsService.Save(_settings);
                            RefreshOpenReaderTabs();
                        },
                        size =>
                        {
                            _settings.HebrewReaderFontSize = size;
                            _settingsService.Save(_settings);
                            RefreshOpenReaderTabs();
                        }),
                    CreateFontSettingRow(
                        "English reader font",
                        GetAllFontOptions(),
                        GetSelectedEnglishFontFamily(),
                        GetSelectedEnglishFontSize(),
                        MinReaderFontSize,
                        MaxReaderFontSize,
                        family =>
                        {
                            _settings.EnglishReaderFontFamily = family;
                            _settingsService.Save(_settings);
                            RefreshOpenReaderTabs();
                        },
                        size =>
                        {
                            _settings.EnglishReaderFontSize = size;
                            _settingsService.Save(_settings);
                            RefreshOpenReaderTabs();
                        })
                }
            }
        };
    }

    private Control CreateFontSettingRow(
        string label,
        IReadOnlyList<FontOption> fontOptions,
        string selectedFamily,
        double currentSize,
        double minSize,
        double maxSize,
        Action<string> setSelectedFamily,
        Action<double> setSize)
    {
        return new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeight.SemiBold
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        CreateFontSettingPicker(fontOptions, selectedFamily, setSelectedFamily),
                        CreateFontSizeSettingPicker(currentSize, minSize, maxSize, setSize)
                    }
                }
            }
        };
    }

    private RadioButton CreateTitleDisplayOption(string label, InstalledBookTitleDisplay value)
    {
        var option = new RadioButton
        {
            Content = label,
            GroupName = "InstalledBookTitleDisplay",
            IsChecked = _settings.InstalledBookTitleDisplay == value,
            Tag = value
        };

        option.IsCheckedChanged += (_, _) =>
        {
            if (option.IsChecked != true || option.Tag is not InstalledBookTitleDisplay selected)
            {
                return;
            }

            _settings.InstalledBookTitleDisplay = selected;
            _settingsService.Save(_settings);
            RefreshInstalledBooksTree();
            RefreshLibraryManagerHeaders();
            UpdateLibraryDetails();
        };

        return option;
    }

    private static ComboBox CreateFontSettingPicker(
        IReadOnlyList<FontOption> options,
        string selectedFamily,
        Action<string> setSelectedFamily)
    {
        var selectedOption = options.FirstOrDefault(option => string.Equals(option.FamilyName, selectedFamily, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault();
        var picker = new ComboBox
        {
            MinWidth = 220,
            MaxDropDownHeight = 360,
            ItemsSource = options,
            SelectedItem = selectedOption
        };

        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not FontOption option)
            {
                return;
            }

            setSelectedFamily(option.FamilyName);
        };

        return picker;
    }

    private ComboBox CreateFontSizeSettingPicker(
        double currentSize,
        double minSize,
        double maxSize,
        Action<double> setSize)
    {
        var sizes = BuildFontSizeOptions(minSize, maxSize);
        var picker = new ComboBox
        {
            Width = 78,
            IsEditable = true,
            ItemsSource = sizes,
            SelectedItem = FormatFontSize(currentSize),
            Text = FormatFontSize(currentSize)
        };

        CancellationTokenSource? debounceTimer = null;

        void TryApplySize()
        {
            var rawValue = picker.Text;
            if (string.IsNullOrWhiteSpace(rawValue) && picker.SelectedItem is string selected)
            {
                rawValue = selected;
            }

            if (!TryParseFontSize(rawValue, out var parsedSize))
            {
                picker.Text = FormatFontSize(currentSize);
                return;
            }

            var nextSize = Math.Clamp(parsedSize, minSize, maxSize);
            picker.Text = FormatFontSize(nextSize);
            setSize(nextSize);
        }

        void ScheduleApplySize()
        {
            debounceTimer?.Cancel();
            debounceTimer = new CancellationTokenSource();
            var token = debounceTimer.Token;

            Task.Delay(500, token).ContinueWith(_ =>
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.InvokeAsync(TryApplySize);
                }
            }, TaskScheduler.Default);
        }

        // SelectionChanged fires frequently during dropdown interactions, so debounce it
        picker.SelectionChanged += (_, _) => ScheduleApplySize();
        // LostFocus means user is done editing
        picker.LostFocus += (_, _) => TryApplySize();
        // Enter key confirms the input immediately
        picker.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                debounceTimer?.Cancel();
                TryApplySize();
                e.Handled = true;
            }
        };

        return picker;
    }

    private static List<string> BuildFontSizeOptions(double minSize, double maxSize)
    {
        var sizes = new List<string>();
        for (var size = (int)Math.Ceiling(minSize); size <= (int)Math.Floor(maxSize); size++)
        {
            sizes.Add(FormatFontSize(size));
        }

        return sizes;
    }

    private static string FormatFontSize(double size)
    {
        return $"{size:0}px";
    }

    private static bool TryParseFontSize(string? value, out double size)
    {
        size = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2].Trim();
        }

        return double.TryParse(normalized, out size);
    }

    private List<FontOption> GetAllFontOptions()
    {
        if (_allFontOptions is not null)
        {
            return _allFontOptions;
        }

        _allFontOptions = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new FontOption(name))
            .ToList();
        return _allFontOptions;
    }

    private List<FontOption> GetHebrewFontOptions()
    {
        if (_hebrewFontOptions is not null)
        {
            return _hebrewFontOptions;
        }

        _hebrewFontOptions = GetAllFontOptions()
            .Where(option => FontSupportsHebrew(option.FamilyName))
            .ToList();
        if (_hebrewFontOptions.Count == 0)
        {
            _hebrewFontOptions.AddRange(GetAllFontOptions());
        }

        return _hebrewFontOptions;
    }

    private void ApplyUiFontSetting()
    {
        FontFamily = new FontFamily(GetSelectedUiFontFamily());
        FontSize = GetSelectedUiFontSize();
    }

    private void RefreshOpenReaderTabs()
    {
        foreach (var state in _openReaderTabs.Values)
        {
            RenderReaderContent(state);
        }

        SaveLayoutState();
    }

    private string GetSelectedUiFontFamily()
    {
        return ResolveInstalledFont(_settings.UiFontFamily, GetAllFontOptions(), "Inter", "Segoe UI");
    }

    private string GetSelectedEnglishFontFamily()
    {
        return ResolveInstalledFont(_settings.EnglishReaderFontFamily, GetAllFontOptions(), "Segoe UI", "Arial");
    }

    private string GetSelectedHebrewFontFamily()
    {
        return ResolveInstalledFont(
            _settings.HebrewReaderFontFamily,
            GetHebrewFontOptions(),
            "SBL Hebrew",
            "Noto Serif Hebrew",
            "Ezra SIL",
            "David",
            "Nirmala UI",
            "Segoe UI");
    }

    private double GetSelectedUiFontSize()
    {
        return NormalizeSettingSize(_settings.UiFontSize, DefaultUiFontSize, MinUiFontSize, MaxUiFontSize);
    }

    private double GetSelectedHebrewFontSize()
    {
        return NormalizeSettingSize(_settings.HebrewReaderFontSize, DefaultReaderFontSize, MinReaderFontSize, MaxReaderFontSize);
    }

    private double GetSelectedEnglishFontSize()
    {
        return NormalizeSettingSize(_settings.EnglishReaderFontSize, DefaultReaderFontSize, MinReaderFontSize, MaxReaderFontSize);
    }

    private static double NormalizeSettingSize(double value, double fallback, double minSize, double maxSize)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            value = fallback;
        }

        return Math.Clamp(value, minSize, maxSize);
    }

    private static string ResolveInstalledFont(string configuredFamily, IReadOnlyList<FontOption> options, params string[] preferredFamilies)
    {
        var configured = options.FirstOrDefault(option =>
            string.Equals(option.FamilyName, configuredFamily, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return configured.FamilyName;
        }

        foreach (var family in preferredFamilies)
        {
            var preferred = options.FirstOrDefault(option =>
                string.Equals(option.FamilyName, family, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred.FamilyName;
            }
        }

        return options.FirstOrDefault()?.FamilyName ?? FontFamily.Default.Name;
    }

    private static bool FontSupportsHebrew(string familyName)
    {
        const int hebrewAlephCodepoint = 0x05D0;
        try
        {
            var typeface = new Typeface(new FontFamily(familyName));
            return FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface) &&
                glyphTypeface.CharacterToGlyphMap.TryGetGlyph(hebrewAlephCodepoint, out var alephGlyph) &&
                alephGlyph != 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnInstalledBookSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 &&
            e.AddedItems[0] is TreeViewItem { DataContext: InstalledSefariaCategory { IsBookTitle: true } bookCategory } &&
            _sefariaLibrary.GetInstalledVersionsForTitle(bookCategory.Title).FirstOrDefault() is { } book)
        {
            OpenInstalledBook(book);
        }
    }

    private void OpenInstalledBook(InstalledSefariaBook book)
    {
        OpenInstalledBook(book, null);
    }

    private void OpenInstalledBook(InstalledSefariaBook book, SavedTabState? savedState)
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
        if (installedVersions.Count == 0)
        {
            return;
        }

        var existing = _openReaderTabs.FirstOrDefault(pair => string.Equals(pair.Value.WorkTitle, book.Title, StringComparison.Ordinal));
        if (existing.Key is not null)
        {
            if (SefariaLibraryService.IsHebrew(book))
            {
                var selectedHebrewText = installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal));
                if (selectedHebrewText is not null)
                {
                    existing.Value.Primary = selectedHebrewText;
                    NormalizeHebrewMarksMode(existing.Value);
                    SaveSelectedHebrewText(existing.Value);
                    RenderReaderContent(existing.Value);
                    UpdateReaderTools();
                }
            }
            else
            {
                existing.Value.SelectedTranslation = installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal));
                SaveSelectedTranslation(existing.Value);
                RenderReaderContent(existing.Value);
                UpdateReaderTools();
            }

            _centerTabs.SelectedItem = existing.Key;
            return;
        }

        var hebrewTexts = installedVersions.Where(SefariaLibraryService.IsHebrew).ToList();
        var primary = GetSavedHebrewText(book.Title, hebrewTexts)
            ?? hebrewTexts.FirstOrDefault()
            ?? installedVersions.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal))
            ?? installedVersions[0];
        var translations = installedVersions.Where(version => !SefariaLibraryService.IsHebrew(version)).ToList();
        var selectedTranslation = !SefariaLibraryService.IsHebrew(book)
            ? translations.FirstOrDefault(version => string.Equals(version.Key, book.Key, StringComparison.Ordinal))
            : GetSavedTranslation(book.Title, translations);
        selectedTranslation ??= translations.FirstOrDefault();

        var state = new ReaderTabState
        {
            WorkTitle = book.Title,
            Primary = primary,
            Versions = installedVersions,
            HebrewTexts = hebrewTexts,
            Translations = translations,
            SelectedTranslation = selectedTranslation,
            DisplayMode = GetSavedDisplayMode(book.Title, selectedTranslation is not null)
        };

        ApplySavedReaderState(state, savedState);
        SaveSelectedHebrewText(state);
        SaveSelectedTranslation(state);

        var reader = CreateReaderView(state, out var readerList, out var titleBlock, out var versionBlock);
        state.ReaderList = readerList;
        state.TitleBlock = titleBlock;
        state.VersionBlock = versionBlock;

        var tab = CreateTab(FormatTitle(primary.Title, primary.HebrewTitle), reader);
        tab.Tag = primary.Title;
        _openReaderTabs[tab] = state;
        _tabs.Add(tab);
        _centerTabs.SelectedItem = tab;

        Dispatcher.UIThread.Post(() =>
        {
            if (readerList.Scroll is not null)
            {
                var savedOffset = savedState?.ScrollOffset ?? state.Primary.LastScrollOffset;
                readerList.Scroll.Offset = new Vector(readerList.Scroll.Offset.X, savedOffset);
            }
        });

        RenderReaderContent(state);
        UpdateReaderTools();
    }

    private void RestoreReaderTab(SavedTabState savedTab)
    {
        if (string.IsNullOrWhiteSpace(savedTab.WorkTitle))
        {
            return;
        }

        var versions = _sefariaLibrary.GetInstalledVersionsForTitle(savedTab.WorkTitle);
        if (versions.Count == 0)
        {
            return;
        }

        var selectedBook = versions.FirstOrDefault(version => string.Equals(version.Key, savedTab.PrimaryKey, StringComparison.Ordinal))
            ?? versions.FirstOrDefault(version => string.Equals(version.Key, savedTab.SelectedTranslationKey, StringComparison.Ordinal))
            ?? versions[0];
        OpenInstalledBook(selectedBook, savedTab);
    }

    private static void ApplySavedReaderState(ReaderTabState state, SavedTabState? savedState)
    {
        if (savedState is null)
        {
            NormalizeHebrewMarksMode(state);
            return;
        }

        state.Primary = state.HebrewTexts.FirstOrDefault(version => string.Equals(version.Key, savedState.PrimaryKey, StringComparison.Ordinal))
            ?? state.Primary;
        state.SelectedTranslation = state.Translations.FirstOrDefault(version => string.Equals(version.Key, savedState.SelectedTranslationKey, StringComparison.Ordinal))
            ?? state.SelectedTranslation;
        state.DisplayMode = savedState.DisplayMode;
        state.HebrewMarksMode = savedState.HebrewMarksMode;
        NormalizeHebrewMarksMode(state);

        state.IsNavigationExpanded = savedState.IsNavigationExpanded;
        state.IsDisplayExpanded = savedState.IsDisplayExpanded;
        state.IsTextsExpanded = savedState.IsTextsExpanded;
    }

    private InstalledSefariaBook? GetSavedTranslation(string workTitle, List<InstalledSefariaBook> translations)
    {
        if (!_settings.SelectedTranslationsByBook.TryGetValue(workTitle, out var selectedKey))
        {
            return null;
        }

        return translations.FirstOrDefault(version => string.Equals(version.Key, selectedKey, StringComparison.Ordinal));
    }

    private InstalledSefariaBook? GetSavedHebrewText(string workTitle, List<InstalledSefariaBook> hebrewTexts)
    {
        if (!_settings.SelectedHebrewTextsByBook.TryGetValue(workTitle, out var selectedKey))
        {
            return null;
        }

        return hebrewTexts.FirstOrDefault(version => string.Equals(version.Key, selectedKey, StringComparison.Ordinal));
    }

    private ReaderDisplayMode GetSavedDisplayMode(string workTitle, bool hasTranslation)
    {
        if (_settings.ReaderDisplayModesByBook.TryGetValue(workTitle, out var mode))
        {
            return mode;
        }

        return hasTranslation ? ReaderDisplayMode.TranslationBelow : ReaderDisplayMode.PrimaryOnly;
    }

    private static bool SupportsCantillation(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Tanakh", StringComparison.OrdinalIgnoreCase));
    }

    private static void NormalizeHebrewMarksMode(ReaderTabState state)
    {
        if (!SupportsCantillation(state.Primary) && state.HebrewMarksMode == HebrewMarksMode.NikkudAndCantillation)
        {
            state.HebrewMarksMode = HebrewMarksMode.Nikkud;
        }
    }

    private static string FormatReaderVersionTitle(InstalledSefariaBook book)
    {
        return string.IsNullOrWhiteSpace(book.VersionTitle)
            ? book.LanguageCode
            : book.VersionTitle;
    }

    private Control CreateReaderView(
        ReaderTabState state,
        out ListBox readerList,
        out TextBlock titleBlock,
        out TextBlock versionBlock)
    {
        titleBlock = new TextBlock
        {
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        versionBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var header = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(32, 24, 32, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                titleBlock,
                versionBlock
            }
        };

        readerList = new ListBox
        {
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(32, 0, 96, 24),
            SelectionMode = SelectionMode.Single,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
            {
                Orientation = Orientation.Vertical,
                CacheLength = 2
            }),
            ItemTemplate = new FuncDataTemplate<ReaderDisplayRow>((unit, _) => unit is null
                ? new Control()
                : CreateReaderDisplayRow(state, unit),
                true)
        };
        readerList.Classes.Add("reader-list");
        var currentReaderList = readerList;
        readerList.SelectionChanged += (_, e) =>
        {
            var selectedRow = currentReaderList.SelectedItem as ReaderDisplayRow;
            OnReaderParagraphSelectionChanged(state, selectedRow);
            e.Handled = true;
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(readerList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalSnapPointsType(readerList, SnapPointsType.None);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brushes.White,
            Children =
            {
                header,
                readerList
            }
        };
        Grid.SetRow(readerList, 1);

        return layout;
    }

    private void RenderReaderContent(ReaderTabState state)
    {
        if (state.ReaderList is null || state.TitleBlock is null || state.VersionBlock is null)
        {
            return;
        }

        var primaryIsHebrew = SefariaLibraryService.IsHebrew(state.Primary);
        ApplyReaderTitle(state.TitleBlock, state.Primary.Title, state.Primary.HebrewTitle);
        state.TitleBlock.TextAlignment = TextAlignment.Center;
        state.TitleBlock.FlowDirection = FlowDirection.LeftToRight;
        state.VersionBlock.Text = FormatReaderVersionTitle(state.Primary);
        state.VersionBlock.TextAlignment = TextAlignment.Center;
        state.VersionBlock.FlowDirection = primaryIsHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        var primaryUnits = _sefariaLibrary.ReadInstalledBookUnits(state.Primary);
        var translationUnits = state.SelectedTranslation is null
            ? new List<ReaderTextUnit>()
            : _sefariaLibrary.ReadInstalledBookUnits(state.SelectedTranslation);
        var navigationPages = BuildReaderNavigationPages(state);
        var isTalmudNavigation = navigationPages.Count > 0;
        var chapterTitlesByPage = navigationPages
            .GroupBy(page => page.Page, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => FormatChapterTitle(group.First()), StringComparer.Ordinal);

        var showTranslation = state.SelectedTranslation is not null && state.DisplayMode != ReaderDisplayMode.PrimaryOnly;
        var count = showTranslation ? Math.Max(primaryUnits.Count, translationUnits.Count) : primaryUnits.Count;
        var items = new List<ReaderDisplayRow>(count);
        var pageRows = new Dictionary<string, ReaderDisplayRow>(StringComparer.Ordinal);
        var currentPage = string.Empty;
        for (var i = 0; i < count; i++)
        {
            var primary = i < primaryUnits.Count ? primaryUnits[i] : null;
            var translation = showTranslation && i < translationUnits.Count ? translationUnits[i] : null;
            var reference = primary?.Reference ?? translation?.Reference ?? string.Empty;
            var page = GetReferencePart(reference, 0);
            var chapterTitle = chapterTitlesByPage.TryGetValue(page, out var navigationChapterTitle)
                ? navigationChapterTitle
                : FormatChapterTitle(primary, translation);
            if (!string.IsNullOrWhiteSpace(page) && !string.Equals(page, currentPage, StringComparison.Ordinal))
            {
                currentPage = page;
                var heading = new ReaderDisplayRow(
                    Primary: null,
                    Translation: null,
                    IsChapterHeading: true,
                    ChapterKey: page,
                    ChapterHeading: FormatChapterHeading(page, chapterTitle));
                items.Add(heading);
                pageRows[page] = heading;
            }

            items.Add(new ReaderDisplayRow(
                Primary: primary,
                Translation: translation,
                IsChapterHeading: false,
                ChapterKey: page,
                ChapterHeading: string.Empty));
        }

        state.NavigationItems = isTalmudNavigation
            ? navigationPages
                .Where(page => pageRows.ContainsKey(page.Page))
                .Select(page => new ReaderNavigationItem(
                    FormatNavigationChapterLabel(page.Page),
                    pageRows[page.Page],
                    FormatChapterTitle(page)))
                .ToList()
            : pageRows
                .Select(pair => new ReaderNavigationItem(
                    FormatNavigationChapterLabel(pair.Key),
                    pair.Value,
                    pair.Value.ChapterHeading))
                .ToList();
        state.HasTalmudNavigation = isTalmudNavigation;
        state.NavigationChapters = BuildReaderNavigationChapters(state.NavigationItems);
        state.ReaderList.ItemsSource = items;
    }

    private void OnReaderParagraphSelectionChanged(ReaderTabState state, ReaderDisplayRow? row)
    {
        state.CommentaryLoadCts?.Cancel();
        state.CommentaryLoadCts = null;

        state.SelectedReaderRow = row is { IsChapterHeading: false } ? row : null;
        state.SelectedCommentaryRef = state.SelectedReaderRow is null
            ? string.Empty
            : BuildSefariaAnchorRef(state, state.SelectedReaderRow, preferTranslation: false);
        state.Commentaries = new List<SefariaCommentaryItem>();
        state.CommentaryError = string.Empty;
        state.IsCommentaryLoading = false;

        if (string.IsNullOrWhiteSpace(state.SelectedCommentaryRef))
        {
            UpdateReaderTools();
            return;
        }

        state.IsCommentaryLoading = true;
        UpdateReaderTools();

        var cts = new CancellationTokenSource();
        state.CommentaryLoadCts = cts;
        _ = LoadCommentariesForSelectionAsync(state, state.SelectedCommentaryRef, cts);
    }

    private async Task LoadCommentariesForSelectionAsync(
        ReaderTabState state,
        string anchorRef,
        CancellationTokenSource cts)
    {
        var requestedAnchorRef = anchorRef;
        var appliedResults = false;
        try
        {
            var commentaries = await _sefariaLibrary.GetCommentariesAsync(anchorRef, cts.Token);
            if (commentaries.Count == 0 && state.SelectedReaderRow is not null)
            {
                var fallbackRef = BuildSefariaAnchorRef(state, state.SelectedReaderRow, preferTranslation: true);
                if (!string.IsNullOrWhiteSpace(fallbackRef) &&
                    !string.Equals(fallbackRef, anchorRef, StringComparison.Ordinal))
                {
                    commentaries = await _sefariaLibrary.GetCommentariesAsync(fallbackRef, cts.Token);
                    if (commentaries.Count > 0)
                    {
                        anchorRef = fallbackRef;
                    }
                }
            }

            if (cts.IsCancellationRequested ||
                !ReferenceEquals(state.CommentaryLoadCts, cts) ||
                !string.Equals(state.SelectedCommentaryRef, requestedAnchorRef, StringComparison.Ordinal))
            {
                return;
            }

            state.SelectedCommentaryRef = anchorRef;
            state.Commentaries = commentaries;
            state.CommentaryError = string.Empty;
            appliedResults = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            if (IsActiveCommentaryLoad(state, requestedAnchorRef, cts))
            {
                state.CommentaryError = "Commentaries could not be loaded.";
            }
        }
        finally
        {
            if (IsActiveCommentaryLoad(state, requestedAnchorRef, cts) ||
                (appliedResults && !cts.IsCancellationRequested && ReferenceEquals(state.CommentaryLoadCts, cts)))
            {
                state.IsCommentaryLoading = false;
                state.CommentaryLoadCts = null;
                UpdateReaderTools();
            }

            cts.Dispose();
        }
    }

    private static bool IsActiveCommentaryLoad(
        ReaderTabState state,
        string requestedAnchorRef,
        CancellationTokenSource cts)
    {
        return !cts.IsCancellationRequested &&
            ReferenceEquals(state.CommentaryLoadCts, cts) &&
            string.Equals(state.SelectedCommentaryRef, requestedAnchorRef, StringComparison.Ordinal);
    }

    private static string BuildSefariaAnchorRef(
        ReaderTabState state,
        ReaderDisplayRow row,
        bool preferTranslation)
    {
        var unit = preferTranslation
            ? row.Translation ?? row.Primary
            : row.Primary ?? row.Translation;
        if (unit is null)
        {
            return string.Empty;
        }

        var reference = NormalizeReaderReferenceForSefaria(unit.Reference);
        return string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : $"{state.Primary.Title} {reference}";
    }

    private static string NormalizeReaderReferenceForSefaria(string reference)
    {
        reference = reference.Trim();
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        return reference.Replace('.', ':');
    }

    private void ApplyReaderTitle(TextBlock titleBlock, string? englishTitle, string? hebrewTitle)
    {
        titleBlock.Text = null;
        var inlines = titleBlock.Inlines ?? new InlineCollection();
        inlines.Clear();

        var english = string.IsNullOrWhiteSpace(englishTitle) ? "Untitled" : englishTitle;
        var hasHebrew = !string.IsNullOrWhiteSpace(hebrewTitle);

        switch (_settings.InstalledBookTitleDisplay)
        {
            case InstalledBookTitleDisplay.Hebrew:
                AddReaderTitleRun(inlines, hasHebrew ? hebrewTitle! : english, hasHebrew);
                break;

            case InstalledBookTitleDisplay.English:
                AddReaderTitleRun(inlines, english, false);
                break;

            default:
                if (hasHebrew)
                {
                    AddReaderTitleRun(inlines, hebrewTitle!, true);
                    AddReaderTitleRun(inlines, " / ", false);
                }

                AddReaderTitleRun(inlines, english, false);
                break;
        }

        titleBlock.Inlines = inlines;
    }

    private void AddReaderTitleRun(InlineCollection inlines, string text, bool isHebrew)
    {
        inlines.Add(new Run(text)
        {
            FontFamily = isHebrew
                ? new FontFamily(GetSelectedHebrewFontFamily())
                : new FontFamily(GetSelectedEnglishFontFamily())
        });
    }

    private static List<ReaderNavigationChapter> BuildReaderNavigationChapters(List<ReaderNavigationItem> navigationItems)
    {
        var chapters = new List<ReaderNavigationChapter>();
        foreach (var item in navigationItems)
        {
            var current = chapters.LastOrDefault();
            if (current is null || !string.Equals(current.Title, item.ChapterTitle, StringComparison.Ordinal))
            {
                current = new ReaderNavigationChapter(item.ChapterTitle);
                chapters.Add(current);
            }

            current.Items.Add(item);
        }

        return chapters;
    }

    private List<ReaderNavigationPage> BuildReaderNavigationPages(ReaderTabState state)
    {
        var navigationPages = _sefariaLibrary.ReadInstalledBookNavigationPages(state.Primary);
        if (navigationPages.Count > 0)
        {
            return navigationPages;
        }

        if (state.SelectedTranslation is not null)
        {
            navigationPages = _sefariaLibrary.ReadInstalledBookNavigationPages(state.SelectedTranslation);
            if (navigationPages.Count > 0)
            {
                return navigationPages;
            }
        }

        return new List<ReaderNavigationPage>();
    }

    private Control CreateReaderDisplayRow(ReaderTabState state, ReaderDisplayRow row)
    {
        return row.IsChapterHeading
            ? CreateChapterHeading(row.ChapterHeading)
            : CreateReaderUnit(state, row.Primary, row.Translation);
    }

    private static Control CreateChapterHeading(string heading)
    {
        return new TextBlock
        {
            Text = heading,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#344054")),
            Margin = new Thickness(0, 18, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private string FormatChapterHeading(string chapter, string chapterTitle)
    {
        if (!string.IsNullOrWhiteSpace(chapterTitle))
        {
            return $"{chapterTitle} - {chapter}";
        }

        if (!int.TryParse(chapter, out var number))
        {
            return chapter;
        }

        var english = $"Chapter {number}";
        var hebrew = $"פרק {ToHebrewNumber(number)}";
        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => hebrew,
            InstalledBookTitleDisplay.English => english,
            _ => $"{hebrew} / {english}"
        };
    }

    private string FormatNavigationChapterLabel(string chapter)
    {
        if (!int.TryParse(chapter, out var number))
        {
            return chapter;
        }

        return _settings.InstalledBookTitleDisplay == InstalledBookTitleDisplay.Hebrew
            ? ToHebrewNumber(number)
            : number.ToString();
    }

    private string FormatChapterTitle(ReaderTextUnit? primary, ReaderTextUnit? translation)
    {
        var english = FirstNonEmpty(primary?.ChapterTitle, translation?.ChapterTitle);
        var hebrew = FirstNonEmpty(primary?.HebrewChapterTitle, translation?.HebrewChapterTitle);
        return FormatChapterTitleParts(english, hebrew);
    }

    private string FormatChapterTitle(ReaderNavigationPage page)
    {
        return FormatChapterTitleParts(page.ChapterTitle, page.HebrewChapterTitle);
    }

    private string FormatChapterTitleParts(string? english, string? hebrew)
    {
        english = string.IsNullOrWhiteSpace(english) ? string.Empty : english;
        hebrew = string.IsNullOrWhiteSpace(hebrew) ? string.Empty : hebrew;
        if (string.IsNullOrWhiteSpace(english) && string.IsNullOrWhiteSpace(hebrew))
        {
            return string.Empty;
        }

        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => string.IsNullOrWhiteSpace(hebrew) ? english : hebrew,
            InstalledBookTitleDisplay.English => string.IsNullOrWhiteSpace(english) ? hebrew : english,
            _ => string.IsNullOrWhiteSpace(hebrew)
                ? english
                : string.IsNullOrWhiteSpace(english)
                    ? hebrew
                    : $"{hebrew} / {english}"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string FormatSegmentLabel(ReaderTextUnit? unit, bool useHebrew)
    {
        var value = GetReferencePart(unit?.Reference ?? string.Empty, -1);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return useHebrew && int.TryParse(value, out var number)
            ? ToHebrewNumber(number)
            : value;
    }

    private static string GetReferencePart(string reference, int index)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var resolvedIndex = index < 0 ? parts.Length + index : index;
        return resolvedIndex >= 0 && resolvedIndex < parts.Length ? parts[resolvedIndex] : string.Empty;
    }

    private static string ToHebrewNumber(int number)
    {
        if (number <= 0)
        {
            return number.ToString();
        }

        var builder = new StringBuilder();
        var hundreds = new[] { "", "ק", "ר", "ש", "ת" };
        while (number >= 400)
        {
            builder.Append('ת');
            number -= 400;
        }

        if (number >= 100)
        {
            var count = Math.Min(4, number / 100);
            builder.Append(hundreds[count]);
            number -= count * 100;
        }

        if (number == 15)
        {
            builder.Append("טו");
            return builder.ToString();
        }

        if (number == 16)
        {
            builder.Append("טז");
            return builder.ToString();
        }

        var tens = new[] { "", "י", "כ", "ל", "מ", "נ", "ס", "ע", "פ", "צ" };
        var ones = new[] { "", "א", "ב", "ג", "ד", "ה", "ו", "ז", "ח", "ט" };
        if (number >= 10)
        {
            builder.Append(tens[number / 10]);
            number %= 10;
        }

        builder.Append(ones[number]);
        return builder.ToString();
    }

    private Control CreateReaderUnit(ReaderTabState state, ReaderTextUnit? primary, ReaderTextUnit? translation)
    {
        var primaryBlock = CreateReaderTextBlock(
            primary?.Text ?? string.Empty,
            SefariaLibraryService.IsHebrew(state.Primary),
            state.HebrewMarksMode);
        var primaryLabel = CreateSegmentLabel(
            FormatSegmentLabel(primary, SefariaLibraryService.IsHebrew(state.Primary)),
            HorizontalAlignment.Left);
        if (translation is null || state.DisplayMode == ReaderDisplayMode.PrimaryOnly)
        {
            if (!SefariaLibraryService.IsHebrew(state.Primary))
            {
                return CreateLabeledTextRow(primaryBlock, null, primaryLabel);
            }

            return CreateLabeledTextRow(primaryBlock, primaryLabel, null);
        }

        var translationBlock = CreateReaderTextBlock(
            translation.Text,
            false,
            state.HebrewMarksMode);
        var translationLabel = CreateSegmentLabel(FormatSegmentLabel(translation, false), HorizontalAlignment.Right);
        if (state.DisplayMode == ReaderDisplayMode.SideBySide)
        {
            primaryBlock = new Border
            {
                Padding = new Thickness(0, 0, 10, 0),
                Child = primaryBlock
            };
            translationBlock = new Border
            {
                Padding = new Thickness(10, 0, 0, 0),
                Child = translationBlock
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("32,*,*,32"),
                ColumnSpacing = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    primaryLabel,
                    primaryBlock,
                    translationBlock,
                    translationLabel
                }
            };
            Grid.SetColumn(primaryBlock, 1);
            Grid.SetColumn(translationBlock, 2);
            Grid.SetColumn(translationLabel, 3);
            return grid;
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateLabeledTextRow(primaryBlock, primaryLabel, null),
                CreateLabeledTextRow(translationBlock, null, translationLabel)
            }
        };
    }

    private static TextBlock CreateSegmentLabel(string label, HorizontalAlignment alignment)
    {
        return new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#98A2B3")),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 11, 0, 0)
        };
    }

    private static Control CreateLabeledTextRow(Control text, Control? leftLabel, Control? rightLabel)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("32,*,32"),
            ColumnSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                text
            }
        };

        Grid.SetColumn(text, 1);
        if (leftLabel is not null)
        {
            grid.Children.Add(leftLabel);
            Grid.SetColumn(leftLabel, 0);
        }

        if (rightLabel is not null)
        {
            grid.Children.Add(rightLabel);
            Grid.SetColumn(rightLabel, 2);
        }

        return grid;
    }

    private Control CreateReaderTextBlock(
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode)
    {
        var fontSize = isHebrew ? GetSelectedHebrewFontSize() : GetSelectedEnglishFontSize();
        return new ReaderTextView
        {
            SourceText = text,
            IsHebrew = isHebrew,
            HebrewMarksMode = hebrewMarksMode,
            FontSize = fontSize,
            FontFamily = isHebrew
                ? new FontFamily(GetSelectedHebrewFontFamily())
                : new FontFamily(GetSelectedEnglishFontFamily()),
            LineHeight = fontSize + 19,
            Padding = isHebrew
                ? new Thickness(16, 8, 24, 8)
                : new Thickness(16, 8, 16, 8)
        };
    }

    private void CloseReaderTabsForBook(SefariaBookNode book)
    {
        if (_tabs is null)
        {
            return;
        }

        var title = book.Title;
        var matchingTabs = _openReaderTabs
            .Where(pair => string.Equals(pair.Value.WorkTitle, title, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var tab in matchingTabs)
        {
            CloseTab(tab);
        }
    }

    private void RefreshOpenReaderTabForBook(SefariaBookNode book)
    {
        var readerTab = _openReaderTabs.FirstOrDefault(pair => string.Equals(pair.Value.WorkTitle, book.Title, StringComparison.Ordinal));
        if (readerTab.Key is null)
        {
            return;
        }

        var state = readerTab.Value;
        var installedVersions = _sefariaLibrary.GetInstalledVersionsForTitle(book.Title);
        state.Versions = installedVersions;
        state.HebrewTexts = installedVersions.Where(SefariaLibraryService.IsHebrew).ToList();
        state.Translations = installedVersions.Where(version => !SefariaLibraryService.IsHebrew(version)).ToList();

        var downloadedHebrewText = book.SelectedVersion is null
            ? null
            : state.HebrewTexts.FirstOrDefault(version =>
                string.Equals(version.Title, book.Title, StringComparison.Ordinal) &&
                string.Equals(version.LanguageCode, book.SelectedVersion.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.VersionTitle, book.SelectedVersion.VersionTitle, StringComparison.Ordinal));

        state.Primary = state.HebrewTexts.FirstOrDefault(version => string.Equals(version.Key, state.Primary.Key, StringComparison.Ordinal))
            ?? GetSavedHebrewText(book.Title, state.HebrewTexts)
            ?? downloadedHebrewText
            ?? state.HebrewTexts.FirstOrDefault()
            ?? installedVersions.FirstOrDefault(version => string.Equals(version.Key, state.Primary.Key, StringComparison.Ordinal))
            ?? installedVersions.FirstOrDefault()
            ?? state.Primary;
        NormalizeHebrewMarksMode(state);
        SaveSelectedHebrewText(state);

        var downloadedVersion = book.SelectedVersion is null
            ? null
            : state.Translations.FirstOrDefault(version =>
                string.Equals(version.Title, book.Title, StringComparison.Ordinal) &&
                string.Equals(version.LanguageCode, book.SelectedVersion.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.VersionTitle, book.SelectedVersion.VersionTitle, StringComparison.Ordinal));

        state.SelectedTranslation = downloadedVersion
            ?? GetSavedTranslation(book.Title, state.Translations)
            ?? state.SelectedTranslation;

        if (state.SelectedTranslation is not null && state.DisplayMode == ReaderDisplayMode.PrimaryOnly)
        {
            state.DisplayMode = GetSavedDisplayMode(book.Title, true);
        }

        RenderReaderContent(state);

        if (ReferenceEquals(_centerTabs?.SelectedItem, readerTab.Key))
        {
            UpdateReaderTools();
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

    private void UpdateTabHeaderStates()
    {
        if (_tabs is null || _centerTabs is null)
        {
            return;
        }

        foreach (var tab in _tabs)
        {
            if (tab.Header is not Border header || header.Child is not Grid headerLayout)
            {
                continue;
            }

            var selected = ReferenceEquals(tab, _centerTabs.SelectedItem);
            header.Background = selected
                ? Brushes.White
                : Brushes.Transparent;

            if (headerLayout.Children.Count > 3 && headerLayout.Children[3] is Border separator)
            {
                separator.IsVisible = !selected;
            }
        }
    }

    private void UpdateReaderTools()
    {
        if (_rightPanelBody is null || _rightPanelTitle is null)
        {
            return;
        }

        _rightPanelBody.Children.Clear();
        _rightPanelTitle.Text = "Reader Tools";

        if (_centerTabs?.SelectedItem is not TabItem selectedTab)
        {
            _rightPanelBody.Children.Add(new TextBlock
            {
                Text = "Open a text to see reader tools.",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (!_openReaderTabs.TryGetValue(selectedTab, out var readerState))
        {
            _rightPanelBody.Children.Add(new TextBlock
            {
                Text = "Open a text to see reader tools.",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        _rightPanelBody.Children.Add(new TextBlock
        {
            Text = FormatTitle(readerState.Primary.Title, readerState.Primary.HebrewTitle),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            "Navigation",
            CreateReaderNavigationTools(readerState),
            readerState.IsNavigationExpanded,
            value =>
            {
                readerState.IsNavigationExpanded = value;
                SaveLayoutState();
            }));
        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            "Display",
            CreateReaderDisplayTools(readerState),
            readerState.IsDisplayExpanded,
            value =>
            {
                readerState.IsDisplayExpanded = value;
                SaveLayoutState();
            }));
        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            CreateCommentaryGroupHeader(readerState),
            CreateReaderCommentaryTools(readerState),
            readerState.IsCommentariesExpanded,
            value =>
            {
                readerState.IsCommentariesExpanded = value;
                SaveLayoutState();
            }));

        var textTools = new StackPanel { Spacing = 6 };
        if (readerState.HebrewTexts.Count > 1)
        {
            textTools.Children.Add(new TextBlock
            {
                Text = "Hebrew Texts",
                FontWeight = FontWeight.SemiBold
            });

            foreach (var hebrewText in readerState.HebrewTexts)
            {
                var option = new RadioButton
                {
                    Content = hebrewText.VersionTitle,
                    GroupName = $"hebrew-texts-{readerState.WorkTitle}",
                    IsChecked = string.Equals(readerState.Primary.Key, hebrewText.Key, StringComparison.Ordinal),
                    Tag = hebrewText
                };

                option.IsCheckedChanged += (_, _) =>
                {
                    if (option.IsChecked != true || option.Tag is not InstalledSefariaBook selectedHebrewText)
                    {
                        return;
                    }

                    readerState.Primary = selectedHebrewText;
                    NormalizeHebrewMarksMode(readerState);
                    SaveSelectedHebrewText(readerState);
                    RenderReaderContent(readerState);
                    UpdateReaderTools();
                    SaveLayoutState();
                };

                textTools.Children.Add(option);
            }
        }

        if (readerState.Translations.Count == 0)
        {
            textTools.Children.Add(new TextBlock
            {
                Text = "No downloaded translations are available for this text.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            if (textTools.Children.Count > 0)
            {
                textTools.Children.Add(new Border { Height = 6 });
            }

            textTools.Children.Add(new TextBlock
            {
                Text = "Translations",
                FontWeight = FontWeight.SemiBold
            });

            foreach (var translation in readerState.Translations)
            {
                var option = new RadioButton
                {
                    Content = translation.VersionTitle,
                    GroupName = $"translations-{readerState.WorkTitle}",
                    IsChecked = readerState.SelectedTranslation is not null &&
                        string.Equals(readerState.SelectedTranslation.Key, translation.Key, StringComparison.Ordinal),
                    Tag = translation
                };

                option.IsCheckedChanged += (_, _) =>
                {
                    if (option.IsChecked != true || option.Tag is not InstalledSefariaBook selectedTranslation)
                    {
                        return;
                    }

                    readerState.SelectedTranslation = selectedTranslation;
                    SaveSelectedTranslation(readerState);
                    RenderReaderContent(readerState);
                    SaveLayoutState();
                };

                textTools.Children.Add(option);
            }
        }

        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            "Texts",
            textTools,
            readerState.IsTextsExpanded,
            value =>
            {
                readerState.IsTextsExpanded = value;
                SaveLayoutState();
            }));
    }

    private Control CreateCommentaryGroupHeader(ReaderTabState readerState)
    {
        var count = readerState.Commentaries.Count;
        var title = count > 0 ? $"Commentaries ({count})" : "Commentaries";
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (readerState.IsCommentaryContentOpen)
        {
            var backButton = new Button
            {
                Content = "←",
                MinWidth = 24,
                MinHeight = 22,
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            backButton.Click += (_, e) =>
            {
                readerState.IsCommentaryContentOpen = false;
                e.Handled = true;
                UpdateReaderTools();
                SaveLayoutState();
            };

            layout.Children.Add(backButton);
        }

        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(titleBlock);
        Grid.SetColumn(titleBlock, 1);

        var languageButton = new Button
        {
            Content = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew ? "א" : "A",
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        languageButton.Click += (_, e) =>
        {
            readerState.CommentaryLanguage = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
                ? CommentaryLanguage.English
                : CommentaryLanguage.Hebrew;
            e.Handled = true;
            UpdateReaderTools();
            SaveLayoutState();
        };

        layout.Children.Add(languageButton);
        Grid.SetColumn(languageButton, 2);
        return layout;
    }

    private Control CreateReaderCommentaryTools(ReaderTabState readerState)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8
        };

        if (readerState.SelectedReaderRow is null || string.IsNullOrWhiteSpace(readerState.SelectedCommentaryRef))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Select a paragraph to see commentaries.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = readerState.SelectedCommentaryRef,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
            TextWrapping = TextWrapping.Wrap
        });

        if (readerState.IsCommentaryLoading)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Loading commentaries...",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (!string.IsNullOrWhiteSpace(readerState.CommentaryError))
        {
            panel.Children.Add(new TextBlock
            {
                Text = readerState.CommentaryError,
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (readerState.Commentaries.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No commentaries for this paragraph.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        var groups = GetCommentarySourceGroups(readerState.Commentaries);
        panel.Children.Add(readerState.IsCommentaryContentOpen
            ? CreateCommentaryContentBox(readerState, groups)
            : CreateCommentarySourcePicker(readerState, groups));

        return panel;
    }

    private Control CreateCommentarySourcePicker(
        ReaderTabState readerState,
        List<CommentarySourceGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(readerState.SelectedCommentarySourceKey))
        {
            readerState.SelectedCommentarySourceKey = AllCommentariesSelectionKey;
        }

        var panel = new StackPanel
        {
            Spacing = 6
        };

        panel.Children.Add(CreateCommentarySourceRow(
            readerState,
            AllCommentariesSelectionKey,
            GetAllCommentariesLabel(readerState),
            readerState.Commentaries.Count,
            GetAllCommentariesDescription(readerState),
            enabled: true));

        foreach (var group in groups)
        {
            panel.Children.Add(CreateCommentarySourceRow(
                readerState,
                group.Key,
                GetCommentaryGroupDisplayTitle(readerState, group),
                group.Items.Count,
                GetCommentarySourceDescription(readerState, group),
                enabled: true,
                group: group));
        }

        if (!string.Equals(readerState.SelectedCommentarySourceKey, AllCommentariesSelectionKey, StringComparison.Ordinal) &&
            !groups.Any(group => string.Equals(group.Key, readerState.SelectedCommentarySourceKey, StringComparison.OrdinalIgnoreCase)))
        {
            panel.Children.Add(CreateCommentarySourceRow(
                readerState,
                readerState.SelectedCommentarySourceKey,
                GetSelectedCommentarySourceTitle(readerState),
                0,
                "No entries for the selected paragraph.",
                enabled: false));
        }

        return panel;
    }

    private Control CreateCommentarySourceRow(
        ReaderTabState readerState,
        string key,
        string title,
        int count,
        string description,
        bool enabled,
        CommentarySourceGroup? group = null)
    {
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        var isSelected = string.Equals(readerState.SelectedCommentarySourceKey, key, StringComparison.OrdinalIgnoreCase);
        var titleLine = new TextBlock
        {
            FontFamily = new FontFamily(useHebrew ? GetSelectedHebrewFontFamily() : GetSelectedEnglishFontFamily()),
            FontSize = Math.Max(13, GetSelectedUiFontSize()),
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = useHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        titleLine.Inlines?.Add(new Run(title)
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#101828"))
        });
        titleLine.Inlines?.Add(new Run($" ({count})")
        {
            Foreground = new SolidColorBrush(Color.Parse("#98A2B3"))
        });

        var badge = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = useHebrew ? "HE" : "EN",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.Parse("#667085"))
            }
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        header.Children.Add(titleLine);
        header.Children.Add(badge);
        Grid.SetColumn(badge, 1);

        var descriptionBlock = new TextBlock
        {
            Text = description,
            FontSize = Math.Max(11, GetSelectedUiFontSize() - 1),
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = useHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };

        var content = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                header,
                descriptionBlock
            }
        };

        var row = new Button
        {
            Content = content,
            Opacity = enabled ? 1 : 0.62,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = enabled
        };
        row.Classes.Add("commentary-source-row");
        if (isSelected)
        {
            row.Classes.Add("selected");
        }

        if (!enabled)
        {
            return row;
        }

        row.Click += (_, e) =>
        {
            readerState.SelectedCommentarySourceKey = key;
            readerState.IsCommentaryContentOpen = true;
            if (group is not null)
            {
                readerState.SelectedCommentarySourceTitleEnglish = group.EnglishTitle;
                readerState.SelectedCommentarySourceTitleHebrew = group.HebrewTitle;
            }
            else if (string.Equals(key, AllCommentariesSelectionKey, StringComparison.Ordinal))
            {
                readerState.SelectedCommentarySourceTitleEnglish = string.Empty;
                readerState.SelectedCommentarySourceTitleHebrew = string.Empty;
            }

            UpdateReaderTools();
            SaveLayoutState();
            e.Handled = true;
        };

        return row;
    }

    private Control CreateCommentaryContentBox(
        ReaderTabState readerState,
        List<CommentarySourceGroup> groups)
    {
        var content = new StackPanel
        {
            Spacing = 12
        };

        if (string.Equals(readerState.SelectedCommentarySourceKey, AllCommentariesSelectionKey, StringComparison.Ordinal))
        {
            foreach (var group in groups)
            {
                AddCommentarySourceContent(readerState, content, group, includeHeader: true);
            }
        }
        else
        {
            var selectedGroup = groups.FirstOrDefault(group =>
                string.Equals(group.Key, readerState.SelectedCommentarySourceKey, StringComparison.OrdinalIgnoreCase));
            if (selectedGroup is null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "No selected commentary for this paragraph.",
                    Foreground = new SolidColorBrush(Color.Parse("#667085")),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                AddCommentarySourceContent(readerState, content, selectedGroup, includeHeader: true);
            }
        }

        return new Border
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            Background = new SolidColorBrush(Color.Parse("#FCFCFD")),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
    }

    private void AddCommentarySourceContent(
        ReaderTabState readerState,
        StackPanel content,
        CommentarySourceGroup group,
        bool includeHeader)
    {
        if (includeHeader)
        {
            content.Children.Add(new TextBlock
            {
                Text = GetCommentaryGroupDisplayTitle(readerState, group),
                FontWeight = FontWeight.SemiBold,
                FontFamily = new FontFamily(readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
                    ? GetSelectedHebrewFontFamily()
                    : GetSelectedEnglishFontFamily()),
                FontSize = GetCommentaryReaderFontSize(readerState),
                TextWrapping = TextWrapping.Wrap,
                FlowDirection = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight
            });
        }

        foreach (var commentary in group.Items)
        {
            content.Children.Add(CreateCommentaryItem(readerState, commentary));
        }
    }

    private Control CreateCommentaryItem(ReaderTabState readerState, SefariaCommentaryItem commentary)
    {
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        var text = useHebrew
            ? FirstNonEmpty(commentary.HebrewText, commentary.Text)
            : FirstNonEmpty(commentary.Text, commentary.HebrewText);

        var panel = new StackPanel
        {
            Spacing = 4
        };

        panel.Children.Add(new TextBlock
        {
            Text = commentary.Ref,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
            TextWrapping = TextWrapping.Wrap
        });

        if (string.IsNullOrWhiteSpace(text))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No text available in this language.",
                Foreground = new SolidColorBrush(Color.Parse("#98A2B3")),
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        panel.Children.Add(CreateReaderTextBlock(text, useHebrew && !string.IsNullOrWhiteSpace(commentary.HebrewText), readerState.HebrewMarksMode));
        return panel;
    }

    private double GetCommentaryReaderFontSize(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? GetSelectedHebrewFontSize()
            : GetSelectedEnglishFontSize();
    }

    private static List<CommentarySourceGroup> GetCommentarySourceGroups(List<SefariaCommentaryItem> commentaries)
    {
        return commentaries
            .GroupBy(GetCommentaryGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var first = items.First();
                var englishTitle = FirstNonEmpty(first.DisplayTitle, first.IndexTitle, group.Key, "Commentary");
                var hebrewTitle = FirstNonEmpty(first.HebrewDisplayTitle, englishTitle);
                return new CommentarySourceGroup(group.Key, englishTitle, hebrewTitle, items);
            })
            .OrderBy(group => group.EnglishTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetAllCommentariesLabel(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? "כל הקישורים למפרשים"
            : "All Commentaries";
    }

    private static string GetAllCommentariesDescription(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? "פירושים ודיונים סביב טקסטים תורניים, מימי הביניים ועד ימינו."
            : "Interpretations and discussions surrounding Jewish texts, ranging from early medieval to contemporary.";
    }

    private static string GetCommentaryGroupDisplayTitle(
        ReaderTabState readerState,
        CommentarySourceGroup group)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? FirstNonEmpty(group.HebrewTitle, group.EnglishTitle, "Commentary")
            : FirstNonEmpty(group.EnglishTitle, group.HebrewTitle, "Commentary");
    }

    private static string GetCommentarySourceDescription(
        ReaderTabState readerState,
        CommentarySourceGroup group)
    {
        var normalizedTitle = group.EnglishTitle.ToLowerInvariant();
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;

        if (normalizedTitle.Contains("rashi", StringComparison.Ordinal))
        {
            return useHebrew
                ? "הפירוש הנפוץ והמוכר ביותר לתורה, המבאר את פשוטי המקראות בתוספת הרחבות פרשניות."
                : "Most widely-read biblical commentary, explaining the simple meaning of the text with interpretive elaborations.";
        }

        if (normalizedTitle.Contains("ibn ezra", StringComparison.Ordinal))
        {
            return useHebrew
                ? "פירוש פשט המשלב ביאורים המבוססים על דקדוק ובלשנות."
                : "Commentary focused on the simple meaning of the text and incorporating grammar and linguistics.";
        }

        if (normalizedTitle.Contains("ramban", StringComparison.Ordinal) ||
            normalizedTitle.Contains("nachmanides", StringComparison.Ordinal))
        {
            return useHebrew
                ? "פירוש המשלב פרשנות מקראית עם הלכה, הגות ומיסטיקה."
                : "Commentary weaving together biblical interpretation with law, philosophy, and mysticism.";
        }

        if (normalizedTitle.Contains("sforno", StringComparison.Ordinal))
        {
            return useHebrew
                ? "פירוש על התורה מאת רבי עובדיה ספורנו, רב ורופא איטלקי."
                : "Commentary on the Torah by Rabbi Ovadiah Sforno, an Italian rabbi and physician.";
        }

        if (normalizedTitle.Contains("abarbanel", StringComparison.Ordinal) ||
            normalizedTitle.Contains("abravanel", StringComparison.Ordinal))
        {
            return useHebrew
                ? "פירוש על התורה והנביאים, הפותח פעמים רבות בשאלות על הטקסט."
                : "Commentary on the Torah and Prophets, often opening each section with questions on the biblical text.";
        }

        if (normalizedTitle.Contains("tosafot", StringComparison.Ordinal))
        {
            return useHebrew
                ? "פירוש תלמודי מבעלי התוספות, המשווה סוגיות ומיישב קושיות ברחבי הש״ס."
                : "Talmudic commentary comparing passages and resolving questions across the Talmud.";
        }

        return useHebrew
            ? "פירוש המקושר לפסקה שנבחרה."
            : "Commentary connected to the selected passage.";
    }

    private static string GetSelectedCommentarySourceTitle(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? FirstNonEmpty(readerState.SelectedCommentarySourceTitleHebrew, readerState.SelectedCommentarySourceTitleEnglish, "Commentary")
            : FirstNonEmpty(readerState.SelectedCommentarySourceTitleEnglish, readerState.SelectedCommentarySourceTitleHebrew, "Commentary");
    }

    private static string GetCommentaryGroupKey(SefariaCommentaryItem commentary)
    {
        return FirstNonEmpty(commentary.CollectiveTitleEnglish, commentary.IndexTitle, commentary.Ref);
    }

    private Control CreateReaderNavigationTools(ReaderTabState readerState)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 10
        };

        if (readerState.NavigationChapters.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No navigation markers are available.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (!readerState.HasTalmudNavigation)
        {
            return CreateReaderNavigationButtonGrid(readerState.NavigationItems);
        }

        foreach (var chapter in readerState.NavigationChapters)
        {
            var groupPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 6
            };

            if (!string.IsNullOrWhiteSpace(chapter.Title))
            {
                groupPanel.Children.Add(new TextBlock
                {
                    Text = chapter.Title,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            var pagesPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var item in chapter.Items)
            {
                pagesPanel.Children.Add(CreateReaderNavigationButton(readerState, item));
            }

            groupPanel.Children.Add(pagesPanel);
            panel.Children.Add(groupPanel);
        }

        return panel;
    }

    private Control CreateReaderNavigationButtonGrid(IEnumerable<ReaderNavigationItem> items)
    {
        var pagesPanel = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (_centerTabs?.SelectedItem is not TabItem selectedTab ||
            !_openReaderTabs.TryGetValue(selectedTab, out var readerState))
        {
            return pagesPanel;
        }

        foreach (var item in items)
        {
            pagesPanel.Children.Add(CreateReaderNavigationButton(readerState, item));
        }

        return pagesPanel;
    }

    private Button CreateReaderNavigationButton(ReaderTabState readerState, ReaderNavigationItem item)
    {
        var button = new Button
        {
            Content = item.Label,
            Margin = new Thickness(0, 0, 6, 6),
            Tag = item,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Classes.Add("reader-nav-button");

        button.Click += (_, e) =>
        {
            ScrollReaderRowToTop(readerState, item.Row);
            e.Handled = true;
        };

        return button;
    }

    private static void ScrollReaderRowToTop(ReaderTabState readerState, ReaderDisplayRow row)
    {
        if (readerState.ReaderList is null)
        {
            return;
        }

        var readerList = readerState.ReaderList;
        readerList.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            if (readerList.Scroll is null ||
                readerList.ContainerFromItem(row) is not Control container ||
                container.TranslatePoint(new Point(0, 0), readerList) is not { } point)
            {
                return;
            }

            var nextOffset = Math.Max(0, readerList.Scroll.Offset.Y + point.Y);
            readerList.Scroll.Offset = new Vector(readerList.Scroll.Offset.X, nextOffset);
        });
    }

    private static Expander CreateReaderToolsGroup(
        object header,
        Control content,
        bool isExpanded,
        Action<bool> setExpanded)
    {
        var expander = new Expander
        {
            Header = header,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
            Content = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 6, 0, 4),
                Child = content
            }
        };

        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
            {
                setExpanded(expander.IsExpanded);
            }
        };

        return expander;
    }

    private Control CreateReaderDisplayTools(ReaderTabState readerState)
    {
        NormalizeHebrewMarksMode(readerState);

        var hebrewMarksOptions = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Hebrew",
                    FontWeight = FontWeight.SemiBold
                },
                CreateHebrewMarksOption(readerState, "Text only", HebrewMarksMode.TextOnly),
                CreateHebrewMarksOption(readerState, "Text + nikkud", HebrewMarksMode.Nikkud)
            }
        };

        if (SupportsCantillation(readerState.Primary))
        {
            hebrewMarksOptions.Children.Add(
                CreateHebrewMarksOption(readerState, "Text + nikkud + te'amim", HebrewMarksMode.NikkudAndCantillation));
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        CreateReaderDisplayModeButton(readerState, "Hebrew", ReaderDisplayMode.PrimaryOnly),
                        CreateReaderDisplayModeButton(readerState, "Below", ReaderDisplayMode.TranslationBelow),
                        CreateReaderDisplayModeButton(readerState, "Side by side", ReaderDisplayMode.SideBySide)
                    }
                },
                hebrewMarksOptions
            }
        };
    }

    private Button CreateReaderDisplayModeButton(ReaderTabState state, string label, ReaderDisplayMode mode)
    {
        var isSelected = state.DisplayMode == mode;
        var button = new Button
        {
            Content = label,
            MinHeight = 24,
            Padding = new Thickness(8, 2),
            Background = isSelected ? new SolidColorBrush(Color.Parse("#D0E7FF")) : Brushes.Transparent,
            BorderBrush = isSelected ? new SolidColorBrush(Color.Parse("#7AB7F0")) : new SolidColorBrush(Color.Parse("#D0D5DD")),
            Tag = mode
        };

        button.Click += (_, _) =>
        {
            state.DisplayMode = mode;
            _settings.ReaderDisplayModesByBook[state.WorkTitle] = mode;
            _settingsService.Save(_settings);
            RenderReaderContent(state);
            UpdateReaderTools();
            SaveLayoutState();
        };

        return button;
    }

    private RadioButton CreateHebrewMarksOption(ReaderTabState state, string label, HebrewMarksMode mode)
    {
        var option = new RadioButton
        {
            Content = label,
            GroupName = $"hebrew-marks-{state.WorkTitle}",
            IsChecked = state.HebrewMarksMode == mode,
            Tag = mode
        };

        option.IsCheckedChanged += (_, _) =>
        {
            if (option.IsChecked != true || option.Tag is not HebrewMarksMode selectedMode)
            {
                return;
            }

            state.HebrewMarksMode = selectedMode;
            RenderReaderContent(state);
            UpdateReaderTools();
            SaveLayoutState();
        };

        return option;
    }

    private void SaveSelectedTranslation(ReaderTabState state)
    {
        if (state.SelectedTranslation is null)
        {
            _settings.SelectedTranslationsByBook.Remove(state.WorkTitle);
        }
        else
        {
            _settings.SelectedTranslationsByBook[state.WorkTitle] = state.SelectedTranslation.Key;
        }

        _settingsService.Save(_settings);
    }

    private void SaveSelectedHebrewText(ReaderTabState state)
    {
        if (SefariaLibraryService.IsHebrew(state.Primary))
        {
            _settings.SelectedHebrewTextsByBook[state.WorkTitle] = state.Primary.Key;
        }
        else
        {
            _settings.SelectedHebrewTextsByBook.Remove(state.WorkTitle);
        }

        _settingsService.Save(_settings);
    }

    private void CloseTab(TabItem tab)
    {
        if (_tabs is null)
        {
            return;
        }

        SaveReaderTabPosition(tab);
        _openReaderTabs.Remove(tab);
        _tabs.Remove(tab);
        if (_centerTabs is not null)
        {
            _centerTabs.SelectedIndex = _tabs.Count == 0 ? -1 : Math.Max(0, _tabs.Count - 1);
        }
        UpdateTabHeaderStates();
    }

    private void SaveReaderTabPosition(TabItem tab)
    {
        if (!_openReaderTabs.TryGetValue(tab, out var readerTab))
        {
            return;
        }

        if (readerTab.ReaderList?.Scroll is not null)
        {
            _sefariaLibrary.SaveReadingPosition(readerTab.Primary, readerTab.ReaderList.Scroll.Offset.Y);
        }
    }

    private void ApplyLeftPanelState(bool collapsed, double expandedWidth)
    {
        if (_leftColumn is null || _leftSplitter is null || _leftPanelBody is null || _leftPanelTitle is null)
        {
            return;
        }

        _leftCollapsed = collapsed;
        _leftExpandedWidth = Math.Max(CollapsedPanelWidth, expandedWidth);

        _leftColumn.MinWidth = CollapsedPanelWidth;
        _leftColumn.Width = new GridLength(_leftCollapsed ? CollapsedPanelWidth : _leftExpandedWidth, GridUnitType.Pixel);
        _leftSplitter.IsVisible = !_leftCollapsed;

        _leftPanelTitle.IsVisible = !_leftCollapsed;
        _leftPanelBody.IsVisible = !_leftCollapsed;
    }

    private void ApplyRightPanelState(bool collapsed, double expandedWidth)
    {
        if (_rightColumn is null || _rightSplitter is null || _rightPanelBody is null || _rightPanelTitle is null)
        {
            return;
        }

        _rightCollapsed = collapsed;
        _rightExpandedWidth = Math.Max(CollapsedPanelWidth, expandedWidth);

        _rightColumn.MinWidth = CollapsedPanelWidth;
        _rightColumn.Width = new GridLength(_rightCollapsed ? CollapsedPanelWidth : _rightExpandedWidth, GridUnitType.Pixel);
        _rightSplitter.IsVisible = !_rightCollapsed;

        _rightPanelTitle.IsVisible = !_rightCollapsed;
        _rightPanelBody.IsVisible = !_rightCollapsed;
    }

    private void ToggleLeftPanel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_leftColumn is null)
        {
            return;
        }

        if (!_leftCollapsed)
        {
            _leftExpandedWidth = Math.Max(CollapsedPanelWidth, _leftColumn.Width.Value);
        }

        ApplyLeftPanelState(!_leftCollapsed, _leftExpandedWidth);
    }

    private void ToggleRightPanel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_rightColumn is null)
        {
            return;
        }

        if (!_rightCollapsed)
        {
            _rightExpandedWidth = Math.Max(CollapsedPanelWidth, _rightColumn.Width.Value);
        }

        ApplyRightPanelState(!_rightCollapsed, _rightExpandedWidth);
    }

    private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_leftPanelBody?.SelectedItem is not string selected || _tabs is null || _centerTabs is null)
        {
            return;
        }

        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Tag as string, selected, StringComparison.Ordinal));
        if (existing is not null)
        {
            _centerTabs.SelectedItem = existing;
            return;
        }

        var tab = CreateTab(selected);
        _tabs.Add(tab);
        _centerTabs.SelectedItem = tab;
    }

    private void SaveLayoutState()
    {
        if (_leftColumn is null || _rightColumn is null || _tabs is null || _centerTabs is null)
        {
            return;
        }

        foreach (var tab in _openReaderTabs.Keys.ToList())
        {
            SaveReaderTabPosition(tab);
        }

        if (!_leftCollapsed)
        {
            _leftExpandedWidth = Math.Max(CollapsedPanelWidth, _leftColumn.Width.Value);
        }

        if (!_rightCollapsed)
        {
            _rightExpandedWidth = Math.Max(CollapsedPanelWidth, _rightColumn.Width.Value);
        }

        var state = new LayoutState
        {
            LeftCollapsed = _leftCollapsed,
            RightCollapsed = _rightCollapsed,
            LeftExpandedWidth = _leftExpandedWidth,
            RightExpandedWidth = _rightExpandedWidth,
            Tabs = _tabs.Select(CreateSavedTabState).ToList(),
            SelectedTabIndex = _tabs.Count == 0 ? -1 : Math.Clamp(_centerTabs.SelectedIndex, 0, _tabs.Count - 1)
        };
        state.OpenTabs = state.Tabs
            .Where(tab => tab.Kind == SavedTabKind.Utility && !string.IsNullOrWhiteSpace(tab.Title))
            .Select(tab => tab.Title)
            .ToList();

        var folder = GetStateFolder();
        Directory.CreateDirectory(folder);
        var filePath = GetStateFilePath();
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    private static LayoutState? ReadState()
    {
        var filePath = GetStateFilePath();
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<LayoutState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStateFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Stndr");
    }

    private static string GetStateFilePath()
    {
        return Path.Combine(GetStateFolder(), "layout-state.json");
    }

    private SavedTabState CreateSavedTabState(TabItem tab)
    {
        if (_openReaderTabs.TryGetValue(tab, out var readerState))
        {
            return new SavedTabState
            {
                Kind = SavedTabKind.Reader,
                Title = tab.Tag as string ?? readerState.WorkTitle,
                WorkTitle = readerState.WorkTitle,
                PrimaryKey = readerState.Primary.Key,
                SelectedTranslationKey = readerState.SelectedTranslation?.Key,
                DisplayMode = readerState.DisplayMode,
                HebrewMarksMode = readerState.HebrewMarksMode,
                IsNavigationExpanded = readerState.IsNavigationExpanded,
                IsDisplayExpanded = readerState.IsDisplayExpanded,
                IsTextsExpanded = readerState.IsTextsExpanded,
                ScrollOffset = readerState.ReaderList?.Scroll?.Offset.Y ?? readerState.Primary.LastScrollOffset
            };
        }

        return new SavedTabState
        {
            Kind = SavedTabKind.Utility,
            Title = tab.Tag as string ?? "Tab"
        };
    }

    private sealed class LayoutState
    {
        public bool LeftCollapsed { get; set; }
        public bool RightCollapsed { get; set; }
        public double LeftExpandedWidth { get; set; } = DefaultExpandedPanelWidth;
        public double RightExpandedWidth { get; set; } = DefaultExpandedPanelWidth;
        public List<SavedTabState> Tabs { get; set; } = new();
        public List<string> OpenTabs { get; set; } = new();
        public int SelectedTabIndex { get; set; }
    }

    private enum SavedTabKind
    {
        Utility,
        Reader
    }

    private sealed class SavedTabState
    {
        public SavedTabKind Kind { get; set; } = SavedTabKind.Utility;
        public string Title { get; set; } = string.Empty;
        public string WorkTitle { get; set; } = string.Empty;
        public string? PrimaryKey { get; set; }
        public string? SelectedTranslationKey { get; set; }
        public ReaderDisplayMode DisplayMode { get; set; } = ReaderDisplayMode.PrimaryOnly;
        public HebrewMarksMode HebrewMarksMode { get; set; } = HebrewMarksMode.NikkudAndCantillation;
        public bool IsNavigationExpanded { get; set; } = true;
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsTextsExpanded { get; set; } = true;
        public double ScrollOffset { get; set; }
    }

    private sealed class ReaderTabState
    {
        public string WorkTitle { get; set; } = string.Empty;
        public InstalledSefariaBook Primary { get; set; } = new();
        public List<InstalledSefariaBook> Versions { get; set; } = new();
        public List<InstalledSefariaBook> HebrewTexts { get; set; } = new();
        public List<InstalledSefariaBook> Translations { get; set; } = new();
        public InstalledSefariaBook? SelectedTranslation { get; set; }
        public ReaderDisplayMode DisplayMode { get; set; }
        public HebrewMarksMode HebrewMarksMode { get; set; } = HebrewMarksMode.NikkudAndCantillation;
        public bool HasTalmudNavigation { get; set; }
        public List<ReaderNavigationItem> NavigationItems { get; set; } = new();
        public List<ReaderNavigationChapter> NavigationChapters { get; set; } = new();
        public bool IsNavigationExpanded { get; set; } = true;
        public bool IsDisplayExpanded { get; set; } = true;
        public bool IsCommentariesExpanded { get; set; } = true;
        public bool IsTextsExpanded { get; set; } = true;
        public ReaderDisplayRow? SelectedReaderRow { get; set; }
        public string SelectedCommentaryRef { get; set; } = string.Empty;
        public bool IsCommentaryContentOpen { get; set; }
        public string SelectedCommentarySourceKey { get; set; } = AllCommentariesSelectionKey;
        public string SelectedCommentarySourceTitleEnglish { get; set; } = string.Empty;
        public string SelectedCommentarySourceTitleHebrew { get; set; } = string.Empty;
        public List<SefariaCommentaryItem> Commentaries { get; set; } = new();
        public bool IsCommentaryLoading { get; set; }
        public string CommentaryError { get; set; } = string.Empty;
        public CommentaryLanguage CommentaryLanguage { get; set; } = CommentaryLanguage.English;
        public CancellationTokenSource? CommentaryLoadCts { get; set; }
        public ListBox? ReaderList { get; set; }
        public TextBlock? TitleBlock { get; set; }
        public TextBlock? VersionBlock { get; set; }
    }

    private sealed record ReaderDisplayRow(
        ReaderTextUnit? Primary,
        ReaderTextUnit? Translation,
        bool IsChapterHeading,
        string ChapterKey,
        string ChapterHeading);

    private sealed record ReaderNavigationItem(string Label, ReaderDisplayRow Row, string ChapterTitle);

    private sealed record CommentarySourceGroup(
        string Key,
        string EnglishTitle,
        string HebrewTitle,
        List<SefariaCommentaryItem> Items);

    private sealed class ReaderNavigationChapter
    {
        public ReaderNavigationChapter(string title)
        {
            Title = title;
        }

        public string Title { get; }
        public List<ReaderNavigationItem> Items { get; } = new();
    }

    private sealed record FontOption(string FamilyName)
    {
        public override string ToString()
        {
            return FamilyName;
        }
    }
}
