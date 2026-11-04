using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ScanCenter.Core;

namespace ScanCenter.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings s, DeviceInfo? info)
    {
        InitializeComponent();
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = s.User;
        if (info != null)
            foreach (var d in info.Devices) DeviceBox.Items.Add(new ComboBoxItem { Content = d.Name, ToolTip = d.Label });
        DeviceBox.Text = s.Device;
        FindOnStartBox.IsChecked = s.FindDevicesOnStart;

        var res = (info?.Resolutions.Count > 0 ? info.Resolutions : Catalog.DefaultResolutions.ToList()).Where(r => r <= 300).ToList();
        if (!res.Contains(s.PreviewResolution)) res.Add(s.PreviewResolution);
        foreach (var r in res.OrderBy(x => x)) PreviewResBox.Items.Add(new ComboBoxItem { Content = $"{r} dpi", Tag = r });
        SelectTag(PreviewResBox, s.PreviewResolution);
        PreviewMinBox.IsChecked = s.PreviewUseMinimum;
        PreviewResBox.IsEnabled = !s.PreviewUseMinimum;
        PreviewMinBox.Click += (_, _) => PreviewResBox.IsEnabled = PreviewMinBox.IsChecked != true;
        PreviewStartBox.IsChecked = s.PreviewOnStart;

        RootBox.Text = s.DefaultFolder;
        JpegSlider.Value = s.JpegQuality;
        JpegValue.Text = s.JpegQuality.ToString(CultureInfo.InvariantCulture);
        JpegSlider.ValueChanged += (_, _) => JpegValue.Text = ((int)JpegSlider.Value).ToString(CultureInfo.InvariantCulture);
        OcrBox.Items.Add(new ComboBoxItem { Content = "Windows display language", Tag = "" });
        var langs = Ocr.Languages();
        foreach (var (tag, name) in langs) OcrBox.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        SelectTag(OcrBox, s.OcrLanguage);
        OcrNote.Text = langs.Count == 0
            ? "No text recognition language is installed. Add one in Windows Settings > Time & language > Language & region."
            : $"Installed in Windows: {string.Join(", ", langs.Select(l => l.Name))}. More can be added in Windows Settings > Time & language > Language & region.";
        SoundBox.IsChecked = s.Sound;
        NotifyBox.IsChecked = s.Notify;

        StartupBox.IsChecked = s.StartWithWindows;
        TopBox.IsChecked = s.TopMost;
        EscBox.IsChecked = s.DisableEsc;
        HideBox.IsChecked = s.HideUnsupported;
        ShareBox.IsChecked = s.ShareWithSaneTwain;
        ShareBox.Visibility = SaneTwainIni.Exists ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (t, label) in new[] { (ThemeChoice.System, "Match Windows"), (ThemeChoice.Light, "Light"), (ThemeChoice.Dark, "Dark") })
            ThemeBox.Items.Add(new ComboBoxItem { Content = label, Tag = t });
        SelectTag(ThemeBox, s.Theme);

        PrinterBox.Items.Add(new ComboBoxItem { Content = $"Windows default ({Printing.DefaultPrinterName()})", Tag = "" });
        var printers = Printing.PrinterNames();
        foreach (var p in printers) PrinterBox.Items.Add(new ComboBoxItem { Content = p, Tag = p });
        if (s.Printer.Length > 0 && !printers.Contains(s.Printer, StringComparer.OrdinalIgnoreCase))
            PrinterBox.Items.Add(new ComboBoxItem { Content = $"{s.Printer} (not installed)", Tag = s.Printer });
        SelectTag(PrinterBox, printers.FirstOrDefault(p => string.Equals(p, s.Printer, StringComparison.OrdinalIgnoreCase)) ?? s.Printer);
        PrintOptionsBox.IsChecked = s.PrintShowOptions;
        PrintWaitBox.IsChecked = s.PrintWaitForAllPages;

        Loaded += async (_, _) => StartupBox.IsChecked = await StartupRegistration.IsEnabledAsync() || s.StartWithWindows;
    }

    private static void SelectTag(ComboBox box, object value)
    {
        foreach (var o in box.Items)
            if (o is ComboBoxItem i && Equals(i.Tag, value)) { box.SelectedItem = i; return; }
        box.SelectedIndex = 0;
    }

    private static object? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

    private int Port => int.TryParse(PortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 6566;

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Default folder for scans" };
        if (System.IO.Directory.Exists(RootBox.Text)) dlg.InitialDirectory = RootBox.Text;
        if (dlg.ShowDialog(this) == true) RootBox.Text = dlg.FolderName;
    }

    private async Task ProbeAsync(bool listOnly)
    {
        FindButton.IsEnabled = TestButton.IsEnabled = false;
        TestResult.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            TestResult.Text = "Enter the server's name or address first.";
            FindButton.IsEnabled = TestButton.IsEnabled = true;
            return;
        }
        TestResult.Text = $"Connecting to {host}:{Port}…";
        var c = new ConnectionInfo(host, Port, UserBox.Text.Trim(), DeviceBox.Text.Trim());
        try
        {
            var info = await Task.Run(() => Scanner.Probe(c, listOnly));
            var cur = DeviceBox.Text;
            DeviceBox.Items.Clear();
            foreach (var d in info.Devices) DeviceBox.Items.Add(new ComboBoxItem { Content = d.Name, ToolTip = d.Label });
            DeviceBox.Text = info.Devices.Any(d => d.Name == cur) ? cur : info.Devices.FirstOrDefault()?.Name ?? "";
            var names = info.Devices.Count > 0 ? string.Join(", ", info.Devices.Select(d => d.Label)) : "none";
            TestResult.Text = $"Connected (saned {info.ServerVersion}). Scanners: {names}";
            TestResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        catch (Exception ex)
        {
            TestResult.Text = Scanner.FriendlyError(ex, host, Port);
            TestResult.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        finally { FindButton.IsEnabled = TestButton.IsEnabled = true; }
    }

    private async void Find_Click(object sender, RoutedEventArgs e) => await ProbeAsync(true);
    private async void Test_Click(object sender, RoutedEventArgs e) => await ProbeAsync(false);

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public void ApplyTo(AppSettings s)
    {
        s.Host = HostBox.Text.Trim();
        s.Port = Port;
        s.User = UserBox.Text.Trim();
        s.Device = DeviceBox.Text.Trim();
        s.FindDevicesOnStart = FindOnStartBox.IsChecked == true;
        s.PreviewResolution = TagOf(PreviewResBox) is int r ? r : s.PreviewResolution;
        s.PreviewUseMinimum = PreviewMinBox.IsChecked == true;
        s.PreviewOnStart = PreviewStartBox.IsChecked == true;
        if (RootBox.Text.Trim().Length > 0) s.DefaultFolder = RootBox.Text.Trim();
        s.JpegQuality = (int)JpegSlider.Value;
        s.OcrLanguage = TagOf(OcrBox) as string ?? "";
        s.Sound = SoundBox.IsChecked == true;
        s.Notify = NotifyBox.IsChecked == true;
        s.StartWithWindows = StartupBox.IsChecked == true;
        s.TopMost = TopBox.IsChecked == true;
        s.DisableEsc = EscBox.IsChecked == true;
        s.HideUnsupported = HideBox.IsChecked == true;
        s.ShareWithSaneTwain = ShareBox.IsChecked == true;
        s.Theme = TagOf(ThemeBox) is ThemeChoice t ? t : ThemeChoice.System;
        s.Printer = TagOf(PrinterBox) as string ?? "";
        s.PrintShowOptions = PrintOptionsBox.IsChecked == true;
        s.PrintWaitForAllPages = PrintWaitBox.IsChecked == true;
    }
}
