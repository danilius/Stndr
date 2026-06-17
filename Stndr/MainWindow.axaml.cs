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
        var centerTabs = this.FindControl<TabControl>("CenterTabs")
            ?? throw new InvalidOperationException("CenterTabs control was not found.");
        _centerTabs = centerTabs;

        _tabs = new ObservableCollection<TabItem>();
        centerTabs.ItemsSource = _tabs;
        centerTabs.SelectionChanged += (_, _) =>
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

}
