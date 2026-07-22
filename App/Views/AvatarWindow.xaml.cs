using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.App.Views;

/// <summary>
/// Borderless PNG avatar window. Preloads pack sprites as frozen BitmapImages,
/// samples <see cref="PixelAvatarDriver"/> on CompositionTarget.Rendering.
/// Supports optional head/body layer split (v0.3) for head-tilt.
/// </summary>
public partial class AvatarWindow : Window
{
    private readonly PixelAvatarDriver _driver;
    private readonly AvatarRuntimeConfig _runtimeCfg;
    private readonly Dictionary<string, ImageSource> _bodyFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _headFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _poseFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _stickerFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _headFileMtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageSource> _placeholderLoop = [];

    private bool _rendering;
    private TimeSpan _lastRenderTime = TimeSpan.Zero;
    private string? _lastBodyState;
    private string? _lastFadeFrom;
    private int _placeholderIndex;
    private double _placeholderAccumMs;
    private bool _usePlaceholderSheet;
    private bool _usePoses;
    private bool _useLayered;
    private string? _lastPoseId;
    private ImageSource? _bodyBitmap;
    private double _canvasW = 1;
    private double _canvasH = 1;
    private float _neckPivotX = 627;
    private float _neckPivotY = 500;
    private double _cutY = 535;
    private double _headRotateBottomY = 515;
    private double _footPivotY = 1180;
    private BitmapScalingMode _scalingMode = BitmapScalingMode.Fant;
    private AvatarPackConfig _pack;

    public AvatarWindow(PixelAvatarDriver driver, AvatarRuntimeConfig runtimeCfg)
    {
        InitializeComponent();
        _driver = driver;
        _runtimeCfg = runtimeCfg;
        _pack = driver.Pack;

        TitleText.Text = driver.Pack.Meta.Name;
        ApplyWindowChrome();
        PreloadAssets();
        ApplyScalingMode();
        ApplyCanvasSize();

        if (_usePlaceholderSheet || string.Equals(driver.Pack.Meta.Name, "dev_placeholder", StringComparison.OrdinalIgnoreCase))
            TitleText.Text = "dev_placeholder（缺立绘，检查 assets/avatar）";

        _driver.AvatarConfigReloaded += OnAvatarConfigReloaded;
        Loaded += (_, _) => StartRendering();
        Closed += (_, _) =>
        {
            _driver.AvatarConfigReloaded -= OnAvatarConfigReloaded;
            StopRendering();
        };
    }

    private void ApplyWindowChrome()
    {
        Topmost = _runtimeCfg.Topmost;
        if (_runtimeCfg.WindowWidth > 0) Width = _runtimeCfg.WindowWidth;
        if (_runtimeCfg.WindowHeight > 0) Height = _runtimeCfg.WindowHeight;

        if (_runtimeCfg.AllowsTransparency)
        {
            // Full transparency option — may disable HW accel on some drivers.
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;
        }
        else
        {
            Background = ParseBrush(_runtimeCfg.BackgroundColor, Brushes.Lime);
        }
    }

    private void PreloadAssets()
    {
        _pack = _driver.Pack;
        var dir = _driver.AssetsDirectory;
        _canvasW = Math.Max(1, _pack.Meta.Canvas.Width);
        _canvasH = Math.Max(1, _pack.Meta.Canvas.Height);

        _usePoses = TryLoadPoses(_pack, dir);
        if (_usePoses)
        {
            _useLayered = false;
            HeadLayer.Visibility = Visibility.Collapsed;
            ApplyHeadRotateClip();
            LoadSingleLayerBodies(_pack, dir);
            DebugLog.Write($"[AvatarWindow] poses mode on: {_poseFrames.Count} poses (layers skipped)");
        }
        else
        {
            _useLayered = TryLoadLayered(_pack, dir);

            if (!_useLayered)
            {
                HeadLayer.Visibility = Visibility.Collapsed;
                ApplyHeadRotateClip();
                LoadSingleLayerBodies(_pack, dir);
                if (_pack.Layers is { Enabled: true })
                {
                    DebugLog.Write("[AvatarWindow] layers.enabled but layered load failed — single-layer fallback (tilt→whole body)");
                    TitleText.Text = $"{_pack.Meta.Name}（单图层回退）";
                }
            }
        }

        // If no HD frames but placeholder exists, use it (single-layer only).
        if (!_useLayered && _bodyFrames.Count == 0)
        {
            var idle = AvatarConfigLoader.ResolveDevPlaceholderIdle(dir);
            if (idle is not null)
            {
                _usePlaceholderSheet = true;
                LoadPlaceholderSheet(idle);
            }
        }

        _stickerFrames.Clear();
        foreach (var (id, item) in _pack.Stickers.Items)
        {
            var path = Path.Combine(dir, item.File);
            if (!File.Exists(path)) continue;
            try
            {
                _stickerFrames[id] = LoadFrozenBitmap(path);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[AvatarWindow] sticker load failed {path}: {ex.Message}");
            }
        }

        SeedFirstFrame();
        ApplyBodyPivot();
    }

    /// <summary>Foot pivot from <c>meta.pivot</c> — ScaleY must anchor here (A1), not canvas bottom.</summary>
    private void ApplyBodyPivot()
    {
        _footPivotY = _pack.Meta.Pivot.Y;
        var pivotX = _pack.Meta.Pivot.X / _canvasW;
        var pivotY = _footPivotY / _canvasH;
        BodyLayer.RenderTransformOrigin = new Point(
            Math.Clamp(pivotX, 0, 1),
            Math.Clamp(pivotY, 0, 1));
    }

    /// <summary>Lock body/head layers to pack canvas pixels so neck_pivot maps 1:1 onto HeadTilt.Center.</summary>
    private void ApplyCanvasSize()
    {
        BodyLayer.Width = _canvasW;
        BodyLayer.Height = _canvasH;
        BodyImageA.Width = _canvasW;
        BodyImageA.Height = _canvasH;
        BodyImageB.Width = _canvasW;
        BodyImageB.Height = _canvasH;
        HeadLayer.Width = _canvasW;
        HeadLayer.Height = _canvasH;
        HeadImageA.Width = _canvasW;
        HeadImageA.Height = _canvasH;
        HeadImageB.Width = _canvasW;
        HeadImageB.Height = _canvasH;
        ApplyHeadPivot();
    }

    private void ApplyHeadPivot()
    {
        // Prefer RotateTransform.CenterX/Y in canvas pixels over RenderTransformOrigin fractions —
        // fractions break when the head layer's layout size ≠ the painted bitmap.
        HeadTilt.CenterX = _neckPivotX;
        HeadTilt.CenterY = _neckPivotY;
        ApplyHeadRotateClip();
    }

    /// <summary>
    /// Clip head bitmaps above shoulder flare so tilt only moves face/neck; shoulders stay on body.
    /// </summary>
    private void ApplyHeadRotateClip()
    {
        // Called from TryLoadLayered before _useLayered is assigned by the caller — key off Visibility.
        if (HeadLayer.Visibility != Visibility.Visible
            || _headRotateBottomY <= 0
            || _headRotateBottomY >= _canvasH)
        {
            HeadImageA.Clip = null;
            HeadImageB.Clip = null;
            return;
        }

        var rect = new RectangleGeometry(new Rect(0, 0, _canvasW, _headRotateBottomY));
        if (rect.CanFreeze) rect.Freeze();
        HeadImageA.Clip = rect;
        HeadImageB.Clip = rect;
    }

    private bool TryLoadLayered(AvatarPackConfig pack, string dir)
    {
        _headFrames.Clear();
        _headFileMtimes.Clear();
        _bodyBitmap = null;

        if (pack.Layers is not { Enabled: true } layers)
            return false;
        if (string.IsNullOrWhiteSpace(layers.Body))
            return false;

        var bodyPath = Path.Combine(dir, layers.Body);
        var headDirRel = layers.HeadDir.TrimEnd('/', '\\');
        var headDir = Path.Combine(dir, headDirRel);
        if (!File.Exists(bodyPath) || !Directory.Exists(headDir))
        {
            DebugLog.Write("[AvatarWindow] layers.enabled but body/head assets missing — fallback single-layer");
            return false;
        }

        try
        {
            _bodyBitmap = LoadFrozenBitmap(bodyPath);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[AvatarWindow] layered body load failed: {ex.Message}");
            return false;
        }

        foreach (var (name, def) in pack.States)
        {
            if (string.IsNullOrWhiteSpace(def.File)) continue;
            var fileName = Path.GetFileName(def.File);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            var path = Path.Combine(headDir, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                _headFrames[name] = LoadFrozenBitmap(path);
                _headFileMtimes[path] = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[AvatarWindow] head load failed {path}: {ex.Message}");
            }
        }

        if (_headFrames.Count == 0)
        {
            DebugLog.Write("[AvatarWindow] no head sprites loaded — fallback single-layer");
            _bodyBitmap = null;
            return false;
        }

        // body.png paints a second upright 呆毛 + 蕾丝 under the head. Punch those out so
        // tilt only shows the rotating head's lace/ahoge (not a ghost copy on the body).
        if (_bodyBitmap is not null)
        {
            ImageSource? maskSrc = null;
            if (_headFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutralHead))
                maskSrc = neutralHead;
            else if (_headFrames.Count > 0)
                maskSrc = _headFrames.Values.First();

            if (maskSrc is BitmapSource punchMask)
            {
                try
                {
                    var bodySrc = _bodyBitmap as BitmapSource
                                  ?? throw new InvalidOperationException("body is not BitmapSource");
                    _bodyBitmap = PunchBodyHeadDecorations(bodySrc, punchMask);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"[AvatarWindow] body decoration punch failed: {ex.Message}");
                }
            }
        }

        _neckPivotX = layers.NeckPivot.X;
        _neckPivotY = layers.NeckPivot.Y;
        // Breath follow uses cut_y (not neck_pivot / body top). Fallback if pack omits cut_y.
        _cutY = layers.CutY > 0 ? layers.CutY : layers.NeckPivot.Y;
        // Shoulder flare starts ~516 on this pack; clip rotating head above it.
        _headRotateBottomY = layers.HeadRotateBottomY > 0
            ? layers.HeadRotateBottomY
            : Math.Min(_cutY, 515);
        HeadLayer.Visibility = Visibility.Visible;
        ApplyHeadPivot();

        BodyImageA.Source = _bodyBitmap;
        BodyImageA.Opacity = 1;
        BodyImageB.Opacity = 0;
        BodyImageB.Source = null;

        DebugLog.Write($"[AvatarWindow] layered mode on: {_headFrames.Count} heads, body={layers.Body}");
        return true;
    }

    private bool TryLoadPoses(AvatarPackConfig pack, string dir)
    {
        _poseFrames.Clear();
        if (pack.Poses?.List is not { Count: > 0 } list)
            return false;

        var loaded = 0;
        foreach (var (id, def) in list)
        {
            if (string.IsNullOrWhiteSpace(def.File)) continue;
            var path = Path.Combine(dir, def.File);
            if (!File.Exists(path)) continue;
            try
            {
                _poseFrames[id] = LoadFrozenBitmap(path);
                loaded++;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[AvatarWindow] pose load failed {path}: {ex.Message}");
            }
        }

        return loaded > 0;
    }

    private void LoadSingleLayerBodies(AvatarPackConfig pack, string dir)
    {
        _bodyFrames.Clear();
        _usePlaceholderSheet = false;
        _placeholderLoop.Clear();

        foreach (var (name, def) in pack.States)
        {
            if (string.IsNullOrWhiteSpace(def.File)) continue;
            var path = Path.Combine(dir, def.File);
            if (!File.Exists(path)) continue;

            // Placeholder pack points every state at the Idle sheet — special-case that.
            if (IsPlaceholderSheet(path))
            {
                _usePlaceholderSheet = true;
                LoadPlaceholderSheet(path);
                continue;
            }

            try
            {
                _bodyFrames[name] = LoadFrozenBitmap(path);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[AvatarWindow] failed to load {path}: {ex.Message}");
            }
        }
    }

    private void SeedFirstFrame()
    {
        if (_usePoses)
        {
            var defaultId = _pack.Poses?.Default ?? PoseController.Front;
            if (_poseFrames.TryGetValue(defaultId, out var pose))
                BodyImageA.Source = pose;
            else if (_poseFrames.Count > 0)
                BodyImageA.Source = _poseFrames.Values.First();
            else if (_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutral))
                BodyImageA.Source = neutral;
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
            return;
        }

        if (_useLayered)
        {
            if (_headFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutral))
                HeadImageA.Source = neutral;
            else if (_headFrames.Count > 0)
                HeadImageA.Source = _headFrames.Values.First();
            HeadImageA.Opacity = 1;
            HeadImageB.Opacity = 0;
            return;
        }

        if (_usePlaceholderSheet && _placeholderLoop.Count > 0)
            BodyImageA.Source = _placeholderLoop[0];
        else if (_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out var bodyNeutral))
            BodyImageA.Source = bodyNeutral;
        else if (_bodyFrames.Count > 0)
            BodyImageA.Source = _bodyFrames.Values.First();
    }

    private void OnAvatarConfigReloaded(object? sender, EventArgs e)
    {
        // FileSystemWatcher callback may arrive off the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            _pack = _driver.Pack;
            TitleText.Text = _pack.Meta.Name;
            _canvasW = Math.Max(1, _pack.Meta.Canvas.Width);
            _canvasH = Math.Max(1, _pack.Meta.Canvas.Height);

            var dir = _driver.AssetsDirectory;
            var wasLayered = _useLayered;
            var needsHeadRebuild = WasLayeredHeadChanged(dir);

            _usePoses = TryLoadPoses(_pack, dir);
            if (_usePoses)
            {
                _useLayered = false;
                HeadLayer.Visibility = Visibility.Collapsed;
                if (_bodyFrames.Count == 0)
                    LoadSingleLayerBodies(_pack, dir);
                SeedFirstFrame();
                ApplyScalingMode();
                ApplyCanvasSize();
            }
            else if (wasLayered && _pack.Layers is { Enabled: true } layers && !needsHeadRebuild)
            {
                // Motion/tilt/cut params changed only — keep bitmaps, refresh pivots.
                _neckPivotX = layers.NeckPivot.X;
                _neckPivotY = layers.NeckPivot.Y;
                _cutY = layers.CutY > 0 ? layers.CutY : layers.NeckPivot.Y;
                _headRotateBottomY = layers.HeadRotateBottomY > 0
                    ? layers.HeadRotateBottomY
                    : Math.Min(_cutY, 515);
                ApplyCanvasSize();
            }
            else
            {
                // Full asset refresh (do not touch _bodyFrames cache requirement is for
                // hot-reload of motion params; when layer mode flips we must rebuild).
                _useLayered = TryLoadLayered(_pack, dir);
                if (!_useLayered)
                {
                    HeadLayer.Visibility = Visibility.Collapsed;
                    // Hard requirement: do not rebuild _bodyFrames on hot-reload of motion-only
                    // changes. When leaving layered mode or first entering single-layer, load once.
                    if (_bodyFrames.Count == 0)
                        LoadSingleLayerBodies(_pack, dir);
                }
                SeedFirstFrame();
                ApplyScalingMode();
                ApplyCanvasSize();
            }

            ApplyBodyPivot();
            _lastBodyState = null; // force ApplyBody to re-bind sources next frame
            _lastPoseId = null;
        });
    }

    private bool WasLayeredHeadChanged(string dir)
    {
        if (_pack.Layers is not { Enabled: true } layers) return true;
        var headDir = Path.Combine(dir, layers.HeadDir.TrimEnd('/', '\\'));
        if (!Directory.Exists(headDir)) return true;

        foreach (var (name, def) in _pack.States)
        {
            if (string.IsNullOrWhiteSpace(def.File)) continue;
            var fileName = Path.GetFileName(def.File);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            var path = Path.Combine(headDir, fileName);
            if (!File.Exists(path))
            {
                if (_headFrames.ContainsKey(name)) return true;
                continue;
            }
            var mtime = File.GetLastWriteTimeUtc(path);
            if (!_headFileMtimes.TryGetValue(path, out var prev) || prev != mtime)
                return true;
            if (!_headFrames.ContainsKey(name))
                return true;
        }
        return false;
    }

    private void LoadPlaceholderSheet(string path)
    {
        _placeholderLoop.Clear();
        try
        {
            var sheet = LoadFrozenBitmap(path);
            const int frame = 144;
            // Idle sheet: 720×576 → 5×4 cells of 144; use first row (5 frames).
            var cols = Math.Max(1, (int)(sheet.PixelWidth / frame));
            var count = Math.Min(5, cols);
            for (var i = 0; i < count; i++)
            {
                var crop = new CroppedBitmap(sheet, new Int32Rect(i * frame, 0, frame, frame));
                if (crop.CanFreeze) crop.Freeze();
                _placeholderLoop.Add(crop);
            }
            DebugLog.Write($"[AvatarWindow] placeholder sheet loaded: {_placeholderLoop.Count} frames from {path}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[AvatarWindow] placeholder sheet failed: {ex.Message}");
        }
    }

    private static bool IsPlaceholderSheet(string path)
        => path.Contains("dev_placeholder", StringComparison.OrdinalIgnoreCase)
           && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           && (path.Contains("Idle", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Run", StringComparison.OrdinalIgnoreCase));

    private void ApplyScalingMode()
    {
        _scalingMode = _runtimeCfg.ScalingMode?.ToLowerInvariant() switch
        {
            "nearest" => BitmapScalingMode.NearestNeighbor,
            "linear" or "fant" or "highquality" => BitmapScalingMode.HighQuality,
            _ => _usePlaceholderSheet ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality,
        };

        RenderOptions.SetBitmapScalingMode(BodyImageA, _scalingMode);
        RenderOptions.SetBitmapScalingMode(BodyImageB, _scalingMode);
        RenderOptions.SetBitmapScalingMode(HeadImageA, _scalingMode);
        RenderOptions.SetBitmapScalingMode(HeadImageB, _scalingMode);
        RenderOptions.SetBitmapScalingMode(StickerImage, BitmapScalingMode.HighQuality);
    }

    private static BitmapImage LoadFrozenBitmap(string path)
    {
        // StreamSource + OnLoad: .NET 10 UriSource can fail on some NTFS-wrapped PNGs
        // ("未找到适用于完成此操作的图像处理组件") even when the logical bytes are valid.
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8
            || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            throw new InvalidDataException(
                $"Not a PNG (bad magic, len={bytes.Length}): {path}. " +
                "Re-save layered avatar assets so the file starts with \\x89PNG.");
        }

        using var ms = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Clears body pixels that duplicate the head's 呆毛 and 蕾丝 (white frill + dark outline).
    /// Leaves face/eyes/neck alone for the seam.
    /// </summary>
    private static ImageSource PunchBodyHeadDecorations(BitmapSource body, BitmapSource head)
    {
        var body32 = EnsureBgra32(body);
        var head32 = EnsureBgra32(head);
        var w = body32.PixelWidth;
        var h = body32.PixelHeight;
        if (head32.PixelWidth != w || head32.PixelHeight != h)
            throw new InvalidOperationException(
                $"body/head size mismatch: {w}x{h} vs {head32.PixelWidth}x{head32.PixelHeight}");

        const int bytesPerPixel = 4;
        var stride = w * bytesPerPixel;
        var bodyPx = new byte[stride * h];
        var headPx = new byte[stride * h];
        body32.CopyPixels(bodyPx, stride, 0);
        head32.CopyPixels(headPx, stride, 0);

        var yMax = Math.Min(h, 220);
        var punched = 0;
        for (var y = 0; y < yMax; y++)
        {
            var row = y * stride;
            for (var x = 0; x < w; x++)
            {
                var i = row + x * bytesPerPixel;
                var ba = bodyPx[i + 3];
                if (ba < 16) continue;

                var ha = headPx[i + 3];
                var hb = headPx[i];
                var hg = headPx[i + 1];
                var hr = headPx[i + 2];
                var bb = bodyPx[i];
                var bg = bodyPx[i + 1];
                var br = bodyPx[i + 2];

                // Ahoge tip + fringe residuals just outside the head matte.
                var inAhogeRect = y is >= 55 and <= 85 && x is >= 555 and <= 610;
                var ahogeHit = y < 80 && (ha > 16 || inAhogeRect);

                // Lace: white frill + dark blue/purple outline under the headband.
                var laceHit = y is >= 66 and < 205 && ha > 16
                              && (IsLaceLike(hr, hg, hb) || IsLaceLike(br, bg, bb));

                if (!ahogeHit && !laceHit) continue;

                bodyPx[i] = 0;
                bodyPx[i + 1] = 0;
                bodyPx[i + 2] = 0;
                bodyPx[i + 3] = 0;
                punched++;
            }
        }

        var wb = new WriteableBitmap(w, h, body32.DpiX, body32.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), bodyPx, stride, 0);
        if (wb.CanFreeze) wb.Freeze();
        DebugLog.Write($"[AvatarWindow] body decoration punch cleared {punched} px");
        return wb;
    }

    /// <summary>White lace frill or dark outline (blue-biased, not skin/hair midtones).</summary>
    private static bool IsLaceLike(byte r, byte g, byte b)
    {
        if (r >= 198 && g >= 198 && b >= 215)
            return true; // near-white lace
        // dark blue/purple outline around lace
        if (b >= Math.Max(r, g) + 3 && b >= 70 && (r + g + b) <= 450)
            return true;
        return false;
    }

    private static BitmapSource EnsureBgra32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32)
            return src;
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        if (converted.CanFreeze) converted.Freeze();
        return converted;
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        _ = _driver.StartAsync();
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var args = (RenderingEventArgs)e;
        // CompositionTarget.Rendering can fire multiple times with the same RenderingTime
        // in one frame. deltaMs==0 must skip — substituting 16.67 would advance the state
        // machine / motion engine an extra "frame" and looks like random speed jitter.
        if (_lastRenderTime != TimeSpan.Zero && args.RenderingTime == _lastRenderTime)
            return;

        double deltaMs = _lastRenderTime == TimeSpan.Zero
            ? 16.67
            : (args.RenderingTime - _lastRenderTime).TotalMilliseconds;
        _lastRenderTime = args.RenderingTime;

        if (deltaMs <= 0)
            return;
        if (deltaMs > 250)
            deltaMs = 16.67; // hitch clamp only — not for zero-delta re-entries

        var sample = _driver.Sample(deltaMs);
        ApplyBody(sample, deltaMs);
        ApplyMotion(sample.Motion);
        ApplySticker(sample.State.Sticker);

        // Live readout for Monitor QA.
        if (_usePoses)
            TitleText.Text = $"{_pack.Meta.Name}  pose {sample.Pose.PoseId}";
        else if (_useLayered)
        {
            var tilt = sample.Motion.TiltDeg;
            TitleText.Text = Math.Abs(tilt) < 0.05f
                ? $"{_pack.Meta.Name}  tilt 0°"
                : $"{_pack.Meta.Name}  tilt {tilt:+0.0;-0.0}°";
        }
    }

    private void ApplyBody(AvatarRenderSample sample, double deltaMs)
    {
        var state = sample.State.BodyState;

        if (_usePoses)
        {
            ApplyPoseBody(sample, deltaMs, state);
            return;
        }

        if (_useLayered)
        {
            ApplyLayeredHead(sample, state);
            return;
        }

        if (_usePlaceholderSheet && _placeholderLoop.Count > 0)
        {
            // Dev sheet: loop idle frames for "alive" feel. Prefer a real sprite if one loaded for this state.
            if (_bodyFrames.TryGetValue(state, out var stateFrame))
            {
                BodyImageA.Source = stateFrame;
            }
            else
            {
                _placeholderAccumMs += deltaMs;
                const double frameMs = 120;
                if (_placeholderAccumMs >= frameMs)
                {
                    _placeholderAccumMs = 0;
                    _placeholderIndex = (_placeholderIndex + 1) % _placeholderLoop.Count;
                    BodyImageA.Source = _placeholderLoop[_placeholderIndex];
                }
            }
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
            _lastBodyState = state;
            return;
        }

        if (!_bodyFrames.TryGetValue(state, out var current))
        {
            if (!_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out current))
                return;
        }

        var fading = sample.State.FadeFromState is not null && sample.State.FadeT < 1f;
        if (fading && sample.State.FadeFromState is { } from
            && _bodyFrames.TryGetValue(from, out var prev))
        {
            BodyImageB.Source = prev;
            BodyImageA.Source = current;
            BodyImageA.Opacity = sample.State.FadeT;
            BodyImageB.Opacity = 1f - sample.State.FadeT;
        }
        else
        {
            if (!string.Equals(_lastBodyState, state, StringComparison.OrdinalIgnoreCase))
                BodyImageA.Source = current;
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
        }

        _lastBodyState = state;
        _lastFadeFrom = sample.State.FadeFromState;
    }

    private void ApplyPoseBody(AvatarRenderSample sample, double deltaMs, string state)
    {
        var pose = sample.Pose;
        var fading = pose.FadeFromPoseId is not null && pose.FadeT < 1f;

        // Settled on front: full expression / mouth / blink via sprite states.
        if (pose.FullExpression && !fading)
        {
            ApplyExpressionBody(sample, deltaMs, state);
            _lastPoseId = pose.PoseId;
            return;
        }

        if (fading && pose.FadeFromPoseId is { } fromId)
        {
            var fromSrc = ResolvePoseBitmap(fromId, state, useExpressionOnFront: false);
            var toSrc = ResolvePoseBitmap(pose.PoseId, state, useExpressionOnFront: false);
            if (fromSrc is not null && toSrc is not null)
            {
                BodyImageB.Source = fromSrc;
                BodyImageA.Source = toSrc;
                BodyImageA.Opacity = pose.FadeT;
                BodyImageB.Opacity = 1f - pose.FadeT;
            }
        }
        else if (ResolvePoseBitmap(pose.PoseId, state, useExpressionOnFront: false) is { } current)
        {
            if (!string.Equals(_lastPoseId, pose.PoseId, StringComparison.OrdinalIgnoreCase))
                BodyImageA.Source = current;
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
        }

        _lastPoseId = pose.PoseId;
        _lastBodyState = state;
    }

    /// <summary>
    /// Front pose during cross-fade: prefer poses/front.png for foot alignment; else expression sprite.
    /// Settled front uses expression sprites via <see cref="ApplyExpressionBody"/>.
    /// </summary>
    private ImageSource? ResolvePoseBitmap(string poseId, string bodyState, bool useExpressionOnFront)
    {
        var isFront = string.Equals(poseId, PoseController.Front, StringComparison.OrdinalIgnoreCase);

        if (isFront && useExpressionOnFront)
        {
            if (_bodyFrames.TryGetValue(bodyState, out var expr)) return expr;
            if (_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutral)) return neutral;
        }

        if (_poseFrames.TryGetValue(poseId, out var pose)) return pose;

        if (isFront)
        {
            if (_bodyFrames.TryGetValue(bodyState, out var expr)) return expr;
            if (_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutral)) return neutral;
        }

        return null;
    }

    private void ApplyExpressionBody(AvatarRenderSample sample, double deltaMs, string state)
    {
        if (_usePlaceholderSheet && _placeholderLoop.Count > 0)
        {
            if (_bodyFrames.TryGetValue(state, out var stateFrame))
                BodyImageA.Source = stateFrame;
            else
            {
                _placeholderAccumMs += deltaMs;
                const double frameMs = 120;
                if (_placeholderAccumMs >= frameMs)
                {
                    _placeholderAccumMs = 0;
                    _placeholderIndex = (_placeholderIndex + 1) % _placeholderLoop.Count;
                    BodyImageA.Source = _placeholderLoop[_placeholderIndex];
                }
            }
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
            _lastBodyState = state;
            return;
        }

        if (!_bodyFrames.TryGetValue(state, out var current))
        {
            if (!_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out current))
                return;
        }

        var fading = sample.State.FadeFromState is not null && sample.State.FadeT < 1f;
        if (fading && sample.State.FadeFromState is { } from
            && _bodyFrames.TryGetValue(from, out var prev))
        {
            BodyImageB.Source = prev;
            BodyImageA.Source = current;
            BodyImageA.Opacity = sample.State.FadeT;
            BodyImageB.Opacity = 1f - sample.State.FadeT;
        }
        else
        {
            if (!string.Equals(_lastBodyState, state, StringComparison.OrdinalIgnoreCase))
                BodyImageA.Source = current;
            BodyImageA.Opacity = 1;
            BodyImageB.Opacity = 0;
        }

        _lastBodyState = state;
        _lastFadeFrom = sample.State.FadeFromState;
    }

    private void ApplyLayeredHead(AvatarRenderSample sample, string state)
    {
        // Body stays pinned to the single body bitmap for the whole session.
        if (BodyImageA.Source != _bodyBitmap && _bodyBitmap is not null)
            BodyImageA.Source = _bodyBitmap;
        BodyImageA.Opacity = 1;
        BodyImageB.Opacity = 0;

        if (!_headFrames.TryGetValue(state, out var current))
        {
            if (!_headFrames.TryGetValue(AvatarStateMachine.Neutral, out current))
                return;
        }

        var fading = sample.State.FadeFromState is not null && sample.State.FadeT < 1f;
        if (fading && sample.State.FadeFromState is { } from
            && _headFrames.TryGetValue(from, out var prev))
        {
            HeadImageB.Source = prev;
            HeadImageA.Source = current;
            HeadImageA.Opacity = sample.State.FadeT;
            HeadImageB.Opacity = 1f - sample.State.FadeT;
        }
        else
        {
            if (!string.Equals(_lastBodyState, state, StringComparison.OrdinalIgnoreCase))
                HeadImageA.Source = current;
            HeadImageA.Opacity = 1;
            HeadImageB.Opacity = 0;
        }

        _lastBodyState = state;
        _lastFadeFrom = sample.State.FadeFromState;
    }

    private void ApplyMotion(MotionFrame m)
    {
        var ox = m.OffsetX;
        var oy = m.OffsetY;
        if (_runtimeCfg.SnapMotionToPixels)
        {
            ox = MathF.Round(ox);
            oy = MathF.Round(oy);
        }

        BodyTranslate.X = ox;
        BodyTranslate.Y = oy;
        BodyScale.ScaleX = m.ScaleX;
        BodyScale.ScaleY = m.ScaleY;
        // m.Alpha reserved for global fades; body cross-fade uses Image opacity.

        if (_useLayered)
        {
            // Layered: body stays planted; sway + intentional tilt both go to the head.
            BodyRotate.Angle = 0;
            HeadTilt.Angle = m.TiltDeg + m.RotationDeg;
            HeadTranslate.X = 0;
            HeadTranslate.Y = 0;
        }
        else
        {
            // Poses or single-layer: no head tilt — whole image rotates only.
            BodyRotate.Angle = m.RotationDeg;
            HeadTilt.Angle = 0;
            HeadTranslate.X = 0;
            HeadTranslate.Y = 0;
        }
    }

    private void ApplySticker(StickerFrame? sticker)
    {
        if (sticker is null)
        {
            StickerImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_stickerFrames.TryGetValue(sticker.Value.Id, out var src))
        {
            StickerImage.Visibility = Visibility.Collapsed;
            return;
        }

        StickerImage.Source = src;
        StickerImage.Visibility = Visibility.Visible;
        StickerImage.Opacity = sticker.Value.Alpha;
        StickerScale.ScaleX = sticker.Value.Scale;
        StickerScale.ScaleY = sticker.Value.Scale;

        // Map pack-canvas anchor into the window's sticker canvas.
        var canvasW = Math.Max(1.0, _pack.Meta.Canvas.Width);
        var canvasH = Math.Max(1.0, _pack.Meta.Canvas.Height);
        var sx = StickerCanvas.ActualWidth > 0 ? StickerCanvas.ActualWidth / canvasW : Width / canvasW;
        var sy = StickerCanvas.ActualHeight > 0 ? StickerCanvas.ActualHeight / canvasH : Height / canvasH;

        var x = sticker.Value.AnchorX * sx;
        var y = sticker.Value.AnchorY * sy;

        // Centre sticker on anchor.
        if (src is BitmapSource bs)
        {
            x -= bs.PixelWidth * sticker.Value.Scale * sx * 0.5 * (canvasW / Math.Max(1, bs.PixelWidth));
            // Simpler: after layout, use DesiredSize
        }

        // Use post-layout size when available
        StickerImage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dw = StickerImage.DesiredSize.Width * sticker.Value.Scale;
        var dh = StickerImage.DesiredSize.Height * sticker.Value.Scale;
        if (dw > 0)
        {
            System.Windows.Controls.Canvas.SetLeft(StickerImage, sticker.Value.AnchorX * sx - dw / 2);
            System.Windows.Controls.Canvas.SetTop(StickerImage, sticker.Value.AnchorY * sy - dh / 2);
        }
        else
        {
            System.Windows.Controls.Canvas.SetLeft(StickerImage, x);
            System.Windows.Controls.Canvas.SetTop(StickerImage, y);
        }
    }

    private void OnChromeDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static Brush ParseBrush(string? hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var converted = ColorConverter.ConvertFromString(hex);
            if (converted is Color c)
                return new SolidColorBrush(c);
        }
        catch { /* use fallback */ }
        return fallback;
    }
}
