using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ScanCenter.Core.Sane;

/// <summary>
/// Client for the SANE network protocol (saned, TCP port 6566).
/// One instance owns one control connection and is used from one thread at a time.
/// </summary>
public sealed class SaneClient : IDisposable
{
    private const uint NetInit = 0, NetGetDevices = 1, NetOpen = 2, NetClose = 3, NetGetOptionDescriptors = 4,
        NetControlOption = 5, NetGetParameters = 6, NetStart = 7, NetCancel = 8, NetAuthorize = 9, NetExit = 10;
    private const uint SaneVersion = (1u << 24) | 3u;
    private const uint InfoReloadOptions = 2;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly int _timeoutMs;
    private TcpClient? _tcp;
    private NetworkStream? _s;
    private uint? _handle;

    public IReadOnlyList<SaneOption> Options { get; private set; } = Array.Empty<SaneOption>();
    public uint ServerVersion { get; private set; }
    public string ServerVersionText => $"{ServerVersion >> 24}.{(ServerVersion >> 16) & 0xff}.{ServerVersion & 0xffff}";

    public SaneClient(string host, int port = 6566, string user = "", string password = "", int timeoutMs = 10000)
    {
        _host = host; _port = port; _user = user; _password = password; _timeoutMs = timeoutMs;
    }

    // ------------------------------------------------------------------ connection
    public SaneClient Connect()
    {
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = _timeoutMs, SendTimeout = _timeoutMs };
        var connect = _tcp.ConnectAsync(_host, _port);
        if (!connect.Wait(_timeoutMs))
            throw new TimeoutException($"No answer from {_host}:{_port} after {_timeoutMs / 1000} s.");
        _s = _tcp.GetStream();
        W(NetInit); W(SaneVersion); WStr(_user);
        var status = R();
        ServerVersion = R();
        Check(status, "init");
        return this;
    }

    public void Dispose()
    {
        try { if (_handle is uint h && _s != null) { W(NetClose); W(h); R(); } } catch { /* closing anyway */ }
        _handle = null;
        try { if (_s != null) W(NetExit); } catch { }
        _s?.Dispose(); _tcp?.Dispose();
        _s = null; _tcp = null;
    }

    // ------------------------------------------------------------------ wire helpers
    private void Send(ReadOnlySpan<byte> b) => _s!.Write(b);

    private void W(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        Send(b);
    }

    private void WStr(string? s)
    {
        if (s == null) { W(0); return; }
        var b = Latin1.GetBytes(s + "\0");
        W((uint)b.Length); Send(b);
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf[got..]);
            if (n <= 0) throw new IOException("saned closed the connection.");
            got += n;
        }
    }

    private byte[] RBytes(int n) { var b = new byte[n]; ReadExact(_s!, b); return b; }

    private uint R() { Span<byte> b = stackalloc byte[4]; ReadExact(_s!, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
    private int RInt() => unchecked((int)R());

    private string? RStr()
    {
        var n = R();
        if (n == 0) return null;
        var b = RBytes((int)n);
        int z = Array.IndexOf(b, (byte)0);
        return Latin1.GetString(b, 0, z < 0 ? b.Length : z);
    }

    private static void Check(uint status, string where)
    {
        if (status != (uint)SaneStatus.Good) throw new SaneException((SaneStatus)status, where);
    }

    private void Authorize(string resource)
    {
        W(NetAuthorize); WStr(resource); WStr(_user); WStr(_password);
        R();
    }

    // ------------------------------------------------------------------ devices
    public List<SaneDevice> GetDevices()
    {
        W(NetGetDevices);
        var status = R();
        var n = R();
        var list = new List<SaneDevice>();
        for (int i = 0; i < n; i++)
        {
            if (R() != 0) continue; // NULL pointer ends the list
            list.Add(new SaneDevice(RStr() ?? "", RStr() ?? "", RStr() ?? "", RStr() ?? ""));
        }
        Check(status, "list scanners");
        return list;
    }

    public void Open(string device)
    {
        W(NetOpen); WStr(device);
        uint status, handle;
        while (true)
        {
            status = R(); handle = R();
            var resource = RStr();
            if (!string.IsNullOrEmpty(resource)) { Authorize(resource); continue; }
            break;
        }
        Check(status, $"open {device}");
        _handle = handle;
        ReloadOptions();
    }

    // ------------------------------------------------------------------ options
    public void ReloadOptions()
    {
        W(NetGetOptionDescriptors); W(_handle!.Value);
        var n = R();
        var opts = new List<SaneOption>();
        for (int i = 0; i < n; i++)
        {
            if (R() != 0) continue;
            var o = new SaneOption
            {
                Index = i, Name = RStr() ?? "", Title = RStr() ?? "", Description = RStr() ?? "",
                Type = (SaneValueType)R(), Unit = (SaneUnit)R(), Size = RInt(), Cap = (SaneCap)R(),
                ConstraintType = (SaneConstraint)R(),
            };
            switch (o.ConstraintType)
            {
                case SaneConstraint.Range:
                    if (R() == 0) o.Range = (o.ToNumber(RInt()), o.ToNumber(RInt()), o.ToNumber(RInt()));
                    break;
                case SaneConstraint.WordList:
                {
                    var m = R();
                    var words = new int[m];
                    for (int k = 0; k < m; k++) words[k] = RInt();
                    o.WordList = words.Skip(1).Select(o.ToNumber).ToArray(); // first word is the count
                    break;
                }
                case SaneConstraint.StringList:
                {
                    var m = R();
                    var strs = new List<string>();
                    for (int k = 0; k < m; k++) { var s = RStr(); if (s != null) strs.Add(s); }
                    o.StringList = strs.ToArray();
                    break;
                }
            }
            opts.Add(o);
        }
        Options = opts;
    }

    public SaneOption? Find(string name) => Options.FirstOrDefault(o => o.Name == name && name.Length > 0);

    private object? Control(SaneOption o, uint action, object? value)
    {
        W(NetControlOption); W(_handle!.Value); W((uint)o.Index); W(action); W((uint)o.Type);
        switch (o.Type)
        {
            case SaneValueType.String:
            {
                var raw = new byte[Math.Max(o.Size, 1)];
                if (action == 1 && value is string str)
                {
                    var b = Latin1.GetBytes(str);
                    Array.Copy(b, raw, Math.Min(b.Length, raw.Length - 1));
                }
                W((uint)raw.Length); W((uint)raw.Length); Send(raw);
                break;
            }
            case SaneValueType.Bool or SaneValueType.Int or SaneValueType.Fixed:
            {
                int count = Math.Max(o.Size / 4, 1);
                W((uint)(count * 4)); W((uint)count);
                for (int i = 0; i < count; i++)
                {
                    int word = 0;
                    if (action == 1 && value != null)
                    {
                        double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                        word = o.Type == SaneValueType.Fixed ? (int)Math.Round(d * 65536.0) : (int)Math.Round(d);
                    }
                    W(unchecked((uint)word));
                }
                break;
            }
            default:
                W(0); W(0);
                break;
        }

        while (true)
        {
            var status = R();
            var info = R();
            var vtype = (SaneValueType)R();
            R(); // value size
            var len = (int)R();
            object? result = null;
            if (vtype == SaneValueType.String)
            {
                var b = RBytes(len);
                int z = Array.IndexOf(b, (byte)0);
                result = Latin1.GetString(b, 0, z < 0 ? b.Length : z);
            }
            else if (vtype is SaneValueType.Bool or SaneValueType.Int or SaneValueType.Fixed)
            {
                var vals = new double[len];
                for (int i = 0; i < len; i++) { var w = RInt(); vals[i] = vtype == SaneValueType.Fixed ? w / 65536.0 : w; }
                result = vals.Length == 1 ? vals[0] : vals;
            }
            else if (len > 0) RBytes(len);
            var resource = RStr();
            if (!string.IsNullOrEmpty(resource)) { Authorize(resource); continue; }
            Check(status, $"setting '{o.Name}'");
            if ((info & InfoReloadOptions) != 0) ReloadOptions();
            return result;
        }
    }

    public object? Get(string name) => Find(name) is { } o ? Control(o, 0, null) : null;

    /// <summary>Sets an option if the scanner has it and it can be set now. Returns the applied value, or null.</summary>
    public object? Set(string name, object value)
    {
        var o = Find(name);
        if (o == null || !o.Settable) return null;
        if (o.Type == SaneValueType.String && o.StringList != null && value is string s && !o.StringList.Contains(s)) return null;
        Control(o, 1, value);
        try { return Get(name); } catch { return value; }
    }

    // ------------------------------------------------------------------ acquisition
    public SaneParameters GetParameters()
    {
        W(NetGetParameters); W(_handle!.Value);
        var status = R();
        var p = new SaneParameters((SaneFrame)R(), R() != 0, RInt(), RInt(), RInt(), RInt());
        Check(status, "read scan parameters");
        return p;
    }

    public void Cancel()
    {
        try { W(NetCancel); W(_handle!.Value); R(); } catch { }
    }

    private (TcpClient data, uint byteOrder) Start()
    {
        W(NetStart); W(_handle!.Value);
        uint status, port, order;
        while (true)
        {
            status = R(); port = R(); order = R();
            var resource = RStr();
            if (!string.IsNullOrEmpty(resource)) { Authorize(resource); continue; }
            break;
        }
        Check(status, "start");
        var data = new TcpClient { ReceiveTimeout = Math.Max(_timeoutMs, 120000) };
        data.Connect(_host, (int)port);
        return (data, order);
    }

    /// <summary>
    /// Reads one image (all frames). Does not cancel afterwards, so a document feeder can continue;
    /// call <see cref="Cancel"/> when the batch is done.
    /// </summary>
    /// <param name="onData">Called with (buffer, bytes so far, parameters) as data arrives.</param>
    public (SaneParameters Params, byte[] Data) ReadImage(Action<byte[], int, SaneParameters>? onData, CancellationToken ct)
    {
        var frames = new Dictionary<SaneFrame, byte[]>();
        SaneParameters p;
        Span<byte> hdr = stackalloc byte[4];
        Span<byte> st = stackalloc byte[1];
        while (true)
        {
            var (data, order) = Start();
            p = GetParameters();
            long expected = p.Lines > 0 ? (long)p.BytesPerLine * p.Lines : 4 << 20;
            var buf = new byte[expected];
            int used = 0;
            try
            {
                var ds = data.GetStream();
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Cancel();
                        throw new SaneException(SaneStatus.Cancelled, "scan");
                    }
                    ReadExact(ds, hdr);
                    uint n = BinaryPrimitives.ReadUInt32BigEndian(hdr);
                    if (n == 0xFFFFFFFF)
                    {
                        ReadExact(ds, st);
                        var status = (SaneStatus)st[0];
                        if (status != SaneStatus.Good && status != SaneStatus.Eof)
                            throw new SaneException(status, "reading the image");
                        break;
                    }
                    if (used + n > buf.Length) Array.Resize(ref buf, (int)Math.Max(buf.Length * 2L, used + n));
                    ReadExact(ds, buf.AsSpan(used, (int)n));
                    used += (int)n;
                    onData?.Invoke(buf, used, p);
                }
            }
            finally { data.Dispose(); }

            if (used != buf.Length) Array.Resize(ref buf, used);
            if (p.Depth == 16 && order == 0x1234)
                for (int i = 0; i + 1 < buf.Length; i += 2) (buf[i], buf[i + 1]) = (buf[i + 1], buf[i]);
            frames[p.Format] = buf;
            if (p.LastFrame) break;
        }

        if (p.Format is SaneFrame.Red or SaneFrame.Green or SaneFrame.Blue)
        {
            var r = frames.GetValueOrDefault(SaneFrame.Red, Array.Empty<byte>());
            var g = frames.GetValueOrDefault(SaneFrame.Green, Array.Empty<byte>());
            var b = frames.GetValueOrDefault(SaneFrame.Blue, Array.Empty<byte>());
            var outb = new byte[r.Length * 3];
            for (int i = 0; i < r.Length; i++) { outb[3 * i] = r[i]; outb[3 * i + 1] = g[i]; outb[3 * i + 2] = b[i]; }
            p = p with { Format = SaneFrame.Rgb, BytesPerLine = p.BytesPerLine * 3 };
            return (p, outb);
        }
        return (p, frames[p.Format]);
    }
}
