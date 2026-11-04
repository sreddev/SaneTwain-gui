using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanCenter.Core;

public enum FileKind { Pdf, PdfOcr, Tiff, Jpeg, Png }
public enum AfterSave { None, ShowInFolder, Open, Mail, Print }
public enum ThemeChoice { System, Light, Dark }

public static class Catalog
{
    public static readonly (FileKind Kind, string Label, string Ext, bool MultiPage)[] FileKinds =
    {
        (FileKind.Pdf, "PDF, all pages in one file", "pdf", true),
        (FileKind.PdfOcr, "Searchable PDF (OCR)", "pdf", true),
        (FileKind.Tiff, "TIFF, all pages in one file", "tif", true),
        (FileKind.Jpeg, "JPEG, one file per page", "jpg", false),
        (FileKind.Png, "PNG, one file per page", "png", false),
    };

    public static (FileKind Kind, string Label, string Ext, bool MultiPage) Info(FileKind k) => FileKinds.First(f => f.Kind == k);

    public static readonly (AfterSave Value, string Label)[] AfterSaveChoices =
    {
        (AfterSave.None, "Do nothing else"),
        (AfterSave.ShowInFolder, "Show the file in its folder"),
        (AfterSave.Open, "Open the file"),
        (AfterSave.Mail, "Attach it to a new e-mail"),
        (AfterSave.Print, "Print it"),
    };

    /// <summary>Document sizes in millimetres. Sizes larger than the scanner's glass are hidden.</summary>
    public static readonly (string Name, double W, double H)[] DocSizes =
    {
        ("A4", 210, 297), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("A5", 148, 210), ("A6", 105, 148),
        ("Business card", 90, 55), ("Photo 10 × 15 cm", 100, 150), ("Photo 13 × 18 cm", 130, 180), ("Full glass", 0, 0),
    };

    public static readonly string[] DefaultModes = { "Lineart", "Gray", "Color" };
    public static readonly int[] DefaultResolutions = { 75, 100, 150, 200, 300, 600, 1200 };
}

public sealed class Profile
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Pattern { get; set; } = "{profile}_{date}_{time}";
    public FileKind Kind { get; set; } = FileKind.Pdf;
    public int Resolution { get; set; } = 300;
    /// <summary>Kind of colour: "color", "gray" or "lineart"; mapped to the scanner's own mode names.</summary>
    public string ColorKind { get; set; } = "gray";
    /// <summary>The scanner's exact mode name, once known. Wins over ColorKind.</summary>
    public string Mode { get; set; } = "";
    public string Source { get; set; } = "";
    public string Size { get; set; } = "A4";
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public bool AskMorePages { get; set; } = true;
    public AfterSave After { get; set; } = AfterSave.ShowInFolder;
    public bool CreateFolder { get; set; } = true;
    public bool SaveFile { get; set; } = true;
    /// <summary>Icon for the button; empty means the built-in icon for its key.</summary>
    public string IconKey { get; set; } = "";

    public static readonly string[] BuiltInKeys = { "file", "image", "ocr", "email", "copy" };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustom => Array.IndexOf(BuiltInKeys, Key) < 0;

    public Profile Clone() => (Profile)MemberwiseClone();

    /// <summary>A file-name-friendly tag from a label, used for {profile}.</summary>
    public static string TagFromLabel(string label)
    {
        var chars = label.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length > 0 ? new string(chars) : "Scan";
    }

    public static List<Profile> Defaults(string root) => new()
    {
        new() { Key = "file", Label = "Scan to File", Tag = "File", Folder = Path.Combine(root, "Documents"),
                Pattern = "{profile}_{date}_{time}", Kind = FileKind.Pdf, Resolution = 300, ColorKind = "gray",
                AskMorePages = true, After = AfterSave.ShowInFolder },
        new() { Key = "image", Label = "Scan to Image", Tag = "Image", Folder = Path.Combine(root, "Images"),
                Pattern = "Photo_{date}_{n}", Kind = FileKind.Jpeg, Resolution = 600, ColorKind = "color",
                AskMorePages = false, After = AfterSave.Open },
        new() { Key = "ocr", Label = "Scan to OCR", Tag = "OCR", Folder = Path.Combine(root, "OCR"),
                Pattern = "OCR_{date}_{n}", Kind = FileKind.PdfOcr, Resolution = 300, ColorKind = "gray", Contrast = 10,
                AskMorePages = true, After = AfterSave.Open },
        new() { Key = "email", Label = "Scan to E-mail", Tag = "Mail", Folder = Path.Combine(root, "Mail"),
                Pattern = "Mail_{date}_{time}", Kind = FileKind.Pdf, Resolution = 150, ColorKind = "color",
                AskMorePages = true, After = AfterSave.Mail },
        new() { Key = "copy", Label = "Copy", Tag = "Copy", Folder = Path.Combine(root, "Copies"),
                Pattern = "Copy_{date}_{time}", Kind = FileKind.Pdf, Resolution = 300, ColorKind = "color",
                AskMorePages = false, After = AfterSave.Print, SaveFile = false },
    };
}

public sealed class AppSettings
{
    // Connection
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6566;
    public string User { get; set; } = "";
    public string Device { get; set; } = "";
    public bool FindDevicesOnStart { get; set; } = true;
    // Preview
    public int PreviewResolution { get; set; } = 100;
    public bool PreviewUseMinimum { get; set; } = true;
    public bool PreviewOnStart { get; set; }
    // Saving
    public string DefaultFolder { get; set; } = DefaultRoot();
    public int JpegQuality { get; set; } = 90;
    public string OcrLanguage { get; set; } = "";   // empty = Windows display language
    public bool Sound { get; set; }
    public bool Notify { get; set; } = true;
    // General
    public bool TopMost { get; set; }
    public bool DisableEsc { get; set; }
    public bool HideUnsupported { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;
    public bool StartWithWindows { get; set; }
    public bool ShareWithSaneTwain { get; set; } = true;
    // Printer
    public string Printer { get; set; } = "";       // empty = Windows default
    public bool PrintShowOptions { get; set; } = true;
    public bool PrintWaitForAllPages { get; set; }
    // Window
    public string WindowPlacement { get; set; } = "";
    public string LastProfile { get; set; } = "file";
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>Settings layout version, for one-time upgrades of older files.</summary>
    public int? SchemaVersion { get; set; }

    public const int CurrentSchema = 2;

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans");
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>SCANCENTER_HOME overrides the settings folder (used for testing).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("SCANCENTER_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanCenter");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static bool IsFirstRun => !File.Exists(FilePath);

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            // A damaged file shouldn't stop the app; keep it for inspection and start fresh.
            try { File.Copy(FilePath, FilePath + ".damaged", true); } catch { }
            s = new AppSettings();
        }

        if (IsFirstRun && SaneTwainIni.Exists) SaneTwainIni.Import(s);

        // Version 2: Dark became the default theme.
        if (!IsFirstRun && (s.SchemaVersion ?? 1) < 2 && s.Theme == ThemeChoice.System) s.Theme = ThemeChoice.Dark;
        s.SchemaVersion = AppSettings.CurrentSchema;

        // Make sure every built-in button exists (new versions may add buttons).
        var defaults = Profile.Defaults(s.DefaultFolder);
        foreach (var d in defaults)
            if (s.Profiles.All(p => p.Key != d.Key)) s.Profiles.Add(d);
        s.Profiles = s.Profiles.OrderBy(p => defaults.FindIndex(d => d.Key == p.Key) is var i && i < 0 ? 99 : i).ToList();
        return s;
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, FilePath, true);
        if (s.ShareWithSaneTwain && SaneTwainIni.Exists)
        {
            try { SaneTwainIni.Export(s); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
