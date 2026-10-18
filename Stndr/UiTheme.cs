using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Stndr;

/// <summary>Shared design tokens for native controls and browser-rendered text.</summary>
public static class UiTheme
{
    public sealed record TextStyle(string FontFamily = "Georgia", double Size = 14, string Color = "Text", int Weight = 400, double LineHeight = 1.5, string HebrewFontFamily = "David");
    public sealed class Definition
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "Warm parchment";
        public Dictionary<string, string> Colors { get; set; } = new();
        public Dictionary<string, TextStyle> Text { get; set; } = new();
        public Dictionary<string, double> Metrics { get; set; } = new();
    }
    public static readonly AttachedProperty<string?> RoleProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, string?>("Role", inherits: true);
    public static string? GetRole(Control control) => control.GetValue(RoleProperty);
    public static void SetRole(Control control, string? value) => control.SetValue(RoleProperty, value);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    public static Definition Current { get; private set; } = ReadDefaults();
    public static string? FilePath { get; private set; }
    public static string? LoadError { get; private set; }

    private static Definition ReadDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Themes", "warm.json");
        return JsonSerializer.Deserialize<Definition>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The bundled theme is empty.");
    }
    public static Definition Parse(string json)
    {
        var supplied = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("The theme must be a JSON object.");
        var merged = JsonSerializer.SerializeToNode(ReadDefaults(), JsonOptions)!.AsObject();
        Merge(merged, supplied);
        var patch = merged.Deserialize<Definition>(JsonOptions)
            ?? throw new InvalidDataException("The theme must be a JSON object.");
        if (patch.Colors is null || patch.Text is null || patch.Metrics is null)
            throw new InvalidDataException("Colors, Text, and Metrics must be JSON objects.");
        if (patch.Version != 1) throw new InvalidDataException("Unsupported theme version.");
        var result = ReadDefaults();
        result.Name = patch.Name;
        foreach (var (key, value) in patch.Colors) { Color.Parse(value); result.Colors[key] = value; }
        foreach (var (key, value) in patch.Text)
        {
            if (value is null || string.IsNullOrWhiteSpace(value.FontFamily) || value.FontFamily.Contains(';') || string.IsNullOrWhiteSpace(value.HebrewFontFamily) || value.HebrewFontFamily.Contains(';') ||
                !double.IsFinite(value.Size) || value.Size < 6 || value.Size > 120 ||
                value.Weight < 100 || value.Weight > 900 || !double.IsFinite(value.LineHeight) ||
                value.LineHeight < 0.8 || value.LineHeight > 4)
                throw new InvalidDataException($"Invalid text category: {key}.");
            if (!result.Colors.ContainsKey(value.Color)) Color.Parse(value.Color);
            result.Text[key] = value;
        }
        foreach (var (key, value) in patch.Metrics)
        {
            if (!double.IsFinite(value) || value < 0 || value > 2000)
                throw new InvalidDataException($"Invalid metric: {key}.");
            if (key == "NavigationPreviewCount" && (value < 1 || value > 30 || value != Math.Truncate(value)))
                throw new InvalidDataException("NavigationPreviewCount must be an integer from 1 to 30.");
            if (key == "PageHeaderHeight" && value < 32)
                throw new InvalidDataException("PageHeaderHeight must be at least 32.");
            result.Metrics[key] = value;
        }
        return result;
    }
    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var actualKey = target.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject child && target[actualKey] is JsonObject existing) Merge(existing, child);
            else target[actualKey] = value?.DeepClone();
        }
    }
    public static void Configure(string? dataFolder)
    {
        FilePath = string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(dataFolder, "theme.json");
        try
        {
            if (FilePath is not null && !File.Exists(FilePath))
                File.WriteAllText(FilePath, JsonSerializer.Serialize(ReadDefaults(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LoadError = ex.Message; return; }
        Reload();
    }
    public static bool Reload()
    {
        try
        {
            var next = FilePath is not null && File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : ReadDefaults();
            Current = next;
            LoadError = null;
            ApplyResources();
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or FormatException or InvalidDataException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
            return false;
        }
    }
    public static void Save() { if (FilePath is not null) File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions)); }
    public static TextStyle Type(string role) => Current.Text.TryGetValue(role, out var value) ? value : Current.Text["Body"];
    // Reader content keeps the fonts its theme entry names; every other role takes its Hebrew
    // glyphs from the "Hebrew display font" setting (HebrewUi).
    private static readonly HashSet<string> ReaderContentRoles = new(StringComparer.Ordinal)
    {
        "ReaderHebrew", "ReaderEnglish", "CommentaryHebrew", "CommentaryEnglish", "HebrewHeading", "SearchResult", "SearchResultHebrew"
    };
    private static readonly HashSet<string> HebrewDisplayRoles = new(StringComparer.Ordinal)
    {
        "HebrewUi", "DictionaryHeadword", "ReferenceHebrew", "DictionaryDefinitionHebrew"
    };
    public static string HebrewFont(string role) =>
        ReaderContentRoles.Contains(role) ? Type(role).HebrewFontFamily : Type("HebrewUi").FontFamily;
    public static string PrimaryFont(string role) =>
        HebrewDisplayRoles.Contains(role) ? Type("HebrewUi").FontFamily : Type(role).FontFamily;
    public static string FontStack(string role) => PrimaryFont(role) + ", " + HebrewFont(role);
    public static FontFamily Font(string role) => new(FontStack(role));
    public static double Metric(string key) => Current.Metrics[key];
    public static string Hex(string key) => Current.Colors.TryGetValue(key, out var value) ? value : key;
    public static SolidColorBrush Brush(string key)
    {
        if (!Brushes.TryGetValue(key, out var brush)) Brushes[key] = brush = new SolidColorBrush(Color.Parse(Hex(key)));
        return brush;
    }
    public static bool IsDark
    {
        get { var c = Color.Parse(Hex("Surface")); return c.R * .2126 + c.G * .7152 + c.B * .0722 < 128; }
    }
    public static string CssColor(string key)
    {
        var c = Color.Parse(Hex(key));
        return $"rgba({c.R},{c.G},{c.B},{(c.A / 255d).ToString(CultureInfo.InvariantCulture)})";
    }
    public static string CssText(string role)
    {
        var t = Type(role);
        var font = JsonSerializer.Serialize(PrimaryFont(role)) + "," + JsonSerializer.Serialize(HebrewFont(role));
        return $"font-family:{font};font-size:{t.Size.ToString(CultureInfo.InvariantCulture)}px;color:{CssColor(t.Color)};font-weight:{t.Weight};line-height:{t.LineHeight.ToString(CultureInfo.InvariantCulture)};";
    }
    public static void Initialize()
    {
        ApplyResources();
        Control.LoadedEvent.AddClassHandler<Control>((control, _) => ApplyControl(control));
    }
    public static void ApplyControl(Control control)
    {
        var role = GetRole(control);
        if (control is TextBlock text)
        {
            // Icon glyphs are not prose and must retain their symbol font.
            if (text.FontFamily.Name.Contains("Assets") || text.FontFamily.Name.Contains("Symbol")) { text.Foreground = Brush("Icon"); return; }
            var t = Type(role ?? "Body");
            text.FontFamily = Font(role ?? "Body");
            text.FontSize = t.Size;
            text.Foreground = Brush(role == "MainButton" ? "Icon" : t.Color);
            text.FontWeight = (FontWeight)t.Weight;
            text.LineHeight = t.Size * t.LineHeight;
        }
        else if (control is TemplatedControl templated)
        {
            var resolvedRole = role ?? (control is TextBox ? "Input" : control is Button ? "Button" : "Body");
            var t = Type(resolvedRole);
            templated.FontFamily = Font(resolvedRole);
            templated.FontSize = t.Size;
            templated.Foreground = Brush(t.Color);
        }
    }
    public static void ApplyTree(Control root)
    {
        ApplyResources();
        ApplyControl(root);
        root.InvalidateVisual();
        foreach (var control in root.GetVisualDescendants().OfType<Control>()) { ApplyControl(control); control.InvalidateVisual(); }
    }
    private static void ApplyResources()
    {
        foreach (var (key, brush) in Brushes)
            brush.Color = Color.TryParse(Hex(key), out var color) ? color : Color.Parse(Hex("Text"));
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.Resources["Theme.PopupShadow"] = BoxShadows.Parse("0 4 12 0 " + Hex("Shadow"));
        app.Resources["Theme.ButtonCorner"] = new CornerRadius(Metric("ButtonRadius"));
        app.Resources["Theme.PanelCorner"] = new CornerRadius(Metric("PanelRadius"));
        foreach (var (key, value) in Current.Colors) app.Resources["Theme." + key] = Brush(key);
        foreach (var (key, value) in Current.Metrics) app.Resources["Theme." + key] = value;
        foreach (var (key, value) in Current.Text)
        {
            app.Resources["Theme." + key + ".Font"] = Font(key);
            app.Resources["Theme." + key + ".Size"] = value.Size;
            app.Resources["Theme." + key + ".Color"] = Brush(value.Color);
        }
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            app.Resources[key] = Color.Parse(Hex("Icon"));
        foreach (var key in new[] { "ThemeBackgroundBrush", "SystemControlBackgroundAltHighBrush", "TextControlBackground", "ComboBoxBackground", "FlyoutPresenterBackground", "MenuFlyoutPresenterBackground" })
            app.Resources[key] = Brush("Surface");
        foreach (var key in new[] { "ThemeForegroundBrush", "SystemControlForegroundBaseHighBrush", "TextControlForeground", "ComboBoxForeground" })
            app.Resources[key] = Brush("Text");
        app.Resources["TextControlBorderBrush"] = Brush("Border");
        app.Resources["TextControlBorderBrushPointerOver"] = Brush("Icon");
        app.Resources["TextControlBorderBrushFocused"] = Brush("Icon");
    }
}
