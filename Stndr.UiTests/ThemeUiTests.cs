using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Stndr.UiTests.UiTestAppBuilder))]

namespace Stndr.UiTests;

public static class UiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[CollectionDefinition("UI", DisableParallelization = true)]
public class UiCollection { }

[Collection("UI")]
public class ThemeUiTests
{
    [Fact]
    public async Task Shell_search_stays_inside_library_and_utility_views_render()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTestAppBuilder));
        await session.Dispatch(async () =>
        {
        var root = Path.Combine(Path.GetTempPath(), "Stndr-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new AppSettingsService(Path.Combine(root, "bootstrap"));
        settings.SetConfiguredDataFolder(Path.Combine(root, "data"));
        var window = new MainWindow(settings, startServices: false) { Width = 1400, Height = 800, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            var librarySearch = window.FindControl<TextBox>("LeftPanelSearchBox")!;
            Assert.NotNull(librarySearch.FindAncestorOfType<Expander>());
            Assert.True(librarySearch.IsVisible);
            Assert.NotNull(window.FindControl<TextBox>("GlobalSearchBox"));
            var output = Environment.GetEnvironmentVariable("STNDR_UI_ARTIFACTS");
            if (!string.IsNullOrEmpty(output)) Directory.CreateDirectory(output);
            Capture(window, output, "shell");
            foreach (var view in new[] { "Dictionary", "AdvancedSearch", "Settings" })
            {
                typeof(MainWindow).GetMethod("Open" + view + "Clicked", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, new object?[] { null, new Avalonia.Interactivity.RoutedEventArgs() });
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(60);
                Assert.NotEmpty(window.FindControl<Grid>("CenterTabContentHost")!.Children);
                Capture(window, output, view);
            }

            var globalSearch = window.FindControl<TextBox>("GlobalSearchBox")!;
            globalSearch.Text = "sample query";
            globalSearch.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            await Task.Delay(60);
            Assert.Equal("Advanced Search", ((TabItem)window.FindControl<TabControl>("CenterTabs")!.SelectedItem!).Tag);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.Text == "sample query" && box != globalSearch);

            await CaptureReaders(window, output);
            var toggle = typeof(MainWindow).GetMethod("ToggleLeftPanel", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toggle.Invoke(window, new object?[] { null, new Avalonia.Interactivity.RoutedEventArgs() });
            toggle.Invoke(window, new object?[] { null, new Avalonia.Interactivity.RoutedEventArgs() });
            Assert.True(librarySearch.IsVisible);

            var originalTheme = File.ReadAllText(UiTheme.FilePath!);
            try
            {
                foreach (var (name, path) in UiTheme.BundledColorSchemes())
                {
                    UiTheme.ApplyColorScheme(path);
                    UiTheme.ApplyTree(window);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(name, UiTheme.Current.Name);
                    Capture(window, output, "scheme-" + name);
                }

                File.WriteAllText(UiTheme.FilePath!, """{"Colors":{"Surface":"#202428","Window":"#171A1E","Text":"#F3EEE5","Muted":"#BCB4AA","Icon":"#DAB888","Border":"#41464D","SurfaceAlt":"#282D32","Hover":"#393D43","Selection":"#484136"},"Text":{"Reference":{"Size":21}}}""");
                Assert.True(UiTheme.Reload(), UiTheme.LoadError);

                UiTheme.ApplyTree(window);
                Assert.True(UiTheme.IsDark);
                Assert.Equal(21, UiTheme.Type("Reference").Size);
                Assert.Equal("Georgia", UiTheme.Type("Reference").FontFamily);
                var reference = new TextBlock { [UiTheme.RoleProperty] = "Reference", Text = "Genesis 1:1" };
                UiTheme.ApplyControl(reference);
                Assert.Equal(21, reference.FontSize);
                Assert.Contains("font-size:21px", UiTheme.CssText("Reference"));

                Capture(window, output, "dark-theme");
                File.WriteAllText(UiTheme.FilePath!, """{"Text":null}""");
                Assert.False(UiTheme.Reload());
                Assert.True(UiTheme.IsDark);
            }
            finally { File.WriteAllText(UiTheme.FilePath!, originalTheme); UiTheme.Reload(); }
        }
        finally { window.Close(); }
        return true;
        }, default);
    }

    // The native WebView cannot draw on a headless platform. Export its production HTML
    // separately and reserve its exact rectangle in the native chrome snapshot.
    private static async Task CaptureReaders(MainWindow window, string? output)
    {
        if (string.IsNullOrEmpty(output)) return;
        var samples = new[]
        {
            ("Chumash", "Genesis", "בראשית", "פרק א / Chapter 1",
                "בְּרֵאשִׁית בָּרָא אֱלֹהִים אֵת הַשָּׁמַיִם וְאֵת הָאָרֶץ׃",
                "In the beginning God created the heavens and the earth."),
            ("Talmud", "Berakhot", "ברכות", "משנה / Mishnah",
                "מֵאֵימָתַי קוֹרִין אֶת שְׁמַע בְּעַרְבִית מִשָּׁעָה שֶׁהַכֹּהֲנִים נִכְנָסִים לֶאֱכוֹל בִּתְרוּמָתָן׃",
                "From what time may one recite the Shema in the evening? From the time when the priests enter to eat their terumah."),
            ("Siddur", "Siddur Edot HaMizrach", "סידור עדות המזרח", "אשרי / Ashrei",
                "אַשְׁרֵי יוֹשְׁבֵי בֵיתֶךָ עוֹד יְהַלְלוּךָ סֶּלָה׃",
                "Happy are those who dwell in Your house; they will yet praise You, Selah.")
        };
        var main = typeof(MainWindow);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var stateType = main.GetNestedType("ReaderTabState", BindingFlags.NonPublic)!;
        var rowType = main.GetNestedType("ReaderDisplayRow", BindingFlags.NonPublic)!;
        var installedData = Environment.GetEnvironmentVariable("STNDR_VISUAL_DATA");
        SefariaLibraryService? library = null;
        if (!string.IsNullOrWhiteSpace(installedData))
        {
            library = new SefariaLibraryService(installedData);
            main.GetField("_sefariaLibrary", flags)!.SetValue(window, library);
            await (Task)main.GetMethod("RefreshInstalledBooksTreeAsync", flags)!.Invoke(window, null)!;
        }

        foreach (var sample in samples)
        {
            var book = new InstalledSefariaBook { Title = sample.Item2, HebrewTitle = sample.Item3, LanguageCode = "he" };
            var translation = new InstalledSefariaBook { Title = sample.Item2, LanguageCode = "en" };
            var state = Activator.CreateInstance(stateType)!;
            void Set(string name, object? value) => stateType.GetProperty(name)!.SetValue(state, value);
            Set("Primary", book); Set("SelectedTranslation", translation); Set("WorkTitle", book.Title);
            Set("DisplayMode", ReaderDisplayMode.TranslationBelow);
            Set("IsTextsExpanded", false); Set("IsCommentariesExpanded", false); Set("IsSedrotExpanded", false);
            var rows = (System.Collections.IList)stateType.GetProperty("ReaderRows")!.GetValue(state)!;
            rows.Add(Activator.CreateInstance(rowType, new object?[] { null, null, true, "section", sample.Item4 })!);
            for (var i = 1; i <= 5; i++)
                rows.Add(Activator.CreateInstance(rowType, new object?[] {
                    new ReaderTextUnit("1." + i, sample.Item5), new ReaderTextUnit("1." + i, sample.Item6), false, "section", "" })!);
            var html = (string)main.GetMethod("BuildReaderWebDocument", flags)!.Invoke(window, new[] { state })!;
            File.WriteAllText(Path.Combine(output, sample.Item1 + ".html"), html);
            if (library is not null)
            {
                var versions = library.GetInstalledVersionsForTitle(sample.Item2).ToList();
                var primary = versions.FirstOrDefault(SefariaLibraryService.IsHebrew);
                Assert.NotNull(primary);
                state = main.GetMethod("CreateReaderTabState", flags)!.Invoke(window, new object?[] { primary, versions, null })!;
                Set("IsTextsExpanded", false); Set("IsCommentariesExpanded", false); Set("IsSedrotExpanded", false);
            }
            var args = new object?[] { state, null, null, null, null };
            var view = (Grid)main.GetMethod("CreateReaderView", flags)!.Invoke(window, args)!;
            ((TextBlock)args[2]!).Text = sample.Item3 + "   |   " + sample.Item2;
            ((TextBlock)args[3]!).Text = sample.Item4.Replace(" / ", "   /   ");
            var web = view.Children.OfType<NativeWebView>().Single();
            view.Children.Remove(web);
            Set("ReaderWebView", null);
            Set("TitleBlock", args[2]); Set("ChapterBlock", args[3]); Set("VersionBlock", args[4]);
            if (library is not null)
            {
                main.GetMethod("RenderReaderContent", flags)!.Invoke(window, new[] { state });
                rows = (System.Collections.IList)stateType.GetProperty("ReaderRows")!.GetValue(state)!;
                var selectedRows = rows.Cast<object>().ToList();
                if (sample.Item1 == "Siddur")
                {
                    var index = selectedRows.FindIndex(row => (rowType.GetProperty("ChapterHeading")!.GetValue(row) as string)?.Contains("Ashrei", StringComparison.OrdinalIgnoreCase) == true);
                    if (index >= 0) selectedRows = selectedRows.Skip(index).ToList();
                }
                selectedRows = selectedRows.Take(35).ToList();
                rows.Clear(); foreach (var row in selectedRows) rows.Add(row);
                if (rows.Count > 0)
                    main.GetMethod("UpdateReaderChapterHeader", flags)!.Invoke(window, new[] { state, rows[0] });
                html = (string)main.GetMethod("BuildReaderWebDocument", flags)!.Invoke(window, new[] { state })!;
                File.WriteAllText(Path.Combine(output, sample.Item1 + ".html"), html);
            }
            var placeholder = new Border { Background = UiTheme.Brush("Surface") };
            Grid.SetRow(placeholder, 1); view.Children.Add(placeholder);
            var tab = (TabItem)main.GetMethod("CreateTab", flags)!.Invoke(window, new object?[] { sample.Item2, view })!;
            ((System.Collections.IDictionary)main.GetField("_openReaderTabs", flags)!.GetValue(window)!).Add(tab, state);
            ((System.Collections.IList)main.GetField("_tabs", flags)!.GetValue(window)!).Add(tab);
            window.FindControl<TabControl>("CenterTabs")!.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            Capture(window, output, sample.Item1 + "-chrome");
            var position = placeholder.TranslatePoint(default, window)!.Value;
            File.WriteAllText(Path.Combine(output, sample.Item1 + "-bounds.json"),
                System.Text.Json.JsonSerializer.Serialize(new { x = position.X, y = position.Y, width = placeholder.Bounds.Width, height = placeholder.Bounds.Height }));
            Assert.InRange(view.Children[0].Bounds.Height, 60, 72);
        }
    }

    private static void Capture(Window window, string? output, string name)
    {
        if (string.IsNullOrEmpty(output)) return;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(output, name + ".png"));
    }

    [Fact]
    public void Invalid_theme_values_are_rejected_without_changing_current_theme()
    {
        var before = UiTheme.Current;
        Assert.ThrowsAny<Exception>(() => UiTheme.Parse("""{"Colors":{"Text":"invalid-colour"}}"""));
        Assert.ThrowsAny<Exception>(() => UiTheme.Parse("""{"Text":{"Reference":{"Size":-3}}}"""));
        Assert.ThrowsAny<Exception>(() => UiTheme.Parse("""{"Metrics":{"MainButtonHeight":-1}}"""));
        Assert.Same(before, UiTheme.Current);
    }

    [Fact]
    public void Bundled_schemes_make_selected_text_darker_than_the_selected_paragraph()
    {
        static double Luminance(string hex) { var c = Avalonia.Media.Color.Parse(hex); return c.R * .2126 + c.G * .7152 + c.B * .0722; }
        var schemes = UiTheme.BundledColorSchemes();
        Assert.Contains(schemes, scheme => scheme.Name == "Blue");
        Assert.Contains(schemes, scheme => scheme.Name == "Azure");
        Assert.Contains(schemes, scheme => scheme.Name == "Green");
        foreach (var (name, path) in schemes)
        {
            var colors = UiTheme.Parse(File.ReadAllText(path)).Colors;
            Assert.True(Luminance(colors["Selection"]) - Luminance(colors["TextSelection"]) >= 25, name);
        }
    }

    [Fact]
    public void Theme_has_independent_reader_and_reference_typography()
    {
        var theme = UiTheme.Parse("""{"Text":{"Reference":{"FontFamily":"Arial","Size":19,"Color":"Icon"}}}""");
        Assert.Equal("Arial", theme.Text["Reference"].FontFamily);
        Assert.Equal(19, theme.Text["Reference"].Size);
        Assert.Equal("David", theme.Text["ReaderHebrew"].FontFamily);
        Assert.NotEqual(theme.Text["ReaderEnglish"].Size, theme.Text["ReaderHebrew"].Size);
    }
}
