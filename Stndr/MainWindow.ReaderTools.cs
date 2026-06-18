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
    private const double SedraPickerMinimumColumnWidth = 180;
    private const double SedraPickerColumnGap = 6;

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

        readerState.PinnedCommentarySourceKeys = new HashSet<string>(
            _settings.PinnedCommentarySourceKeys,
            StringComparer.OrdinalIgnoreCase);

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
        if (HasTorahSedrot(readerState.Primary))
        {
            _rightPanelBody.Children.Add(CreateReaderToolsGroup(
                CreateSedrotGroupHeader(readerState),
                CreateReaderSedrotTools(readerState),
                readerState.IsSedrotExpanded,
                value =>
                {
                    readerState.IsSedrotExpanded = value;
                    SaveLayoutState();
                }));
        }

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
                    Dispatcher.UIThread.Post(UpdateReaderTools, DispatcherPriority.Background);
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
            "Versions",
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
                Content = "\u2190",
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
            Content = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew ? "\u05d0" : "A",
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

    private Control CreateSedrotGroupHeader(ReaderTabState readerState)
    {
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (readerState.IsSedraContentOpen)
        {
            var backButton = new Button
            {
                Content = "\u2190",
                MinWidth = 24,
                MinHeight = 22,
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            backButton.Click += (_, e) =>
            {
                readerState.IsSedraContentOpen = false;
                e.Handled = true;
                UpdateReaderTools();
                SaveLayoutState();
            };

            layout.Children.Add(backButton);
        }

        var titleBlock = new TextBlock
        {
            Text = FormatSedrotHeading(readerState),
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(titleBlock);
        Grid.SetColumn(titleBlock, 1);
        return layout;
    }

    private string FormatSedrotHeading(ReaderTabState readerState)
    {
        if (readerState.IsSedraContentOpen)
        {
            return _settings.InstalledBookTitleDisplay switch
            {
                InstalledBookTitleDisplay.Hebrew => "\u05e2\u05dc\u05d9\u05d5\u05ea",
                InstalledBookTitleDisplay.English => "Aliyot",
                _ => "\u05e2\u05dc\u05d9\u05d5\u05ea / Aliyot"
            };
        }

        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => "\u05e4\u05e8\u05e9\u05d9\u05d5\u05ea \u05d5\u05e2\u05dc\u05d9\u05d5\u05ea",
            InstalledBookTitleDisplay.English => "Sedrot & Aliyot",
            _ => "\u05e4\u05e8\u05e9\u05d9\u05d5\u05ea \u05d5\u05e2\u05dc\u05d9\u05d5\u05ea / Sedrot & Aliyot"
        };
    }

    private Control CreateReaderSedrotTools(ReaderTabState readerState)
    {
        var sedrot = GetTorahSedrot(readerState.Primary.Title);
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 6
        };

        if (sedrot.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No sedrot are available for this book.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (!readerState.IsSedraContentOpen)
        {
            var pickerGrid = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var sedra in sedrot)
            {
                pickerGrid.Children.Add(CreateSedraPickerRow(readerState, sedra));
            }

            pickerGrid.SizeChanged += (_, _) => ApplySedraPickerGridWidths(pickerGrid);
            Dispatcher.UIThread.Post(() => ApplySedraPickerGridWidths(pickerGrid), DispatcherPriority.Loaded);
            panel.Children.Add(pickerGrid);
            return panel;
        }

        var selectedSedra = sedrot.FirstOrDefault(sedra =>
            string.Equals(sedra.Key, readerState.SelectedSedraKey, StringComparison.Ordinal));
        if (selectedSedra is null)
        {
            readerState.IsSedraContentOpen = false;
            readerState.SelectedSedraKey = string.Empty;
            return CreateReaderSedrotTools(readerState);
        }

        panel.Children.Add(new TextBlock
        {
            Text = FormatSedraTitle(selectedSedra),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = selectedSedra.WholeRef,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var aliyah in selectedSedra.Aliyot)
        {
            panel.Children.Add(CreateAliyahRow(readerState, aliyah));
        }

        return panel;
    }

    private Control CreateSedraPickerRow(ReaderTabState readerState, TorahSedra sedra)
    {
        var title = new TextBlock
        {
            Text = FormatSedraTitle(sedra),
            FontSize = Math.Max(13, GetSelectedUiFontSize()),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var detail = new TextBlock
        {
            Text = sedra.IsCombined ? $"{sedra.WholeRef} - Combined" : sedra.WholeRef,
            FontSize = Math.Max(11, GetSelectedUiFontSize() - 1),
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            TextWrapping = TextWrapping.Wrap
        };

        var button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    title,
                    detail
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, SedraPickerColumnGap, SedraPickerColumnGap)
        };
        button.Classes.Add("commentary-source-row");
        if (string.Equals(readerState.SelectedSedraKey, sedra.Key, StringComparison.Ordinal))
        {
            button.Classes.Add("selected");
        }

        button.Click += (_, e) =>
        {
            readerState.SelectedSedraKey = sedra.Key;
            readerState.IsSedraContentOpen = true;
            if (readerState.ShowAliyot)
            {
                RenderReaderContent(readerState);
            }

            UpdateReaderTools();
            SaveLayoutState();
            e.Handled = true;
        };

        return button;
    }

    private static void ApplySedraPickerGridWidths(WrapPanel pickerGrid)
    {
        var availableWidth = pickerGrid.Bounds.Width;
        if (availableWidth <= 0)
        {
            return;
        }

        var columns = Math.Max(
            1,
            (int)Math.Floor((availableWidth + SedraPickerColumnGap) / (SedraPickerMinimumColumnWidth + SedraPickerColumnGap)));
        var itemWidth = Math.Max(
            0,
            (availableWidth - (columns - 1) * SedraPickerColumnGap) / columns - SedraPickerColumnGap);

        foreach (var child in pickerGrid.Children.OfType<Control>())
        {
            child.Width = itemWidth;
        }
    }

    private Control CreateAliyahRow(ReaderTabState readerState, TorahAliyah aliyah)
    {
        var background = GetAliyahBrush(aliyah.Number);
        var button = new Button
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock
            {
                Text = $"{aliyah.Number}. {FormatAliyahRef(aliyah.Ref)}",
                TextWrapping = TextWrapping.Wrap
            }
        };

        button.Click += (_, e) =>
        {
            ScrollReaderToReference(readerState, aliyah.Ref);
            e.Handled = true;
        };

        return button;
    }

    private string FormatSedraTitle(TorahSedra sedra)
    {
        return _settings.InstalledBookTitleDisplay switch
        {
            InstalledBookTitleDisplay.Hebrew => sedra.HebrewTitle,
            InstalledBookTitleDisplay.English => sedra.EnglishTitle,
            _ => $"{sedra.HebrewTitle} / {sedra.EnglishTitle}"
        };
    }

    private static string FormatAliyahRef(string reference)
    {
        var parts = reference.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? parts[1] : reference;
    }

    private static IBrush GetAliyahBrush(int aliyahNumber)
    {
        var colors = new[]
        {
            "#FFF3C4",
            "#DFF7E8",
            "#DDEBFF",
            "#F5E3FF",
            "#FFE3DF",
            "#E1F5F7",
            "#F1EAD8"
        };
        var color = colors[Math.Clamp(aliyahNumber - 1, 0, colors.Length - 1)];
        return new SolidColorBrush(Color.Parse(color));
    }

    private static void ScrollReaderToReference(ReaderTabState readerState, string aliyahRef)
    {
        var startReference = GetAliyahStartReference(aliyahRef);
        if (string.IsNullOrWhiteSpace(startReference))
        {
            return;
        }

        var row = readerState.ReaderRows
            .Where(candidate => !candidate.IsChapterHeading)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Primary?.Reference, startReference, StringComparison.Ordinal) ||
                string.Equals(candidate.Translation?.Reference, startReference, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        ScrollReaderRowToTop(readerState, row);
    }

    private static string GetAliyahStartReference(string aliyahRef)
    {
        if (string.IsNullOrWhiteSpace(aliyahRef))
        {
            return string.Empty;
        }

        var startIndex = 0;
        while (startIndex < aliyahRef.Length && !char.IsDigit(aliyahRef[startIndex]))
        {
            startIndex++;
        }

        if (startIndex >= aliyahRef.Length)
        {
            return string.Empty;
        }

        var range = aliyahRef[startIndex..].Trim();
        var dashIndex = range.IndexOf('-');
        var start = dashIndex < 0 ? range : range[..dashIndex];
        return start.Replace(':', '.');
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

        var pinnedGroups = groups
            .Where(group => readerState.PinnedCommentarySourceKeys.Contains(group.Key))
            .ToList();
        var unpinnedGroups = groups
            .Where(group => !readerState.PinnedCommentarySourceKeys.Contains(group.Key))
            .ToList();

        foreach (var group in pinnedGroups)
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

        if (pinnedGroups.Count > 0 && unpinnedGroups.Count > 0)
        {
            panel.Children.Add(CreatePinnedCommentarySeparator());
        }

        foreach (var group in unpinnedGroups)
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };
        header.Children.Add(titleLine);
        if (group is not null)
        {
            var isPinned = readerState.PinnedCommentarySourceKeys.Contains(group.Key);
            var pinButton = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(6, 0, 4, 0),
                Opacity = isPinned ? 0.85 : 0.32,
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

                if (!readerState.PinnedCommentarySourceKeys.Add(group.Key))
                {
                    readerState.PinnedCommentarySourceKeys.Remove(group.Key);
                }

                _settings.PinnedCommentarySourceKeys = readerState.PinnedCommentarySourceKeys
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _settingsService.Save(_settings);
                e.Handled = true;
                UpdateReaderTools();
            };
            header.Children.Add(pinButton);
            Grid.SetColumn(pinButton, 1);
        }

        header.Children.Add(badge);
        Grid.SetColumn(badge, 2);

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
            var pinnedGroups = groups
                .Where(group => readerState.PinnedCommentarySourceKeys.Contains(group.Key))
                .ToList();
            var unpinnedGroups = groups
                .Where(group => !readerState.PinnedCommentarySourceKeys.Contains(group.Key))
                .ToList();

            foreach (var group in pinnedGroups)
            {
                AddCommentarySourceContent(readerState, content, group, includeHeader: true);
            }

            if (pinnedGroups.Count > 0 && unpinnedGroups.Count > 0)
            {
                content.Children.Add(CreatePinnedCommentarySeparator());
            }

            foreach (var group in unpinnedGroups)
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

    private static Control CreatePinnedCommentarySeparator()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#98A2B3")),
            Margin = new Thickness(0, 4)
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

        panel.Children.Add(CreateReaderTextBlock(
            text,
            useHebrew && !string.IsNullOrWhiteSpace(commentary.HebrewText),
            readerState.HebrewMarksMode,
            GetCommentaryReaderFontSize(readerState)));
        return panel;
    }

    private double GetCommentaryReaderFontSize(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? GetSelectedHebrewCommentaryFontSize()
            : GetSelectedEnglishCommentaryFontSize();
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
            ? "\u05db\u05dc \u05d4\u05e7\u05d9\u05e9\u05d5\u05e8\u05d9\u05dd \u05dc\u05de\u05e4\u05e8\u05e9\u05d9\u05dd"
            : "All Commentaries";
    }

    private static string GetAllCommentariesDescription(ReaderTabState readerState)
    {
        return readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? "\u05e4\u05d9\u05e8\u05d5\u05e9\u05d9\u05dd \u05d5\u05d3\u05d9\u05d5\u05e0\u05d9\u05dd \u05e1\u05d1\u05d9\u05d1 \u05d8\u05e7\u05e1\u05d8\u05d9\u05dd \u05ea\u05d5\u05e8\u05e0\u05d9\u05d9\u05dd, \u05de\u05d9\u05de\u05d9 \u05d4\u05d1\u05d9\u05e0\u05d9\u05d9\u05dd \u05d5\u05e2\u05d3 \u05d9\u05de\u05d9\u05e0\u05d5."
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
                ? "\u05d4\u05e4\u05d9\u05e8\u05d5\u05e9 \u05d4\u05e0\u05e4\u05d5\u05e5 \u05d5\u05d4\u05de\u05d5\u05db\u05e8 \u05d1\u05d9\u05d5\u05ea\u05e8 \u05dc\u05ea\u05d5\u05e8\u05d4, \u05d4\u05de\u05d1\u05d0\u05e8 \u05d0\u05ea \u05e4\u05e9\u05d5\u05d8\u05d9 \u05d4\u05de\u05e7\u05e8\u05d0\u05d5\u05ea \u05d1\u05ea\u05d5\u05e1\u05e4\u05ea \u05d4\u05e8\u05d7\u05d1\u05d5\u05ea \u05e4\u05e8\u05e9\u05e0\u05d9\u05d5\u05ea."
                : "Most widely-read biblical commentary, explaining the simple meaning of the text with interpretive elaborations.";
        }

        if (normalizedTitle.Contains("ibn ezra", StringComparison.Ordinal))
        {
            return useHebrew
                ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05e4\u05e9\u05d8 \u05d4\u05de\u05e9\u05dc\u05d1 \u05d1\u05d9\u05d0\u05d5\u05e8\u05d9\u05dd \u05d4\u05de\u05d1\u05d5\u05e1\u05e1\u05d9\u05dd \u05e2\u05dc \u05d3\u05e7\u05d3\u05d5\u05e7 \u05d5\u05d1\u05dc\u05e9\u05e0\u05d5\u05ea."
                : "Commentary focused on the simple meaning of the text and incorporating grammar and linguistics.";
        }

        if (normalizedTitle.Contains("ramban", StringComparison.Ordinal) ||
            normalizedTitle.Contains("nachmanides", StringComparison.Ordinal))
        {
            return useHebrew
                ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05d4\u05de\u05e9\u05dc\u05d1 \u05e4\u05e8\u05e9\u05e0\u05d5\u05ea \u05de\u05e7\u05e8\u05d0\u05d9\u05ea \u05e2\u05dd \u05d4\u05dc\u05db\u05d4, \u05d4\u05d2\u05d5\u05ea \u05d5\u05de\u05d9\u05e1\u05d8\u05d9\u05e7\u05d4."
                : "Commentary weaving together biblical interpretation with law, philosophy, and mysticism.";
        }

        if (normalizedTitle.Contains("sforno", StringComparison.Ordinal))
        {
            return useHebrew
                ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05e2\u05dc \u05d4\u05ea\u05d5\u05e8\u05d4 \u05de\u05d0\u05ea \u05e8\u05d1\u05d9 \u05e2\u05d5\u05d1\u05d3\u05d9\u05d4 \u05e1\u05e4\u05d5\u05e8\u05e0\u05d5, \u05e8\u05d1 \u05d5\u05e8\u05d5\u05e4\u05d0 \u05d0\u05d9\u05d8\u05dc\u05e7\u05d9."
                : "Commentary on the Torah by Rabbi Ovadiah Sforno, an Italian rabbi and physician.";
        }

        if (normalizedTitle.Contains("abarbanel", StringComparison.Ordinal) ||
            normalizedTitle.Contains("abravanel", StringComparison.Ordinal))
        {
            return useHebrew
                ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05e2\u05dc \u05d4\u05ea\u05d5\u05e8\u05d4 \u05d5\u05d4\u05e0\u05d1\u05d9\u05d0\u05d9\u05dd, \u05d4\u05e4\u05d5\u05ea\u05d7 \u05e4\u05e2\u05de\u05d9\u05dd \u05e8\u05d1\u05d5\u05ea \u05d1\u05e9\u05d0\u05dc\u05d5\u05ea \u05e2\u05dc \u05d4\u05d8\u05e7\u05e1\u05d8."
                : "Commentary on the Torah and Prophets, often opening each section with questions on the biblical text.";
        }

        if (normalizedTitle.Contains("tosafot", StringComparison.Ordinal))
        {
            return useHebrew
                ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05ea\u05dc\u05de\u05d5\u05d3\u05d9 \u05de\u05d1\u05e2\u05dc\u05d9 \u05d4\u05ea\u05d5\u05e1\u05e4\u05d5\u05ea, \u05d4\u05de\u05e9\u05d5\u05d5\u05d4 \u05e1\u05d5\u05d2\u05d9\u05d5\u05ea \u05d5\u05de\u05d9\u05d9\u05e9\u05d1 \u05e7\u05d5\u05e9\u05d9\u05d5\u05ea \u05d1\u05e8\u05d7\u05d1\u05d9 \u05d4\u05e9\u05f4\u05e1."
                : "Talmudic commentary comparing passages and resolving questions across the Talmud.";
        }

        return useHebrew
            ? "\u05e4\u05d9\u05e8\u05d5\u05e9 \u05d4\u05de\u05e7\u05d5\u05e9\u05e8 \u05dc\u05e4\u05e1\u05e7\u05d4 \u05e9\u05e0\u05d1\u05d7\u05e8\u05d4."
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
        if (readerState.ReaderWebView is not null)
        {
            ScrollReaderWebViewToReference(readerState, row);
            return;
        }

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

    private Button CreateReaderDisplaySettingsButton(ReaderTabState readerState)
    {
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight
        };
        readerState.DisplayFlyout = flyout;
        RefreshReaderDisplayFlyout(readerState);
        flyout.Opened += (_, _) => RefreshReaderDisplayFlyout(readerState);

        var button = new Button
        {
            Content = "A",
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(12, 24, 12, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Flyout = flyout
        };

        return button;
    }

    private void RefreshReaderDisplayFlyout(ReaderTabState readerState)
    {
        if (readerState.DisplayFlyout is not null)
        {
            readerState.DisplayFlyout.Content = CreateReaderDisplayMenu(readerState, compact: true);
        }
    }

    private Control CreateReaderDisplayMenu(ReaderTabState readerState, bool compact)
    {
        NormalizeReaderDisplayState(readerState);

        var panel = new StackPanel
        {
            Spacing = 0,
            Width = compact ? 250 : double.NaN
        };

        panel.Children.Add(CreateReaderDisplayModeRow(
            readerState,
            "Source",
            ReaderDisplayMode.PrimaryOnly,
            enabled: true));
        panel.Children.Add(CreateReaderDisplayModeRow(
            readerState,
            "Source with Translation",
            GetPreferredTranslatedDisplayMode(readerState),
            enabled: readerState.SelectedTranslation is not null,
            selectedOverride: readerState.DisplayMode != ReaderDisplayMode.PrimaryOnly));

        panel.Children.Add(CreateDisplayDivider());
        panel.Children.Add(CreateDisplayLayoutSection(readerState));
        panel.Children.Add(CreateDisplayDivider());
        panel.Children.Add(CreateHebrewMarksToggle(readerState, "Vowels", isCantillationToggle: false));
        panel.Children.Add(CreateHebrewMarksToggle(readerState, "Cantillation", isCantillationToggle: true));
        panel.Children.Add(CreateAliyotToggle(readerState));

        return compact
            ? new Border
            {
            Background = Brushes.White,
                Padding = new Thickness(10),
                Child = panel
            }
            : panel;
    }

    private void NormalizeReaderDisplayState(ReaderTabState state)
    {
        if (state.SelectedTranslation is null && state.DisplayMode != ReaderDisplayMode.PrimaryOnly)
        {
            state.DisplayMode = ReaderDisplayMode.PrimaryOnly;
        }

        NormalizeHebrewMarksMode(state);
    }

    private ReaderDisplayMode GetPreferredTranslatedDisplayMode(ReaderTabState state)
    {
        return state.DisplayMode == ReaderDisplayMode.PrimaryOnly
            ? _settings.ReaderDisplayModesByBook.TryGetValue(state.WorkTitle, out var savedMode) &&
                savedMode != ReaderDisplayMode.PrimaryOnly
                    ? savedMode
                    : ReaderDisplayMode.TranslationBelow
            : state.DisplayMode;
    }

    private Control CreateDisplayLayoutSection(ReaderTabState readerState)
    {
        var hasTranslation = readerState.SelectedTranslation is not null;
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        layout.Children.Add(new TextBlock
        {
            Text = "Layout",
            FontFamily = new FontFamily(GetSelectedUiFontFamily()),
            VerticalAlignment = VerticalAlignment.Center
        });

        var belowButton = CreateReaderLayoutButton(readerState, CreateBelowLayoutIcon(), "Below", ReaderDisplayMode.TranslationBelow, hasTranslation);
        var sideBySideButton = CreateReaderLayoutButton(readerState, CreateSideBySideLayoutIcon(hebrewLeft: true), "Hebrew Left", ReaderDisplayMode.SideBySide, hasTranslation);
        var translationFirstButton = CreateReaderLayoutButton(readerState, CreateSideBySideLayoutIcon(hebrewLeft: false), "Hebrew Right", ReaderDisplayMode.TranslationSideBySide, hasTranslation);
        layout.Children.Add(belowButton);
        layout.Children.Add(sideBySideButton);
        layout.Children.Add(translationFirstButton);
        Grid.SetColumn(belowButton, 1);
        Grid.SetColumn(sideBySideButton, 2);
        Grid.SetColumn(translationFirstButton, 3);

        return layout;
    }

    private Button CreateReaderLayoutButton(
        ReaderTabState state,
        Control icon,
        string tooltip,
        ReaderDisplayMode mode,
        bool enabled)
    {
        var isSelected = state.DisplayMode == mode;
        var button = new Button
        {
            Content = icon,
            Width = 44,
            Height = 42,
            MinWidth = 44,
            MinHeight = 42,
            Padding = new Thickness(4, 0),
            IsEnabled = enabled,
            Background = isSelected ? new SolidColorBrush(Color.Parse("#D8E7FF")) : Brushes.Transparent,
            BorderBrush = isSelected ? new SolidColorBrush(Color.Parse("#7AB7F0")) : Brushes.Transparent,
            Tag = mode
        };
        ToolTip.SetTip(button, tooltip);

        button.Click += (_, _) =>
        {
            if (button.Tag is ReaderDisplayMode selectedMode)
            {
                SetReaderDisplayMode(state, selectedMode);
            }
        };

        return button;
    }

    private Control CreateBelowLayoutIcon()
    {
        var fontSize = GetSelectedHebrewDisplayFontSize();
        return new TextBlock
        {
            Text = "A\n\u05d0",
            FontFamily = new FontFamily(GetSelectedHebrewDisplayFontFamily()),
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            LineHeight = Math.Max(13, fontSize + 1),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Control CreateSideBySideLayoutIcon(bool hebrewLeft)
    {
        var textBlock = CreateLayoutInlineTextBlock();
        if (hebrewLeft)
        {
            AddLayoutGlyphRun(textBlock, "\u05d0", isHebrew: true);
            AddLayoutGlyphRun(textBlock, "A", isHebrew: false);
        }
        else
        {
            AddLayoutGlyphRun(textBlock, "A", isHebrew: false);
            AddLayoutGlyphRun(textBlock, "\u05d0", isHebrew: true);
        }

        return textBlock;
    }

    private TextBlock CreateLayoutInlineTextBlock()
    {
        var fontSize = GetSelectedHebrewDisplayFontSize();
        return new TextBlock
        {
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            LineHeight = Math.Max(13, fontSize + 1),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void AddLayoutGlyphRun(TextBlock textBlock, string text, bool isHebrew)
    {
        textBlock.Inlines ??= new InlineCollection();
        textBlock.Inlines.Add(new Run(text)
        {
            FontFamily = new FontFamily(isHebrew ? GetSelectedHebrewDisplayFontFamily() : GetSelectedUiFontFamily()),
            FontSize = GetSelectedHebrewDisplayFontSize(),
            FontWeight = FontWeight.SemiBold
        });
    }

    private Control CreateReaderDisplayModeRow(
        ReaderTabState state,
        string label,
        ReaderDisplayMode mode,
        bool enabled,
        bool? selectedOverride = null)
    {
        var isSelected = selectedOverride ?? state.DisplayMode == mode;
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isSelected ? new SolidColorBrush(Color.Parse("#D8E7FF")) : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            IsEnabled = enabled,
            Padding = new Thickness(8, 6),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                        Foreground = new SolidColorBrush(Color.Parse("#101828")),
                        FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = isSelected ? "\u2713" : string.Empty,
                        FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                        Foreground = new SolidColorBrush(Color.Parse("#101828")),
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            },
            Tag = mode
        };
        Grid.SetColumn(((Grid)row.Content).Children[1], 1);

        row.Click += (_, _) =>
        {
            if (row.Tag is ReaderDisplayMode selectedMode)
            {
                SetReaderDisplayMode(state, selectedMode);
            }
        };

        return row;
    }

    private Control CreateHebrewMarksToggle(ReaderTabState state, string label, bool isCantillationToggle)
    {
        var hasVowels = state.HebrewMarksMode != HebrewMarksMode.TextOnly;
        var hasCantillation = state.HebrewMarksMode == HebrewMarksMode.NikkudAndCantillation;
        var isChecked = isCantillationToggle ? hasCantillation : hasVowels;
        var isEnabled = !isCantillationToggle || (hasVowels && SupportsCantillation(state.Primary));
        var option = new CheckBox
        {
            Content = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily(GetSelectedUiFontFamily())
            },
            IsChecked = isChecked,
            IsEnabled = isEnabled,
            Margin = new Thickness(0, 3),
            Tag = isCantillationToggle
        };

        option.IsCheckedChanged += (_, _) =>
        {
            if (option.Tag is not bool selectedIsCantillationToggle)
            {
                return;
            }

            if (selectedIsCantillationToggle)
            {
                SetCantillationEnabled(state, option.IsChecked == true);
            }
            else
            {
                SetVowelsEnabled(state, option.IsChecked == true);
            }
        };

        return option;
    }

    private Control CreateAliyotToggle(ReaderTabState state)
    {
        var hasTorahSedrot = HasTorahSedrot(state.Primary);
        var hasSedra = GetSelectedTorahSedra(state) is not null;
        var option = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Aliyot",
                FontFamily = new FontFamily(GetSelectedUiFontFamily())
            },
            IsChecked = state.ShowAliyot,
            IsEnabled = hasTorahSedrot,
            Margin = new Thickness(0, 3)
        };
        ToolTip.SetTip(option, hasSedra ? "Show aliyah divisions" : "Selects the current sedra and shows aliyah divisions");

        option.IsCheckedChanged += (_, _) =>
        {
            var shouldShowAliyot = option.IsChecked == true;
            if (shouldShowAliyot && !EnsureSelectedTorahSedraForCurrentPosition(state))
            {
                state.ShowAliyot = false;
                option.IsChecked = false;
                return;
            }

            state.ShowAliyot = shouldShowAliyot;
            if (shouldShowAliyot)
            {
                state.IsSedrotExpanded = true;
            }

            RenderReaderContent(state);
            UpdateReaderTools();
            RefreshReaderDisplayFlyout(state);
            SaveLayoutState();
        };

        return option;
    }

    private static Control CreateDisplayDivider()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(-10, 8),
            Background = new SolidColorBrush(Color.Parse("#EAECF0"))
        };
    }

    private void SetReaderDisplayMode(ReaderTabState state, ReaderDisplayMode mode)
    {
        if (mode != ReaderDisplayMode.PrimaryOnly && state.SelectedTranslation is null)
        {
            return;
        }

        state.DisplayMode = mode;
        _settings.ReaderDisplayModesByBook[state.WorkTitle] = mode;
        _settingsService.Save(_settings);
        RenderReaderContent(state);
        UpdateReaderTools();
        RefreshReaderDisplayFlyout(state);
        SaveLayoutState();
    }

    private void SetVowelsEnabled(ReaderTabState state, bool enabled)
    {
        state.HebrewMarksMode = enabled ? HebrewMarksMode.Nikkud : HebrewMarksMode.TextOnly;
        RenderReaderContent(state);
        UpdateReaderTools();
        RefreshReaderDisplayFlyout(state);
        SaveLayoutState();
    }

    private void SetCantillationEnabled(ReaderTabState state, bool enabled)
    {
        state.HebrewMarksMode = enabled && SupportsCantillation(state.Primary)
            ? HebrewMarksMode.NikkudAndCantillation
            : HebrewMarksMode.Nikkud;
        RenderReaderContent(state);
        UpdateReaderTools();
        RefreshReaderDisplayFlyout(state);
        SaveLayoutState();
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
}
