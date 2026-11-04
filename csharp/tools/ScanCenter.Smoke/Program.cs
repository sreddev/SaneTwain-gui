// Developer smoke test: probes saned, scans one strip, saves it in every format.
// Usage: ScanCenter.Smoke <host> <outFolder> [--no-scan]
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ScanCenter.Core;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--ocr-test") return OcrTest(args[1]);
        var host = args.Length > 0 ? args[0] : "192.168.254.1";
        var outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "scancenter-smoke");
        var c = new ConnectionInfo(host, 6566, "sauron", "");
        var info = Scanner.Probe(c, false, Console.WriteLine);
        Console.WriteLine($"saned {info.ServerVersion}; device {info.Device}; modes [{string.Join(", ", info.Modes)}]; " +
                          $"res [{string.Join(",", info.Resolutions)}]; sources [{string.Join(", ", info.Sources)}]; max {info.MaxWidthMm:0.#}x{info.MaxHeightMm:0.#} mm");
        foreach (var kv in info.Adjustable) Console.WriteLine($"  {kv.Key}: brightness={kv.Value.Brightness} contrast={kv.Value.Contrast}");
        foreach (var kind in new[] { "color", "gray", "lineart" })
            Console.WriteLine($"  {kind} -> {Scanner.ResolveMode(new Profile { ColorKind = kind }, info.Modes)}");
        if (Array.IndexOf(args, "--no-scan") >= 0) return 0;

        var pages = new List<ScannedPage>();
        var p = new Profile { Key = "t", Tag = "Smoke", Folder = outDir, Pattern = "{profile}_{n}", Resolution = 150, ColorKind = "gray", Size = "A6" };
        int partials = 0;
        var progress = new Progress<ScanProgress>(x => { if (x.Partial != null) partials++; });
        Scanner.Scan(c with { Device = info.Device }, p, info, false, 100, progress, Console.WriteLine, pages.Add, CancellationToken.None);
        Console.WriteLine($"pages={pages.Count} size={pages[0].Image.PixelWidth}x{pages[0].Image.PixelHeight} fmt={pages[0].Image.Format} partials={partials}");
        pages.Add(pages[0]);
        foreach (FileKind k in Enum.GetValues(typeof(FileKind)))
        {
            p.Kind = k;
            var files = Saver.SaveAsync(pages, p, new SaveOptions(85, ""), m => Console.WriteLine("  log: " + m)).GetAwaiter().GetResult();
            foreach (var f in files) Console.WriteLine($"{k}: {Path.GetFileName(f)} {new FileInfo(f).Length / 1024} KB");
        }
        Console.WriteLine("OCR languages: " + string.Join(", ", Ocr.Languages()));
        return 0;
    }

    // Renders a page of text, saves it as a searchable PDF and prints what was recognized.
    static int OcrTest(string outDir)
    {
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, 1240, 1754));
            var tf = new System.Windows.Media.Typeface("Times New Roman");
            string[] lines = { "Invoice 2026-118", "Delivery of scanner parts to the office", "Total amount due: 412.50 EUR" };
            for (int i = 0; i < lines.Length; i++)
                dc.DrawText(new System.Windows.Media.FormattedText(lines[i], System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, tf, 40, System.Windows.Media.Brushes.Black, 1.0), new System.Windows.Point(120, 150 + i * 80));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1240, 1754, 150, 150, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        var gray = new System.Windows.Media.Imaging.FormatConvertedBitmap(rtb, System.Windows.Media.PixelFormats.Gray8, null, 0);
        gray.Freeze();
        var p = new Profile { Key = "o", Tag = "OcrTest", Folder = outDir, Pattern = "{profile}_{n}", Kind = FileKind.PdfOcr };
        var files = Saver.SaveAsync(new[] { new ScannedPage { Image = gray, Dpi = 150 } }, p, new SaveOptions(85, ""), m => Console.WriteLine("  log: " + m)).GetAwaiter().GetResult();
        Console.WriteLine(files[0]);
        return 0;
    }
}
