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
    private const double DefaultLinkSplitWidth = 420;
    private const double DefaultReaderFontSize = 15;
    private const double MinReaderFontSize = 11;
    private const double MaxReaderFontSize = 28;
    private const double DefaultUiFontSize = 13;
    private const double MinUiFontSize = 10;
    private const double MaxUiFontSize = 22;
    private const double DefaultSingleLanguageColumnLetters = 80;
    private const double DefaultDualLanguageColumnLetters = 120;
    private const double MinReaderColumnLetters = 30;
    private const double MaxReaderColumnLetters = 220;
    private const double AverageReaderCharacterWidthFactor = 0.62;
    private const double ReaderSegmentLabelWidth = 28;
    private const string LibraryManagerTabTitle = "Library Manager";
    private const string SettingsTabTitle = "Settings";
    private const string AdvancedSearchTabTitle = "Advanced Search";
    private const string AllCommentariesSelectionKey = "__all_commentaries__";
    private static readonly TimeSpan StartupContentLoadedTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupLayoutUpdatedTimeout = TimeSpan.FromMilliseconds(250);

    private ColumnDefinition? _leftColumn;
    private ColumnDefinition? _rightColumn;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private ScrollViewer? _leftPanelBody;
    private TreeView? _installedBooksTree;
    private ListBox? _savedSearchesList;
    private TextBlock? _leftPanelTitle;
    private Button? _leftPanelSearchButton;
    private TextBox? _leftPanelSearchBox;
    private Border? _leftPanelSearchSuggestionsContainer;
    private ListBox? _leftPanelSearchSuggestions;
    private TextBlock? _dictionaryToolsWord;
    private TextBlock? _dictionaryToolsReference;
    private TextBlock? _dictionaryToolsPrimaryGloss;
    private TextBlock? _dictionaryToolsStatus;
    private bool _isDictionaryToolsExpanded = true;
    private TextBlock? _rightPanelTitle;
    private StackPanel? _rightPanelBody;
    private TabControl? _centerTabs;
    private Grid? _centerTabContentHost;
    private Canvas? _floatingOverlayCanvas;
    private Border? _dictionaryPopup;
    private Border? _dictionaryPopupDragHandle;
    private TextBlock? _dictionaryPopupWord;
    private TextBlock? _dictionaryPopupReference;
    private TextBlock? _dictionaryPopupStatus;
    private Button? _dictionaryPopupDockButton;
    private Button? _dictionaryPopupCloseButton;
    private TreeView? _libraryTree;
    private ScrollViewer? _libraryTreeScrollViewer;
    private TreeViewItem? _selectedLibraryTreeItem;
    private TextBlock? _libraryTitle;
    private TextBlock? _libraryHebrewTitle;
    private TextBlock? _libraryDescription;
    private TextBlock? _libraryStatus;
    private TextBlock? _libraryBookVersionLabel;
    private ComboBox? _libraryVersionBox;
    private ComboBox? _libraryTranslationVersionBox;
    private StackPanel? _libraryBookVersionPanel;
    private StackPanel? _libraryCategoryVersionPanel;
    private ComboBox? _libraryCategoryHebrewVersionBox;
    private ComboBox? _libraryCategoryEnglishVersionBox;
    private ProgressBar? _libraryProgress;
    private Button? _librarySingleHebrewActionButton;
    private Button? _librarySingleTranslationActionButton;
    private Button? _libraryCategoryHebrewActionButton;
    private Button? _libraryCategoryTranslationActionButton;
    private Button? _libraryCancelButton;
    private TextBox? _libraryManagerSearchBox;
    private Border? _librarySearchSuggestionsContainer;
    private ListBox? _librarySearchSuggestions;

    private ObservableCollection<TabItem>? _tabs;
    private readonly AppSettingsService _settingsService = new();
    private readonly SefariaLibraryService _sefariaLibrary;
    private readonly Dictionary<TabItem, ReaderTabState> _openReaderTabs = new();
    private readonly Dictionary<TabItem, Control> _tabContents = new();
    private AppSettings _settings = new();

    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private bool _isSefariaDownloading;
    private bool _suppressLibraryVersionChangeEvents;
    private bool _hasLoadedLayoutState;
    private double _leftExpandedWidth = DefaultExpandedPanelWidth;
    private double _rightExpandedWidth = DefaultExpandedPanelWidth;
    private SefariaCategoryNode? _sefariaRoot;
    private SefariaBookNode? _selectedSefariaBook;
    private SefariaBookNode? _libraryVersionBoxesBook;
    private SefariaCategoryNode? _selectedSefariaCategory;
    private int _librarySelectionVersion;
    private CancellationTokenSource? _sefariaDownloadCts;
    private CancellationTokenSource? _categoryInstallProgressCts;
    private CommentaryReorderDragState? _activeCommentaryReorder;
    private MainWindow.CategorySelectionProgress? _cachedCategoryProgress;
    private List<FontOption>? _allFontOptions;
    private List<FontOption>? _hebrewFontOptions;
    private readonly ObservableCollection<SavedAdvancedSearch> _savedAdvancedSearches = new();
    private bool _savedSearchTitlesHebrew;
    private bool _advancedSearchAutosave;
    private InstalledBookTitleDisplay _advancedSearchScopeTitleDisplay = InstalledBookTitleDisplay.Both;
    private readonly HashSet<string> _advancedSearchExpandedScopeKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDictionaryDocked;
    private string _dictionaryCurrentWord = string.Empty;
    private string _dictionaryCurrentReference = string.Empty;
    private string _dictionaryPrimaryGloss = string.Empty;
    private string _dictionaryStatusText = "Right-click a word in the reader and choose Dictionary.";
    private CancellationTokenSource _dictionaryLookupCts = new();
    private readonly Dictionary<string, IReadOnlyList<SefariaDictionaryEntry>> _dictionaryLookupCache = new(StringComparer.Ordinal);
    private double _dictionaryPopupLeft = 360;
    private double _dictionaryPopupTop = 140;
    private PixelPoint? _dictionaryAnchorScreenPoint;
    private bool _dictionaryPopupUserPositioned;

    public event EventHandler? StartupCompleted;

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
        _leftPanelBody = this.FindControl<ScrollViewer>("LeftPanelBody");
        _installedBooksTree = this.FindControl<TreeView>("InstalledBooksTree");
        _savedSearchesList = this.FindControl<ListBox>("SavedSearchesList");
        _leftPanelTitle = this.FindControl<TextBlock>("LeftPanelTitle");
        _leftPanelSearchButton = this.FindControl<Button>("LeftPanelSearchButton");
        _leftPanelSearchBox = this.FindControl<TextBox>("LeftPanelSearchBox");
        _leftPanelSearchSuggestionsContainer = this.FindControl<Border>("LeftPanelSearchSuggestionsContainer");
        _leftPanelSearchSuggestions = this.FindControl<ListBox>("LeftPanelSearchSuggestions");
        if (_leftPanelSearchSuggestions is not null)
        {
            _leftPanelSearchSuggestions.ItemTemplate = new FuncDataTemplate<object>((item, _) =>
                new TextBlock
                {
                    Text = item switch
                    {
                        InstalledSefariaBook b => b is null ? string.Empty : FormatTitle(b.Title, b.HebrewTitle),
                        InstalledSefariaCategory c => c is null ? string.Empty : FormatTitle(c.Title, c.HebrewTitle),
                        _ => item?.ToString() ?? string.Empty
                    },
                    Padding = new Thickness(6, 4)
                });
        }
        if (_leftPanelSearchBox is not null)
        {
            _leftPanelSearchBox.TextChanged += OnInstalledBooksSearchTextChanged;
            _leftPanelSearchBox.LostFocus += OnInstalledBooksSearchLostFocus;
        }
        if (_leftPanelSearchSuggestions is not null)
            _leftPanelSearchSuggestions.SelectionChanged += OnInstalledBooksSearchSuggestionSelected;
        _rightPanelTitle = this.FindControl<TextBlock>("RightPanelTitle");
        _rightPanelBody = this.FindControl<StackPanel>("RightPanelBody");
        var centerTabs = this.FindControl<TabControl>("CenterTabs")
            ?? throw new InvalidOperationException("CenterTabs control was not found.");
        _centerTabs = centerTabs;
        _centerTabContentHost = this.FindControl<Grid>("CenterTabContentHost")
            ?? throw new InvalidOperationException("CenterTabContentHost control was not found.");
        _floatingOverlayCanvas = this.FindControl<Canvas>("FloatingOverlayCanvas");
        _dictionaryPopup = this.FindControl<Border>("DictionaryPopup");
        _dictionaryPopupDragHandle = this.FindControl<Border>("DictionaryPopupDragHandle");
        _dictionaryPopupWord = this.FindControl<TextBlock>("DictionaryPopupWord");
        _dictionaryPopupReference = this.FindControl<TextBlock>("DictionaryPopupReference");
        _dictionaryPopupStatus = this.FindControl<TextBlock>("DictionaryPopupStatus");
        _dictionaryPopupDockButton = this.FindControl<Button>("DictionaryPopupDockButton");
        _dictionaryPopupCloseButton = this.FindControl<Button>("DictionaryPopupCloseButton");

        _tabs = new ObservableCollection<TabItem>();
        centerTabs.ItemsSource = _tabs;
        if (_savedSearchesList is not null)
        {
            _savedSearchesList.ItemsSource = _savedAdvancedSearches;
        }
        centerTabs.SelectionChanged += (_, _) =>
        {
            UpdateSelectedTabContentVisibility();
            UpdateTabHeaderStates();
            UpdateReaderTools();
            RestoreSelectedReaderWebScrollAfterTabSwitch();
        };

        var configuredDataFolder = _settingsService.GetConfiguredDataFolder();
        if (configuredDataFolder is not null)
        {
            _settings = _settingsService.Load(configuredDataFolder);
            _sefariaLibrary = new SefariaLibraryService(configuredDataFolder);
        }
        else
        {
            _settings = new AppSettings();
            _sefariaLibrary = new SefariaLibraryService(null);
        }
        ApplyUiFontSetting();
        InitializeNavigationItems();
        InitializeUpdateBanner();
        ApplyLeftPanelState(false, DefaultExpandedPanelWidth);
        ApplyRightPanelState(false, DefaultExpandedPanelWidth);
        EnsureDefaultTabs();
        UpdateTabHeaderStates();
        InitializeDictionaryUi();

        this.Opened += async (_, _) => await CompleteStartupAsync();
        this.Closing += (_, _) => SaveLayoutState();
        this.Closed += (_, _) => SaveLayoutState();
        this.SizeChanged += (_, _) => ConstrainDictionaryPopupPosition();
    }

    private async Task CompleteStartupAsync()
    {
        try
        {
            var refreshInstalledBooksTreeTask = RefreshInstalledBooksTreeAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadLayoutState();
                UpdateTabHeaderStates();
            }, DispatcherPriority.Background);

            await refreshInstalledBooksTreeTask;
            await WaitForSelectedTabContentReadyAsync();
        }
        finally
        {
            _hasLoadedLayoutState = true;
            StartUpdateChecks();
            StartupCompleted?.Invoke(this, EventArgs.Empty);
        }

        // Prompt for a Data folder only after startup has completed, so the (topmost) splash
        // window has been dismissed and the modal dialog is actually reachable. The Background
        // priority queues this after the splash-close post raised by StartupCompleted.
        if (!_sefariaLibrary.IsConfigured)
        {
            // Let the splash-close (posted by StartupCompleted at Background priority) run
            // first, so the modal dialog isn't hidden behind the topmost splash window.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await PromptForDataFolderAsync(isStartup: true);
        }
    }

    /// <summary>
    /// Prompts the user to choose a Data folder. When they confirm, the pointer is written,
    /// settings are loaded from that folder, and the library is (re)initialized. Returns true
    /// when a folder was chosen.
    /// </summary>
    private async Task<bool> PromptForDataFolderAsync(bool isStartup)
    {
        var suggested = _sefariaLibrary.IsConfigured && !string.IsNullOrWhiteSpace(_sefariaLibrary.StorageRootFolder)
            ? _sefariaLibrary.StorageRootFolder
            : AppSettingsService.SuggestedDefaultDataFolder;

        var chosen = await DataFolderDialog.ShowAsync(this, suggested);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return false;
        }

        ApplyDataFolder(chosen);
        return true;
    }

    /// <summary>
    /// Points the app at the given Data folder: persists the pointer, loads settings from it,
    /// reinitializes the library and refreshes dependent UI.
    /// </summary>
    private void ApplyDataFolder(string dataFolder)
    {
        _settingsService.SetConfiguredDataFolder(dataFolder);
        _settings = _settingsService.Load(dataFolder);
        _sefariaLibrary.SetStorageRootFolder(dataFolder);
        ApplyUiFontSetting();
        RefreshInstalledBooksTree();
        UpdateLibraryDetails();
        RefreshOpenReaderTabs();
        UpdateReaderTools();
        _ = LoadSefariaLibraryAsync();
    }

    private async Task WaitForSelectedTabContentReadyAsync()
    {
        var selectedContent = await Dispatcher.UIThread.InvokeAsync(
            GetSelectedTabContent,
            DispatcherPriority.Background);
        if (selectedContent is null)
        {
            return;
        }

        await WaitForControlLoadedAsync(selectedContent, StartupContentLoadedTimeout);
        await WaitForControlLayoutUpdatedAsync(selectedContent, StartupLayoutUpdatedTimeout);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private Control? GetSelectedTabContent()
    {
        return _centerTabs?.SelectedItem is TabItem selectedTab &&
            _tabContents.TryGetValue(selectedTab, out var content)
                ? content
                : null;
    }

    private static async Task WaitForControlLoadedAsync(Control control, TimeSpan timeout)
    {
        if (control.IsLoaded)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            completion.TrySetResult();
        }

        control.Loaded += OnLoaded;
        await Task.WhenAny(completion.Task, Task.Delay(timeout));
        control.Loaded -= OnLoaded;
    }

    private static async Task WaitForControlLayoutUpdatedAsync(Control control, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            completion.TrySetResult();
        }

        control.LayoutUpdated += OnLayoutUpdated;
        await Task.WhenAny(completion.Task, Task.Delay(timeout));
        control.LayoutUpdated -= OnLayoutUpdated;
    }

}
