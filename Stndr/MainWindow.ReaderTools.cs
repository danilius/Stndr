using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int LinkPreviewCountPerCategory = 12;

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
        ClearDictionaryToolsControls();
        _rightPanelTitle.Text = "Reader Tools";

        if (_isDictionaryDocked)
        {
            _rightPanelBody.Children.Add(CreateDockedDictionaryToolsControl());
        }

        if (_centerTabs?.SelectedItem is not TabItem selectedTab)
        {
            if (!_isDictionaryDocked)
            {
                _rightPanelBody.Children.Add(new TextBlock
                {
                    Text = "Open a text to see reader tools.",
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return;
        }

        if (!_openReaderTabs.TryGetValue(selectedTab, out var readerState))
        {
            if (!_isDictionaryDocked)
            {
                _rightPanelBody.Children.Add(new TextBlock
                {
                    Text = "Open a text to see reader tools.",
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return;
        }

        ApplyPinnedCommentaryPreferences(readerState);
        ApplyCommentarySortPreferences(readerState);

        _rightPanelBody.Children.Add(new TextBlock
        {
            Text = FormatTitle(readerState.Primary.Title, readerState.Primary.HebrewTitle),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            CreateNavigationGroupHeader(readerState),
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

        _rightPanelBody.Children.Add(CreateReaderToolsGroup(
            CreateLinksGroupHeader(readerState),
            CreateReaderLinksTools(readerState),
            readerState.IsLinksExpanded,
            value =>
            {
                readerState.IsLinksExpanded = value;
                SaveLinksPreferences(readerState);
                SaveLayoutState();
                if (!value)
                {
                    readerState.LinksLoadCts?.Cancel();
                    readerState.LinksLoadCts = null;
                    return;
                }

                EnsureLinksLoadedForCurrentSelection(readerState);
                UpdateReaderTools();
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

        if (textTools.Children.Count > 0)
        {
            textTools.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 8, 0, 4),
                Background = new SolidColorBrush(Color.Parse("#EAECF0"))
            });
        }

        textTools.Children.Add(new TextBlock
        {
            Text = "Other versions may be available to download.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontFamily = new FontFamily(GetSelectedUiFontFamily())
        });

        var manageVersionsButton = new Button
        {
            Content = "Manage Versions",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(8, 6)
        };
        ToolTip.SetTip(manageVersionsButton, "Open this book in Library Manager to download or delete versions.");
        manageVersionsButton.Click += async (_, _) => await OpenLibraryManagerForWorkAsync(readerState.WorkTitle);
        textTools.Children.Add(manageVersionsButton);

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
        var hasBackButton = readerState.IsCommentaryContentOpen;
        var layout = new Grid
        {
            ColumnDefinitions = hasBackButton
                ? new ColumnDefinitions("Auto,*,Auto,Auto")
                : new ColumnDefinitions("*,Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleColumn = hasBackButton ? 1 : 0;
        var sortColumn = hasBackButton ? 2 : 1;
        var languageColumn = hasBackButton ? 3 : 2;

        if (hasBackButton)
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
        Grid.SetColumn(titleBlock, titleColumn);

        var sortFlyout = readerState.CommentarySortFlyout ?? new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight
        };
        readerState.CommentarySortFlyout = sortFlyout;
        RefreshCommentarySortFlyout(readerState);
        sortFlyout.Opened += (_, _) => RefreshCommentarySortFlyout(readerState);

        var sortButton = new Button
        {
            Content = "\u21c5",
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Flyout = sortFlyout
        };
        layout.Children.Add(sortButton);
        Grid.SetColumn(sortButton, sortColumn);

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
            if (readerState.IsCommentarySplitOpen)
            {
                UpdateSplitPaneView(readerState);
            }

            SaveLayoutState();
        };

        layout.Children.Add(languageButton);
        Grid.SetColumn(languageButton, languageColumn);
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

    private static string FormatLinksHeaderTitle(ReaderTabState readerState)
    {
        return readerState.Links.Count > 0 ? $"Links ({readerState.Links.Count})" : "Links";
    }

    private Control CreateLinksGroupHeader(ReaderTabState readerState)
    {
        return new TextBlock
        {
            Text = FormatLinksHeaderTitle(readerState),
            VerticalAlignment = VerticalAlignment.Center
        };
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

        var splitButton = new Button
        {
            Content = "Show in split view",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = readerState.SelectedReaderRow is not null
        };
        splitButton.Click += (_, e) =>
        {
            ShowCommentarySplitView(readerState);
            SaveLayoutState();
            e.Handled = true;
        };
        panel.Children.Add(splitButton);

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

    private Control CreateReaderLinksTools(ReaderTabState readerState)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 8
        };

        if (readerState.SelectedReaderRow is null || string.IsNullOrWhiteSpace(readerState.SelectedLinksRef))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Select a paragraph to see links.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        panel.Children.Add(new TextBlock
        {
            Text = readerState.SelectedLinksRef,
            Foreground = new SolidColorBrush(Color.Parse("#667085")),
            FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
            TextWrapping = TextWrapping.Wrap
        });

        if (readerState.IsLinksLoading)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Loading links...",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (!string.IsNullOrWhiteSpace(readerState.LinksError))
        {
            panel.Children.Add(new TextBlock
            {
                Text = readerState.LinksError,
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (readerState.Links.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Equals(readerState.LoadedLinksRef, readerState.SelectedLinksRef, StringComparison.Ordinal)
                    ? "No links for this paragraph."
                    : "Open this expander to load cached or live links for the selected paragraph.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        var groups = GetLinkCategoryGroups(readerState.Links);
        EnsureSelectedLinkCategories(readerState, groups);

        panel.Children.Add(CreateLinkCategoryFilterPanel(readerState, groups));

        var filteredGroups = groups
            .Where(group => readerState.SelectedLinkCategories.Contains(group.Category))
            .ToList();
        if (filteredGroups.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No categories selected for this work.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        foreach (var group in filteredGroups)
        {
            panel.Children.Add(CreateLinkCategorySection(readerState, group));
        }

        return panel;
    }

    private StackPanel CreateLinkCategoryFilterPanel(
        ReaderTabState readerState,
        List<LinkCategoryGroup> groups)
    {
        var panel = new StackPanel
        {
            Spacing = 6
        };

        var categoryList = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var group in groups)
        {
            categoryList.Children.Add(CreateLinkCategoryToggle(readerState, group));
        }

        panel.Children.Add(CreateReaderToolsGroup(
            "Filters",
            categoryList,
            readerState.IsLinkCategoryMoreExpanded,
            value => readerState.IsLinkCategoryMoreExpanded = value));

        return panel;
    }

    private Control CreateLinkCategoryToggle(ReaderTabState readerState, LinkCategoryGroup group)
    {
        var option = new CheckBox
        {
            Content = $"{group.Category} ({group.Items.Count})",
            IsChecked = readerState.SelectedLinkCategories.Contains(group.Category),
            Margin = new Thickness(0, 0, 0, 4)
        };

        option.IsCheckedChanged += (_, _) =>
        {
            if (option.IsChecked == true)
            {
                readerState.SelectedLinkCategories.Add(group.Category);
            }
            else
            {
                readerState.SelectedLinkCategories.Remove(group.Category);
            }

            readerState.HasInitializedLinkCategorySelection = true;
            SaveLinksPreferences(readerState);
            UpdateReaderTools();
            SaveLayoutState();
        };

        return option;
    }

    private Control CreateLinkCategorySection(ReaderTabState readerState, LinkCategoryGroup group)
    {
        var section = new StackPanel
        {
            Spacing = 4
        };

        section.Children.Add(new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new TextBlock
            {
                Text = $"{group.Category} ({group.Items.Count})",
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        });

        var isExpanded = readerState.ExpandedLinkCategories.Contains(group.Category);
        var visibleCount = isExpanded
            ? group.Items.Count
            : LinkPreviewCountPerCategory;
        foreach (var item in group.Items.Take(visibleCount))
        {
            section.Children.Add(CreateLinkRowEntry(readerState, item));
        }

        if (group.Items.Count > LinkPreviewCountPerCategory)
        {
            var showMoreButton = new Button
            {
                Content = isExpanded
                    ? "Show fewer"
                    : $"Show all {group.Items.Count} links",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4),
                FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
                Margin = new Thickness(0, 2, 0, 0)
            };
            showMoreButton.Click += (_, e) =>
            {
                if (readerState.ExpandedLinkCategories.Contains(group.Category))
                {
                    readerState.ExpandedLinkCategories.Remove(group.Category);
                }
                else
                {
                    readerState.ExpandedLinkCategories.Add(group.Category);
                }

                UpdateReaderTools();
                e.Handled = true;
            };
            section.Children.Add(showMoreButton);
        }

        return section;
    }

    private Control CreateLinkRowEntry(ReaderTabState readerState, SefariaLinkItem item)
    {
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        var title = useHebrew
            ? FirstNonEmpty(
                item.HebrewDisplayTitle,
                item.DisplayTitle,
                ExtractReferenceTitle(item.DisplayReference))
            : FirstNonEmpty(
                item.DisplayTitle,
                ExtractReferenceTitle(item.DisplayReference));
        var heading = BuildLinkHeading(title, item.DisplayReference, item.DisplayTitle, useHebrew);

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    CreateLinkTextBlock(heading, isEmphasized: true)
                }
            }
        };
        button.Click += (_, e) =>
        {
            ToggleLinkPreview(readerState, item);
            e.Handled = true;
        };

        var container = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        container.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Child = button
        });

        if (string.Equals(readerState.ExpandedLinkPreviewRef, item.DisplayReference, StringComparison.Ordinal))
        {
            container.Children.Add(CreateLinkPreviewCard(readerState));
        }

        return container;
    }

    private Control CreateLinkPreviewCard(ReaderTabState readerState)
    {
        var panel = new StackPanel
        {
            Spacing = 8
        };

        if (readerState.IsLinkPreviewLoading)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Loading preview...",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (readerState.ActiveLinkPreview is not null)
        {
            var previewText = GetPreferredLinkPreviewText(readerState);
            var hasInstalledFullSource = HasInstalledFullLinkSource(readerState.ActiveLinkPreview);
            panel.Children.Add(CreateLinkTextBlock(readerState.ActiveLinkPreview.Reference, isEmphasized: true));
            panel.Children.Add(new TextBlock
            {
                Text = readerState.ActiveLinkPreview.IsFromInstalledBook
                    ? "Preview from local data"
                    : "Preview from downloaded excerpt",
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(CreateLinkPreviewBody(readerState, previewText));

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            var splitButton = new Button
            {
                Content = "Show in split view",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            splitButton.Click += (_, e) =>
            {
                ShowLinkPreviewInSplitView(readerState);
                e.Handled = true;
            };
            actionRow.Children.Add(splitButton);

            var openButton = new Button
            {
                Content = readerState.IsLinkSourceTabLoading ? "Downloading source..." : "Open source in new tab",
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !readerState.IsLinkSourceTabLoading
            };
            openButton.Click += (_, e) =>
            {
                _ = OpenLinkSourceInNewTabAsync(readerState);
                e.Handled = true;
            };
            actionRow.Children.Add(openButton);
            panel.Children.Add(actionRow);

            panel.Children.Add(new TextBlock
            {
                Text = hasInstalledFullSource
                    ? "The full book is already installed and will open in a new tab."
                    : "The full book is not installed and will be downloaded if you open it in a new tab.",
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(readerState.LinkPreviewError))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = readerState.LinkPreviewError,
                    FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                    FontSize = GetSelectedUiFontSize(),
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(readerState.LinkPreviewError)
                    ? "This linked text is not installed."
                    : readerState.LinkPreviewError,
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });

            var downloadButton = new Button
            {
                Content = readerState.IsLinkWorkDownloadLoading ? "Downloading..." : "Download work",
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !readerState.IsLinkWorkDownloadLoading
            };
            downloadButton.Click += (_, e) =>
            {
                _ = DownloadLinkWorkForPreviewAsync(readerState);
                e.Handled = true;
            };
            panel.Children.Add(downloadButton);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9FAFB")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel
        };
    }

    private Control CreateReaderSplitWebView(ReaderTabState readerState)
    {
        var webView = new NativeWebView
        {
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        webView.WebMessageReceived += (_, e) => HandleReaderSplitWebMessage(readerState, e.Body);
        NavigateReaderWebView(webView, BuildReaderSplitWebDocument(readerState));
        return webView;
    }

    private void HandleReaderSplitWebMessage(ReaderTabState readerState, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : string.Empty;

            switch (type)
            {
                case "close":
                    if (readerState.IsCommentarySplitOpen)
                    {
                        CloseCommentarySplitView(readerState);
                        SaveLayoutState();
                    }
                    else
                    {
                        CloseLinkSplitView(readerState);
                    }
                    break;

                case "openLinkSource":
                    _ = OpenLinkSourceInNewTabAsync(readerState);
                    break;

                case "downloadLinkWork":
                    _ = DownloadLinkWorkForPreviewAsync(readerState);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed browser messages; split-pane HTML is generated by the app.
        }
    }

    private string BuildReaderSplitWebDocument(ReaderTabState readerState)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html>");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<style>");
        builder.AppendLine(BuildReaderSplitWebCss());
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");

        if (readerState.IsCommentarySplitOpen)
        {
            AppendCommentarySplitWebBody(builder, readerState);
        }
        else
        {
            AppendLinkSplitWebBody(builder, readerState);
        }

        builder.AppendLine("<script>");
        builder.AppendLine(BuildReaderSplitWebScript());
        builder.AppendLine("</script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private string BuildReaderSplitWebCss()
    {
        var uiFont = CssString(GetSelectedUiFontFamily());
        var englishFont = CssString(GetSelectedEnglishFontFamily());
        var hebrewFont = CssString(GetSelectedHebrewFontFamily());
        var uiSize = GetSelectedUiFontSize().ToString(CultureInfo.InvariantCulture);
        var englishCommentarySize = GetSelectedEnglishCommentaryFontSize().ToString(CultureInfo.InvariantCulture);
        var hebrewCommentarySize = GetSelectedHebrewCommentaryFontSize().ToString(CultureInfo.InvariantCulture);

        return $$"""
            :root {
                color-scheme: light;
                --ui-font: {{uiFont}};
                --english-font: {{englishFont}};
                --hebrew-font: {{hebrewFont}};
                --ui-size: {{uiSize}}px;
                --english-commentary-size: {{englishCommentarySize}}px;
                --hebrew-commentary-size: {{hebrewCommentarySize}}px;
                --text: #101828;
                --muted: #667085;
                --faint: #98A2B3;
                --border: #EAECF0;
                --error: #B42318;
            }

            html, body {
                background: #fff;
                color: var(--text);
                font-family: var(--ui-font);
                font-size: var(--ui-size);
                margin: 0;
                min-height: 100%;
            }

            body {
                overflow-y: auto;
                user-select: text;
            }

            header {
                align-items: center;
                border-bottom: 1px solid var(--border);
                box-sizing: border-box;
                display: grid;
                gap: 10px;
                grid-template-columns: minmax(0, 1fr) auto;
                padding: 12px 16px 8px;
                position: sticky;
                top: 0;
                background: #fff;
                z-index: 2;
            }

            h1 {
                font-size: var(--ui-size);
                font-weight: 600;
                line-height: 1.35;
                margin: 0;
                overflow-wrap: anywhere;
            }

            main {
                box-sizing: border-box;
                padding: 14px 16px 18px;
            }

            button {
                background: #fff;
                border: 1px solid #D0D5DD;
                border-radius: 4px;
                color: #344054;
                cursor: default;
                font: inherit;
                padding: 5px 9px;
            }

            button:disabled {
                color: var(--faint);
            }

            .actions {
                display: flex;
                flex-wrap: wrap;
                gap: 8px;
                margin-top: 12px;
            }

            .status,
            .ref,
            .section-label {
                color: var(--muted);
                font-size: max(10px, calc(var(--ui-size) - 1px));
                line-height: 1.45;
            }

            .error {
                color: var(--error);
            }

            .section {
                border-top: 1px solid var(--border);
                padding-top: 12px;
            }

            .section + .section,
            .commentary + .commentary,
            .text-section + .text-section {
                margin-top: 14px;
            }

            .source-title {
                font-weight: 600;
                line-height: 1.45;
                margin: 0 0 8px;
            }

            .commentary {
                line-height: 1.65;
            }

            .text {
                line-height: 1.75;
                overflow-wrap: anywhere;
                white-space: normal;
            }

            .english {
                direction: ltr;
                font-family: var(--english-font);
                font-size: var(--english-commentary-size);
                text-align: left;
            }

            .hebrew {
                direction: rtl;
                font-family: var(--hebrew-font);
                font-size: var(--hebrew-commentary-size);
                text-align: right;
                unicode-bidi: plaintext;
            }

            b, strong {
                font-weight: 700;
            }

            i, em {
                font-style: italic;
            }
            """;
    }

    private static string BuildReaderSplitWebScript()
    {
        return """
            (function () {
                const send = (message) => {
                    if (typeof invokeCSharpAction === 'function') {
                        invokeCSharpAction(JSON.stringify(message));
                    }
                };

                document.addEventListener('click', (event) => {
                    const action = event.target.closest('[data-action]')?.dataset.action;
                    if (!action) {
                        return;
                    }

                    event.preventDefault();
                    send({ type: action });
                });
            })();
            """;
    }

    private void AppendCommentarySplitWebBody(StringBuilder builder, ReaderTabState readerState)
    {
        AppendReaderSplitWebHeader(
            builder,
            string.IsNullOrWhiteSpace(readerState.SelectedCommentaryRef)
                ? "Commentaries"
                : readerState.SelectedCommentaryRef);

        builder.AppendLine("<main>");
        if (readerState.IsCommentaryLoading)
        {
            AppendReaderSplitStatus(builder, "Loading commentaries...");
        }
        else if (!string.IsNullOrWhiteSpace(readerState.CommentaryError))
        {
            AppendReaderSplitStatus(builder, readerState.CommentaryError, isError: true);
        }
        else if (readerState.Commentaries.Count == 0)
        {
            AppendReaderSplitStatus(builder, "No commentaries for this paragraph.");
        }
        else
        {
            var groups = GetCommentarySourceGroups(readerState.Commentaries);
            AppendAllCommentariesWebContent(builder, readerState, groups);
        }

        builder.AppendLine("</main>");
    }

    private void AppendLinkSplitWebBody(StringBuilder builder, ReaderTabState readerState)
    {
        AppendReaderSplitWebHeader(
            builder,
            readerState.ActiveLinkPreview?.Reference ??
            readerState.ActiveLinkPreviewItem?.DisplayReference ??
            "Linked text");

        builder.AppendLine("<main>");
        if (readerState.IsLinkPreviewLoading)
        {
            AppendReaderSplitStatus(builder, "Loading linked text...");
        }
        else if (readerState.ActiveLinkPreview is not null)
        {
            AppendReaderSplitStatus(
                builder,
                readerState.ActiveLinkPreview.IsFromInstalledBook
                    ? "Showing the locally installed source text"
                    : "Showing the downloaded linked excerpt");

            foreach (var section in GetOrderedLinkPreviewSections(readerState.ActiveLinkPreview, readerState.CommentaryLanguage))
            {
                if (string.IsNullOrWhiteSpace(section.Text))
                {
                    continue;
                }

                var isHebrewSection = string.Equals(section.Label, "Hebrew", StringComparison.OrdinalIgnoreCase);
                AppendReaderSplitTextSection(builder, section.Label, section.Text, isHebrewSection, readerState.HebrewMarksMode);
            }

            builder.AppendLine("<div class=\"actions\">");
            builder.AppendLine("<button data-action=\"openLinkSource\">Open source in new tab</button>");
            builder.AppendLine("</div>");
        }
        else
        {
            AppendReaderSplitStatus(
                builder,
                string.IsNullOrWhiteSpace(readerState.LinkPreviewError)
                    ? "This linked text is not installed."
                    : readerState.LinkPreviewError,
                isError: true);

            builder.AppendLine("<div class=\"actions\">");
            builder.Append("<button data-action=\"downloadLinkWork\"");
            if (readerState.IsLinkWorkDownloadLoading)
            {
                builder.Append(" disabled");
            }
            builder.Append(">");
            builder.Append(WebUtility.HtmlEncode(readerState.IsLinkWorkDownloadLoading ? "Downloading..." : "Download work"));
            builder.AppendLine("</button>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</main>");
    }

    private static void AppendReaderSplitWebHeader(StringBuilder builder, string title)
    {
        builder.AppendLine("<header>");
        builder.Append("<h1>");
        builder.Append(WebUtility.HtmlEncode(title));
        builder.AppendLine("</h1>");
        builder.AppendLine("<button data-action=\"close\">Close split</button>");
        builder.AppendLine("</header>");
    }

    private static void AppendReaderSplitStatus(StringBuilder builder, string text, bool isError = false)
    {
        builder.Append("<p class=\"status");
        if (isError)
        {
            builder.Append(" error");
        }
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(text));
        builder.AppendLine("</p>");
    }

    private void AppendAllCommentariesWebContent(
        StringBuilder builder,
        ReaderTabState readerState,
        List<CommentarySourceGroup> groups)
    {
        var (pinnedGroups, unpinnedGroups) = GetOrderedCommentaryGroupSections(readerState, groups);

        foreach (var group in pinnedGroups)
        {
            AppendCommentarySourceWebContent(builder, readerState, group);
        }

        foreach (var group in unpinnedGroups)
        {
            AppendCommentarySourceWebContent(builder, readerState, group);
        }
    }

    private void AppendCommentarySourceWebContent(
        StringBuilder builder,
        ReaderTabState readerState,
        CommentarySourceGroup group)
    {
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        builder.AppendLine("<section class=\"section\">");
        builder.Append("<h2 class=\"source-title ");
        builder.Append(useHebrew ? "hebrew" : "english");
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(GetCommentaryGroupDisplayTitle(readerState, group)));
        builder.AppendLine("</h2>");

        foreach (var commentary in group.Items)
        {
            AppendCommentaryWebItem(builder, readerState, commentary);
        }

        builder.AppendLine("</section>");
    }

    private void AppendCommentaryWebItem(
        StringBuilder builder,
        ReaderTabState readerState,
        SefariaCommentaryItem commentary)
    {
        var preferHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        var text = preferHebrew
            ? FirstNonEmpty(commentary.HebrewText, commentary.Text)
            : FirstNonEmpty(commentary.Text, commentary.HebrewText);
        var isHebrew = preferHebrew && !string.IsNullOrWhiteSpace(commentary.HebrewText);

        builder.AppendLine("<article class=\"commentary\">");
        builder.Append("<div class=\"ref\">");
        builder.Append(WebUtility.HtmlEncode(commentary.Ref));
        builder.AppendLine("</div>");

        if (string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine("<div class=\"status\">No text available in this language.</div>");
        }
        else
        {
            builder.Append("<div class=\"text ");
            builder.Append(isHebrew ? "hebrew" : "english");
            builder.Append("\">");
            builder.Append(SanitizeReaderHtmlForWeb(text, isHebrew, readerState.HebrewMarksMode));
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</article>");
    }

    private void AppendReaderSplitTextSection(
        StringBuilder builder,
        string label,
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode)
    {
        builder.AppendLine("<section class=\"text-section\">");
        builder.Append("<div class=\"section-label\">");
        builder.Append(WebUtility.HtmlEncode(label));
        builder.AppendLine("</div>");
        builder.Append("<div class=\"text ");
        builder.Append(isHebrew ? "hebrew" : "english");
        builder.Append("\">");
        builder.Append(SanitizeReaderHtmlForWeb(text, isHebrew, hebrewMarksMode));
        builder.AppendLine("</div>");
        builder.AppendLine("</section>");
    }

    private Control CreateLinkSplitView(ReaderTabState readerState)
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 16, 8)
        };
        header.Children.Add(new TextBlock
        {
            Text = readerState.ActiveLinkPreview?.Reference ?? readerState.ActiveLinkPreviewItem?.DisplayReference ?? "Linked text",
            FontFamily = new FontFamily(GetSelectedUiFontFamily()),
            FontSize = GetSelectedUiFontSize(),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        var closeButton = new Button
        {
            Content = "Close split",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, e) =>
        {
            CloseLinkSplitView(readerState);
            e.Handled = true;
        };
        header.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);

        var content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16, 0, 16, 16)
        };

        if (readerState.IsLinkPreviewLoading)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Loading linked text...",
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = GetSelectedUiFontSize(),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (readerState.ActiveLinkPreview is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = readerState.ActiveLinkPreview.IsFromInstalledBook
                    ? "Showing the locally installed source text"
                    : "Showing the downloaded linked excerpt",
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var section in GetOrderedLinkPreviewSections(readerState.ActiveLinkPreview, readerState.CommentaryLanguage))
            {
                if (string.IsNullOrWhiteSpace(section.Text))
                {
                    continue;
                }

                content.Children.Add(new TextBlock
                {
                    Text = section.Label,
                    FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                    FontSize = Math.Max(10, GetSelectedUiFontSize() - 1),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#475467"))
                });
                var isHebrewSection = string.Equals(section.Label, "Hebrew", StringComparison.OrdinalIgnoreCase);
                content.Children.Add(CreateLinkPreviewBody(
                    readerState,
                    section.Text,
                    maxLength: null,
                    isHebrewOverride: isHebrewSection));
            }
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(readerState.LinkPreviewError)
                    ? "This linked text is not installed."
                    : readerState.LinkPreviewError,
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = GetSelectedUiFontSize(),
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = header
                },
                new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            }
        };
        Grid.SetRow(layout.Children[1], 1);
        return layout;
    }

    private Control CreateCommentarySplitView(ReaderTabState readerState)
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 16, 8)
        };
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(readerState.SelectedCommentaryRef)
                ? "Commentaries"
                : readerState.SelectedCommentaryRef,
            FontFamily = new FontFamily(GetSelectedUiFontFamily()),
            FontSize = GetSelectedUiFontSize(),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        var closeButton = new Button
        {
            Content = "Close split",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, e) =>
        {
            CloseCommentarySplitView(readerState);
            SaveLayoutState();
            e.Handled = true;
        };
        header.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);

        var content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16, 0, 16, 16)
        };

        if (readerState.IsCommentaryLoading)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Loading commentaries...",
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = GetSelectedUiFontSize(),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (!string.IsNullOrWhiteSpace(readerState.CommentaryError))
        {
            content.Children.Add(new TextBlock
            {
                Text = readerState.CommentaryError,
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = GetSelectedUiFontSize(),
                Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (readerState.Commentaries.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No commentaries for this paragraph.",
                FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                FontSize = GetSelectedUiFontSize(),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            var groups = GetCommentarySourceGroups(readerState.Commentaries);
            AddAllCommentariesContent(readerState, content, groups);
        }

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#EAECF0")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = header
                },
                new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            }
        };
        Grid.SetRow(layout.Children[1], 1);
        return layout;
    }

    private Control CreateLinkSourceLoadingView(
        string workTitle,
        string workHebrewTitle,
        out ProgressBar progressBar,
        out TextBlock statusBlock)
    {
        statusBlock = new TextBlock
        {
            Text = $"Preparing {FormatTitle(workTitle, workHebrewTitle)}...",
            FontFamily = new FontFamily(GetSelectedUiFontFamily()),
            FontSize = GetSelectedUiFontSize(),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Width = 280,
            Height = 18
        };

        return new Grid
        {
            Background = Brushes.White,
            Children =
            {
                new StackPanel
                {
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        statusBlock,
                        progressBar
                    }
                }
            }
        };
    }

    private Control CreateLinkSourceErrorView(string message)
    {
        return new Grid
        {
            Background = Brushes.White,
            Children =
            {
                new TextBlock
                {
                    Text = $"The linked work could not be opened.\n{message}",
                    FontFamily = new FontFamily(GetSelectedUiFontFamily()),
                    FontSize = GetSelectedUiFontSize(),
                    Foreground = new SolidColorBrush(Color.Parse("#B42318")),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(32)
                }
            }
        };
    }

    private bool HasInstalledFullLinkSource(SefariaLinkPreview preview)
    {
        return _sefariaLibrary.GetFullInstalledVersionsForTitle(preview.WorkTitle).Count > 0;
    }

    private string GetPreferredLinkPreviewText(ReaderTabState readerState)
    {
        if (readerState.ActiveLinkPreview is null)
        {
            return string.Empty;
        }

        var text = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew
            ? FirstNonEmpty(
                readerState.ActiveLinkPreview.HebrewText,
                readerState.ActiveLinkPreview.EnglishText)
            : FirstNonEmpty(
                readerState.ActiveLinkPreview.EnglishText,
                readerState.ActiveLinkPreview.HebrewText);
        return NormalizeLinkPreviewText(text, 420);
    }

    private TextBlock CreateLinkTextBlock(
        string text,
        bool isEmphasized = false,
        IBrush? foreground = null,
        double? fontSizeOverride = null)
    {
        var useHebrew = ContainsHebrewText(text);
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(useHebrew ? GetSelectedHebrewFontFamily() : GetSelectedEnglishFontFamily()),
            FontSize = fontSizeOverride ?? GetSelectedLinkPanelFontSize(useHebrew),
            FontWeight = isEmphasized ? FontWeight.SemiBold : FontWeight.Normal,
            FlowDirection = useHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Foreground = foreground ?? new SolidColorBrush(Color.Parse("#344054")),
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static string BuildLinkHeading(
        string displayTitle,
        string fullReference,
        string englishTitle,
        bool useHebrew)
    {
        var referenceTitle = FirstNonEmpty(
            englishTitle,
            ExtractReferenceTitle(fullReference));
        var location = ExtractLinkLocation(fullReference, referenceTitle);
        if (string.IsNullOrWhiteSpace(location))
        {
            return displayTitle;
        }

        return $"{displayTitle}, {FormatLinkLocationLabel(location, useHebrew)}";
    }

    private static string ExtractLinkLocation(string fullReference, string title)
    {
        if (string.IsNullOrWhiteSpace(fullReference) || string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var prefix = title + " ";
        return fullReference.StartsWith(prefix, StringComparison.Ordinal)
            ? fullReference[prefix.Length..].Trim()
            : string.Empty;
    }

    private static string ExtractReferenceTitle(string fullReference)
    {
        if (string.IsNullOrWhiteSpace(fullReference))
        {
            return string.Empty;
        }

        var parts = fullReference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var endIndex = parts.Length;
        while (endIndex > 0 && parts[endIndex - 1].Any(char.IsDigit))
        {
            endIndex--;
        }

        return endIndex == parts.Length
            ? fullReference.Trim()
            : string.Join(' ', parts.Take(endIndex));
    }

    private static string FormatLinkLocationLabel(string location, bool useHebrew)
    {
        if (int.TryParse(location, out _))
        {
            return useHebrew ? $"פרק {location}" : $"Chapter {location}";
        }

        return location;
    }

    private Control CreateLinkPreviewBody(
        ReaderTabState readerState,
        string text,
        int? maxLength = 420,
        bool? isHebrewOverride = null)
    {
        var normalizedText = NormalizeLinkPreviewText(text, maxLength);
        var isHebrew = isHebrewOverride ?? ContainsHebrewText(normalizedText);
        return CreateReaderTextBlock(
            normalizedText,
            isHebrew,
            readerState.HebrewMarksMode,
            GetSelectedLinkPanelFontSize(isHebrew));
    }

    private IEnumerable<(string Label, string Text)> GetOrderedLinkPreviewSections(
        SefariaLinkPreview preview,
        CommentaryLanguage preferredLanguage)
    {
        if (preferredLanguage == CommentaryLanguage.Hebrew)
        {
            if (!string.IsNullOrWhiteSpace(preview.HebrewText))
            {
                yield return ("Hebrew", preview.HebrewText);
            }

            if (!string.IsNullOrWhiteSpace(preview.EnglishText))
            {
                yield return ("English", preview.EnglishText);
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(preview.EnglishText))
        {
            yield return ("English", preview.EnglishText);
        }

        if (!string.IsNullOrWhiteSpace(preview.HebrewText))
        {
            yield return ("Hebrew", preview.HebrewText);
        }
    }

    private double GetSelectedLinkPanelFontSize(bool isHebrew)
    {
        return isHebrew ? GetSelectedHebrewCommentaryFontSize() : GetSelectedEnglishCommentaryFontSize();
    }

    private double GetSelectedLinkSecondaryFontSize(bool isHebrew)
    {
        return Math.Max(10, GetSelectedLinkPanelFontSize(isHebrew) - 1);
    }

    private static string NormalizeLinkPreviewText(string text, int? maxLength)
    {
        text = Regex.Replace(text, "<.*?>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (maxLength is > 0 && text.Length > maxLength.Value)
        {
            text = text[..maxLength.Value].TrimEnd() + "...";
        }

        return text;
    }

    private static bool ContainsHebrewText(string text)
    {
        foreach (var character in text)
        {
            if (character >= '\u0590' && character <= '\u05FF')
            {
                return true;
            }
        }

        return false;
    }

    private static List<LinkCategoryGroup> GetLinkCategoryGroups(IEnumerable<SefariaLinkItem> items)
    {
        return items
            .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LinkCategoryGroup(
                group.Key,
                group
                    .OrderBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.DisplayReference, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    private void EnsureSelectedLinkCategories(ReaderTabState readerState, List<LinkCategoryGroup> groups)
    {
        var availableCategories = new HashSet<string>(
            groups.Select(group => group.Category),
            StringComparer.OrdinalIgnoreCase);
        var selectedCategories = new HashSet<string>(
            readerState.SelectedLinkCategories
                .Where(availableCategories.Contains),
            StringComparer.OrdinalIgnoreCase);

        if (selectedCategories.Count == 0 &&
            groups.Count > 0 &&
            !readerState.HasInitializedLinkCategorySelection)
        {
            selectedCategories = new HashSet<string>(
                GetDefaultLinkCategorySelection(groups),
                StringComparer.OrdinalIgnoreCase);
        }

        if (selectedCategories.SetEquals(readerState.SelectedLinkCategories))
        {
            return;
        }

        readerState.SelectedLinkCategories = selectedCategories;
        readerState.HasInitializedLinkCategorySelection = true;
        SaveLinksPreferences(readerState);
    }

    private static IEnumerable<string> GetDefaultLinkCategorySelection(List<LinkCategoryGroup> groups)
    {
        var preferred = groups.FirstOrDefault(group =>
            string.Equals(group.Category, "Quoting Commentary", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            return new[] { preferred.Category };
        }

        return groups
            .Take(1)
            .Select(group => group.Category);
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

        var (pinnedGroups, unpinnedGroups) = GetOrderedCommentaryGroupSections(readerState, groups);
        var allowReorder = readerState.CommentarySortMode == CommentarySortMode.Custom;

        if (pinnedGroups.Count > 0)
        {
            panel.Children.Add(CreateCommentaryReorderSection(readerState, pinnedGroups, allowReorder));
        }

        if (pinnedGroups.Count > 0 && unpinnedGroups.Count > 0)
        {
            panel.Children.Add(CreatePinnedCommentarySeparator());
        }

        if (unpinnedGroups.Count > 0)
        {
            panel.Children.Add(CreateCommentaryReorderSection(readerState, unpinnedGroups, allowReorder));
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

                SavePinnedCommentaryPreferences(readerState);
                e.Handled = true;
                UpdateReaderTools();
                if (readerState.IsCommentarySplitOpen)
                {
                    UpdateSplitPaneView(readerState);
                }
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
            AddAllCommentariesContent(readerState, content, groups);
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

    private void AddAllCommentariesContent(
        ReaderTabState readerState,
        StackPanel content,
        List<CommentarySourceGroup> groups)
    {
        var (pinnedGroups, unpinnedGroups) = GetOrderedCommentaryGroupSections(readerState, groups);

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

    private static readonly StringComparer HebrewCommentaryTitleComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("he-IL"), ignoreCase: false);

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
            .ToList();
    }

    private (List<CommentarySourceGroup> Pinned, List<CommentarySourceGroup> Unpinned) GetOrderedCommentaryGroupSections(
        ReaderTabState readerState,
        List<CommentarySourceGroup> groups)
    {
        EnsureCommentaryCustomOrder(readerState, groups.Select(group => group.Key));
        var pinnedGroups = OrderCommentaryGroups(
            groups.Where(group => readerState.PinnedCommentarySourceKeys.Contains(group.Key)),
            readerState);
        var unpinnedGroups = OrderCommentaryGroups(
            groups.Where(group => !readerState.PinnedCommentarySourceKeys.Contains(group.Key)),
            readerState);
        return (pinnedGroups, unpinnedGroups);
    }

    private static List<CommentarySourceGroup> OrderCommentaryGroups(
        IEnumerable<CommentarySourceGroup> groups,
        ReaderTabState readerState)
    {
        var groupList = groups.ToList();
        return readerState.CommentarySortMode switch
        {
            CommentarySortMode.Hebrew => groupList
                .OrderBy(
                    group => FirstNonEmpty(group.HebrewTitle, group.EnglishTitle),
                    HebrewCommentaryTitleComparer)
                .ToList(),
            CommentarySortMode.Custom => OrderByCustomOrder(groupList, readerState.CommentaryCustomOrder),
            _ => groupList
                .OrderBy(group => group.EnglishTitle, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static List<CommentarySourceGroup> OrderByCustomOrder(
        List<CommentarySourceGroup> groups,
        IReadOnlyList<string> customOrder)
    {
        var orderIndex = customOrder
            .Select((key, index) => (Key: key, Index: index))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

        return groups
            .OrderBy(group => orderIndex.TryGetValue(group.Key, out var index) ? index : int.MaxValue)
            .ThenBy(group => group.EnglishTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureCommentaryCustomOrder(ReaderTabState readerState, IEnumerable<string> keys)
    {
        if (readerState.CommentarySortMode != CommentarySortMode.Custom)
        {
            return;
        }

        var order = readerState.CommentaryCustomOrder.ToList();
        var changed = false;
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (order.Any(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            order.Add(key);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        readerState.CommentaryCustomOrder = order;
        SaveCommentarySortPreferences(readerState);
    }

    private void InitializeCommentaryCustomOrder(ReaderTabState readerState, List<CommentarySourceGroup> groups)
    {
        if (readerState.CommentaryCustomOrder.Count > 0)
        {
            EnsureCommentaryCustomOrder(readerState, groups.Select(group => group.Key));
            return;
        }

        readerState.CommentaryCustomOrder = groups
            .OrderBy(group => group.EnglishTitle, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();
        SaveCommentarySortPreferences(readerState);
    }

    private void SetCommentarySortMode(ReaderTabState readerState, CommentarySortMode mode)
    {
        if (mode == CommentarySortMode.Custom &&
            readerState.CommentarySortMode != CommentarySortMode.Custom)
        {
            InitializeCommentaryCustomOrder(
                readerState,
                GetCommentarySourceGroups(readerState.Commentaries));
        }

        readerState.CommentarySortMode = mode;
        SaveCommentarySortPreferences(readerState);
        readerState.CommentarySortFlyout?.Hide();
        UpdateReaderTools();
        if (readerState.IsCommentarySplitOpen)
        {
            UpdateSplitPaneView(readerState);
        }
    }

    private void MoveCommentaryWithinSection(
        ReaderTabState readerState,
        string sourceKey,
        int sourceIndex,
        int insertIndex,
        IReadOnlyList<string> sectionKeys)
    {
        if (sourceIndex < 0 || sourceIndex >= sectionKeys.Count)
        {
            return;
        }

        var reorderedSectionKeys = sectionKeys.ToList();
        var movedKey = reorderedSectionKeys[sourceIndex];
        reorderedSectionKeys.RemoveAt(sourceIndex);

        var targetIndex = insertIndex;
        if (insertIndex > sourceIndex)
        {
            targetIndex = insertIndex - 1;
        }

        targetIndex = Math.Clamp(targetIndex, 0, reorderedSectionKeys.Count);
        if (targetIndex == sourceIndex)
        {
            return;
        }

        reorderedSectionKeys.Insert(targetIndex, movedKey);

        var sectionKeySet = new HashSet<string>(reorderedSectionKeys, StringComparer.OrdinalIgnoreCase);
        var sectionQueue = new Queue<string>(reorderedSectionKeys);
        var order = readerState.CommentaryCustomOrder.ToList();
        readerState.CommentaryCustomOrder = order
            .Select(key => sectionKeySet.Contains(key) ? sectionQueue.Dequeue() : key)
            .ToList();
        SaveCommentarySortPreferences(readerState);
        UpdateReaderTools();
        if (readerState.IsCommentarySplitOpen)
        {
            UpdateSplitPaneView(readerState);
        }
    }

    private Control CreateCommentaryReorderSection(
        ReaderTabState readerState,
        List<CommentarySourceGroup> groups,
        bool allowReorder)
    {
        var sectionPanel = new StackPanel
        {
            Spacing = 6
        };

        if (!allowReorder)
        {
            foreach (var group in groups)
            {
                sectionPanel.Children.Add(CreateCommentarySourceRow(
                    readerState,
                    group.Key,
                    GetCommentaryGroupDisplayTitle(readerState, group),
                    group.Items.Count,
                    GetCommentarySourceDescription(readerState, group),
                    enabled: true,
                    group: group));
            }

            return sectionPanel;
        }

        var sectionChrome = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2, 0),
            Child = sectionPanel
        };
        var insertionLine = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.Parse("#1570EF")),
            CornerRadius = new CornerRadius(1),
            IsVisible = false,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 0, 0, 0)
        };
        var sectionHost = new Grid
        {
            Children =
            {
                sectionChrome,
                insertionLine
            }
        };

        var context = new CommentarySectionReorderContext
        {
            ReaderState = readerState,
            Section = sectionPanel,
            SectionChrome = sectionChrome,
            InsertionLine = insertionLine,
            SectionKeys = groups.Select(group => group.Key).ToList()
        };

        foreach (var group in groups)
        {
            var row = CreateCommentarySourceRow(
                readerState,
                group.Key,
                GetCommentaryGroupDisplayTitle(readerState, group),
                group.Items.Count,
                GetCommentarySourceDescription(readerState, group),
                enabled: true,
                group: group);
            sectionPanel.Children.Add(CreateCommentaryReorderRow(context, row, group.Key));
        }

        return sectionHost;
    }

    private Control CreateCommentaryReorderRow(
        CommentarySectionReorderContext context,
        Control row,
        string key)
    {
        var grip = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 0, 4, 0),
            MinWidth = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock
            {
                Text = "\u283f",
                FontSize = Math.Max(12, GetSelectedUiFontSize()),
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0)
            }
        };

        var wrapper = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                grip,
                row
            }
        };
        Grid.SetColumn(row, 1);
        wrapper.Tag = key;

        grip.PointerPressed += (_, e) => BeginCommentaryReorder(context, key, wrapper, grip, e);
        grip.PointerMoved += (_, e) => UpdateCommentaryReorder(grip, e);
        grip.PointerReleased += (_, e) => EndCommentaryReorder(grip, e);
        grip.PointerCaptureLost += (_, _) => CancelCommentaryReorderIfActive(grip);

        return wrapper;
    }

    private void BeginCommentaryReorder(
        CommentarySectionReorderContext context,
        string sourceKey,
        Control wrapper,
        Control grip,
        PointerPressedEventArgs e)
    {
        if (_activeCommentaryReorder is not null ||
            !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var sourceIndex = context.SectionKeys.FindIndex(key =>
            string.Equals(key, sourceKey, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            return;
        }

        var position = e.GetPosition(context.Section);
        _activeCommentaryReorder = new CommentaryReorderDragState
        {
            Context = context,
            SourceKey = sourceKey,
            SourceIndex = sourceIndex,
            DraggedWrapper = wrapper,
            Grip = grip,
            StartPointerY = position.Y,
            InsertIndex = sourceIndex
        };

        wrapper.Opacity = 0.55;
        wrapper.ZIndex = 10;
        context.SectionChrome.Background = new SolidColorBrush(Color.Parse("#F2F4F7"));
        UpdateCommentaryReorderVisuals(position.Y);
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void UpdateCommentaryReorder(Control grip, PointerEventArgs e)
    {
        if (_activeCommentaryReorder?.Grip != grip)
        {
            return;
        }

        var position = e.GetPosition(_activeCommentaryReorder.Context.Section);
        UpdateCommentaryReorderVisuals(position.Y);
        e.Handled = true;
    }

    private void UpdateCommentaryReorderVisuals(double pointerY)
    {
        var drag = _activeCommentaryReorder;
        if (drag is null)
        {
            return;
        }

        var section = drag.Context.Section;
        drag.InsertIndex = GetCommentaryInsertIndex(section, pointerY);
        SetCommentaryTranslateY(drag.DraggedWrapper, pointerY - drag.StartPointerY);

        var rowStep = GetCommentaryRowStep(section, drag.SourceIndex);
        for (var index = 0; index < section.Children.Count; index++)
        {
            var child = section.Children[index];
            if (ReferenceEquals(child, drag.DraggedWrapper))
            {
                continue;
            }

            var shift = 0d;
            if (drag.SourceIndex < drag.InsertIndex &&
                index > drag.SourceIndex &&
                index < drag.InsertIndex)
            {
                shift = -rowStep;
            }
            else if (drag.SourceIndex > drag.InsertIndex &&
                     index >= drag.InsertIndex &&
                     index < drag.SourceIndex)
            {
                shift = rowStep;
            }

            SetCommentaryTranslateY(child, shift);
        }

        PositionCommentaryInsertionLine(drag.Context, drag.InsertIndex, rowStep);
    }

    private void EndCommentaryReorder(Control grip, PointerReleasedEventArgs e)
    {
        if (_activeCommentaryReorder?.Grip != grip)
        {
            return;
        }

        var drag = _activeCommentaryReorder;
        var insertIndex = drag.InsertIndex;
        var sourceIndex = drag.SourceIndex;
        var readerState = drag.Context.ReaderState;
        var sourceKey = drag.SourceKey;
        var sectionKeys = drag.Context.SectionKeys;

        ResetCommentaryReorderVisuals();
        _activeCommentaryReorder = null;
        e.Pointer.Capture(null);
        e.Handled = true;

        MoveCommentaryWithinSection(readerState, sourceKey, sourceIndex, insertIndex, sectionKeys);
    }

    private void CancelCommentaryReorderIfActive(Control grip)
    {
        if (_activeCommentaryReorder?.Grip != grip)
        {
            return;
        }

        ResetCommentaryReorderVisuals();
        _activeCommentaryReorder = null;
    }

    private void ResetCommentaryReorderVisuals()
    {
        var drag = _activeCommentaryReorder;
        if (drag is null)
        {
            return;
        }

        foreach (var child in drag.Context.Section.Children)
        {
            ClearCommentaryTranslate(child);
        }

        drag.Context.InsertionLine.IsVisible = false;
        drag.Context.SectionChrome.Background = Brushes.Transparent;
    }

    private static int GetCommentaryInsertIndex(StackPanel section, double pointerY)
    {
        var cumulative = 0d;
        for (var index = 0; index < section.Children.Count; index++)
        {
            var child = section.Children[index];
            var height = GetCommentaryRowHeight(child);
            var midpoint = cumulative + (height / 2d);
            if (pointerY < midpoint)
            {
                return index;
            }

            cumulative += height + section.Spacing;
        }

        return section.Children.Count;
    }

    private static double GetCommentaryRowHeight(Control row)
    {
        if (row.Bounds.Height > 0)
        {
            return row.Bounds.Height;
        }

        return Math.Max(row.DesiredSize.Height, row.Bounds.Height);
    }

    private static double GetCommentaryRowStep(StackPanel section, int referenceIndex)
    {
        if (section.Children.Count == 0)
        {
            return 0;
        }

        var referenceChild = section.Children[Math.Clamp(referenceIndex, 0, section.Children.Count - 1)];
        return GetCommentaryRowHeight(referenceChild) + section.Spacing;
    }

    private static void PositionCommentaryInsertionLine(
        CommentarySectionReorderContext context,
        int insertIndex,
        double rowStep)
    {
        var y = 0d;
        for (var index = 0; index < insertIndex && index < context.Section.Children.Count; index++)
        {
            y += GetCommentaryRowHeight(context.Section.Children[index]) + context.Section.Spacing;
        }

        context.InsertionLine.Margin = new Thickness(14, Math.Max(0, y - 1), 0, 0);
        context.InsertionLine.IsVisible = insertIndex >= 0;
    }

    private static void SetCommentaryTranslateY(Control control, double y)
    {
        if (control.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            control.RenderTransform = transform;
        }

        transform.Y = y;
    }

    private static void ClearCommentaryTranslate(Control control)
    {
        control.RenderTransform = null;
        control.Opacity = 1;
        control.ZIndex = 0;
    }

    private void RefreshCommentarySortFlyout(ReaderTabState readerState)
    {
        if (readerState.CommentarySortFlyout is not null)
        {
            readerState.CommentarySortFlyout.Content = CreateCommentarySortMenu(readerState);
        }
    }

    private Control CreateCommentarySortMenu(ReaderTabState readerState)
    {
        var panel = new StackPanel
        {
            Spacing = 0,
            Width = 220
        };

        panel.Children.Add(CreateCommentarySortModeRow(
            readerState,
            "English A\u2013Z",
            CommentarySortMode.English));
        panel.Children.Add(CreateCommentarySortModeRow(
            readerState,
            "Hebrew \u05d0\u2013\u05ea",
            CommentarySortMode.Hebrew));
        panel.Children.Add(CreateCommentarySortModeRow(
            readerState,
            "Custom",
            CommentarySortMode.Custom));

        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(10),
            Child = panel
        };
    }

    private Control CreateCommentarySortModeRow(
        ReaderTabState readerState,
        string label,
        CommentarySortMode mode)
    {
        var isSelected = readerState.CommentarySortMode == mode;
        var checkmark = new TextBlock
        {
            Text = isSelected ? "\u2713" : string.Empty,
            FontFamily = new FontFamily(GetSelectedUiFontFamily()),
            Foreground = new SolidColorBrush(Color.Parse("#101828")),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var contentGrid = new Grid
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
                checkmark
            }
        };
        Grid.SetColumn(checkmark, 1);

        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isSelected ? new SolidColorBrush(Color.Parse("#D8E7FF")) : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(8, 6),
            Content = contentGrid
        };

        row.Click += (_, e) =>
        {
            SetCommentarySortMode(readerState, mode);
            e.Handled = true;
        };

        return row;
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

    private string GetCommentarySourceDescription(
        ReaderTabState readerState,
        CommentarySourceGroup group)
    {
        var useHebrew = readerState.CommentaryLanguage == CommentaryLanguage.Hebrew;
        var indexTitle = group.Items.FirstOrDefault()?.IndexTitle;
        foreach (var lookupTitle in new[]
                 {
                     indexTitle,
                     group.EnglishTitle,
                     group.Key
                 })
        {
            if (string.IsNullOrWhiteSpace(lookupTitle))
            {
                continue;
            }

            if (_sefariaLibrary.TryGetWorkShortDescription(lookupTitle, out var english, out var hebrew))
            {
                var description = useHebrew
                    ? FirstNonEmpty(hebrew, english)
                    : FirstNonEmpty(english, hebrew);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }
            }
        }

        var normalizedTitle = group.EnglishTitle.ToLowerInvariant();

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

    private Control CreateNavigationGroupHeader(ReaderTabState readerState)
    {
        if (!readerState.HasTalmudNavigation)
        {
            return new TextBlock
            {
                Text = "Navigation",
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleBlock = new TextBlock
        {
            Text = "Navigation",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        layout.Children.Add(titleBlock);
        Grid.SetColumn(titleBlock, 0);

        var jumpBox = new TextBox
        {
            Text = readerState.NavigationJumpQuery,
            PlaceholderText = "Jump\u2026",
            MinWidth = 88,
            MaxWidth = 140,
            Height = 24,
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        jumpBox.TextChanged += (_, _) =>
        {
            readerState.NavigationJumpQuery = jumpBox.Text ?? string.Empty;
            SaveLayoutState();
        };
        jumpBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            NavigateToNavigationJumpTarget(readerState, readerState.NavigationJumpQuery);
            e.Handled = true;
        };
        layout.Children.Add(jumpBox);
        Grid.SetColumn(jumpBox, 1);

        var toggleButton = CreateNavigationHeaderIconButton(
            IsAnyNavigationTopicExpanded(readerState) ? "\u21D1" : "\u21D3",
            IsAnyNavigationTopicExpanded(readerState) ? "Collapse all topics" : "Expand all topics");
        toggleButton.Click += (_, e) =>
        {
            SetAllNavigationTopicsExpanded(readerState, !IsAnyNavigationTopicExpanded(readerState));
            e.Handled = true;
            UpdateReaderTools();
            SaveLayoutState();
        };
        layout.Children.Add(toggleButton);
        Grid.SetColumn(toggleButton, 2);

        return layout;
    }

    private static Button CreateNavigationHeaderIconButton(string content, string tooltip)
    {
        var button = new Button
        {
            Content = content,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D5DD")),
            BorderThickness = new Thickness(1),
            MinWidth = 24,
            MinHeight = 22,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private Control CreateReaderNavigationTools(ReaderTabState readerState)
    {
        if (readerState.NavigationChapters.Count == 0)
        {
            return new TextBlock
            {
                Text = "No navigation markers are available.",
                TextWrapping = TextWrapping.Wrap
            };
        }

        if (!readerState.HasTalmudNavigation)
        {
            return CreateReaderNavigationButtonGrid(readerState.NavigationItems);
        }

        readerState.NavigationTopicExpanders.Clear();
        var validTopicKeys = new HashSet<string>(
            readerState.NavigationChapters.Select(chapter => chapter.Key),
            StringComparer.Ordinal);
        foreach (var staleKey in readerState.ExpandedNavigationTopics.Keys
                     .Where(key => !validTopicKeys.Contains(key))
                     .ToList())
        {
            readerState.ExpandedNavigationTopics.Remove(staleKey);
        }

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 4
        };

        foreach (var chapter in readerState.NavigationChapters)
        {
            panel.Children.Add(CreateNavigationTopicExpander(readerState, chapter));
        }

        if (!string.IsNullOrWhiteSpace(readerState.CurrentChapterKey))
        {
            SyncActiveNavigationTopicExpansion(readerState, readerState.CurrentChapterKey);
        }

        return panel;
    }

    private Expander CreateNavigationTopicExpander(ReaderTabState readerState, ReaderNavigationChapter chapter)
    {
        var pagesPanel = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var item in chapter.Items)
        {
            pagesPanel.Children.Add(CreateReaderNavigationButton(readerState, item));
        }

        var isExpanded = readerState.NavigationTopicsAllExpanded ||
            (readerState.ExpandedNavigationTopics.TryGetValue(chapter.Key, out var expanded) && expanded);

        var expander = new Expander
        {
            Header = CreateNavigationTopicHeader(readerState, chapter),
            Content = pagesPanel,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 0)
        };

        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty)
            {
                return;
            }

            if (expander.IsExpanded)
            {
                if (!readerState.NavigationTopicsAllExpanded)
                {
                    CollapseNavigationTopicsExcept(readerState, chapter.Key);
                }

                readerState.ExpandedNavigationTopics[chapter.Key] = true;
            }
            else
            {
                readerState.ExpandedNavigationTopics[chapter.Key] = false;
                readerState.NavigationTopicsAllExpanded = false;
            }

            SaveLayoutState();
        };

        readerState.NavigationTopicExpanders[chapter.Key] = expander;
        return expander;
    }

    private Control CreateNavigationTopicHeader(ReaderTabState readerState, ReaderNavigationChapter chapter)
    {
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleButton = new Button
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        var titleText = string.IsNullOrWhiteSpace(chapter.Title) ? "Section" : chapter.Title;
        titlePanel.Children.Add(new TextBlock
        {
            Text = titleText,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left
        });

        titleButton.Content = titlePanel;
        ToolTip.SetTip(titleButton, "Jump to the start of this section");
        titleButton.Click += (_, e) =>
        {
            if (chapter.Items.Count > 0)
            {
                ScrollReaderRowToTop(readerState, chapter.Items[0].Row);
            }

            e.Handled = true;
        };

        layout.Children.Add(titleButton);
        Grid.SetColumn(titleButton, 0);

        if (!string.IsNullOrWhiteSpace(chapter.RangeLabel))
        {
            layout.Children.Add(new TextBlock
            {
                Text = chapter.RangeLabel,
                Foreground = new SolidColorBrush(Color.Parse("#667085")),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            Grid.SetColumn(layout.Children[^1], 1);
        }

        return layout;
    }

    private static bool IsAnyNavigationTopicExpanded(ReaderTabState readerState)
    {
        return readerState.NavigationTopicsAllExpanded ||
            readerState.ExpandedNavigationTopics.Values.Any(isExpanded => isExpanded);
    }

    private void SetAllNavigationTopicsExpanded(ReaderTabState readerState, bool expandAll)
    {
        readerState.NavigationTopicsAllExpanded = expandAll;
        foreach (var chapter in readerState.NavigationChapters)
        {
            readerState.ExpandedNavigationTopics[chapter.Key] = expandAll;
            if (readerState.NavigationTopicExpanders.TryGetValue(chapter.Key, out var expander))
            {
                expander.IsExpanded = expandAll;
            }
        }
    }

    private static void CollapseNavigationTopicsExcept(ReaderTabState readerState, string activeTopicKey)
    {
        foreach (var chapter in readerState.NavigationChapters)
        {
            if (string.Equals(chapter.Key, activeTopicKey, StringComparison.Ordinal))
            {
                continue;
            }

            readerState.ExpandedNavigationTopics[chapter.Key] = false;
            if (readerState.NavigationTopicExpanders.TryGetValue(chapter.Key, out var expander))
            {
                expander.IsExpanded = false;
            }
        }
    }

    private void SyncActiveNavigationTopicExpansion(ReaderTabState readerState, string chapterKey)
    {
        if (!readerState.HasTalmudNavigation || string.IsNullOrWhiteSpace(chapterKey))
        {
            return;
        }

        var topicKey = readerState.NavigationChapters
            .FirstOrDefault(chapter => chapter.Items.Any(item =>
                string.Equals(item.Row.ChapterKey, chapterKey, StringComparison.Ordinal)))
            ?.Key;
        if (string.IsNullOrWhiteSpace(topicKey) ||
            string.Equals(topicKey, readerState.ActiveNavigationTopicKey, StringComparison.Ordinal))
        {
            return;
        }

        readerState.ActiveNavigationTopicKey = topicKey;
        readerState.ExpandedNavigationTopics[topicKey] = true;
        if (!readerState.NavigationTopicsAllExpanded)
        {
            CollapseNavigationTopicsExcept(readerState, topicKey);
        }

        if (readerState.NavigationTopicExpanders.TryGetValue(topicKey, out var expander))
        {
            expander.IsExpanded = true;
        }
    }

    private void NavigateToNavigationJumpTarget(ReaderTabState readerState, string query)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query) || readerState.NavigationItems.Count == 0)
        {
            return;
        }

        ReaderNavigationItem? target = null;
        ReaderNavigationChapter? targetChapter = null;

        if (TryResolveNavigationSimanNumber(query, out var simanNumber))
        {
            var simanKey = simanNumber.ToString();
            target = readerState.NavigationItems.FirstOrDefault(item =>
                string.Equals(item.Row.ChapterKey, simanKey, StringComparison.Ordinal) ||
                string.Equals(item.Label, simanKey, StringComparison.Ordinal) ||
                string.Equals(item.Label, FormatNavigationChapterLabel(simanKey), StringComparison.Ordinal));
        }

        if (target is null)
        {
            var normalizedQuery = NormalizeHebrewNumeralInput(query);
            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                target = readerState.NavigationItems.FirstOrDefault(item =>
                    string.Equals(NormalizeHebrewNumeralInput(item.Label), normalizedQuery, StringComparison.Ordinal));
            }
        }

        if (target is null)
        {
            targetChapter = readerState.NavigationChapters.FirstOrDefault(chapter =>
                chapter.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                chapter.Title.Contains(query, StringComparison.Ordinal));
            target = targetChapter?.Items.FirstOrDefault();
        }

        if (target is null)
        {
            return;
        }

        var topicKey = readerState.NavigationChapters
            .FirstOrDefault(chapter => chapter.Items.Contains(target))
            ?.Key;
        if (!string.IsNullOrWhiteSpace(topicKey))
        {
            readerState.ActiveNavigationTopicKey = topicKey;
            readerState.ExpandedNavigationTopics[topicKey] = true;
            if (!readerState.NavigationTopicsAllExpanded)
            {
                CollapseNavigationTopicsExcept(readerState, topicKey);
            }

            if (readerState.NavigationTopicExpanders.TryGetValue(topicKey, out var expander))
            {
                expander.IsExpanded = true;
            }
        }

        ScrollReaderRowToTop(readerState, target.Row);
        SaveLayoutState();
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
