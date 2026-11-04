using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanCenter.Core.Sane;

namespace ScanCenter.Core;

public sealed class ScannedPage
{
    public required BitmapSource Image { get; init; }
    public required int Dpi { get; init; }
}

public sealed class ScanProgress
{
    public long Bytes { get; init; }
    public long Total { get; init; }
    public int PageNumber { get; init; }
    /// <summary>Downscaled image of the lines received so far (frozen).</summary>
    public BitmapSource? Partial { get; init; }
    public double Fraction { get; init; }
}

public sealed class DeviceInfo
{
    public List<SaneDevice> Devices { get; set; } = new();
    public string Device { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public List<string> Modes { get; set; } = new();
    public List<int> Resolutions { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public double MaxWidthMm { get; set; } = 215.9;
    public double MaxHeightMm { get; set; } = 297;
    /// <summary>Per mode: whether brightness and contrast can be set.</summary>
    public Dictionary<string, (bool Brightness, bool Contrast)> Adjustable { get; set; } = new();

    public static bool IsFeeder(string source) =>
        source.Contains("ADF", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("feeder", StringComparison.OrdinalIgnoreCase) ||
        source.Contains("duplex", StringComparison.OrdinalIgnoreCase);
}

public sealed record ConnectionInfo(string Host, int Port, string User, string Device);

public static class Scanner
{
    public static string FriendlyError(Exception e, string host, int port) => e switch
    {
        SaneException { Status: SaneStatus.Cancelled } => "Scan cancelled.",
        SaneException { Status: SaneStatus.DeviceBusy } => "The scanner is busy. If another program is scanning, wait for it to finish and try again.",
        SaneException { Status: SaneStatus.CoverOpen } => "The scanner cover is open. Close it and try again.",
        SaneException { Status: SaneStatus.Jammed } => "Paper is jammed in the document feeder. Clear it and try again.",
        SaneException { Status: SaneStatus.NoDocs } => "The document feeder is empty. Put the pages in the feeder and try again.",
        SaneException { Status: SaneStatus.AccessDenied } => "saned refused access. Check the user name in Settings and the server's saned.conf.",
        SaneException se => $"The scanner reported an error ({se.Message}).",
        TimeoutException => $"No answer from {host}:{port}. Check that the server is on and port {port} is open.",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"{host}:{port} refused the connection. Check that saned is running on the server.",
        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            $"Couldn't find a computer called \"{host}\". Check the server address in Settings.",
        SocketException se => $"Couldn't reach {host}:{port} ({se.Message}).",
        IOException => $"The connection to {host}:{port} was interrupted. Try again.",
        AggregateException ae when ae.InnerException != null => FriendlyError(ae.InnerException, host, port),
        _ => e.Message,
    };

    /// <summary>Maps a profile's colour choice onto the scanner's own mode names.</summary>
    public static string ResolveMode(Profile p, IReadOnlyList<string> modes)
    {
        if (modes.Count == 0) return p.Mode;
        if (p.Mode.Length > 0 && modes.Contains(p.Mode)) return p.Mode;
        string? Pick(params string[] words) => modes.FirstOrDefault(m => words.Any(w => m.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return p.ColorKind switch
        {
            "color" => modes.FirstOrDefault(m => m.Contains("24", StringComparison.Ordinal) && m.Contains("olor", StringComparison.OrdinalIgnoreCase))
                       ?? Pick("color", "colour") ?? modes[^1],
            "lineart" => Pick("lineart", "black", "binary", "halftone") ?? modes[0],
            _ => modes.FirstOrDefault(m => (m.Contains("gray", StringComparison.OrdinalIgnoreCase) || m.Contains("grey", StringComparison.OrdinalIgnoreCase))
                                           && !m.Contains("diffusion", StringComparison.OrdinalIgnoreCase)
                                           && !m.Contains("halftone", StringComparison.OrdinalIgnoreCase))
                 ?? Pick("gray", "grey") ?? modes[0],
        };
    }

    public static string ColorKindOf(string mode)
    {
        var m = mode.ToLowerInvariant();
        if (m.Contains("color") || m.Contains("colour")) return "color";
        if (m.Contains("gray") || m.Contains("grey")) return m.Contains("diffusion") || m.Contains("halftone") ? "lineart" : "gray";
        return "lineart";
    }

    public static (double W, double H) SizeMm(string size, DeviceInfo? info)
    {
        var s = Catalog.DocSizes.FirstOrDefault(d => d.Name == size);
        if (s.Name == null) s = Catalog.DocSizes[0];
        double maxW = info?.MaxWidthMm ?? 215.9, maxH = info?.MaxHeightMm ?? 297;
        if (s.W <= 0) return (maxW, maxH);
        return (Math.Min(s.W, maxW), Math.Min(s.H, maxH));
    }

    // ------------------------------------------------------------------ probe
    public static DeviceInfo Probe(ConnectionInfo c, bool listOnly, Action<string>? log = null)
    {
        var info = new DeviceInfo();
        using var sc = new SaneClient(c.Host, c.Port, c.User).Connect();
        info.ServerVersion = sc.ServerVersionText;
        info.Devices = sc.GetDevices();
        if (listOnly || info.Devices.Count == 0) return info;
        var dev = info.Devices.Any(d => d.Name == c.Device) ? c.Device : info.Devices[0].Name;
        info.Device = dev;
        sc.Open(dev);
        if (sc.Find("mode")?.StringList is { } modes) info.Modes = modes.ToList();
        if (sc.Find("source")?.StringList is { } sources) info.Sources = sources.ToList();
        var res = sc.Find("resolution");
        if (res?.WordList is { } words) info.Resolutions = words.Select(w => (int)Math.Round(w)).ToList();
        else if (res?.Range is { } r)
            info.Resolutions = new[] { 50, 75, 100, 150, 200, 300, 400, 600, 1200, 2400 }.Where(x => x >= r.Min && x <= r.Max).ToList();
        if (sc.Find("br-x")?.Range is { } rx) info.MaxWidthMm = rx.Max;
        if (sc.Find("br-y")?.Range is { } ry) info.MaxHeightMm = ry.Max;
        var current = sc.Get("mode") as string;
        foreach (var m in info.Modes)
        {
            sc.Set("mode", m);
            info.Adjustable[m] = (sc.Find("brightness")?.Active ?? false, sc.Find("contrast")?.Active ?? false);
        }
        if (current != null) sc.Set("mode", current);
        log?.Invoke($"Read the capabilities of {dev}: {info.Modes.Count} scan types, {info.Resolutions.Count} resolutions" +
                    (info.Sources.Count > 1 ? $", sources: {string.Join(", ", info.Sources)}" : ""));
        return info;
    }

    // ------------------------------------------------------------------ scanning
    /// <summary>
    /// Scans with a profile's settings. From the glass this is one page; from a document feeder it's every
    /// page in the feeder. Each page is handed to <paramref name="onPage"/> as soon as it's complete.
    /// </summary>
    public static int Scan(ConnectionInfo c, Profile p, DeviceInfo? info, bool preview, int previewDpi,
        IProgress<ScanProgress> progress, Action<string> log, Action<ScannedPage> onPage, CancellationToken ct,
        int firstPageNumber = 1)
    {
        log($"Connecting to saned at {c.Host}:{c.Port}");
        using var sc = new SaneClient(c.Host, c.Port, c.User).Connect();
        var dev = c.Device;
        if (string.IsNullOrEmpty(dev))
        {
            var devs = sc.GetDevices();
            if (devs.Count == 0)
                throw new InvalidOperationException("saned is running but reports no scanners. Check that the scanner is on and connected to the server.");
            dev = devs[0].Name;
        }
        sc.Open(dev);
        log($"Opened {dev}");

        var modes = sc.Find("mode")?.StringList ?? Array.Empty<string>();
        var mode = ResolveMode(p, modes);
        var applied = new List<string>();
        if (mode.Length > 0 && sc.Set("mode", mode) is string m) applied.Add($"mode={m}");
        var feeder = false;
        if (!preview && p.Source.Length > 0 && sc.Set("source", p.Source) is string src)
        {
            applied.Add($"source={src}");
            feeder = DeviceInfo.IsFeeder(src);
        }
        int dpi = preview ? previewDpi : p.Resolution;
        var gotDpi = sc.Set("resolution", dpi);
        if (gotDpi is double gd) dpi = (int)Math.Round(gd);
        applied.Add($"resolution={dpi}");
        var (w, h) = SizeMm(p.Size, info);
        sc.Set("tl-x", 0.0); sc.Set("tl-y", 0.0); sc.Set("br-x", w); sc.Set("br-y", h);
        applied.Add($"area={w:0.#}×{h:0.#} mm");
        foreach (var (name, val) in new[] { ("brightness", p.Brightness), ("contrast", p.Contrast) })
        {
            var o = sc.Find(name);
            if (o is { Active: true }) { sc.Set(name, val); applied.Add($"{name}={val}"); }
            else if (val != 0) log($"{char.ToUpper(name[0])}{name[1..]} isn't available in {mode}, so it was left to the scanner");
        }
        log("Settings: " + string.Join(", ", applied));

        int pages = 0;
        try
        {
            while (true)
            {
                int pageNo = firstPageNumber + pages;
                var est = sc.GetParameters();
                long total = est.Lines > 0 ? (long)est.BytesPerLine * est.Lines : 0;
                long lastEmit = 0;
                log(feeder ? $"Scanning page {pageNo} from the feeder…" : "Scanning…");
                var t0 = DateTime.UtcNow;
                (SaneParameters par, byte[] data) img;
                try
                {
                    img = sc.ReadImage((buf, used, par) =>
                    {
                        long now = Environment.TickCount64;
                        if (now - lastEmit < 250) { progress.Report(new ScanProgress { Bytes = used, Total = total, PageNumber = pageNo }); return; }
                        lastEmit = now;
                        int lines = used / Math.Max(1, par.BytesPerLine);
                        BitmapSource? part = lines > 2 ? ImageConvert.Thumbnail(ImageConvert.ToBitmap(par, buf, lines, dpi), 900) : null;
                        progress.Report(new ScanProgress
                        {
                            Bytes = used, Total = total, PageNumber = pageNo, Partial = part,
                            Fraction = par.Lines > 0 ? (double)lines / par.Lines : 0,
                        });
                    }, ct);
                }
                catch (SaneException e) when (feeder && pages > 0 && e.Status == SaneStatus.NoDocs)
                {
                    log($"The feeder is empty after {pages} {(pages == 1 ? "page" : "pages")}");
                    break;
                }
                var bmp = ImageConvert.ToBitmap(img.par, img.data, null, dpi);
                progress.Report(new ScanProgress { Bytes = img.data.Length, Total = img.data.Length, PageNumber = pageNo,
                    Partial = ImageConvert.Thumbnail(bmp, 900), Fraction = 1 });
                log($"Page {pageNo} received: {bmp.PixelWidth} × {bmp.PixelHeight} px at {dpi} dpi, " +
                    $"{img.data.Length / 1048576.0:0.0} MB in {(DateTime.UtcNow - t0).TotalSeconds:0} s");
                onPage(new ScannedPage { Image = bmp, Dpi = dpi });
                pages++;
                if (!feeder) break;
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            sc.Cancel();
        }
        return pages;
    }
}

public static class ImageConvert
{
    /// <summary>Turns raw SANE frame data into a frozen WPF bitmap.</summary>
    public static BitmapSource ToBitmap(SaneParameters p, byte[] data, int? lines, int dpi)
    {
        int h = lines ?? data.Length / Math.Max(1, p.BytesPerLine);
        int w = p.PixelsPerLine;
        int bpl = p.BytesPerLine;
        BitmapSource bmp;
        if (p.Depth == 1)
        {
            // SANE: 1 = black. WPF BlackWhite: 1 = white. Invert.
            var buf = new byte[bpl * h];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)~data[i];
            bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.BlackWhite, null, buf, bpl);
        }
        else if (p.Format == SaneFrame.Rgb)
        {
            if (p.Depth == 16) { data = HighBytes(data, bpl * h); bpl /= 2; }
            bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Rgb24, null, data, bpl);
        }
        else
        {
            if (p.Depth == 16) { data = HighBytes(data, bpl * h); bpl /= 2; }
            bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Gray8, null, data, bpl);
        }
        bmp.Freeze();
        return bmp;
    }

    private static byte[] HighBytes(byte[] d, int len)
    {
        var o = new byte[len / 2];
        for (int i = 0; i < o.Length; i++) o[i] = d[2 * i];
        return o;
    }

    public static BitmapSource Thumbnail(BitmapSource src, int maxWidth)
    {
        if (src.PixelWidth <= maxWidth) return src;
        double k = (double)maxWidth / src.PixelWidth;
        BitmapSource s = src.Format == PixelFormats.BlackWhite ? new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0) : src;
        var t = new TransformedBitmap(s, new ScaleTransform(k, k));
        var wb = new WriteableBitmap(t);
        wb.Freeze();
        return wb;
    }
}
