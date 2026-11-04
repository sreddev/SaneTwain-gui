using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace ScanCenter.Core;

public sealed class SaveException : Exception
{
    public SaveException(string message) : base(message) { }
}

public sealed record SaveOptions(int JpegQuality, string OcrLanguage);

public static class Saver
{
    static Saver()
    {
        try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch (InvalidOperationException) { }
    }

    public static async Task<List<string>> SaveAsync(IReadOnlyList<ScannedPage> pages, Profile p, SaveOptions o,
        Action<string> log, CancellationToken ct = default)
    {
        if (pages.Count == 0) return new();
        var folder = Naming.ExpandFolder(p.Folder);
        if (!Directory.Exists(folder))
        {
            if (!p.CreateFolder)
                throw new SaveException($"The folder {folder} doesn't exist. Choose another folder, or turn on \"Create the folder if it's missing\".");
            Directory.CreateDirectory(folder);
            log($"Created folder {folder}");
        }

        var kind = p.Kind;
        var info = Catalog.Info(kind);
        Ocr.Engine? ocr = null;
        if (kind == FileKind.PdfOcr)
        {
            ocr = Ocr.Create(o.OcrLanguage, out var problem);
            if (ocr == null) { log(problem + " The file was saved as a normal PDF without searchable text."); kind = FileKind.Pdf; }
        }

        var groups = info.MultiPage ? new List<IReadOnlyList<ScannedPage>> { pages } : pages.Select(x => (IReadOnlyList<ScannedPage>)new[] { x }).ToList();
        var when = DateTime.Now;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var (path, note) = Naming.ChoosePath(folder, p, info.Ext, when, reserved);
            reserved.Add(path);
            if (note != null) log(note);
            var tmp = Path.Combine(folder, $".~{Path.GetFileNameWithoutExtension(path)}.partial.{info.Ext}");
            try
            {
                switch (kind)
                {
                    case FileKind.Pdf: WritePdf(g, tmp, o.JpegQuality, null, log); break;
                    case FileKind.PdfOcr:
                    {
                        var words = new List<IReadOnlyList<Ocr.Word>>();
                        for (int i = 0; i < g.Count; i++)
                        {
                            log($"Recognizing text on page {i + 1} of {g.Count} ({ocr!.Language})");
                            words.Add(await ocr.RecognizeAsync(g[i].Image).ConfigureAwait(false));
                        }
                        WritePdf(g, tmp, o.JpegQuality, words, log);
                        log($"Found {words.Sum(w => w.Count)} words");
                        break;
                    }
                    case FileKind.Tiff: WriteTiff(g, tmp); break;
                    case FileKind.Jpeg: WriteSingle(new JpegBitmapEncoder { QualityLevel = o.JpegQuality }, ToGrayIfBw(g[0].Image), tmp); break;
                    case FileKind.Png: WriteSingle(new PngBitmapEncoder(), g[0].Image, tmp); break;
                }
                File.Move(tmp, path);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new SaveException($"Windows refused to write {path}. Check that the folder isn't read-only. ({e.Message})");
            }
            catch (IOException e)
            {
                throw new SaveException($"Couldn't write {path}: {e.Message}");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            result.Add(path);
        }
        return result;
    }

    private static BitmapSource ToGrayIfBw(BitmapSource b)
    {
        if (b.Format != PixelFormats.BlackWhite) return b;
        var c = new FormatConvertedBitmap(b, PixelFormats.Gray8, null, 0);
        c.Freeze();
        return c;
    }

    private static void WriteSingle(BitmapEncoder enc, BitmapSource img, string path)
    {
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void WriteTiff(IReadOnlyList<ScannedPage> pages, string path)
    {
        var enc = new TiffBitmapEncoder
        {
            Compression = pages.All(x => x.Image.Format == PixelFormats.BlackWhite) ? TiffCompressOption.Ccitt4 : TiffCompressOption.Lzw,
        };
        foreach (var pg in pages) enc.Frames.Add(BitmapFrame.Create(pg.Image));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static MemoryStream Encode(BitmapSource img, int quality)
    {
        BitmapEncoder enc = img.Format == PixelFormats.BlackWhite
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(img));
        var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        return ms;
    }

    private static void WritePdf(IReadOnlyList<ScannedPage> pages, string path, int quality,
        IReadOnlyList<IReadOnlyList<Ocr.Word>>? text, Action<string> log)
    {
        using var doc = new PdfDocument();
        doc.Info.Creator = "SaneTwain Scan Center";
        XFont? font = text != null ? new XFont("Arial", 10) : null;
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        for (int i = 0; i < pages.Count; i++)
        {
            var pg = pages[i];
            double wPt = pg.Image.PixelWidth * 72.0 / pg.Dpi, hPt = pg.Image.PixelHeight * 72.0 / pg.Dpi;
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(wPt);
            page.Height = XUnit.FromPoint(hPt);
            using var gfx = XGraphics.FromPdfPage(page);
            using (var ms = Encode(pg.Image, quality))
            using (var xi = XImage.FromStream(ms))
                gfx.DrawImage(xi, 0, 0, wPt, hPt);

            if (text != null && font != null)
            {
                double k = 72.0 / pg.Dpi; // pixels -> points
                foreach (var w in text[i])
                {
                    if (string.IsNullOrWhiteSpace(w.Text) || w.Width <= 0 || w.Height <= 0) continue;
                    double size = Math.Max(1, w.Height * k * 0.9);
                    var f = new XFont("Arial", size);
                    var measured = gfx.MeasureString(w.Text, f).Width;
                    if (measured <= 0) continue;
                    var state = gfx.Save();
                    gfx.TranslateTransform(w.X * k, (w.Y + w.Height) * k);
                    gfx.ScaleTransform(w.Width * k / measured, 1);
                    gfx.DrawString(w.Text, f, invisible, 0, -size * 0.2, XStringFormats.BaseLineLeft);
                    gfx.Restore(state);
                }
            }
        }
        doc.Save(path);
    }
}
