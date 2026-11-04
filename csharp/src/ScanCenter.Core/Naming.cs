using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ScanCenter.Core;

public static partial class Naming
{
    [GeneratedRegex("[\\\\/:*?\"<>|\\x00-\\x1f]")]
    private static partial Regex Invalid();

    public static string Render(string pattern, Profile p, int n, DateTime when)
    {
        var name = pattern.Replace("{profile}", p.Tag)
            .Replace("{date}", when.ToString("yyyy-MM-dd"))
            .Replace("{time}", when.ToString("HHmmss"))
            .Replace("{n}", n.ToString("000"));
        name = Invalid().Replace(name, "-").Trim().TrimEnd('.');
        return name.Length == 0 ? "Scan" : name;
    }

    /// <summary>
    /// Picks a file name in <paramref name="folder"/> that doesn't exist yet. With {n} in the pattern the
    /// number counts up; otherwise " (2)", " (3)"… is added. Returns the path and a note if it was renamed.
    /// </summary>
    public static (string Path, string? Note) ChoosePath(string folder, Profile p, string ext, DateTime when,
        ISet<string>? reserved = null)
    {
        bool Free(string path) => !File.Exists(path) && (reserved == null || !reserved.Contains(path));

        if (p.Pattern.Contains("{n}"))
        {
            for (int n = 1; n < 100000; n++)
            {
                var path = System.IO.Path.Combine(folder, $"{Render(p.Pattern, p, n, when)}.{ext}");
                if (Free(path)) return (path, null);
            }
        }
        var baseName = Render(p.Pattern, p, 1, when);
        var first = System.IO.Path.Combine(folder, $"{baseName}.{ext}");
        if (Free(first)) return (first, null);
        int k = 2;
        string candidate;
        while (!Free(candidate = System.IO.Path.Combine(folder, $"{baseName} ({k}).{ext}"))) k++;
        return (candidate, $"{System.IO.Path.GetFileName(first)} already exists, so it was saved as {System.IO.Path.GetFileName(candidate)}");
    }

    public static string ExpandFolder(string folder) => Environment.ExpandEnvironmentVariables(folder.Trim());
}
