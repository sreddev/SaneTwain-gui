using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace ScanCenter.Core;

/// <summary>Text recognition with the OCR engine built into Windows (no extra install needed).</summary>
public static class Ocr
{
    public sealed record Word(string Text, double X, double Y, double Width, double Height);

    /// <summary>Languages with a Windows OCR pack installed, as (tag, display name).</summary>
    public static List<(string Tag, string Name)> Languages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList(); }
        catch { return new(); }
    }

    public static Engine? Create(string languageTag, out string problem)
    {
        problem = "";
        OcrEngine? e = null;
        try
        {
            if (!string.IsNullOrEmpty(languageTag))
            {
                var lang = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(lang)) e = OcrEngine.TryCreateFromLanguage(lang);
            }
            e ??= OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) { problem = $"Windows text recognition isn't available ({ex.Message})."; return null; }
        if (e == null)
        {
            problem = "No text recognition language is installed in Windows. Add one in Settings > Time & language > Language & region (choose a language > Language options > Optical character recognition).";
            return null;
        }
        return new Engine(e);
    }

    public sealed class Engine
    {
        private readonly OcrEngine _e;
        internal Engine(OcrEngine e) => _e = e;
        public string Language => _e.RecognizerLanguage.DisplayName;

        public async Task<IReadOnlyList<Word>> RecognizeAsync(BitmapSource img)
        {
            // Windows OCR has a maximum image size; scale down if needed and map positions back.
            double max = OcrEngine.MaxImageDimension;
            double k = Math.Min(1.0, Math.Min(max / img.PixelWidth, max / img.PixelHeight));
            BitmapSource src = img;
            if (k < 1)
            {
                var t = new TransformedBitmap(img, new System.Windows.Media.ScaleTransform(k, k));
                t.Freeze();
                src = t;
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            using var ras = ms.AsRandomAccessStream();
            var dec = await WinBitmapDecoder.CreateAsync(ras);
            using var sb = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var res = await _e.RecognizeAsync(sb);
            var words = new List<Word>();
            foreach (var line in res.Lines)
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    words.Add(new Word(w.Text, r.X / k, r.Y / k, r.Width / k, r.Height / k));
                }
            return words;
        }
    }
}
