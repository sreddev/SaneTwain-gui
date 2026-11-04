using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanCenter.App;

/// <summary>Draws the scanner glass, the image as it arrives and the moving lamp.</summary>
public sealed class PlatenView : FrameworkElement
{
    private BitmapSource? _image;
    private double _fraction;
    private bool _scanning;
    private (double W, double H) _area = (210, 297);
    private (double W, double H) _glass = (215.9, 297);

    private static readonly Brush Glass = Frozen(new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x3d)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1));
    private static readonly Brush Corner = Frozen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
    private static readonly Brush LampCore = Frozen(new SolidColorBrush(Color.FromRgb(255, 242, 220)));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public void SetGlass(double w, double h)
    {
        // Some backends report a longer area than the real glass (e.g. Legal on A4 glass); cap the drawing at 2:3.
        _glass = (w, Math.Min(h, w * 1.42));
        InvalidateVisual();
    }

    public void Show(BitmapSource? img, double fraction, (double W, double H)? area = null, bool scanning = false)
    {
        _image = img; _fraction = fraction; _scanning = scanning;
        if (area is { } a) _area = a;
        InvalidateVisual();
    }

    public BitmapSource? Image => _image;

    protected override void OnRender(DrawingContext dc)
    {
        double aspect = _glass.W / _glass.H;
        double h = ActualHeight - 8, w = h * aspect;
        if (w > ActualWidth - 8) { w = ActualWidth - 8; h = w / aspect; }
        if (w <= 0 || h <= 0) return;
        double x0 = (ActualWidth - w) / 2, y0 = 4;
        var glassRect = new Rect(x0, y0, w, h);
        dc.DrawRoundedRectangle(Glass, null, glassRect, 6, 6);
        dc.PushClip(new RectangleGeometry(glassRect, 6, 6));
        for (int i = 1; i < 8; i++) dc.DrawLine(GridPen, new Point(x0 + w * i / 8, y0), new Point(x0 + w * i / 8, y0 + h));
        for (int i = 1; i < 11; i++) dc.DrawLine(GridPen, new Point(x0, y0 + h * i / 11), new Point(x0 + w, y0 + h * i / 11));
        double s = w * 0.05;
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(x0 + 5, y0 + 5), true, true);
            g.LineTo(new Point(x0 + 5 + s, y0 + 5), false, false);
            g.LineTo(new Point(x0 + 5, y0 + 5 + s), false, false);
        }
        dc.DrawGeometry(Corner, null, tri);

        double aw = w * Math.Min(1, _area.W / _glass.W), ah = h * Math.Min(1, _area.H / _glass.H);
        if (_image != null)
        {
            if (_scanning && _fraction > 0)
                dc.DrawImage(_image, new Rect(x0, y0, aw, ah * _fraction));
            else if (!_scanning)
                dc.DrawImage(_image, new Rect(x0, y0, aw, Math.Min(h, aw * _image.PixelHeight / Math.Max(1, _image.PixelWidth))));
        }
        if (_scanning)
        {
            double ly = y0 + ah * _fraction;
            var lamp = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 242, 163, 58), 0),
                    new GradientStop(Color.FromArgb(230, 255, 214, 150), 0.5),
                    new GradientStop(Color.FromArgb(0, 242, 163, 58), 1),
                },
            };
            dc.DrawRectangle(lamp, null, new Rect(x0, ly - 22, w, 44));
            dc.DrawRectangle(LampCore, null, new Rect(x0, ly - 1, w, 2));
        }
        dc.Pop();
    }
}
