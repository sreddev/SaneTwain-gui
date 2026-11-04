using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScanCenter.App;

public enum TrayState { Ready, Scanning, Offline }

/// <summary>
/// Draws the notification-area icon (concept B): heavy capture corners in the taskbar's text colour
/// and a yellow scan line, plus a status dot. Drawn at the exact tray size so it stays sharp.
/// </summary>
public static class TrayIcon
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>Whether the taskbar is light (Settings > Personalization > Colors > Windows mode).</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    private static int TraySize()
    {
        try { return Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem())); }
        catch { return 16; }
    }

    public static Icon Create(TrayState state)
    {
        int n = TraySize();
        bool light = TaskbarIsLight();
        var fg = light ? Color.FromArgb(0x11, 0x11, 0x11) : Color.White;
        var accent = light ? Color.FromArgb(0x8F, 0x64, 0x00) : Color.FromArgb(0xFF, 0xD4, 0x00);
        float k = n / 16f;

        using var bmp = new Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using var pen = new Pen(fg, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float a = 1.6f * k, b = 14.4f * k, len = 3.8f * k, r = 1.3f * k;
            void Corner(float x, float y, float dx, float dy)
            {
                using var p = new GraphicsPath();
                p.AddLine(x, y + dy * len, x, y + dy * r);
                p.AddLine(x + dx * r, y, x + dx * len, y);
                g.DrawPath(pen, p);
            }
            Corner(a, a, 1, 1);
            Corner(b, a, -1, 1);
            Corner(b, b, -1, -1);
            Corner(a, b, 1, -1);
            using (var acc = new SolidBrush(accent))
            using (var bar = RoundRect(1f * k, 6.7f * k, 14f * k, 2.6f * k, 1.3f * k))
                g.FillPath(acc, bar);

            if (state != TrayState.Ready)
            {
                var dot = state == TrayState.Scanning ? Color.FromArgb(0xF5, 0xA6, 0x23) : Color.FromArgb(0xE5, 0x48, 0x3E);
                float d = 6.5f * k, x = n - d - 0.2f * k, y = n - d - 0.2f * k;
                using var ring = new SolidBrush(light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x1C, 0x1C, 0x1C));
                g.FillEllipse(ring, x - 1.2f * k, y - 1.2f * k, d + 2.4f * k, d + 2.4f * k);
                using var fill = new SolidBrush(dot);
                g.FillEllipse(fill, x, y, d, d);
            }
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
