using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace SpookyCam;

/// <summary>Per-frame state shared by all effects: clock, RNG and a cache of size-dependent masks.</summary>
public sealed class EffectContext
{
    public double Time;
    public readonly Random Rng = new();
    private readonly Dictionary<string, Mat> _cache = new();

    public Mat Cached(string key, Size size, Func<Size, Mat> build)
    {
        var k = $"{key}:{size.Width}x{size.Height}";
        if (!_cache.TryGetValue(k, out var m))
        {
            m = build(size);
            _cache[k] = m;
        }
        return m;
    }
}

public abstract class Effect
{
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract string Description { get; }
    /// <summary>src and dst are BGR 8-bit 3-channel. dst may be reallocated.</summary>
    public abstract void Apply(Mat src, Mat dst, EffectContext ctx);
    public virtual void Reset() { }
}

public static class EffectCatalog
{
    public static readonly Effect[] All =
    {
        new Normal(),           // 0
        new GhostTrails(),      // 1  horror
        new NightVision(),      // 2  horror
        new BloodMoon(),        // 3  horror
        new PossessedTv(),      // 4  horror
        new JumpScare(),        // 5  jump scare (momentary, returns to the previous effect)
        new HauntedNegative(),  // 6  horror
        new Kaleidoscope(),     // 7  psychedelic
        new AcidRainbow(),      // 8  psychedelic
        new LiquidDream(),      // 9  psychedelic
        // Thermal is still defined below; add it here to put it on a key.
    };
}

// ---------------------------------------------------------------------------
// Shared image helpers
// ---------------------------------------------------------------------------
public static class Fx
{
    public static unsafe Mat BuildVignette(Size s, double strength, double power)
    {
        var m = new Mat(s, MatType.CV_32FC3);
        float cx = s.Width / 2f, cy = s.Height / 2f;
        float dmax = MathF.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < s.Height; y++)
        {
            float* row = (float*)m.Ptr(y);
            float dy = y - cy;
            for (int x = 0; x < s.Width; x++)
            {
                float dx = x - cx;
                float d = MathF.Sqrt(dx * dx + dy * dy) / dmax;
                float v = 1f - (float)(strength * Math.Pow(d, power));
                v = Math.Clamp(v, 0f, 1f);
                row[x * 3] = v; row[x * 3 + 1] = v; row[x * 3 + 2] = v;
            }
        }
        return m;
    }

    public static unsafe Mat BuildScanlines(Size s, float darkness, int every)
    {
        var m = new Mat(s, MatType.CV_32FC3, Scalar.All(1.0));
        for (int y = 0; y < s.Height; y += every)
        {
            float* row = (float*)m.Ptr(y);
            for (int x = 0; x < s.Width * 3; x++) row[x] = darkness;
        }
        return m;
    }

    /// <summary>Multiply an 8-bit BGR image by a float mask (CV_32FC3, 0..1).</summary>
    public static void MulMask(Mat img, Mat mask3)
    {
        using var f = new Mat();
        img.ConvertTo(f, MatType.CV_32FC3, 1.0 / 255);
        Cv2.Multiply(f, mask3, f);
        f.ConvertTo(img, MatType.CV_8UC3, 255);
    }

    public static void Vignette(Mat img, EffectContext ctx, double strength, double power = 2.0)
    {
        var mask = ctx.Cached($"vig{strength:F2}_{power:F2}", img.Size(), s => BuildVignette(s, strength, power));
        MulMask(img, mask);
    }

    /// <summary>Monochrome film grain. amount 0..1.</summary>
    public static void Grain(Mat img, double amount)
    {
        using var n1 = new Mat(img.Size(), MatType.CV_8UC1);
        Cv2.Randu(n1, Scalar.All(0), Scalar.All(255));
        if (img.Channels() == 3)
        {
            using var n3 = new Mat();
            Cv2.CvtColor(n1, n3, ColorConversionCodes.GRAY2BGR);
            Cv2.AddWeighted(img, 1.0, n3, amount, -amount * 127.5, img);
        }
        else
        {
            Cv2.AddWeighted(img, 1.0, n1, amount, -amount * 127.5, img);
        }
    }

    private static unsafe Mat BuildHueLut(int shift)
    {
        var lut = new Mat(1, 256, MatType.CV_8UC1);
        byte* p = (byte*)lut.Data;
        for (int i = 0; i < 256; i++) p[i] = (byte)((i + shift) % 180);
        return lut;
    }

    /// <summary>Rotate hue of a BGR image. shift is in OpenCV hue units (0..179 = full circle).</summary>
    public static void HueShift(Mat bgr, int shift, double satGain = 1.0, Mat? hueOffset = null)
    {
        shift = ((shift % 180) + 180) % 180;
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var ch = Cv2.Split(hsv);
        if (hueOffset != null) Cv2.Add(ch[0], hueOffset, ch[0]);
        using (var lut = BuildHueLut(shift)) Cv2.LUT(ch[0], lut, ch[0]);
        if (Math.Abs(satGain - 1.0) > 1e-3) ch[1].ConvertTo(ch[1], MatType.CV_8UC1, satGain, 20);
        Cv2.Merge(ch, hsv);
        foreach (var c in ch) c.Dispose();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
    }

    public static void Translate(Mat src, Mat dst, double dx, double dy)
    {
        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, 1.0); m.Set(0, 1, 0.0); m.Set(0, 2, dx);
        m.Set(1, 0, 0.0); m.Set(1, 1, 1.0); m.Set(1, 2, dy);
        Cv2.WarpAffine(src, dst, m, src.Size(), InterpolationFlags.Linear, BorderTypes.Reflect101);
    }

    /// <summary>Build a BGR image from a gray image with per-channel gains.</summary>
    public static void GrayTint(Mat gray, Mat dst, double b, double g, double r)
    {
        using var cb = new Mat(); using var cg = new Mat(); using var cr = new Mat();
        gray.ConvertTo(cb, MatType.CV_8UC1, b);
        gray.ConvertTo(cg, MatType.CV_8UC1, g);
        gray.ConvertTo(cr, MatType.CV_8UC1, r);
        Cv2.Merge(new[] { cb, cg, cr }, dst);
    }

    /// <summary>Shift one channel left (dx &lt; 0) or right (dx &gt; 0) against the others.</summary>
    public static void RgbSplit(Mat img, int dx, int dy = 0)
    {
        if (dx == 0 && dy == 0) return;
        var ch = Cv2.Split(img);
        using var r = new Mat(); using var b = new Mat();
        Translate(ch[2], r, dx, dy);
        Translate(ch[0], b, -dx, -dy);
        Cv2.Merge(new[] { b, ch[1], r }, img);
        foreach (var c in ch) c.Dispose();
    }

    /// <summary>Roll a horizontal band of rows sideways by off pixels (wrapping).</summary>
    public static void RollBand(Mat img, int y0, int y1, int off)
    {
        int w = img.Width;
        off = ((off % w) + w) % w;
        if (off == 0 || y1 <= y0) return;
        using var band = img.RowRange(y0, y1);
        using var left = band.ColRange(0, w - off).Clone();
        using var right = band.ColRange(w - off, w).Clone();
        using (var dl = band.ColRange(0, off)) right.CopyTo(dl);
        using (var dr = band.ColRange(off, w)) left.CopyTo(dr);
    }

    public static void Text(Mat img, string text, Point at, double scale, Scalar color, int thickness = 1)
    {
        Cv2.PutText(img, text, at + new Point(2, 2), HersheyFonts.HersheySimplex, scale, Scalar.Black, thickness + 2, LineTypes.AntiAlias);
        Cv2.PutText(img, text, at, HersheyFonts.HersheySimplex, scale, color, thickness, LineTypes.AntiAlias);
    }
}

// ---------------------------------------------------------------------------
// 0 — passthrough
// ---------------------------------------------------------------------------
public sealed class Normal : Effect
{
    public override string Name => "Normal";
    public override string Category => "Camera";
    public override string Description => "Plain camera feed";
    public override void Apply(Mat src, Mat dst, EffectContext ctx) => src.CopyTo(dst);
}

// ---------------------------------------------------------------------------
// 1 — Ghost Trails (spooky): motion smears into pale blue phantoms
// ---------------------------------------------------------------------------
public sealed class GhostTrails : Effect
{
    private Mat? _acc;

    public override string Name => "Ghost Trails";
    public override string Category => "Horror";
    public override string Description => "Move slowly... leave your spirit behind";

    public override void Reset() { _acc?.Dispose(); _acc = null; }

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        if (_acc == null || _acc.Size() != src.Size())
        {
            _acc?.Dispose();
            _acc = new Mat();
            src.ConvertTo(_acc, MatType.CV_32FC3);
        }
        Cv2.AccumulateWeighted(src, _acc, 0.10, null!);

        using var trail = new Mat();
        _acc.ConvertTo(trail, MatType.CV_8UC3);

        // Current frame shows faintly on top of the trail so the subject reads as a ghost.
        using var mix = new Mat();
        Cv2.AddWeighted(trail, 0.75, src, 0.45, 0, mix);

        using var gray = new Mat();
        Cv2.CvtColor(mix, gray, ColorConversionCodes.BGR2GRAY);
        gray.ConvertTo(gray, MatType.CV_8UC1, 1.25, -20);
        Fx.GrayTint(gray, dst, 1.0, 0.85, 0.65);

        // Slow breathing flicker.
        double flicker = 0.9 + 0.1 * Math.Sin(ctx.Time * 1.7) + (ctx.Rng.NextDouble() < 0.03 ? -0.3 : 0);
        dst.ConvertTo(dst, MatType.CV_8UC3, flicker);

        Fx.Vignette(dst, ctx, 1.1, 1.8);
        Fx.Grain(dst, 0.10);
    }
}

// ---------------------------------------------------------------------------
// 2 — Night Vision (spooky): grainy green security cam with scanlines
// ---------------------------------------------------------------------------
public sealed class NightVision : Effect
{
    public override string Name => "Night Vision";
    public override string Category => "Horror";
    public override string Description => "Found footage. Something is in the room.";

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        gray.ConvertTo(gray, MatType.CV_8UC1, 1.5, 25);
        Fx.Grain(gray, 0.35);
        Fx.GrayTint(gray, dst, 0.15, 1.0, 0.30);

        var mask = ctx.Cached("night", dst.Size(), s =>
        {
            using var vig = Fx.BuildVignette(s, 1.3, 2.2);
            using var scan = Fx.BuildScanlines(s, 0.65f, 3);
            var m = new Mat();
            Cv2.Multiply(vig, scan, m);
            return m;
        });
        Fx.MulMask(dst, mask);

        // Occasional signal roll.
        if (ctx.Rng.NextDouble() < 0.05)
            Fx.RollBand(dst, ctx.Rng.Next(0, dst.Height - 20), dst.Height, ctx.Rng.Next(-30, 30));

        // Timestamp like a real camera.
        Fx.Text(dst, $"REC  {DateTime.Now:yyyy-MM-dd HH:mm:ss}", new Point(dst.Width - 420, dst.Height - 30), 0.7, new Scalar(80, 255, 80), 2);
        if ((int)(ctx.Time * 2) % 2 == 0)
            Cv2.Circle(dst, new Point(dst.Width - 445, dst.Height - 38), 8, new Scalar(60, 60, 255), -1, LineTypes.AntiAlias);
    }
}

// ---------------------------------------------------------------------------
// 3 — Blood Moon (spooky): crimson, high contrast, heartbeat pulse
// ---------------------------------------------------------------------------
public sealed class BloodMoon : Effect
{
    public override string Name => "Blood Moon";
    public override string Category => "Horror";
    public override string Description => "Crimson tide with a pounding heartbeat";

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        var ch = Cv2.Split(src);
        ch[2].ConvertTo(ch[2], MatType.CV_8UC1, 1.6, 15);   // red up
        ch[1].ConvertTo(ch[1], MatType.CV_8UC1, 0.40, 0);   // green down
        ch[0].ConvertTo(ch[0], MatType.CV_8UC1, 0.30, 0);   // blue down
        Cv2.Merge(ch, dst);
        foreach (var c in ch) c.Dispose();

        // Heartbeat: two quick thumps then a rest, ~75 bpm.
        double beat = ctx.Time * 1.25 % 1.0;
        double pulse = Math.Exp(-Math.Pow((beat - 0.08) * 12, 2)) * 0.35 + Math.Exp(-Math.Pow((beat - 0.32) * 12, 2)) * 0.25;
        dst.ConvertTo(dst, MatType.CV_8UC3, 0.85 + pulse, -10);

        Fx.Vignette(dst, ctx, 1.4, 1.6);
        Fx.Grain(dst, 0.14);
    }
}

// ---------------------------------------------------------------------------
// 4 — Possessed TV (spooky): glitch bands, RGB tearing, static, inversion flashes
// ---------------------------------------------------------------------------
public sealed class PossessedTv : Effect
{
    public override string Name => "Possessed TV";
    public override string Category => "Horror";
    public override string Description => "The broadcast is coming from inside the house";

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        var rng = ctx.Rng;
        src.CopyTo(dst);
        int w = dst.Width, h = dst.Height;

        // Intensity ramps up in bursts.
        double burst = Math.Max(0, Math.Sin(ctx.Time * 0.9) + Math.Sin(ctx.Time * 2.3) * 0.5);
        bool heavy = burst > 0.9 || rng.NextDouble() < 0.08;

        // Chromatic tearing.
        int split = heavy ? rng.Next(8, 28) : rng.Next(0, 6);
        Fx.RgbSplit(dst, split * (rng.Next(2) == 0 ? 1 : -1));

        // Displaced horizontal bands.
        int bands = heavy ? rng.Next(5, 12) : rng.Next(0, 4);
        for (int i = 0; i < bands; i++)
        {
            int y0 = rng.Next(0, h - 8);
            int y1 = Math.Min(h, y0 + rng.Next(4, h / 6));
            Fx.RollBand(dst, y0, y1, rng.Next(-w / 4, w / 4));
        }

        // Pure static bands.
        if (heavy && rng.NextDouble() < 0.6)
        {
            int y0 = rng.Next(0, h - 20);
            int y1 = Math.Min(h, y0 + rng.Next(6, 40));
            using var band = dst.RowRange(y0, y1);
            using var n1 = new Mat(band.Size(), MatType.CV_8UC1);
            Cv2.Randu(n1, Scalar.All(0), Scalar.All(255));
            Cv2.CvtColor(n1, band, ColorConversionCodes.GRAY2BGR);
        }

        // Inversion flash and vertical hold slip.
        if (rng.NextDouble() < (heavy ? 0.25 : 0.02)) Cv2.BitwiseNot(dst, dst);
        if (rng.NextDouble() < 0.03)
        {
            using var slipped = new Mat();
            Fx.Translate(dst, slipped, 0, rng.Next(-h / 3, h / 3));
            slipped.CopyTo(dst);
        }

        // Desaturated, contrasty, scanlines and lots of grain.
        using var gray = new Mat();
        Cv2.CvtColor(dst, gray, ColorConversionCodes.BGR2GRAY);
        using var gray3 = new Mat();
        Cv2.CvtColor(gray, gray3, ColorConversionCodes.GRAY2BGR);
        Cv2.AddWeighted(dst, 0.65, gray3, 0.35, 0, dst);
        dst.ConvertTo(dst, MatType.CV_8UC3, 1.2, -15);

        var mask = ctx.Cached("tv", dst.Size(), s =>
        {
            using var vig = Fx.BuildVignette(s, 0.9, 2.5);
            using var scan = Fx.BuildScanlines(s, 0.75f, 2);
            var m = new Mat();
            Cv2.Multiply(vig, scan, m);
            return m;
        });
        Fx.MulMask(dst, mask);
        Fx.Grain(dst, heavy ? 0.45 : 0.22);
    }
}

// ---------------------------------------------------------------------------
// 5 — Thermal (spooky): predator-vision heat map
// ---------------------------------------------------------------------------
public sealed class Thermal : Effect
{
    public override string Name => "Thermal";
    public override string Category => "Horror";
    public override string Description => "Heat signature. It can see you breathing.";

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(0, 0), 2.5);
        Cv2.Normalize(gray, gray, 0, 255, NormTypes.MinMax);
        Cv2.ApplyColorMap(gray, dst, ColormapTypes.Inferno);
        Fx.Grain(dst, 0.06);
    }
}

// ---------------------------------------------------------------------------
// 6 — Haunted Negative (spooky): inverted, washed out, flickering
// ---------------------------------------------------------------------------
public sealed class HauntedNegative : Effect
{
    public override string Name => "Haunted Negative";
    public override string Category => "Horror";
    public override string Description => "The other side of the mirror";

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        Cv2.BitwiseNot(src, dst);

        using var gray = new Mat();
        Cv2.CvtColor(dst, gray, ColorConversionCodes.BGR2GRAY);
        using var gray3 = new Mat();
        Cv2.CvtColor(gray, gray3, ColorConversionCodes.GRAY2BGR);
        Cv2.AddWeighted(dst, 0.55, gray3, 0.45, 0, dst);

        // Sickly cyan-green cast.
        var ch = Cv2.Split(dst);
        ch[2].ConvertTo(ch[2], MatType.CV_8UC1, 0.75, 0);
        ch[1].ConvertTo(ch[1], MatType.CV_8UC1, 1.05, 5);
        Cv2.Merge(ch, dst);
        foreach (var c in ch) c.Dispose();

        // Light-bulb flicker.
        double r = ctx.Rng.NextDouble();
        double gain = r < 0.05 ? 0.35 : r < 0.09 ? 1.6 : 1.0 + 0.05 * Math.Sin(ctx.Time * 23);
        dst.ConvertTo(dst, MatType.CV_8UC3, gain);

        Fx.Vignette(dst, ctx, 1.0, 2.0);
        Fx.Grain(dst, 0.12);
    }
}

// ---------------------------------------------------------------------------
// 5 — Jump Scare: flashes assets/jumpscare.jpg over the feed for a couple of
// seconds with a zoom and shake, then the previous effect comes back.
// ---------------------------------------------------------------------------
public sealed class JumpScare : Effect
{
    public const double Duration = 2.2;
    public static readonly string ImagePath = Path.Combine(AppContext.BaseDirectory, "assets", "jumpscare.jpg");
    public static readonly string SoundPath = Path.Combine(AppContext.BaseDirectory, "assets", "jumpscare-scream.mp3");

    private readonly Mat? _image;
    private double _start = -1000;

    public JumpScare()
    {
        if (File.Exists(ImagePath))
        {
            var m = Cv2.ImRead(ImagePath, ImreadModes.Color);
            if (!m.Empty()) _image = m; else m.Dispose();
        }
    }

    public override string Name => "Jump Scare";
    public override string Category => "Scare";
    public override string Description => _image == null ? "assets/jumpscare.jpg not found" : "BOO";
    public bool HasImage => _image != null;

    public void Trigger(double now) => _start = now;
    public bool IsActive(double now) => now - _start >= 0 && now - _start < Duration;

    /// <summary>Used only if the scare is somehow selected as a mode: show the feed with the scare on top.</summary>
    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        src.CopyTo(dst);
        Overlay(dst, ctx);
    }

    /// <summary>Draw the scare over an already-rendered frame. No-op when inactive or without an image.</summary>
    public void Overlay(Mat img, EffectContext ctx)
    {
        if (_image == null || !IsActive(ctx.Time)) return;
        double t = (ctx.Time - _start) / Duration;   // 0..1
        int w = img.Width, h = img.Height;

        // Aspect-fill the frame, then zoom in over the duration with a fast punch at the start.
        double cover = Math.Max((double)w / _image.Width, (double)h / _image.Height);
        double zoom = 1.05 + 0.35 * t + 0.15 * Math.Exp(-t * 12);
        double scale = cover * zoom;
        var scaledSize = new Size((int)Math.Ceiling(_image.Width * scale), (int)Math.Ceiling(_image.Height * scale));
        using var scaled = new Mat();
        Cv2.Resize(_image, scaled, scaledSize, 0, 0, InterpolationFlags.Linear);

        // Shake hard at first, settle towards the end.
        int shake = (int)(28 * (1 - t) * (1 - t)) + 1;
        int ox = (scaled.Width - w) / 2 + ctx.Rng.Next(-shake, shake + 1);
        int oy = (scaled.Height - h) / 2 + ctx.Rng.Next(-shake, shake + 1);
        ox = Math.Clamp(ox, 0, scaled.Width - w);
        oy = Math.Clamp(oy, 0, scaled.Height - h);
        using var crop = scaled[new Rect(ox, oy, w, h)];

        if (t > 0.85)
        {
            // Fade back to the live feed over the last 15 percent.
            double a = (1 - t) / 0.15;
            Cv2.AddWeighted(crop, a, img, 1 - a, 0, img);
        }
        else
        {
            crop.CopyTo(img);
        }

        // White flash for the first few frames, red strobe after that.
        if (t < 0.06)
            Cv2.AddWeighted(img, 0.4, new Mat(img.Size(), img.Type(), Scalar.White), 0.6, 0, img);
        else if (ctx.Rng.NextDouble() < 0.12)
            Cv2.AddWeighted(img, 0.7, new Mat(img.Size(), img.Type(), new Scalar(0, 0, 200)), 0.3, 0, img);
    }
}

// ---------------------------------------------------------------------------
// 7 — Kaleidoscope (psychedelic): six-fold mirror that slowly spins and shifts hue
// ---------------------------------------------------------------------------
public sealed class Kaleidoscope : Effect
{
    private const int Segments = 6;
    private Mat? _mapX, _mapY;
    private Size _mapSize;

    public override string Name => "Kaleidoscope";
    public override string Category => "Psychedelic";
    public override string Description => "Six-fold mirror, slowly turning";

    public override void Reset() { _mapX?.Dispose(); _mapY?.Dispose(); _mapX = _mapY = null; }

    private unsafe void EnsureMaps(Size s)
    {
        if (_mapX != null && _mapSize == s) return;
        Reset();
        _mapSize = s;
        _mapX = new Mat(s, MatType.CV_32FC1);
        _mapY = new Mat(s, MatType.CV_32FC1);
        double cx = s.Width / 2.0, cy = s.Height / 2.0;
        double seg = 2 * Math.PI / Segments;
        for (int y = 0; y < s.Height; y++)
        {
            float* px = (float*)_mapX!.Ptr(y);
            float* py = (float*)_mapY!.Ptr(y);
            double dy = y - cy;
            for (int x = 0; x < s.Width; x++)
            {
                double dx = x - cx;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double a = Math.Atan2(dy, dx) % seg;
                if (a < 0) a += seg;
                if (a > seg / 2) a = seg - a;
                a -= seg / 4; // center the wedge so it samples a symmetric slice
                px[x] = (float)(cx + r * Math.Cos(a));
                py[x] = (float)(cy + r * Math.Sin(a));
            }
        }
    }

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        EnsureMaps(src.Size());
        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        double angle = ctx.Time * 18;
        double zoom = 1.15 + 0.12 * Math.Sin(ctx.Time * 0.8);
        using var rot = Cv2.GetRotationMatrix2D(center, angle, zoom);
        using var spun = new Mat();
        Cv2.WarpAffine(src, spun, rot, src.Size(), InterpolationFlags.Linear, BorderTypes.Reflect101);
        Cv2.Remap(spun, dst, _mapX!, _mapY!, InterpolationFlags.Linear, BorderTypes.Reflect101);
        Fx.HueShift(dst, (int)(ctx.Time * 25), 1.4);
    }
}

// ---------------------------------------------------------------------------
// 8 — Acid Rainbow (psychedelic): hue cycles through the spectrum across the frame
// ---------------------------------------------------------------------------
public sealed class AcidRainbow : Effect
{
    public override string Name => "Acid Rainbow";
    public override string Category => "Psychedelic";
    public override string Description => "Every colour at once, sliding across the room";

    private static unsafe Mat BuildHueGradient(Size s)
    {
        var m = new Mat(s, MatType.CV_8UC1);
        double span = s.Width + s.Height;
        for (int y = 0; y < s.Height; y++)
        {
            byte* row = (byte*)m.Ptr(y);
            for (int x = 0; x < s.Width; x++)
                row[x] = (byte)(70.0 * (x + y) / span);
        }
        return m;
    }

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        src.CopyTo(dst);
        var grad = ctx.Cached("huegrad", dst.Size(), BuildHueGradient);
        Fx.HueShift(dst, (int)(ctx.Time * 70), 1.9, grad);

        // Posterize value slightly for that printed-poster look.
        using var hsv = new Mat();
        Cv2.CvtColor(dst, hsv, ColorConversionCodes.BGR2HSV);
        var ch = Cv2.Split(hsv);
        ch[2].ConvertTo(ch[2], MatType.CV_8UC1, 1.0 / 32);
        ch[2].ConvertTo(ch[2], MatType.CV_8UC1, 32, 16);
        Cv2.Merge(ch, hsv);
        foreach (var c in ch) c.Dispose();
        Cv2.CvtColor(hsv, dst, ColorConversionCodes.HSV2BGR);

        // Soft glow.
        using var blur = new Mat();
        Cv2.GaussianBlur(dst, blur, new Size(0, 0), 6);
        Cv2.AddWeighted(dst, 0.8, blur, 0.4, 0, dst);
    }
}

// ---------------------------------------------------------------------------
// 9 — Liquid Dream (psychedelic): the picture melts in slow waves with colour fringing
// ---------------------------------------------------------------------------
public sealed class LiquidDream : Effect
{
    private Mat? _mapX, _mapY;
    private Size _mapSize;

    public override string Name => "Liquid Dream";
    public override string Category => "Psychedelic";
    public override string Description => "Reality melts. Walls breathe.";

    public override void Reset() { _mapX?.Dispose(); _mapY?.Dispose(); _mapX = _mapY = null; }

    private unsafe void BuildMaps(Size s, double t)
    {
        if (_mapX == null || _mapSize != s)
        {
            Reset();
            _mapSize = s;
            _mapX = new Mat(s, MatType.CV_32FC1);
            _mapY = new Mat(s, MatType.CV_32FC1);
        }
        int w = s.Width, h = s.Height;
        double amp = 16 + 8 * Math.Sin(t * 0.6);

        var rowX = new float[h]; var rowY = new float[h];
        var colX = new float[w]; var colY = new float[w];
        for (int y = 0; y < h; y++)
        {
            rowX[y] = (float)(amp * Math.Sin(y * 0.022 + t * 2.1));
            rowY[y] = (float)(amp * 0.4 * Math.Sin(y * 0.015 - t * 1.1));
        }
        for (int x = 0; x < w; x++)
        {
            colX[x] = (float)(amp * 0.5 * Math.Sin(x * 0.017 - t * 1.4));
            colY[x] = (float)(amp * Math.Sin(x * 0.02 + t * 1.7));
        }
        for (int y = 0; y < h; y++)
        {
            float* px = (float*)_mapX!.Ptr(y);
            float* py = (float*)_mapY!.Ptr(y);
            float ox = rowX[y], oy = rowY[y] + y;
            for (int x = 0; x < w; x++)
            {
                px[x] = x + ox + colX[x];
                py[x] = oy + colY[x];
            }
        }
    }

    public override void Apply(Mat src, Mat dst, EffectContext ctx)
    {
        BuildMaps(src.Size(), ctx.Time);
        Cv2.Remap(src, dst, _mapX!, _mapY!, InterpolationFlags.Linear, BorderTypes.Reflect101);
        Fx.RgbSplit(dst, (int)(7 + 5 * Math.Sin(ctx.Time * 2.2)), (int)(3 * Math.Sin(ctx.Time * 1.3)));
        Fx.HueShift(dst, (int)(ctx.Time * 12), 1.5);
    }
}
