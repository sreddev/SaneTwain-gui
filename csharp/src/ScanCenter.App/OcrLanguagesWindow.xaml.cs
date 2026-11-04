using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScanCenter.Core;

namespace ScanCenter.App;

/// <summary>Installs and removes Windows OCR language packs (one UAC prompt per change).</summary>
public partial class OcrLanguagesWindow : Window
{
    private readonly List<(OcrPacks.Pack Pack, CheckBox Box, TextBlock State)> _rows = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _busy;

    /// <summary>True when the set of installed languages changed while the window was open.</summary>
    public bool Changed { get; private set; }

    public OcrLanguagesWindow()
    {
        InitializeComponent();
        Build();
        if (OcrPacks.RestartPending())
            Show("Windows is waiting for a restart to finish an earlier update. Installing languages usually fails until the PC is restarted.",
                 "SystemFillColorCautionBrush");
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        _poll.Tick += (_, _) => RefreshInstalled();
        Closing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "Windows is still installing the languages. It carries on in the background if you close this window; " +
                    "the new languages appear in Settings when it's done.\n\nClose anyway?", "OCR languages",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        Closed += (_, _) => _poll.Stop();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void Build()
    {
        List.Items.Clear();
        _rows.Clear();
        foreach (var pack in OcrPacks.All())
        {
            var state = new TextBlock { FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            state.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = pack.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(state);
            var box = new CheckBox { Content = label, IsChecked = pack.Installed, Margin = new Thickness(0, 2, 8, 2), ToolTip = pack.Tag };
            box.Click += (_, _) => UpdateSummary();
            _rows.Add((pack, box, state));
            List.Items.Add(box);
        }
        UpdateStates();
        UpdateSummary();
    }

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        foreach (var (pack, box, _) in _rows)
            box.Visibility = f.Length == 0 || pack.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                             || pack.Tag.Contains(f, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<string> ToAdd => _rows.Where(r => r.Box.IsChecked == true && !r.Pack.Installed).Select(r => r.Pack.Tag);
    private IEnumerable<string> ToRemove => _rows.Where(r => r.Box.IsChecked != true && r.Pack.Installed).Select(r => r.Pack.Tag);

    private void UpdateStates()
    {
        foreach (var (pack, _, state) in _rows) state.Text = pack.Installed ? "installed" : "";
    }

    private void UpdateSummary()
    {
        int add = ToAdd.Count(), remove = ToRemove.Count();
        int installed = _rows.Count(r => r.Pack.Installed);
        SummaryText.Text = $"{installed} installed";
        ApplyButton.Content = (add, remove) switch
        {
            (0, 0) => "Install",
            (_, 0) => $"Install {add}",
            (0, _) => $"Remove {remove}",
            _ => $"Install {add}, remove {remove}",
        };
        ApplyButton.IsEnabled = !_busy && add + remove > 0;
    }

    // Picks up packs as Windows finishes each one, so the list ticks over during a long download.
    private void RefreshInstalled()
    {
        var have = Ocr.Languages().Select(l => l.Tag).ToList();
        for (int i = 0; i < _rows.Count; i++)
        {
            var (pack, box, state) = _rows[i];
            bool now = OcrPacks.IsInstalled(pack.Tag, have);
            if (now == pack.Installed) continue;
            Changed = true;
            _rows[i] = (pack with { Installed = now }, box, state);
        }
        UpdateStates();
        UpdateSummary();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var add = ToAdd.ToList();
        var remove = ToRemove.ToList();
        if (add.Count + remove.Count == 0 || _busy) return;
        _busy = true;
        foreach (var r in _rows) r.Box.IsEnabled = false;
        ResultText.Visibility = Visibility.Collapsed;
        BusyText.Text = add.Count > 0
            ? $"Windows is downloading {Names(add)} from Windows Update. This can take a few minutes per language. You can keep using Scan Center meanwhile."
            : $"Windows is removing {Names(remove)}.";
        BusyPanel.Visibility = Visibility.Visible;
        UpdateSummary();
        _poll.Start();

        var result = await OcrPacks.ChangeAsync(add, remove);

        _poll.Stop();
        _busy = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        foreach (var r in _rows) r.Box.IsEnabled = true;
        RefreshInstalled();
        // Show what Windows actually has now, so a failed pack doesn't stay ticked.
        foreach (var (pack, box, _) in _rows) box.IsChecked = pack.Installed;
        UpdateSummary();

        var missing = add.Where(t => !_rows.First(r => r.Pack.Tag == t).Pack.Installed).ToList();
        if (result.Cancelled) Show("Nothing was changed: Windows didn't get administrator permission.", "TextFillColorSecondaryBrush");
        else if (result.Problem.Length > 0) Show($"Couldn't start the installation: {result.Problem}", "SystemFillColorCriticalBrush");
        else if (result.Failed > 0 || missing.Count > 0)
            Show($"Windows couldn't finish {(missing.Count > 0 ? Names(missing) : $"{result.Failed} of the changes")}. " +
                 (OcrPacks.RestartPending()
                     ? "Windows is waiting for a restart to finish an earlier update, and it won't add languages until then. Restart the PC and try again."
                     : "Check the internet connection and that Windows Update isn't paused, then try again."), "SystemFillColorCriticalBrush");
        else Show(add.Count > 0 ? $"Done. {Names(add)} can now be chosen as the OCR language." : "Done.", "SystemFillColorSuccessBrush");
    }

    private void Show(string text, string brush)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        ResultText.Visibility = Visibility.Visible;
    }

    private string Names(IEnumerable<string> tags)
    {
        var names = tags.Select(t => _rows.FirstOrDefault(r => r.Pack.Tag == t).Pack?.Name ?? t).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }
}
