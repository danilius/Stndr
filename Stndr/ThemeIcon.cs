using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Stndr;

/// <summary>Resolution-independent, recolourable line icons.</summary>
public sealed class ThemeIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ThemeIcon, string>(nameof(Kind), "book");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["book"] = "M12 5 C8 2 4 3 2 4 L2 20 C6 18 9 19 12 21 C15 19 18 18 22 20 L22 4 C18 2 15 3 12 5 Z M12 5 L12 21 M5 6 L5 16 M19 6 L19 16",
        ["search"] = "M16 10 A6 6 0 1 1 4 10 A6 6 0 1 1 16 10 M15 15 L21 21",
        ["bookmark"] = "M6 3 L18 3 L18 21 L12 16 L6 21 Z",
        ["note"] = "M5 3 L19 3 Q21 3 21 5 L21 19 Q21 21 19 21 L5 21 Q3 21 3 19 L3 5 Q3 3 5 3 Z M7 8 L17 8 M7 12 L17 12 M7 16 L14 16",
        ["highlight"] = "M5 15 L15 3 L21 9 L11 19 Z M5 15 L3 22 L11 19 M12 6 L18 12",
        ["settings"] = "M9.78 4.73 L10.09 2.18 L13.91 2.18 L14.22 4.73 L15.57 5.29 L17.59 3.71 L20.29 6.41 L18.71 8.43 L19.27 9.78 L21.82 10.09 L21.82 13.91 L19.27 14.22 L18.71 15.57 L20.29 17.59 L17.59 20.29 L15.57 18.71 L14.22 19.27 L13.91 21.82 L10.09 21.82 L9.78 19.27 L8.43 18.71 L6.41 20.29 L3.71 17.59 L5.29 15.57 L4.73 14.22 L2.18 13.91 L2.18 10.09 L4.73 9.78 L5.29 8.43 L3.71 6.41 L6.41 3.71 L8.43 5.29 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12",
        ["list"] = "M3 5 L4 5 M8 5 L21 5 M3 12 L4 12 M8 12 L21 12 M3 19 L4 19 M8 19 L21 19",
        ["comment"] = "M21 11 C21 5 17 3 12 3 C7 3 3 6 3 11 C3 14 5 16 7 17 L5 22 L12 19 C17 19 21 16 21 11 Z",
        ["link"] = "M10 14 L14 10 M8 16 L6 18 A4 4 0 0 1 1 12 L7 6 A4 4 0 0 1 13 7 M16 8 L18 6 A4 4 0 0 1 23 12 L17 18 A4 4 0 0 1 11 17",
        ["scroll"] = "M5 5 L19 5 L19 19 L5 19 Z M5 3 C0 0 0 9 5 7 M19 3 C24 0 24 9 19 7 M5 17 C0 15 0 24 5 21 M19 17 C24 15 24 24 19 21",
        ["scales"] = "M12 2 L12 21 M7 21 L17 21 M3 7 L21 7 M6 7 L2 15 L10 15 Z M18 7 L14 15 L22 15 Z",
        ["leaf"] = "M3 21 C3 10 8 4 21 3 C20 17 13 20 6 18 M3 21 L16 8",
        ["light"] = "M8 17 C8 13 4 13 4 8 A8 8 0 0 1 20 8 C20 13 16 13 16 17 Z M9 21 L15 21",
        ["chevron"] = "M9 5 L16 12 L9 19",
        ["chevron-down"] = "M6 9 L12 15 L18 9",
        ["back"] = "M15 5 L8 12 L15 19",
        ["expand"] = "M3 9 L3 3 L9 3 M15 3 L21 3 L21 9 M21 15 L21 21 L15 21 M9 21 L3 21 L3 15",
        ["more"] = "M4 12 L5 12 M11 12 L12 12 M18 12 L19 12",
        ["dot"] = "M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12"
    };
    static ThemeIcon() { AffectsRender<ThemeIcon>(KindProperty); }
    public ThemeIcon() { Width = UiTheme.Metric("IconSize"); Height = Width; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(Kind == "dot" ? UiTheme.Brush("Muted") : null,
                new Pen(UiTheme.Brush("Icon"), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                Geometry.Parse(Paths.TryGetValue(Kind, out var path) ? path : Paths["book"]));
    }
}
