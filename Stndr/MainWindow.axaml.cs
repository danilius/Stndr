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
    private const double DefaultSingleLanguageColumnLetters = 80;
    private const double DefaultDualLanguageColumnLetters = 120;
    private const double MinReaderColumnLetters = 30;
    private const double MaxReaderColumnLetters = 220;
    private const double AverageReaderCharacterWidthFactor = 0.62;
    private const double ReaderSegmentLabelWidth = 28;
    private const string LibraryManagerTabTitle = "Library Manager";
    private const string SettingsTabTitle = "Settings";
    private const string AllCommentariesSelectionKey = "__all_commentaries__";
    private static readonly TimeSpan StartupContentLoadedTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupLayoutUpdatedTimeout = TimeSpan.FromMilliseconds(250);

    private ColumnDefinition? _leftColumn;
    private ColumnDefinition? _rightColumn;
    private GridSplitter? _leftSplitter;
    private GridSplitter? _rightSplitter;
    private TreeView? _leftPanelBody;
    private TextBlock? _leftPanelTitle;
    private TextBlock? _rightPanelTitle;
    private StackPanel? _rightPanelBody;
    private TabControl? _centerTabs;
    private Grid? _centerTabContentHost;
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
    private readonly Dictionary<TabItem, Control> _tabContents = new();
    private AppSettings _settings = new();

    private bool _leftCollapsed;
    private bool _rightCollapsed;
    private bool _isSefariaDownloading;
    private bool _hasLoadedLayoutState;
    private double _leftExpandedWidth = DefaultExpandedPanelWidth;
    private double _rightExpandedWidth = DefaultExpandedPanelWidth;
    private SefariaCategoryNode? _sefariaRoot;
    private SefariaBookNode? _selectedSefariaBook;
    private CancellationTokenSource? _sefariaDownloadCts;
    private List<FontOption>? _allFontOptions;
    private List<FontOption>? _hebrewFontOptions;

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
        _leftPanelBody = this.FindControl<TreeView>("LeftPanelBody");
        _leftPanelTitle = this.FindControl<TextBlock>("LeftPanelTitle");
        _rightPanelTitle = this.FindControl<TextBlock>("RightPanelTitle");
        _rightPanelBody = this.FindControl<StackPanel>("RightPanelBody");
        var centerTabs = this.FindControl<TabControl>("CenterTabs")
            ?? throw new InvalidOperationException("CenterTabs control was not found.");
        _centerTabs = centerTabs;
        _centerTabContentHost = this.FindControl<Grid>("CenterTabContentHost")
            ?? throw new InvalidOperationException("CenterTabContentHost control was not found.");

        _tabs = new ObservableCollection<TabItem>();
        centerTabs.ItemsSource = _tabs;
        centerTabs.SelectionChanged += (_, _) =>
        {
            UpdateSelectedTabContentVisibility();
            UpdateTabHeaderStates();
            UpdateReaderTools();
            RestoreSelectedReaderWebScrollAfterTabSwitch();
        };

        _settings = _settingsService.Load();
        ApplyUiFontSetting();
        InitializeNavigationItems();
        ApplyLeftPanelState(false, DefaultExpandedPanelWidth);
        ApplyRightPanelState(false, DefaultExpandedPanelWidth);
        EnsureDefaultTabs();
        RefreshInstalledBooksTree();
        UpdateTabHeaderStates();

        this.Opened += async (_, _) => await CompleteStartupAsync();
        this.Closing += (_, _) => SaveLayoutState();
        this.Closed += (_, _) => SaveLayoutState();
    }

    private async Task CompleteStartupAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadLayoutState();
                UpdateTabHeaderStates();
            }, DispatcherPriority.Background);

            await WaitForSelectedTabContentReadyAsync();
        }
        finally
        {
            _hasLoadedLayoutState = true;
            StartupCompleted?.Invoke(this, EventArgs.Empty);
        }
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
