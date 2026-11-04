using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ScanCenter.Core;

namespace ScanCenter.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ScanCenter.SingleInstance";
    private static string PipeName => $"ScanCenter.Show.{Environment.UserName}";

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            SignalFirstInstance();
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();
        ApplyTheme(Settings.Theme);

        var win = new MainWindow(Settings);
        MainWindow = win;
        bool atStartup = StartupRegistration.LaunchedAtStartup(e.Args);
        if (atStartup && Settings.StartWithWindows)
            win.Log("Started with Windows; waiting in the notification area");
        else
            win.Show();

        _pipeCts = new CancellationTokenSource();
        _ = ListenForOtherInstancesAsync(win, _pipeCts.Token);
#if DEBUG
        DevSnapshot(win, e.Args);
#endif
    }

#if DEBUG
    /// <summary>Developer aid: --snapshot file.png seconds [--scan] [--settings] renders the window to a PNG and quits.</summary>
    private void DevSnapshot(MainWindow win, string[] args)
    {
        int i = Array.IndexOf(args, "--snapshot");
        if (i < 0 || i + 2 >= args.Length) return;
        var file = args[i + 1];
        var secs = double.Parse(args[i + 2], System.Globalization.CultureInfo.InvariantCulture);
        if (args.Contains("--add-custom")) win.Dispatcher.BeginInvoke(async () => { await Task.Delay(3000); win.DevAddCustom(); });
        if (args.Contains("--scan")) win.Dispatcher.BeginInvoke(async () => { await Task.Delay(4000); win.StartSession(); });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Window target = win;
            if (args.Contains("--settings"))
            {
                var sw = new SettingsWindow(Settings, null) { Owner = win };
                sw.Show();
                if (args.Contains("--printer-tab")) sw.Tabs.SelectedIndex = 4;
                sw.UpdateLayout();
                target = sw;
            }
            win.Dispatcher.BeginInvoke(() =>
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(target);
                var content = (System.Windows.FrameworkElement)target.Content;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Media.DrawingVisual();
                using (var dc = bg.RenderOpen())
                    dc.DrawRectangle(target.TryFindResource("SolidBackgroundFillColorBaseBrush") as System.Windows.Media.Brush
                                     ?? System.Windows.Media.Brushes.White, null,
                        new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                rtb.Render(bg);
                rtb.Render(content);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(file)) enc.Save(fs);
                if (args.Contains("--hittest")) File.WriteAllText(file + ".hittest.txt", win.DevHitTest());
                if (args.Contains("--hittest"))
                    foreach (var st in new[] { TrayState.Ready, TrayState.Scanning, TrayState.Offline })
                        using (var ic = TrayIcon.Create(st)) using (var b = ic.ToBitmap()) b.Save($"{file}.tray-{st}.png");
                Settings.StartWithWindows = false;
                Shutdown();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        timer.Start();
    }
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public static void ApplyTheme(ThemeChoice t)
    {
        Current.ThemeMode = t switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    private static void SignalFirstInstance()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(1000);
            c.WriteByte(1);
        }
        catch (Exception) { /* the other copy may be closing */ }
    }

    private static async Task ListenForOtherInstancesAsync(MainWindow win, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                await win.Dispatcher.InvokeAsync(win.BringToFront);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }
}
