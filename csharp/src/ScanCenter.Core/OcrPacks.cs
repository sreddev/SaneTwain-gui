using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScanCenter.Core;

/// <summary>
/// Windows OCR language packs ("Language.OCR~~~xx-XX~0.0.1.0" optional features). Windows can't load OCR
/// models from an app, so the packs are installed into Windows itself. That needs administrator rights,
/// so it runs in one elevated PowerShell (one UAC prompt) that downloads from Windows Update.
/// </summary>
public static class OcrPacks
{
    /// <summary>Every language Windows OCR offers (Windows 10 2004 and later).</summary>
    public static readonly string[] Tags =
    {
        "ar-SA", "bg-BG", "bs-Latn-BA", "cs-CZ", "da-DK", "de-DE", "el-GR", "en-GB", "en-US", "es-ES", "es-MX",
        "fi-FI", "fr-CA", "fr-FR", "hr-HR", "hu-HU", "it-IT", "ja-JP", "ko-KR", "nb-NO", "nl-NL", "pl-PL",
        "pt-BR", "pt-PT", "ro-RO", "ru-RU", "sk-SK", "sl-SI", "sr-Cyrl-RS", "sr-Latn-RS", "sv-SE", "tr-TR",
        "zh-CN", "zh-HK", "zh-TW",
    };

    public sealed record Pack(string Tag, string Name, bool Installed)
    {
        public string Capability => OcrPacks.Capability(Tag);
    }

    public static string Capability(string tag) => $"Language.OCR~~~{CapabilityTag(tag)}~0.0.1.0";

    // Windows names them with the language in lower case and the rest in upper case: "en-US", "sr-CYRL-RS".
    private static string CapabilityTag(string tag)
    {
        var parts = tag.Split('-');
        parts[0] = parts[0].ToLowerInvariant();
        for (int i = 1; i < parts.Length; i++) parts[i] = parts[i].ToUpperInvariant();
        return string.Join("-", parts);
    }

    public static string DisplayName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).DisplayName; } catch { return tag; }
    }

    /// <summary>All packs, with the ones Windows OCR can use now marked installed. Sorted by name.</summary>
    public static List<Pack> All()
    {
        var have = Ocr.Languages().Select(l => l.Tag).ToList();
        return Tags.Select(t => new Pack(t, DisplayName(t), IsInstalled(t, have)))
                   .OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Windows reports some packs without the region ("ru" for ru-RU), so a shorter tag counts too.</summary>
    public static bool IsInstalled(string tag, IEnumerable<string> available) =>
        available.Any(a => tag.Equals(a, StringComparison.OrdinalIgnoreCase)
                        || (tag.StartsWith(a + "-", StringComparison.OrdinalIgnoreCase) && !a.Contains('-')));

    /// <summary>
    /// Windows has servicing work waiting for a restart. Until then, adding features fails with 0x8024001e,
    /// so the app says "restart first" instead of a vague error.
    /// </summary>
    public static bool RestartPending()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return k != null;
        }
        catch { return false; }
    }

    public sealed record ChangeResult(bool Cancelled, int Failed, string Problem);

    /// <summary>Installs and removes packs in one elevated PowerShell. The exit code is the number that failed.</summary>
    public static Task<ChangeResult> ChangeAsync(IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken ct = default)
    {
        static string List(IEnumerable<string> tags) => string.Join(",", tags.Select(t => $"'{Capability(t)}'"));
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; $failed = 0")
            .AppendLine($"foreach ($n in @({List(add)})) {{ try {{ Add-WindowsCapability -Online -Name $n | Out-Null }} catch {{ $failed++ }} }}")
            .AppendLine($"foreach ($n in @({List(remove)})) {{ try {{ Remove-WindowsCapability -Online -Name $n | Out-Null }} catch {{ $failed++ }} }}")
            .AppendLine("exit $failed")
            .ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        return Task.Run(() =>
        {
            Process? p;
            try { p = Process.Start(psi); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return new ChangeResult(true, 0, ""); } // UAC declined
            catch (Exception e) { return new ChangeResult(false, add.Count + remove.Count, e.Message); }
            if (p == null) return new ChangeResult(false, add.Count + remove.Count, "PowerShell didn't start.");
            using (p)
            {
                p.WaitForExitAsync(ct).GetAwaiter().GetResult();
                return new ChangeResult(false, p.ExitCode, "");
            }
        }, ct);
    }
}
