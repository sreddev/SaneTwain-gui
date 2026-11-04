using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace ScanCenter.App;

/// <summary>
/// Immersive title bar: the app header acts as the window caption. The maximize button reports itself to
/// Windows as the real maximize button (HTMAXBUTTON) so Windows 11 shows snap layouts when you hover it.
/// </summary>
public partial class MainWindow
{
    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
        WM_NCMOUSELEAVE = 0x02A2, WM_MOUSEMOVE = 0x0200, HTMAXBUTTON = 9;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private bool _maxPressed;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src) src.AddHook(TitleBarHook);
        ApplyFrameTheme();
        StateChanged += (_, _) => UpdateMaximizedLayout();
        UpdateMaximizedLayout();
    }

    /// <summary>Dark or light window frame and rounded corners to match the app theme.</summary>
    public void ApplyFrameTheme()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int dark = Icons.IsDark(_s.Theme) ? 1 : 0;
        int round = DWMWCP_ROUND;
        try
        {
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch (DllNotFoundException) { }
    }

    private void UpdateMaximizedLayout()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaxButton, max ? "Restore" : "Maximize");
        if (!max) { RootGrid.Margin = new Thickness(0); return; }
        // A maximized window extends past the screen by its frame; keep content inside the visible area.
        var h = new WindowInteropHelper(this).Handle;
        uint dpi = h != IntPtr.Zero ? GetDpiForWindow(h) : 96;
        int frame = GetSystemMetricsForDpi(32 /* SM_CXFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi);
        RootGrid.Margin = new Thickness(frame * 96.0 / dpi);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private bool OverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF)), y = unchecked((short)((lp >> 16) & 0xFFFF));
        var tl = MaxButton.PointToScreen(new Point(0, 0));
        var br = MaxButton.PointToScreen(new Point(MaxButton.ActualWidth, MaxButton.ActualHeight));
        return x >= tl.X && x < br.X && y >= tl.Y && y < br.Y;
    }

    private IntPtr TitleBarHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMaxButton(lParam))
                {
                    MaxButton.Tag = _maxPressed ? "pressed" : "hover";
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                if (MaxButton.Tag != null && !_maxPressed) MaxButton.Tag = null;
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                _maxPressed = true;
                MaxButton.Tag = "pressed";
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                if (_maxPressed) ToggleMaximize();
                _maxPressed = false;
                MaxButton.Tag = null;
                handled = true;
                break;
            case WM_NCMOUSEMOVE when wParam.ToInt32() != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
            case WM_MOUSEMOVE:
                if (MaxButton.Tag != null) { MaxButton.Tag = null; _maxPressed = false; }
                break;
        }
        return IntPtr.Zero;
    }

#if DEBUG
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Developer check: what Windows hears when it asks what's under the maximize button and the title.</summary>
    public string DevHitTest()
    {
        var h = new WindowInteropHelper(this).Handle;
        IntPtr At(FrameworkElement el, double fx, double fy)
        {
            var p = el.PointToScreen(new Point(el.ActualWidth * fx, el.ActualHeight * fy));
            long lp = ((long)(short)p.Y << 16) | (ushort)(short)p.X;
            return SendMessage(h, WM_NCHITTEST, IntPtr.Zero, new IntPtr(lp));
        }
        return $"max={At(MaxButton, .5, .5)} min={At(MinButton, .5, .5)} close={At(CloseButton, .5, .5)} " +
               $"settings={At(AppSettingsButton, .5, .5)} title={At(DeviceText, .5, .5)}";
    }
#endif
}
