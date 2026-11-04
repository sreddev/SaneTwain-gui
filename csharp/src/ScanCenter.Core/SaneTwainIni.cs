using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ScanCenter.Core;

/// <summary>
/// Shares connection, preview, JPEG and printer settings with SaneTwain through its
/// %USERPROFILE%\sanetwain.ini. Only keys SaneTwain already uses are written, in place,
/// so the rest of the file (and its layout) stays as it was.
/// </summary>
public static class SaneTwainIni
{
    /// <summary>SCANCENTER_SANETWAIN_INI overrides the file location (used for testing).</summary>
    public static string PathOverride { get; set; } = Environment.GetEnvironmentVariable("SCANCENTER_SANETWAIN_INI") ?? "";

    public static string FilePath => PathOverride.Length > 0 ? PathOverride
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sanetwain.ini");

    public static bool Exists => File.Exists(FilePath);

    private static readonly Encoding Ansi = CodePagesEncoding();

    private static Encoding CodePagesEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { return Encoding.Latin1; }
    }

    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? cur = null;
        foreach (var raw in File.ReadAllLines(path, Ansi))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1];
                if (!data.TryGetValue(name, out cur)) data[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (cur != null && eq > 0) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return data;
    }

    private static string? Get(Dictionary<string, Dictionary<string, string>> d, string sec, string key) =>
        d.TryGetValue(sec, out var s) && s.TryGetValue(key, out var v) ? v : null;

    private static bool Bool(string? v, bool dflt) => v == null ? dflt : v.Trim() is not ("0" or "" or "false");
    private static int Int(string? v, int dflt) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : dflt;

    public static void Import(AppSettings s)
    {
        var d = Read(FilePath);
        s.Host = Get(d, "Connection", "Host") ?? s.Host;
        s.Port = Int(Get(d, "Connection", "Port"), s.Port);
        s.User = Get(d, "Connection", "User") ?? s.User;
        s.Device = Get(d, "Connection", "Device") ?? s.Device;
        s.FindDevicesOnStart = Bool(Get(d, "Connection", "GetDevices"), s.FindDevicesOnStart);
        s.PreviewResolution = Int(Get(d, "Preview", "Resolution"), s.PreviewResolution);
        s.PreviewUseMinimum = Bool(Get(d, "Preview", "UseMinimum"), s.PreviewUseMinimum);
        s.PreviewOnStart = Bool(Get(d, "Preview", "AutoStart"), s.PreviewOnStart);
        s.TopMost = Bool(Get(d, "General", "TopMost"), s.TopMost);
        s.DisableEsc = Bool(Get(d, "General", "DisableEsc"), s.DisableEsc);
        s.HideUnsupported = Bool(Get(d, "General", "HideDisabled"), s.HideUnsupported);
        s.JpegQuality = Math.Clamp(Int(Get(d, "Save", "JPEGQuality"), s.JpegQuality), 10, 100);
        s.Printer = Get(d, "Printer", "Default") ?? s.Printer;
        s.PrintShowOptions = Bool(Get(d, "Printer", "Setup"), s.PrintShowOptions);
        s.PrintWaitForAllPages = Bool(Get(d, "Printer", "Delayed"), s.PrintWaitForAllPages);
    }

    public static void Export(AppSettings s)
    {
        string b(bool v) => v ? "1" : "0";
        var values = new (string Sec, string Key, string Val)[]
        {
            ("Connection", "Host", s.Host), ("Connection", "Port", s.Port.ToString(CultureInfo.InvariantCulture)),
            ("Connection", "User", s.User), ("Connection", "Device", s.Device),
            ("Connection", "GetDevices", b(s.FindDevicesOnStart)),
            ("Preview", "Resolution", s.PreviewResolution.ToString(CultureInfo.InvariantCulture)),
            ("Preview", "UseMinimum", b(s.PreviewUseMinimum)), ("Preview", "AutoStart", b(s.PreviewOnStart)),
            ("General", "TopMost", b(s.TopMost)), ("General", "DisableEsc", b(s.DisableEsc)),
            ("General", "HideDisabled", b(s.HideUnsupported)),
            ("Save", "JPEGQuality", s.JpegQuality.ToString(CultureInfo.InvariantCulture)),
            ("Printer", "Default", s.Printer), ("Printer", "Setup", b(s.PrintShowOptions)),
            ("Printer", "Delayed", b(s.PrintWaitForAllPages)),
        };
        var path = FilePath;
        var backup = path + ".before-scancenter";
        if (!File.Exists(backup)) File.Copy(path, backup);
        var lines = File.ReadAllLines(path, Ansi).ToList();
        foreach (var (sec, key, val) in values) SetValue(lines, sec, key, val);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, Ansi);
        File.Move(tmp, path, true);
    }

    /// <summary>Updates key=value in a section in place; appends the key or section if missing.</summary>
    public static void SetValue(List<string> lines, string section, string key, string value)
    {
        int secStart = -1, secEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (secStart >= 0) { secEnd = i; break; }
                if (string.Equals(t[1..^1], section, StringComparison.OrdinalIgnoreCase)) secStart = i;
            }
        }
        if (secStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }
        int lastKey = secStart;
        for (int i = secStart + 1; i < secEnd; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t[0] is ';' or '#') continue;
            lastKey = i;
            int eq = t.IndexOf('=');
            if (eq > 0 && string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }
        lines.Insert(lastKey + 1, $"{key}={value}");
    }
}
