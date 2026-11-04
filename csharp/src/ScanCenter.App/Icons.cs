using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using ScanCenter.Core;

namespace ScanCenter.App;

/// <summary>
/// Outline icons for the scan buttons. They keep ControlCenter3's metaphors (folder, picture, "T" page,
/// envelope, printer) in a Windows 11 line style. Drawn on a 32 × 32 grid.
/// </summary>
public static class Icons
{
    public static readonly (string Key, string Name)[] Choices =
    {
        ("custom", "Custom"), ("file", "File"), ("image", "Image"), ("ocr", "OCR"), ("email", "E-mail"), ("copy", "Copy"),
    };

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["file"] = "M4,10 A2,2 0 0 1 6,8 H12 L14.5,11 H26 A2,2 0 0 1 28,13 V24 A2,2 0 0 1 26,26 H6 A2,2 0 0 1 4,24 Z M16,14.5 V21.5 M12.8,18.5 L16,21.7 L19.2,18.5",
        ["image"] = "M7,6 H25 A3,3 0 0 1 28,9 V23 A3,3 0 0 1 25,26 H7 A3,3 0 0 1 4,23 V9 A3,3 0 0 1 7,6 Z M9,12.5 A2.5,2.5 0 1 0 14,12.5 A2.5,2.5 0 1 0 9,12.5 Z M5,23 L12,16 L17,21 L20.5,17.5 L27,24",
        ["ocr"] = "M5,10 V7 A2,2 0 0 1 7,5 H10 M22,5 H25 A2,2 0 0 1 27,7 V10 M27,22 V25 A2,2 0 0 1 25,27 H22 M10,27 H7 A2,2 0 0 1 5,25 V22 M11,11 H21 M16,11 V22",
        ["email"] = "M6,7 H22 A2.5,2.5 0 0 1 24.5,9.5 V20.5 A2.5,2.5 0 0 1 22,23 H6 A2.5,2.5 0 0 1 3.5,20.5 V9.5 A2.5,2.5 0 0 1 6,7 Z M4.5,8.5 L14,15.5 L23.5,8.5 M22,25 H29 M26,22 L29,25 L26,28",
        ["copy"] = "M9,12 V5 H20 L23,8 V12 M6.5,12 H25.5 A2.5,2.5 0 0 1 28,14.5 V19.5 A2.5,2.5 0 0 1 25.5,22 H6.5 A2.5,2.5 0 0 1 4,19.5 V14.5 A2.5,2.5 0 0 1 6.5,12 Z M9,19 H23 V27 H9 Z M12.5,23 H19.5",
        ["custom"] = "M15,28 H8 A2,2 0 0 1 6,26 V6 A2,2 0 0 1 8,4 H19 L24,9 V15 M19,4 V9 H24 M23,19 V28 M18.5,23.5 H27.5",
        ["plus"] = "M16,8 V24 M8,16 H24",
    };

    // (dark theme, light theme)
    private static readonly Dictionary<string, (Color Dark, Color Light)> Tints = new()
    {
        ["file"] = (Color.FromRgb(0xF5, 0xC0, 0x4F), Color.FromRgb(0xC9, 0x8A, 0x0E)),
        ["image"] = (Color.FromRgb(0xD6, 0x8A, 0xF0), Color.FromRgb(0xA5, 0x45, 0xC7)),
        ["ocr"] = (Color.FromRgb(0x94, 0xAF, 0xD6), Color.FromRgb(0x4E, 0x6D, 0x99)),
        ["email"] = (Color.FromRgb(0x6D, 0xB6, 0xFF), Color.FromRgb(0x23, 0x7F, 0xD1)),
        ["copy"] = (Color.FromRgb(0x4F, 0xD1, 0xC2), Color.FromRgb(0x17, 0x8C, 0x80)),
        ["custom"] = (Color.FromRgb(0xF2, 0x8C, 0x7A), Color.FromRgb(0xC8, 0x55, 0x3F)),
    };

    public static string KeyFor(Profile p) =>
        p.IconKey is { Length: > 0 } k && Paths.ContainsKey(k) ? k : Paths.ContainsKey(p.Key) ? p.Key : "custom";

    public static Color ColorFor(string key, bool dark)
    {
        var t = Tints.TryGetValue(key, out var c) ? c : Tints["custom"];
        return dark ? t.Dark : t.Light;
    }

    public static FrameworkElement Glyph(string key, Brush stroke, double size, double thickness = 2)
    {
        var path = new Path
        {
            Data = Geometry.Parse(Paths.TryGetValue(key, out var d) ? d : Paths["custom"]),
            Stroke = stroke, StrokeThickness = thickness, Width = 32, Height = 32, Stretch = Stretch.None,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = false,
        };
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    /// <summary>Whether the app is currently drawn dark (resolves "Match Windows").</summary>
    public static bool IsDark(ThemeChoice t)
    {
        if (t == ThemeChoice.Dark) return true;
        if (t == ThemeChoice.Light) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }
}
