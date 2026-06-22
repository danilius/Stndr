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
    private Control CreateSettingsView()
    {
        var hebrewOption = CreateTitleDisplayOption("Hebrew", InstalledBookTitleDisplay.Hebrew);
        var englishOption = CreateTitleDisplayOption("English", InstalledBookTitleDisplay.English);
        var bothOption = CreateTitleDisplayOption("Hebrew / English", InstalledBookTitleDisplay.Both);
        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(24),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
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
                        CreateDataStorageFolderSettingRow(),
                        new TextBlock
                        {
                            Text = "Title language",
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
                            "Hebrew display font",
                            GetHebrewFontOptions(),
                            GetSelectedHebrewDisplayFontFamily(),
                            GetSelectedHebrewDisplayFontSize(),
                            MinReaderFontSize,
                            MaxReaderFontSize,
                            family =>
                            {
                                _settings.HebrewDisplayFontFamily = family;
                                _settingsService.Save(_settings);
                                RefreshDisplayFlyouts();
                            },
                            size =>
                            {
                                _settings.HebrewDisplayFontSize = size;
                                _settingsService.Save(_settings);
                                RefreshDisplayFlyouts();
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
                            }),
                        CreateFontSizeSettingRow(
                            "Hebrew commentary font size",
                            GetSelectedHebrewCommentaryFontSize(),
                            MinReaderFontSize,
                            MaxReaderFontSize,
                            size =>
                            {
                                _settings.HebrewCommentaryFontSize = size;
                                _settingsService.Save(_settings);
                                RefreshOpenLinkSplitViews();
                                UpdateReaderTools();
                            }),
                        CreateFontSizeSettingRow(
                            "English commentary font size",
                            GetSelectedEnglishCommentaryFontSize(),
                            MinReaderFontSize,
                            MaxReaderFontSize,
                            size =>
                            {
                                _settings.EnglishCommentaryFontSize = size;
                                _settingsService.Save(_settings);
                                RefreshOpenLinkSplitViews();
                                UpdateReaderTools();
                            }),
                        CreateLetterCountSettingRow(
                            "Single-language reader width",
                            GetSingleLanguageReaderColumnLetters(),
                            value =>
                            {
                                _settings.SingleLanguageReaderColumnLetters = value;
                                _settingsService.Save(_settings);
                                RefreshOpenReaderTabs();
                            }),
                        CreateLetterCountSettingRow(
                            "Dual-language reader width",
                            GetDualLanguageReaderColumnLetters(),
                            value =>
                            {
                                _settings.DualLanguageReaderColumnLetters = value;
                                _settingsService.Save(_settings);
                                RefreshOpenReaderTabs();
                            })
                    }
                }
            }
        };
    }

    private Control CreateDataStorageFolderSettingRow()
    {
        var statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#475467")),
            TextWrapping = TextWrapping.Wrap
        };

        var pathText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        };

        void RefreshDisplay()
        {
            if (_sefariaLibrary.IsConfigured && !string.IsNullOrWhiteSpace(_sefariaLibrary.StorageRootFolder))
            {
                pathText.Text = _sefariaLibrary.StorageRootFolder;
                statusText.Text = "Contains a 'sources' folder, a 'database' folder and settings.json.";
            }
            else
            {
                pathText.Text = "No data folder set.";
                statusText.Text = "Choose a folder to start downloading and managing texts.";
            }
        }

        var chooseButton = new Button
        {
            Content = "Change folder…",
            MinWidth = 130
        };
        chooseButton.Click += async (_, _) =>
        {
            try
            {
                var chosen = await PromptForDataFolderAsync(isStartup: false);
                if (chosen)
                {
                    RefreshDisplay();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                statusText.Text = $"Could not use that folder: {ex.Message}";
            }
        };

        RefreshDisplay();

        return new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock
                {
                    Text = "Data storage folder",
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "The single folder where Stndr keeps everything: settings, downloaded Hebrew " +
                           "sources and translations, and its local databases. Delete its sub-folders to reset.",
                    Foreground = new SolidColorBrush(Color.Parse("#475467")),
                    TextWrapping = TextWrapping.Wrap
                },
                pathText,
                chooseButton,
                statusText
            }
        };
    }

    private Control CreateFontSizeSettingRow(
        string label,
        double currentSize,
        double minSize,
        double maxSize,
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
                CreateFontSizeSettingPicker(currentSize, minSize, maxSize, setSize)
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

    private static Control CreateLetterCountSettingRow(
        string label,
        double currentValue,
        Action<double> setValue)
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
                CreateLetterCountSettingPicker(currentValue, setValue)
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
            RefreshOpenReaderTabs();
            RefreshDisplayFlyouts();
            UpdateReaderTools();
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

    private static ComboBox CreateLetterCountSettingPicker(
        double currentValue,
        Action<double> setValue)
    {
        var values = BuildLetterCountOptions();
        var picker = new ComboBox
        {
            Width = 120,
            IsEditable = true,
            ItemsSource = values,
            SelectedItem = FormatLetterCount(currentValue),
            Text = FormatLetterCount(currentValue)
        };

        var appliedValue = Math.Clamp(currentValue, MinReaderColumnLetters, MaxReaderColumnLetters);

        void TryApplyValue()
        {
            var rawValue = picker.Text;
            if (string.IsNullOrWhiteSpace(rawValue) && picker.SelectedItem is string selected)
            {
                rawValue = selected;
            }

            if (!TryParseLetterCount(rawValue, out var parsedValue))
            {
                picker.Text = FormatLetterCount(currentValue);
                return;
            }

            var nextValue = Math.Clamp(parsedValue, MinReaderColumnLetters, MaxReaderColumnLetters);
            picker.Text = FormatLetterCount(nextValue);
            if (Math.Abs(nextValue - appliedValue) < 0.001)
            {
                return;
            }

            appliedValue = nextValue;
            setValue(nextValue);
        }

        picker.DropDownClosed += (_, _) => TryApplyValue();
        picker.LostFocus += (_, _) => TryApplyValue();
        picker.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TryApplyValue();
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

    private static List<string> BuildLetterCountOptions()
    {
        var values = new List<string>();
        for (var value = 40; value <= 200; value += 10)
        {
            values.Add(FormatLetterCount(value));
        }

        return values;
    }

    private static string FormatLetterCount(double value)
    {
        return $"{value:0} letters";
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

    private static bool TryParseLetterCount(string? value, out double count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith("letters", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^7].Trim();
        }
        else if (normalized.EndsWith("letter", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^6].Trim();
        }

        return double.TryParse(normalized, out count);
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

    private void RefreshDisplayFlyouts()
    {
        foreach (var state in _openReaderTabs.Values)
        {
            RefreshReaderDisplayFlyout(state);
        }
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

    private string GetSelectedHebrewDisplayFontFamily()
    {
        return ResolveInstalledFont(
            _settings.HebrewDisplayFontFamily,
            GetHebrewFontOptions(),
            GetSelectedHebrewFontFamily(),
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

    private double GetSelectedHebrewDisplayFontSize()
    {
        return NormalizeSettingSize(_settings.HebrewDisplayFontSize, 18, MinReaderFontSize, MaxReaderFontSize);
    }

    private double GetSelectedHebrewCommentaryFontSize()
    {
        return NormalizeSettingSize(_settings.HebrewCommentaryFontSize, GetSelectedHebrewFontSize(), MinReaderFontSize, MaxReaderFontSize);
    }

    private double GetSelectedEnglishCommentaryFontSize()
    {
        return NormalizeSettingSize(_settings.EnglishCommentaryFontSize, GetSelectedEnglishFontSize(), MinReaderFontSize, MaxReaderFontSize);
    }

    private double GetSingleLanguageReaderColumnLetters()
    {
        return NormalizeSettingSize(
            _settings.SingleLanguageReaderColumnLetters,
            DefaultSingleLanguageColumnLetters,
            MinReaderColumnLetters,
            MaxReaderColumnLetters);
    }

    private double GetDualLanguageReaderColumnLetters()
    {
        return NormalizeSettingSize(
            _settings.DualLanguageReaderColumnLetters,
            DefaultDualLanguageColumnLetters,
            MinReaderColumnLetters,
            MaxReaderColumnLetters);
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
}
