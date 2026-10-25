using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace SpookyCam;

public sealed class MainForm : Form
{
    private readonly PictureBox _view;
    private readonly Effect[] _effects = EffectCatalog.All;
    private readonly JumpScare _scare;
    private readonly EffectContext _ctx = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _worker;
    private volatile bool _running;
    private volatile int _current;
    private volatile bool _mirror = true;
    private volatile bool _hud = true;
    private volatile bool _saveRequested;
    private volatile int _requestedCamera;
    private int _pendingFrames;

    private volatile string _toast = "";
    private double _toastUntil;

    private bool _fullscreen;
    private FormWindowState _prevState;
    private Rectangle _prevBounds;

    public MainForm()
    {
        _scare = _effects.OfType<JumpScare>().First();
        Text = "SpookyCam";
        BackColor = Color.Black;
        ClientSize = new System.Drawing.Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;

        _view = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
        };
        Controls.Add(_view);

        KeyDown += OnKeyDown;
        Load += (_, _) => Start();
        FormClosing += (_, _) => Stop();
        FormClosed += (_, _) => ScareSound.Close();
    }

    private void Start()
    {
        _running = true;
        _worker = new Thread(CaptureLoop) { IsBackground = true, Name = "camera" };
        _worker.Start();
        ShowToast("1-9 effects  0 normal  5 jump scare  F fullscreen  M mirror  S snapshot  H hide help");
        if (!_scare.HasImage) ShowToast("Jump scare image missing: put jumpscare.jpg in the assets folder");
    }

    private void Stop()
    {
        _running = false;
        _worker?.Join(1500);
    }

    // ------------------------------------------------------------------ input

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        int? digit = e.KeyCode switch
        {
            >= Keys.D0 and <= Keys.D9 => e.KeyCode - Keys.D0,
            >= Keys.NumPad0 and <= Keys.NumPad9 => e.KeyCode - Keys.NumPad0,
            _ => null,
        };
        if (digit is int d && d < _effects.Length)
        {
            if (_effects[d] is JumpScare) TriggerScare();
            else SelectEffect(d);
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Right:
            case Keys.Space:
                SelectEffect(NextEffect(_current, +1));
                break;
            case Keys.Left:
                SelectEffect(NextEffect(_current, -1));
                break;
            case Keys.F:
            case Keys.F11:
                ToggleFullscreen();
                break;
            case Keys.Escape:
                if (_fullscreen) ToggleFullscreen(); else Close();
                break;
            case Keys.M:
                _mirror = !_mirror;
                ShowToast(_mirror ? "Mirror on" : "Mirror off");
                break;
            case Keys.H:
                _hud = !_hud;
                break;
            case Keys.S:
                _saveRequested = true;
                break;
            case Keys.C:
                _requestedCamera = (_requestedCamera + 1) % 4;
                break;
        }
    }

    private void SelectEffect(int idx)
    {
        _current = idx;
        ShowToast($"{idx}  {_effects[idx].Name}");
    }

    /// <summary>Step through the effects, skipping the jump scare so cycling never triggers it by accident.</summary>
    private int NextEffect(int from, int step)
    {
        int idx = from;
        do idx = (idx + step + _effects.Length) % _effects.Length;
        while (_effects[idx] is JumpScare);
        return idx;
    }

    private void TriggerScare()
    {
        _scare.Trigger(_clock.Elapsed.TotalSeconds);
        ScareSound.Play(JumpScare.SoundPath);
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var screen = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
            _fullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            Bounds = _prevBounds;
            if (_prevState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            _fullscreen = false;
        }
    }

    private void ShowToast(string text)
    {
        _toast = text;
        _toastUntil = _clock.Elapsed.TotalSeconds + 3.0;
    }

    // ------------------------------------------------------------------ capture

    private static VideoCapture? OpenCamera(int index)
    {
        var cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            cap = new VideoCapture(index);
        }
        if (!cap.IsOpened())
        {
            cap.Dispose();
            return null;
        }
        // MJPG lets USB 2 webcams deliver 720p at 30 fps; uncompressed YUY2 is often capped at 10 fps.
        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        cap.Set(VideoCaptureProperties.FrameWidth, 1280);
        cap.Set(VideoCaptureProperties.FrameHeight, 720);
        cap.Set(VideoCaptureProperties.Fps, 30);
        return cap;
    }

    private void CaptureLoop()
    {
        VideoCapture? cap = null;
        int camIndex = -1;
        using var frame = new Mat();
        using var output = new Mat();
        var fpsWatch = Stopwatch.StartNew();
        int frames = 0;
        double fps = 0;
        int lastIdx = -1;

        try
        {
            while (_running)
            {
                if (cap == null || camIndex != _requestedCamera)
                {
                    cap?.Dispose();
                    camIndex = _requestedCamera;
                    cap = OpenCamera(camIndex);
                    if (cap == null)
                    {
                        using var blank = new Mat(new CvSize(1280, 720), MatType.CV_8UC3, Scalar.Black);
                        Fx.Text(blank, $"No camera at index {camIndex}", new CvPoint(380, 340), 1.2, Scalar.White, 2);
                        Fx.Text(blank, "Press C to try the next camera, Esc to quit", new CvPoint(330, 400), 0.8, new Scalar(180, 180, 180), 1);
                        Present(blank);
                        for (int i = 0; i < 20 && _running && camIndex == _requestedCamera; i++) Thread.Sleep(100);
                        if (camIndex == _requestedCamera) _requestedCamera = (camIndex + 1) % 4;
                        continue;
                    }
                    ShowToast($"Camera {camIndex}  {cap.FrameWidth}x{cap.FrameHeight}");
                }

                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(5);
                    continue;
                }
                if (_mirror) Cv2.Flip(frame, frame, FlipMode.Y);

                _ctx.Time = _clock.Elapsed.TotalSeconds;
                int idx = _current;
                var fx = _effects[idx];
                if (idx != lastIdx)
                {
                    // Reset here, on the thread that uses the effect, so a key press never
                    // disposes buffers while a frame is being processed.
                    fx.Reset();
                    lastIdx = idx;
                }
                try
                {
                    fx.Apply(frame, output, _ctx);
                }
                catch (Exception ex)
                {
                    frame.CopyTo(output);
                    ShowToast("Effect error: " + ex.Message);
                }

                if (_saveRequested)
                {
                    _saveRequested = false;
                    Snapshot(output);
                }

                frames++;
                if (fpsWatch.ElapsedMilliseconds >= 500)
                {
                    fps = frames * 1000.0 / fpsWatch.ElapsedMilliseconds;
                    frames = 0;
                    fpsWatch.Restart();
                }

                bool scaring = _scare.IsActive(_ctx.Time);
                if (scaring) _scare.Overlay(output, _ctx);
                if (_hud && !scaring) DrawHud(output, idx, fx, fps);
                Present(output);
            }
        }
        finally
        {
            cap?.Dispose();
        }
    }

    private void Snapshot(Mat img)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SpookyCam");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"SpookyCam_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Cv2.ImWrite(path, img);
            ShowToast("Saved " + path);
        }
        catch (Exception ex)
        {
            ShowToast("Save failed: " + ex.Message);
        }
    }

    private void DrawHud(Mat img, int idx, Effect fx, double fps)
    {
        var accent = fx.Category switch
        {
            "Horror" => new Scalar(0, 140, 255),      // orange (BGR)
            "Psychedelic" => new Scalar(255, 80, 255), // magenta
            "Scare" => new Scalar(0, 0, 230),          // red
            _ => new Scalar(220, 220, 220),
        };

        Fx.Text(img, $"[{idx}] {fx.Name}", new CvPoint(20, 45), 1.1, accent, 2);
        Fx.Text(img, fx.Description, new CvPoint(22, 78), 0.6, new Scalar(230, 230, 230), 1);
        Fx.Text(img, $"{fps:F0} fps", new CvPoint(img.Width - 110, 40), 0.7, new Scalar(200, 200, 200), 1);

        // Key legend along the bottom, wrapping onto a second row if it does not fit.
        const double legendScale = 0.5;
        int x = 20;
        int rowHeight = 24;
        int y = img.Height - 22 - rowHeight;
        for (int i = 0; i < _effects.Length; i++)
        {
            string label = $"{i} {_effects[i].Name}";
            var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, legendScale, 2, out _);
            if (x + size.Width > img.Width - 20)
            {
                x = 20;
                y += rowHeight;
            }
            var col = i == idx ? accent : new Scalar(170, 170, 170);
            Fx.Text(img, label, new CvPoint(x, y), legendScale, col, i == idx ? 2 : 1);
            x += size.Width + 22;
        }

        if (_clock.Elapsed.TotalSeconds < _toastUntil && _toast.Length > 0)
        {
            var size = Cv2.GetTextSize(_toast, HersheyFonts.HersheySimplex, 0.7, 2, out _);
            Fx.Text(img, _toast, new CvPoint((img.Width - size.Width) / 2, img.Height - 60), 0.7, Scalar.White, 2);
        }
    }

    private void Present(Mat img)
    {
        if (Volatile.Read(ref _pendingFrames) > 1) return; // UI thread is behind; drop this frame
        if (!IsHandleCreated || IsDisposed) return;

        var bmp = BitmapConverter.ToBitmap(img);
        Interlocked.Increment(ref _pendingFrames);
        try
        {
            BeginInvoke(new Action(() =>
            {
                var old = _view.Image;
                _view.Image = bmp;
                old?.Dispose();
                Interlocked.Decrement(ref _pendingFrames);
            }));
        }
        catch (Exception)
        {
            bmp.Dispose();
            Interlocked.Decrement(ref _pendingFrames);
        }
    }
}

/// <summary>
/// Plays the jump scare MP3 through the Windows MCI API (winmm), so no extra packages are needed.
/// Call from the UI thread only: MCI ties an alias to the thread that opened it.
/// </summary>
internal static class ScareSound
{
    private const string Alias = "spookyscare";
    private static bool _open;
    private static string? _openPath;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLen, IntPtr callback);

    public static void Play(string path)
    {
        if (!File.Exists(path)) return;
        if (!_open || _openPath != path)
        {
            Close();
            if (mciSendString($"open \"{path}\" type mpegvideo alias {Alias}", null, 0, IntPtr.Zero) != 0) return;
            _open = true;
            _openPath = path;
        }
        mciSendString($"seek {Alias} to start", null, 0, IntPtr.Zero);
        mciSendString($"play {Alias}", null, 0, IntPtr.Zero);
    }

    public static void Close()
    {
        if (!_open) return;
        mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        _open = false;
        _openPath = null;
    }
}
