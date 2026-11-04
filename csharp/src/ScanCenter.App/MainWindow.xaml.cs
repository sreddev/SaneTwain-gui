using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScanCenter.Core;
using WinForms = System.Windows.Forms;

namespace ScanCenter.App;

public sealed record SavedRow(string Name, string Folder, string Pages, string Size, string Time, string Path);
public sealed record TrayItem(BitmapSource Thumb, string Label);

public partial class MainWindow : Window
{
    public static readonly RoutedCommand ScanCommand = new();
    public static readonly RoutedCommand PreviewCommand = new();
    public static readonly RoutedCommand EscapeCommand = new();

    private const string IdleHint = "Place the document face down, top edge against the corner arrow.";

    private readonly AppSettings _s;
    private string _active;
    private DeviceInfo? _info;
    private bool? _connected;
    private readonly List<ScannedPage> _pages = new();
    private Profile? _session;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loading;
    private bool _feederSession;
    private Action? _errorAction;
    private readonly ObservableCollection<SavedRow> _saved = new();
    private readonly ObservableCollection<TrayItem> _tray = new();
    private readonly Dictionary<string, ToggleButton> _profileButtons = new();
    private Button? _addTile;
    private readonly DispatcherTimer _saveTimer;
    private WinForms.NotifyIcon? _notify;
    private bool _reallyQuit;
    private bool _toldAboutTray;
    private List<string> _lastSaved = new();

    // printing during one document
    private int _printed;
    private PrintDialog? _printDialog;
    private bool _printSkip;

    public MainWindow(AppSettings settings)
    {
        _s = settings;
        _active = _s.Profiles.Any(p => p.Key == _s.LastProfile) ? _s.LastProfile : _s.Profiles[0].Key;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

        CommandBindings.Add(new CommandBinding(ScanCommand, (_, _) => { if (ScanButton.IsEnabled) StartSession(); }));
        CommandBindings.Add(new CommandBinding(PreviewCommand, (_, _) => { if (PreviewButton.IsEnabled) _ = RunScanAsync(true); }));
        CommandBindings.Add(new CommandBinding(EscapeCommand, (_, _) => OnEscape()));

        SavedList.ItemsSource = _saved;
        TrayList.ItemsSource = _tray;
        foreach (var k in Catalog.FileKinds) TypeBox.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Kind });
        foreach (var a in Catalog.AfterSaveChoices) AfterBox.Items.Add(new ComboBoxItem { Content = a.Label, Tag = a.Value });

        WireSettings();
        WireCustom();
        RestorePlacement();
        Topmost = _s.TopMost;
        SetupNotifyIcon();

        Log($"Settings: {SettingsStore.FilePath}");
        if (SaneTwainIni.Exists && _s.ShareWithSaneTwain) Log($"Sharing connection settings with SaneTwain ({SaneTwainIni.FilePath})");
        RenderProfile();
        UpdateActions();
        Loaded += async (_, _) => await OnFirstShownAsync();
        if (!IsVisible) Dispatcher.BeginInvoke(async () => await OnFirstShownAsync());
    }

    private bool _started;

    private async Task OnFirstShownAsync()
    {
        if (_started) return;
        _started = true;
        if (string.IsNullOrWhiteSpace(_s.Host))
        {
            SetStatus("Enter your scanner server in Settings to start.", "err");
            ShowError("Connect to your scanner", "SaneTwain Scan Center talks to a scanner shared by saned on your network. " +
                      "Open Settings and enter the server's name or address.", "Open Settings", () => AppSettings_Click(this, new RoutedEventArgs()));
            SetDevice();
            return;
        }
        await ProbeAsync();
    }

    private Profile Prof => _s.Profiles.First(p => p.Key == _active);
    private ConnectionInfo Conn => new(_s.Host, _s.Port, _s.User, _s.Device);

    // ------------------------------------------------------------------ log & status
    public void Log(string msg)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private Brush OkBrush => Res("SystemFillColorSuccessBrush", Color.FromRgb(0x1d, 0x8a, 0x5a));
    private Brush BusyBrush => Res("SystemFillColorCautionBrush", Color.FromRgb(0xf2, 0xa3, 0x3a));
    private Brush ErrBrush => Res("SystemFillColorCriticalBrush", Color.FromRgb(0xc4, 0x35, 0x2d));
    private Brush MutedBrush => Res("TextFillColorTertiaryBrush", Colors.Gray);

    private void SetStatus(string msg, string state = "ok", double? progress = null)
    {
        StatusText.Text = msg;
        StatusText.ToolTip = msg;
        StatusDot.Fill = state switch { "busy" => BusyBrush, "err" => ErrBrush, _ => OkBrush };
        if (progress is double p)
        {
            StatusProgress.IsIndeterminate = p < 0;
            if (p >= 0) StatusProgress.Value = p;
        }
    }

    private void SetDevice()
    {
        var model = _info?.Devices.FirstOrDefault(d => d.Name == _s.Device)?.Label;
        DeviceText.Text = string.IsNullOrWhiteSpace(_s.Host) ? "No scanner server set"
            : $"{(model != null ? model + " · " : "")}{(_s.Device.Length > 0 ? _s.Device : "no scanner chosen")} · {_s.Host}:{_s.Port}";
        DeviceDot.Fill = _connected switch { true => OkBrush, false => ErrBrush, _ => MutedBrush };
        UpdateTrayIcon();
    }

    private void ShowError(string title, string body, string? actionLabel = null, Action? action = null)
    {
        ErrorTitle.Text = title;
        ErrorBody.Text = body;
        _errorAction = action;
        ErrorAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        ErrorAction.Content = actionLabel ?? "Retry";
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void ErrorAction_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.Visibility = Visibility.Collapsed;
        var a = _errorAction; _errorAction = null; a?.Invoke();
    }

    private void ErrorDismiss_Click(object sender, RoutedEventArgs e) => ErrorBar.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ scan buttons (tiles)
    private bool IsDark => Icons.IsDark(_s.Theme);

    private (SolidColorBrush Color, SolidColorBrush Tint) BrushesFor(Profile p)
    {
        var c = Icons.ColorFor(Icons.KeyFor(p), IsDark);
        var color = new SolidColorBrush(c);
        var tint = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
        color.Freeze(); tint.Freeze();
        return (color, tint);
    }

    private void BuildTiles()
    {
        bool hadFocus = _profileButtons.Values.Any(b => b.IsKeyboardFocusWithin);
        TileList.Children.Clear();
        _profileButtons.Clear();
        var mono = (FontFamily)FindResource("MonoFont");
        var iconFont = (FontFamily)FindResource("IconFont");
        bool enabled = !_busy && _pages.Count == 0;
        foreach (var p in _s.Profiles)
        {
            var key = p.Key;
            var (color, tint) = BrushesFor(p);
            var label = new TextBlock { Text = p.Label.StartsWith("Scan to ") ? p.Label[8..] : p.Label, FontWeight = FontWeights.SemiBold,
                FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var sub = new TextBlock { Text = TileSubtitle(p), FontSize = 10.5, FontFamily = mono, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Icons.Glyph(Icons.KeyFor(p), color, 22));
            stack.Children.Add(label);
            stack.Children.Add(sub);
            var tile = new ToggleButton
            {
                Style = (Style)FindResource("ScanTile"), Foreground = color, Tag = tint, IsChecked = key == _active, IsEnabled = enabled,
                ToolTip = $"{p.Label}\n{(p.After == AfterSave.Print && !p.SaveFile ? "Prints without saving a file" : p.Folder)}",
            };
            AutomationProperties.SetName(tile, p.Label);
            var go = new Button
            {
                Width = 22, Height = 22, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = color, BorderThickness = new Thickness(0), ToolTip = $"Scan now with {p.Label}",
                Content = new TextBlock { Text = "\uE768", FontFamily = iconFont, FontSize = 10, Foreground = Brushes.Black },
            };
            AutomationProperties.SetName(go, $"Scan now with {p.Label}");
            go.SetBinding(VisibilityProperty, new Binding(nameof(IsMouseOver)) { Source = tile, Converter = (IValueConverter)FindResource("BoolToVis") });
            go.Click += (_, e) =>
            {
                e.Handled = true;
                if (_busy || _pages.Count > 0) return;
                SelectProfile(key);
                StartSession();
            };
            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(go);
            tile.Content = grid;
            tile.Click += (_, _) => SelectProfile(key);
            TileList.Children.Add(tile);
            _profileButtons[key] = tile;
        }
        var plusText = new TextBlock { Text = "Custom", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        plusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var plusStack = new StackPanel();
        plusStack.Children.Add(Icons.Glyph("plus", (Brush)FindResource("TextFillColorSecondaryBrush"), 22));
        plusStack.Children.Add(plusText);
        _addTile = new Button { Style = (Style)FindResource("AddTile"), Content = plusStack, IsEnabled = enabled,
            ToolTip = "Add your own scan button, starting from the settings of the chosen one" };
        AutomationProperties.SetName(_addTile, "Add a custom scan button");
        _addTile.Click += AddCustom_Click;
        TileList.Children.Add(_addTile);
        if (hadFocus && _profileButtons.TryGetValue(_active, out var act)) act.Focus();
    }

    private static string TileSubtitle(Profile p)
    {
        if (p.After == AfterSave.Print && !p.SaveFile) return "prints";
        var f = p.Folder.TrimEnd('\\', '/');
        var name = Path.GetFileName(f);
        return name.Length > 0 ? name : f;
    }

    private void SelectProfile(string key)
    {
        if (_busy || _pages.Count > 0)
        {
            BuildTiles();
            SetStatus("Finish or discard the pages you're scanning before switching buttons.", "busy");
            return;
        }
        CommitName();
        _active = key;
        _s.LastProfile = key;
        RenderProfile();
        SaveSoon();
    }

    // ------------------------------------------------------------------ custom buttons
    private void WireCustom()
    {
        foreach (var (k, n) in Icons.Choices) IconBox.Items.Add(new ComboBoxItem { Content = n, Tag = k });
        NameBox.LostFocus += (_, _) => CommitName();
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        IconBox.SelectionChanged += (_, _) =>
        {
            if (_loading || IconBox.SelectedItem is not ComboBoxItem i || !Prof.IsCustom) return;
            Prof.IconKey = (string)i.Tag;
            RenderProfile();
            SaveSoon();
        };
    }

    private void CommitName()
    {
        if (!_s.Profiles.Any(x => x.Key == _active)) return;
        var p = Prof;
        if (!p.IsCustom) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name == p.Label) { NameBox.Text = p.Label; return; }
        p.Label = name;
        p.Tag = Profile.TagFromLabel(name);
        Log($"Renamed the scan button to \"{name}\"");
        RenderProfile();
        BuildTrayMenu();
        SaveSoon();
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pages.Count > 0) return;
        CommitName();
        int n = 1;
        while (_s.Profiles.Any(x => x.Label == $"Custom {n}")) n++;
        var label = $"Custom {n}";
        var p = Prof.Clone();
        p.Key = "custom-" + Guid.NewGuid().ToString("N")[..8];
        p.Label = label;
        p.Tag = Profile.TagFromLabel(label);
        p.IconKey = "custom";
        p.Folder = Path.Combine(_s.DefaultFolder, label);
        p.Pattern = "{profile}_{date}_{time}";
        if (p.After == AfterSave.Print) p.SaveFile = true;
        _s.Profiles.Add(p);
        _active = _s.LastProfile = p.Key;
        Log($"Added the scan button \"{label}\". It starts with the settings of the button that was chosen.");
        RenderProfile();
        BuildTrayMenu();
        SaveSettings();
        NameBox.Focus();
        NameBox.SelectAll();
    }

#if DEBUG
    public void DevAddCustom() => AddCustom_Click(this, new RoutedEventArgs());
#endif

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var p = Prof;
        if (!p.IsCustom || _busy || _pages.Count > 0) return;
        var r = MessageBox.Show(this, $"Delete the scan button \"{p.Label}\"? Files it already saved stay where they are.",
            "Delete scan button", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        _s.Profiles.Remove(p);
        _active = _s.LastProfile = _s.Profiles[0].Key;
        Log($"Deleted the scan button \"{p.Label}\"");
        RenderProfile();
        BuildTrayMenu();
        SaveSettings();
    }

    // ------------------------------------------------------------------ settings panel
    private void WireSettings()
    {
        CreateFolderBox.Click += (_, _) => Set(p => p.CreateFolder = CreateFolderBox.IsChecked == true);
        PatternBox.TextChanged += (_, _) => Set(p => p.Pattern = PatternBox.Text.Length > 0 ? PatternBox.Text : "Scan_{n}");
        TypeBox.SelectionChanged += (_, _) => { if (TypeBox.SelectedItem is ComboBoxItem i) Set(p => p.Kind = (FileKind)i.Tag); };
        ResBox.SelectionChanged += (_, _) => { if (ResBox.SelectedItem is ComboBoxItem i) Set(p => p.Resolution = (int)i.Tag); };
        SizeBox.SelectionChanged += (_, _) => { if (SizeBox.SelectedItem is ComboBoxItem i) Set(p => p.Size = (string)i.Tag); };
        SourceBox.SelectionChanged += (_, _) => { if (SourceBox.SelectedItem is ComboBoxItem i) Set(p => p.Source = (string)i.Tag); };
        ModeBox.SelectionChanged += (_, _) =>
        {
            if (ModeBox.SelectedItem is ComboBoxItem i) Set(p => { p.Mode = (string)i.Tag; p.ColorKind = Scanner.ColorKindOf(p.Mode); });
        };
        BrightnessSlider.ValueChanged += (_, _) => Set(p => p.Brightness = (int)BrightnessSlider.Value);
        ContrastSlider.ValueChanged += (_, _) => Set(p => p.Contrast = (int)ContrastSlider.Value);
        MultiBox.Click += (_, _) => Set(p => p.AskMorePages = MultiBox.IsChecked == true);
        AfterBox.SelectionChanged += (_, _) => { if (AfterBox.SelectedItem is ComboBoxItem i) Set(p => p.After = (AfterSave)i.Tag); };
        SaveFileBox.Click += (_, _) => Set(p => p.SaveFile = SaveFileBox.IsChecked == true);
        WriteToDiskBox.Click += (_, _) => Set(p => p.WriteToDisk = WriteToDiskBox.IsChecked == true);
    }

    private void Set(Action<Profile> change)
    {
        if (_loading) return;
        change(Prof);
        RefreshDerived();
        SaveSoon();
    }

    private static void Select(ComboBox box, object value)
    {
        foreach (var o in box.Items)
            if (o is ComboBoxItem i && Equals(i.Tag, value)) { box.SelectedItem = i; return; }
        box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
    }

    private void RenderProfile()
    {
        _loading = true;
        var p = Prof;
        BuildTiles();
        var (color, tint) = BrushesFor(p);
        SideTitle.Text = p.Label;
        SideIcon.Content = Icons.Glyph(Icons.KeyFor(p), color, 22);
        SideIconBox.Background = tint;
        CustomPanel.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Visibility = p.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        if (!NameBox.IsKeyboardFocused) NameBox.Text = p.Label;
        Select(IconBox, Icons.KeyFor(p));
        ScanButton.Content = p.Label;
        FolderBox.Text = p.Folder;
        FolderBox.ToolTip = p.Folder;
        CreateFolderBox.IsChecked = p.CreateFolder;
        if (PatternBox.Text != p.Pattern) PatternBox.Text = p.Pattern;
        Select(TypeBox, p.Kind);

        ResBox.Items.Clear();
        var resolutions = (_info?.Resolutions.Count > 0 ? _info.Resolutions : Catalog.DefaultResolutions.ToList()).ToList();
        if (!resolutions.Contains(p.Resolution)) resolutions.Add(p.Resolution);
        foreach (var r in resolutions.OrderBy(x => x))
            ResBox.Items.Add(new ComboBoxItem { Content = r > 1200 ? $"{r} dpi (interpolated)" : $"{r} dpi", Tag = r });
        Select(ResBox, p.Resolution);

        SizeBox.Items.Clear();
        foreach (var d in Catalog.DocSizes)
        {
            bool fits = d.W <= 0 || _info == null || (d.W <= _info.MaxWidthMm + 0.5 && d.H <= _info.MaxHeightMm + 0.5);
            if (!fits) continue;
            var label = d.W <= 0 ? $"Full glass ({(_info?.MaxWidthMm ?? 215.9):0.#} × {(_info?.MaxHeightMm ?? 297):0.#} mm)" : $"{d.Name} ({d.W:0.#} × {d.H:0.#} mm)";
            SizeBox.Items.Add(new ComboBoxItem { Content = label, Tag = d.Name });
        }
        Select(SizeBox, p.Size);

        var sources = _info?.Sources ?? new List<string>();
        SourceBox.Items.Clear();
        foreach (var s in sources) SourceBox.Items.Add(new ComboBoxItem { Content = s, Tag = s });
        SourceRow.Visibility = sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (sources.Count > 0) Select(SourceBox, sources.Contains(p.Source) ? p.Source : sources[0]);

        ModeBox.Items.Clear();
        var modes = _info?.Modes ?? new List<string>();
        if (modes.Count > 0)
        {
            foreach (var m in modes) ModeBox.Items.Add(new ComboBoxItem { Content = m, Tag = m });
            Select(ModeBox, Scanner.ResolveMode(p, modes));
        }
        else
        {
            foreach (var (tag, label) in new[] { ("color", "Color"), ("gray", "Gray"), ("lineart", "Black & white") })
                ModeBox.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
            Select(ModeBox, p.ColorKind);
        }
        BrightnessSlider.Value = p.Brightness;
        ContrastSlider.Value = p.Contrast;
        MultiBox.IsChecked = p.AskMorePages;
        Select(AfterBox, p.After);
        SaveFileBox.IsChecked = p.SaveFile;
        WriteToDiskBox.IsChecked = p.WriteToDisk;
        _loading = false;
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        var p = Prof;
        BrightnessValue.Text = p.Brightness == 0 ? "0" : p.Brightness.ToString("+0;-0", CultureInfo.InvariantCulture);
        ContrastValue.Text = p.Contrast == 0 ? "0" : p.Contrast.ToString("+0;-0", CultureInfo.InvariantCulture);
        var folder = Naming.ExpandFolder(p.Folder);
        bool exists = Directory.Exists(folder);
        var ext = Catalog.Info(p.Kind).Ext;
        var next = exists ? Naming.ChoosePath(folder, p, ext, DateTime.Now).Path : Path.Combine(folder, $"{Naming.Render(p.Pattern, p, 1, DateTime.Now)}.{ext}");
        NamePreview.Text = $"Next file: {Path.GetFileName(next)}";
        NamePreview.ToolTip = next;

        var mode = _info != null ? Scanner.ResolveMode(p, _info.Modes) : "";
        var (bOk, cOk) = _info != null && _info.Adjustable.TryGetValue(mode, out var adj) ? adj : (true, true);
        BrightnessRow.IsEnabled = bOk; ContrastRow.IsEnabled = cOk;
        var hide = _s.HideUnsupported;
        BrightnessRow.Visibility = bOk || !hide ? Visibility.Visible : Visibility.Collapsed;
        ContrastRow.Visibility = cOk || !hide ? Visibility.Visible : Visibility.Collapsed;
        AdjustNote.Text = $"The scanner sets brightness and contrast itself in {mode}.";
        AdjustNote.Visibility = (!bOk || !cOk) && !hide && mode.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!bOk && cOk) AdjustNote.Text = $"The scanner sets brightness itself in {mode}.";
        if (bOk && !cOk) AdjustNote.Text = $"The scanner sets contrast itself in {mode}.";

        var (w, h) = Scanner.SizeMm(p.Size, _info);
        double px = w / 25.4 * p.Resolution * (h / 25.4 * p.Resolution);
        double bpp = p.ColorKind == "color" ? 3 : p.ColorKind == "lineart" ? 1 / 8.0 : 1;
        var est = $"About {FormatSize((long)(px * bpp))} of image data per page ({(int)(w / 25.4 * p.Resolution)} × {(int)(h / 25.4 * p.Resolution)} px).";

        bool printing = p.After == AfterSave.Print;
        SaveFileBox.Visibility = printing ? Visibility.Visible : Visibility.Collapsed;
        bool savingOn = p.SaveFile || !printing;
        foreach (UIElement el in new UIElement[] { FolderBox, ChangeFolderButton, CreateFolderBox, PatternBox, TypeBox, NamePreview })
            el.IsEnabled = savingOn;
        MissingFolder.Visibility = !exists && savingOn ? Visibility.Visible : Visibility.Collapsed;
        if (!savingOn) NamePreview.Text = "No file is saved; pages only go to the printer.";
        WriteToDiskBox.IsEnabled = savingOn;
        WriteToDiskNote.Text = !savingOn ? "Not used: this button only prints."
            : p.WriteToDisk ? "The file is saved as soon as the scan ends."
            : "The pages wait in the tray until you click Save file.";
        if (printing)
        {
            var where = _s.Printer.Length > 0 ? _s.Printer : $"the Windows default printer ({Printing.DefaultPrinterName()})";
            est += $" Prints on {where}.";
        }
        Estimate.Text = est;
        StatusMeta.Text = GlassMeta.Text = $"{p.Size} · {p.Resolution} dpi · {(mode.Length > 0 ? mode : p.ColorKind)}";
    }

    public static string FormatSize(long n) => n >= 1048576 ? $"{n / 1048576.0:0.0} MB" : $"{Math.Max(1, n / 1024)} KB";

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        var tok = (string)((Button)sender).Tag;
        int at = PatternBox.CaretIndex;
        PatternBox.Text = PatternBox.Text.Insert(at, tok);
        PatternBox.CaretIndex = at + tok.Length;
        PatternBox.Focus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = Profile.Defaults(_s.DefaultFolder).FirstOrDefault(x => x.Key == _active);
        if (d == null) return;
        var i = _s.Profiles.FindIndex(x => x.Key == _active);
        _s.Profiles[i] = d;
        RenderProfile();
        SaveSettings();
        Log($"{d.Label}: settings reset to defaults");
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(Naming.ExpandFolder(Prof.Folder)) ? Prof.Folder : _s.DefaultFolder;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder for {Prof.Label}", InitialDirectory = Directory.Exists(start) ? start : "" };
        if (dlg.ShowDialog(this) == true)
        {
            Prof.Folder = dlg.FolderName;
            Log($"{Prof.Label}: files will be saved in {Prof.Folder}");
            RenderProfile();
            SaveSettings();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = Naming.ExpandFolder(Prof.Folder);
        if (Directory.Exists(f)) Shell.Open(f);
        else SetStatus($"{f} doesn't exist yet. It will be created when the first file is saved.", "busy");
    }

    // ------------------------------------------------------------------ settings persistence
    private void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"Couldn't save settings: {e.Message}"); }
    }

    private async void AppSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var old = (_s.Host, _s.Port, _s.User, _s.Device);
        var dlg = new SettingsWindow(_s, _info) { Owner = IsVisible ? this : null };
        if (dlg.ShowDialog() != true) return;
        dlg.ApplyTo(_s);
        App.ApplyTheme(_s.Theme);
        ApplyFrameTheme();
        Topmost = _s.TopMost;
        var startupProblem = await StartupRegistration.SetAsync(_s.StartWithWindows);
        if (startupProblem != null)
        {
            _s.StartWithWindows = await StartupRegistration.IsEnabledAsync();
            ShowError("Start with Windows", startupProblem);
        }
        SaveSettings();
        Log($"Saved app settings");
        ErrorBar.Visibility = Visibility.Collapsed;
        if (old != (_s.Host, _s.Port, _s.User, _s.Device) || _connected != true) await ProbeAsync();
        RenderProfile();
        SetDevice();
    }

    // ------------------------------------------------------------------ device
    private async Task ProbeAsync()
    {
        _connected = null;
        SetDevice();
        SetStatus($"Connecting to saned at {_s.Host}:{_s.Port}…", "busy", -1);
        try
        {
            var c = Conn;
            var info = await Task.Run(() => Scanner.Probe(c, false, m => Dispatcher.Invoke(() => Log(m))));
            if (info.Devices.Count == 0)
            {
                _connected = false;
                SetStatus("No scanner found", "err", 0);
                ShowError("No scanner found", $"saned at {_s.Host} is running but reports no scanners. Check that the scanner is switched on and connected to the server.",
                    "Try again", () => _ = ProbeAsync());
                SetDevice();
                return;
            }
            _connected = true;
            _info = info;
            if (_s.Device != info.Device)
            {
                _s.Device = info.Device;
                Log($"Using scanner {info.Device}");
                SaveSettings();
            }
            Platen.SetGlass(info.MaxWidthMm, info.MaxHeightMm);
            Log($"Connected to saned {info.ServerVersion} at {_s.Host}:{_s.Port}");
            ErrorBar.Visibility = Visibility.Collapsed;
            SetStatus("Ready", "ok", 0);
            RenderProfile();
            if (_s.PreviewOnStart && !_busy) await RunScanAsync(true);
        }
        catch (Exception e)
        {
            _connected = false;
            var msg = Scanner.FriendlyError(e, _s.Host, _s.Port);
            Log(msg);
            SetStatus("Scanner offline", "err", 0);
            ShowError("Scanner not reachable", msg + " Your settings and folders are kept.", "Retry", () => _ = ProbeAsync());
        }
        finally { SetDevice(); }
    }

    // ------------------------------------------------------------------ scanning
    private int MinRes => _info?.Resolutions.Count > 0 ? _info.Resolutions.Min() : 100;

    private void UpdateActions()
    {
        bool idle = !_busy, inSession = _pages.Count > 0;
        ScanButton.IsEnabled = idle && !inSession;
        PreviewButton.IsEnabled = idle && !inSession;
        AppSettingsButton.IsEnabled = idle && !inSession;
        CancelButton.Visibility = _busy && _cts != null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _profileButtons.Values) b.IsEnabled = idle && !inSession;
        if (_addTile != null) _addTile.IsEnabled = idle && !inSession;
        DeleteProfileButton.IsEnabled = ResetButton.IsEnabled = idle && !inSession;
        MoreYes.IsEnabled = MoreNo.IsEnabled = DiscardButton.IsEnabled = idle;
        UpdateTrayIcon();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => StartSession();
    private void Preview_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); SetStatus("Cancelling…", "busy"); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (!_busy) _ = RunScanAsync(false); }
    private void Finish_Click(object sender, RoutedEventArgs e) => _ = FinishSessionAsync();

    private void OnEscape()
    {
        if (_s.DisableEsc) return;
        if (_busy) _cts?.Cancel();
        else if (ErrorBar.Visibility == Visibility.Visible) ErrorBar.Visibility = Visibility.Collapsed;
    }

    public void StartSession()
    {
        if (_busy) return;
        _pages.Clear();
        _session = Prof.Clone();  // settings stay fixed for this document
        _printed = 0; _printDialog = null; _printSkip = false;
        _feederSession = _info != null && _session.Source.Length > 0 && DeviceInfo.IsFeeder(_session.Source);
        RenderTray();
        _ = RunScanAsync(false);
    }

    private async Task RunScanAsync(bool preview)
    {
        if (_busy) return;
        if (!preview && _session == null) return;
        var p = preview ? Prof.Clone() : _session!;
        ErrorBar.Visibility = MoreBar.Visibility = Visibility.Collapsed;
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateActions();
        var area = Scanner.SizeMm(p.Size, _info);
        Platen.Show(null, 0, area, scanning: true);
        Hint.Text = "Scanning. Keep the lid closed until the lamp stops.";
        string label = preview ? "Preview" : $"Page {_pages.Count + 1}";
        SetStatus($"{label}: connecting…", "busy", -1);

        var progress = new Progress<ScanProgress>(x =>
        {
            var lbl = preview ? "Preview" : $"Page {x.PageNumber}";
            if (x.Total > 0) SetStatus($"{lbl}: received {FormatSize(x.Bytes)} of {FormatSize(x.Total)}", "busy", Math.Min(1, (double)x.Bytes / x.Total));
            else SetStatus($"{lbl}: received {FormatSize(x.Bytes)}", "busy", -1);
            if (x.Partial != null) Platen.Show(x.Partial, x.Fraction, area, scanning: x.Fraction < 1);
        });
        int previewDpi = _s.PreviewUseMinimum ? MinRes : Math.Max(_s.PreviewResolution, MinRes);
        var c = Conn;
        var info = _info;
        var ct = _cts.Token;
        int first = _pages.Count + 1;
        bool failed = false, cancelled = false;
        try
        {
            await Task.Run(() => Scanner.Scan(c, p, info, preview, previewDpi, progress,
                m => Dispatcher.Invoke(() => Log(m)),
                pg => Dispatcher.Invoke(() => OnPage(pg, preview)), ct, first), ct);
            if (preview) { SetStatus("Preview ready. Nothing was saved.", "ok", 1); Log("Preview done"); }
        }
        catch (Exception e)
        {
            var msg = Scanner.FriendlyError(e, _s.Host, _s.Port);
            if (e is OperationCanceledException || msg == "Scan cancelled.")
            {
                cancelled = true;
                Log("Scan cancelled");
                SetStatus("Scan cancelled", "ok", 0);
            }
            else
            {
                failed = true;
                Log(msg);
                SetStatus("Scan failed", "err", 0);
                ShowError("The scan didn't finish", msg, "Try again", () => { if (preview) _ = RunScanAsync(true); else _ = RunScanAsync(false); });
            }
            if (Platen.Image != null) Platen.Show(Platen.Image, 1, area, scanning: false);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose(); _cts = null;
            Hint.Text = IdleHint;
            UpdateActions();
        }

        if (preview) return;
        if (_pages.Count == 0)
        {
            if (!failed) { _session = null; UpdateActions(); }
            return;
        }
        if (failed || cancelled || p.AskMorePages || WaitsForSave(p)) AskMore();
        else await FinishSessionAsync();
    }

    private void OnPage(ScannedPage pg, bool preview)
    {
        if (preview) return;
        _pages.Add(pg);
        RenderTray();
        if (_session!.After == AfterSave.Print && !_s.PrintWaitForAllPages) PrintPages(_pages.Skip(_printed).ToList(), _session);
    }

    /// <summary>"Write file to disk" is off: the scan ends with the pages waiting for Save file. Print-only buttons never wait.</summary>
    private static bool WaitsForSave(Profile p) => !p.WriteToDisk && !(p.After == AfterSave.Print && !p.SaveFile);

    private void AskMore()
    {
        int n = _pages.Count;
        var pagesText = $"{n} {(n == 1 ? "page" : "pages")}";
        bool waitSave = _session != null && WaitsForSave(_session);
        MoreTitle.Text = waitSave ? $"{pagesText} scanned, not saved yet" : $"{pagesText} scanned";
        MoreText.Text = _feederSession
            ? "Load more pages into the feeder and scan them, or finish to save the file now."
            : "Place the next page on the glass and scan it, or finish to save the file now.";
        if (waitSave)
            MoreText.Text = "Write file to disk is off for this button, so nothing has been saved yet. Click Save file to write it to "
                + $"{Naming.ExpandFolder(_session!.Folder)}, scan more pages, or discard them.";
        MoreYes.Content = _feederSession ? "Scan more pages" : "Scan next page";
        MoreNo.Content = waitSave ? $"Save file ({pagesText})" : $"Finish and save ({pagesText})";
        if (_session?.After == AfterSave.Print && !_session.SaveFile) MoreNo.Content = $"Finish ({pagesText})";
        // When saving is the expected next step, make Save file the main button.
        bool saveFirst = waitSave && _session?.AskMorePages == false;
        var accent = (Style)FindResource("AccentButtonStyle");
        if (saveFirst) { MoreYes.ClearValue(StyleProperty); MoreNo.Style = accent; }
        else { MoreYes.Style = accent; MoreNo.ClearValue(StyleProperty); }
        MoreBar.Visibility = Visibility.Visible;
        SetStatus(waitSave ? "Scan finished: click Save file to write it to disk" : "Waiting: scan the next page or finish", "ok", 1);
        UpdateActions();
        if (saveFirst) MoreNo.Focus(); else MoreYes.Focus();
        if (waitSave && (!IsVisible || WindowState == WindowState.Minimized))
            _notify?.ShowBalloonTip(5000, "Scan finished, not saved yet", $"{pagesText} waiting. Open Scan Center and click Save file.", WinForms.ToolTipIcon.Info);
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        int n = _pages.Count;
        _pages.Clear(); _session = null;
        MoreBar.Visibility = Visibility.Collapsed;
        RenderTray(); UpdateActions();
        Log($"Discarded {n} scanned {(n == 1 ? "page" : "pages")}");
        SetStatus("Pages discarded", "ok", 0);
    }

    private async Task FinishSessionAsync()
    {
        if (_pages.Count == 0 || _busy || _session == null) return;
        MoreBar.Visibility = Visibility.Collapsed;
        var p = _session;
        var pages = _pages.ToList();
        if (p.After == AfterSave.Print && !p.SaveFile)
        {
            PrintPages(pages.Skip(_printed).ToList(), p);
            EndSession();
            SetStatus($"Copy finished: {pages.Count} {(pages.Count == 1 ? "page" : "pages")}", "ok", 1);
            return;
        }
        _busy = true;
        UpdateActions();
        SetStatus("Saving…", "busy", -1);
        try
        {
            var opts = new SaveOptions(_s.JpegQuality, _s.OcrLanguage);
            var paths = await Task.Run(() => Saver.SaveAsync(pages, p, opts, m => Dispatcher.Invoke(() => { Log(m); SetStatus(m, "busy", -1); })));
            _busy = false;
            OnSaved(paths, p, pages);
        }
        catch (Exception e)
        {
            _busy = false;
            var msg = e is SaveException ? e.Message : $"Couldn't save the file: {e.Message}";
            Log(msg);
            SetStatus("Not saved", "err", 0);
            ShowError("The file wasn't saved", msg + " The scanned pages are kept, so you can fix this and save again.", "Save again", () =>
            {
                // pick up a folder change made in the panel meanwhile
                if (_session != null) { _session.Folder = Prof.Folder; _session.CreateFolder = Prof.CreateFolder; }
                _ = FinishSessionAsync();
            });
            AskMore();
        }
        finally { UpdateActions(); }
    }

    private void EndSession()
    {
        _pages.Clear();
        _session = null;
        RenderTray();
        UpdateActions();
    }

    private void OnSaved(List<string> paths, Profile p, List<ScannedPage> pages)
    {
        EndSession();
        _lastSaved = paths;
        foreach (var path in paths)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            _saved.Insert(0, new SavedRow(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "",
                (paths.Count == 1 ? pages.Count : 1).ToString(CultureInfo.CurrentCulture), FormatSize(size), DateTime.Now.ToString("HH:mm:ss"), path));
            Log($"Saved {path} ({FormatSize(size)})");
        }
        SavedList.SelectedIndex = 0;
        var first = paths[0];
        var more = paths.Count > 1 ? $" and {paths.Count - 1} more" : "";
        SetStatus($"Saved {first}{more}", "ok", 1);
        RefreshDerived();
        if (_s.Notify) _notify?.ShowBalloonTip(5000, "Scan saved", $"{Path.GetFileName(first)}{more}\n{Path.GetDirectoryName(first)}", WinForms.ToolTipIcon.Info);
        if (_s.Sound) System.Media.SystemSounds.Asterisk.Play();
        try
        {
            switch (p.After)
            {
                case AfterSave.ShowInFolder: Shell.ShowInFolder(first); break;
                case AfterSave.Open: Shell.Open(first); break;
                case AfterSave.Print: PrintPages(pages.Skip(_printed).ToList(), p); break;
                case AfterSave.Mail:
                    var hwnd = new WindowInteropHelper(this).Handle;
                    _ = Task.Run(() => Mail.Send(paths, hwnd)).ContinueWith(t =>
                    {
                        if (!t.Result) Dispatcher.Invoke(() =>
                        {
                            Log("No e-mail program answered, so the file is shown in its folder instead");
                            Shell.ShowInFolder(first);
                        });
                    }, TaskScheduler.Default);
                    break;
            }
        }
        catch (Exception e) { Log($"Couldn't finish the after-save step: {e.Message}"); }
    }

    // ------------------------------------------------------------------ printing
    private void PrintPages(List<ScannedPage> pages, Profile p)
    {
        if (pages.Count == 0 || _printSkip) return;
        if (_printDialog == null)
        {
            var (dlg, problem) = Printing.Prepare(_s.Printer, _s.PrintShowOptions);
            if (dlg == null)
            {
                _printSkip = true;
                if (problem.Length == 0) { Log("Printing cancelled; the scanned pages are kept"); return; }
                Log(problem);
                ShowError("Nothing was printed", problem);
                return;
            }
            _printDialog = dlg;
        }
        try
        {
            SetStatus($"Printing {pages.Count} {(pages.Count == 1 ? "page" : "pages")}…", "busy", -1);
            Printing.Print(_printDialog, pages, $"{p.Label} {DateTime.Now:yyyy-MM-dd HH:mm}");
            _printed += pages.Count;
            var name = _printDialog.PrintQueue?.FullName ?? "the printer";
            Log($"Sent {pages.Count} {(pages.Count == 1 ? "page" : "pages")} to {name}");
            SetStatus($"Sent to {name}", "ok", 1);
        }
        catch (Exception e)
        {
            _printSkip = true;
            Log($"Printing failed: {e.Message}");
            ShowError("Printing failed", $"Windows couldn't print: {e.Message} Check that the printer is on and connected.");
        }
    }

    // ------------------------------------------------------------------ tray & saved list
    private void RenderTray()
    {
        _tray.Clear();
        for (int i = 0; i < _pages.Count; i++) _tray.Add(new TrayItem(ImageConvert.Thumbnail(_pages[i].Image, 120), $"p{i + 1}"));
        bool has = _pages.Count > 0;
        TrayList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TrayEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemovePage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || TrayList.SelectedIndex < 0) return;
        _pages.RemoveAt(TrayList.SelectedIndex);
        RenderTray();
        if (_pages.Count > 0) AskMore();
        else Discard_Click(sender, e);
    }

    private SavedRow? SelectedSaved => SavedList.SelectedItem as SavedRow;

    private void SavedList_DoubleClick(object sender, MouseButtonEventArgs e) => SavedOpen_Click(sender, e);

    private void SavedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSaved is { } r)
        {
            if (File.Exists(r.Path)) Shell.Open(r.Path);
            else SetStatus("That file has been moved or deleted.", "err");
        }
    }

    private void SavedShow_Click(object sender, RoutedEventArgs e) { if (SelectedSaved is { } r) Shell.ShowInFolder(r.Path); }
    private void SavedCopy_Click(object sender, RoutedEventArgs e) { if (SelectedSaved is { } r) Clipboard.SetText(r.Path); }

    // ------------------------------------------------------------------ notification area
    private void SetupNotifyIcon()
    {
        _trayState = TrayState.Ready;
        _notify = new WinForms.NotifyIcon { Icon = TrayIcon.Create(_trayState), Text = "SaneTwain Scan Center", Visible = true };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        BuildTrayMenu();
        _notify.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Dispatcher.Invoke(BringToFront); };
        _notify.BalloonTipClicked += (_, _) => { if (_lastSaved.Count > 0) Shell.ShowInFolder(_lastSaved[0]); };
    }

    private TrayState _trayState;

    /// <summary>Keeps the tray icon's status dot in step: scanning (amber) or offline (red).</summary>
    private void UpdateTrayIcon(bool force = false)
    {
        if (_notify == null) return;
        var state = _busy && _cts != null ? TrayState.Scanning : _connected == false ? TrayState.Offline : TrayState.Ready;
        if (state == _trayState && !force) return;
        _trayState = state;
        var old = _notify.Icon;
        _notify.Icon = TrayIcon.Create(state);
        _notify.Text = state switch
        {
            TrayState.Scanning => "SaneTwain Scan Center: scanning",
            TrayState.Offline => "SaneTwain Scan Center: scanner offline",
            _ => "SaneTwain Scan Center",
        };
        old?.Dispose();
    }

    // The taskbar switched between light and dark: redraw the tray icon in the matching colour.
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { UpdateTrayIcon(force: true); ApplyFrameTheme(); });

    private void BuildTrayMenu()
    {
        if (_notify == null) return;
        var old = _notify.ContextMenuStrip;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open SaneTwain Scan Center", null, (_, _) => BringToFront());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        foreach (var p in _s.Profiles)
        {
            var key = p.Key;
            menu.Items.Add(p.Label, null, (_, _) => Dispatcher.Invoke(() =>
            {
                BringToFront();
                if (_busy || _pages.Count > 0) return;
                SelectProfile(key);
                StartSession();
            }));
        }
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(QuitApp));
        _notify.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = _s.TopMost;
    }

    private void QuitApp()
    {
        _reallyQuit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitName();
        if (_s.StartWithWindows && !_reallyQuit)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _notify?.ShowBalloonTip(5000, "SaneTwain Scan Center is still running",
                    "Click the icon next to the clock to open it. Right-click it to scan or to quit.", WinForms.ToolTipIcon.Info);
            }
            return;
        }
        _reallyQuit = false;
        if (_pages.Count > 0)
        {
            if (!IsVisible) Show();
            var r = MessageBox.Show(this, $"{_pages.Count} scanned pages haven't been saved yet. Save them before closing?",
                "Unsaved pages", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) { e.Cancel = true; _ = FinishSessionAsync(); return; }
        }
        _cts?.Cancel();
        SavePlacement();
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_notify != null) { _notify.Visible = false; _notify.Dispose(); }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void SavePlacement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _s.WindowPlacement = string.Join(";", new[] { b.Left, b.Top, b.Width, b.Height }.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture)))
                             + (WindowState == WindowState.Maximized ? ";max" : "");
    }

    private void RestorePlacement()
    {
        var parts = _s.WindowPlacement.Split(';');
        if (parts.Length < 4) return;
        if (!int.TryParse(parts[0], out var l) || !int.TryParse(parts[1], out var t) ||
            !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h)) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(l, t, w, h))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l; Top = t; Width = Math.Max(MinWidth, w); Height = Math.Max(MinHeight, h);
        if (parts.Length > 4 && parts[4] == "max") WindowState = WindowState.Maximized;
    }
}
