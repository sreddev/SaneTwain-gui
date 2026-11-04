using System;

namespace ScanCenter.Core.Sane;

public enum SaneStatus
{
    Good = 0, Unsupported = 1, Cancelled = 2, DeviceBusy = 3, Invalid = 4, Eof = 5,
    Jammed = 6, NoDocs = 7, CoverOpen = 8, IoError = 9, NoMem = 10, AccessDenied = 11,
}

public enum SaneValueType { Bool = 0, Int = 1, Fixed = 2, String = 3, Button = 4, Group = 5 }
public enum SaneUnit { None = 0, Pixel = 1, Bit = 2, Mm = 3, Dpi = 4, Percent = 5, Microsecond = 6 }
public enum SaneConstraint { None = 0, Range = 1, WordList = 2, StringList = 3 }
public enum SaneFrame { Gray = 0, Rgb = 1, Red = 2, Green = 3, Blue = 4 }

[Flags]
public enum SaneCap
{
    SoftSelect = 1, HardSelect = 2, SoftDetect = 4, Emulated = 8, Automatic = 16, Inactive = 32, Advanced = 64,
}

public sealed class SaneException : Exception
{
    public SaneStatus Status { get; }

    public SaneException(SaneStatus status, string where)
        : base($"{where}: {Describe(status)}") => Status = status;

    public static string Describe(SaneStatus s) => s switch
    {
        SaneStatus.Unsupported => "operation not supported",
        SaneStatus.Cancelled => "cancelled",
        SaneStatus.DeviceBusy => "the scanner is busy",
        SaneStatus.Invalid => "invalid argument",
        SaneStatus.Eof => "no more data",
        SaneStatus.Jammed => "the document feeder is jammed",
        SaneStatus.NoDocs => "the document feeder is empty",
        SaneStatus.CoverOpen => "the scanner cover is open",
        SaneStatus.IoError => "error talking to the scanner",
        SaneStatus.NoMem => "out of memory",
        SaneStatus.AccessDenied => "access denied",
        _ => $"unknown status {(int)s}",
    };
}

public sealed record SaneDevice(string Name, string Vendor, string Model, string Type)
{
    public string Label => $"{Vendor} {Model}".Trim() is { Length: > 0 } l ? l : Name;
}

public sealed record SaneParameters(SaneFrame Format, bool LastFrame, int BytesPerLine, int PixelsPerLine, int Lines, int Depth);

public sealed class SaneOption
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public SaneValueType Type { get; init; }
    public SaneUnit Unit { get; init; }
    public int Size { get; init; }
    public SaneCap Cap { get; init; }
    public SaneConstraint ConstraintType { get; init; }
    public (double Min, double Max, double Quant)? Range { get; set; }
    public double[]? WordList { get; set; }
    public string[]? StringList { get; set; }

    public bool Active => (Cap & SaneCap.Inactive) == 0;
    public bool Settable => (Cap & SaneCap.SoftSelect) != 0 && Active;

    public double ToNumber(int word) => Type == SaneValueType.Fixed ? word / 65536.0 : word;
}
