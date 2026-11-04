using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Printing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using Microsoft.Win32;
using ScanCenter.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace ScanCenter.App;

/// <summary>
/// Start with Windows. Installed from the Store (MSIX) this uses the package's startup task, which users
/// can also switch in Settings > Apps > Startup; run unpackaged it uses the HKCU Run key.
/// </summary>
public static class StartupRegistration
{
    private const string TaskId = "ScanCenterStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "SaneTwain Scan Center";

    public static bool IsPackaged
    {
        get
        {
            try { return Package.Current != null; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static bool LaunchedAtStartup(string[] args)
    {
        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) return true;
        if (!IsPackaged) return false;
        try { return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask; }
        catch { return false; }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            try
            {
                var t = await StartupTask.GetAsync(TaskId);
                return t.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch { return false; }
        }
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) != null;
    }

    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static async Task<string?> SetAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var t = await StartupTask.GetAsync(TaskId);
            if (!enabled) { t.Disable(); return null; }
            var state = await t.RequestEnableAsync();
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser =>
                    "Starting with Windows was turned off in Windows Settings. Turn Scan Center on in Settings > Apps > Startup.",
                StartupTaskState.DisabledByPolicy => "Your organization doesn't allow apps to start with Windows on this PC.",
                _ => "Windows didn't turn on starting with Windows.",
            };
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(RunName, false);
        return null;
    }
}

public static class Printing
{
    public static List<string> PrinterNames()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                .Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception) { return new(); }
    }

    public static string DefaultPrinterName()
    {
        try { return LocalPrintServer.GetDefaultPrintQueue().FullName; }
        catch (Exception) { return ""; }
    }

    /// <summary>Sets up a print dialog for a printer. Returns (dialog, problem); problem is "" when cancelled by the user too.</summary>
    public static (PrintDialog? Dialog, string Problem) Prepare(string printerName, bool showOptions)
    {
        var dlg = new PrintDialog();
        try
        {
            if (printerName.Length > 0)
            {
                using var server = new LocalPrintServer();
                var q = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                    .FirstOrDefault(x => string.Equals(x.FullName, printerName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(x.Name, printerName, StringComparison.OrdinalIgnoreCase));
                if (q == null)
                    return (null, $"The printer \"{printerName}\" isn't installed on this PC. Choose another one in Settings > Printer.");
                dlg.PrintQueue = q;
                dlg.PrintTicket = q.DefaultPrintTicket;
            }
            else if (string.IsNullOrEmpty(DefaultPrinterName()))
                return (null, "Windows has no default printer. Choose a printer in Settings > Printer.");
        }
        catch (PrintSystemException e) { return (null, $"Windows couldn't open the printer: {e.Message}"); }

        if (showOptions && dlg.ShowDialog() != true) return (null, "");
        return (dlg, "");
    }

    /// <summary>Prints pages at their real size, scaled down only if they don't fit the paper.</summary>
    public static void Print(PrintDialog dlg, IReadOnlyList<ScannedPage> pages, string title)
    {
        var caps = dlg.PrintQueue.GetPrintCapabilities(dlg.PrintTicket);
        double ox = caps.PageImageableArea?.OriginWidth ?? 0, oy = caps.PageImageableArea?.OriginHeight ?? 0;
        double aw = caps.PageImageableArea?.ExtentWidth ?? dlg.PrintableAreaWidth;
        double ah = caps.PageImageableArea?.ExtentHeight ?? dlg.PrintableAreaHeight;
        double pw = dlg.PrintableAreaWidth, ph = dlg.PrintableAreaHeight;
        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(pw, ph);
        foreach (var pg in pages)
        {
            double w = pg.Image.PixelWidth * 96.0 / pg.Dpi, h = pg.Image.PixelHeight * 96.0 / pg.Dpi;
            double k = Math.Min(1.0, Math.Min(aw / w, ah / h));
            var fp = new FixedPage { Width = pw, Height = ph };
            var img = new Image { Source = pg.Image, Width = w * k, Height = h * k, Stretch = System.Windows.Media.Stretch.Fill };
            FixedPage.SetLeft(img, ox);
            FixedPage.SetTop(img, oy);
            fp.Children.Add(img);
            var pc = new PageContent();
            ((IAddChild)pc).AddChild(fp);
            doc.Pages.Add(pc);
        }
        dlg.PrintDocument(doc.DocumentPaginator, title);
    }
}

public static class Shell
{
    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
