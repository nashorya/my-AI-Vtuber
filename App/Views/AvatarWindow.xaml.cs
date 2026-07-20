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
    private bool _useLayered;
    private ImageSource? _bodyBitmap;
    private double _canvasW = 1;
    private double _canvasH = 1;
    private float _neckPivotX = 627;
    private float _neckPivotY = 500;
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

        _useLayered = TryLoadLayered(_pack, dir);

        if (!_useLayered)
        {
            HeadLayer.Visibility = Visibility.Collapsed;
            LoadSingleLayerBodies(_pack, dir);
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

        // Pivot: RenderTransformOrigin is already foot-center (0.5, 1).
        // Fine-tune if pack pivot is not dead-center horizontally.
        var pivotX = _pack.Meta.Pivot.X / _canvasW;
        BodyLayer.RenderTransformOrigin = new Point(Math.Clamp(pivotX, 0, 1), 1.0);
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

        _neckPivotX = layers.NeckPivot.X;
        _neckPivotY = layers.NeckPivot.Y;
        HeadLayer.Visibility = Visibility.Visible;
        HeadLayer.RenderTransformOrigin = new Point(
            Math.Clamp(_neckPivotX / _canvasW, 0, 1),
            Math.Clamp(_neckPivotY / _canvasH, 0, 1));

        BodyImageA.Source = _bodyBitmap;
        BodyImageA.Opacity = 1;
        BodyImageB.Opacity = 0;
        BodyImageB.Source = null;

        DebugLog.Write($"[AvatarWindow] layered mode on: {_headFrames.Count} heads, body={layers.Body}");
        return true;
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

            if (wasLayered && _pack.Layers is { Enabled: true } && !needsHeadRebuild)
            {
                // Motion/tilt params changed only — keep bitmaps, refresh neck pivot.
                _neckPivotX = _pack.Layers.NeckPivot.X;
                _neckPivotY = _pack.Layers.NeckPivot.Y;
                HeadLayer.RenderTransformOrigin = new Point(
                    Math.Clamp(_neckPivotX / _canvasW, 0, 1),
                    Math.Clamp(_neckPivotY / _canvasH, 0, 1));
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
            }

            var pivotX = _pack.Meta.Pivot.X / _canvasW;
            BodyLayer.RenderTransformOrigin = new Point(Math.Clamp(pivotX, 0, 1), 1.0);
            _lastBodyState = null; // force ApplyBody to re-bind sources next frame
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
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
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
    }

    private void ApplyBody(AvatarRenderSample sample, double deltaMs)
    {
        var state = sample.State.BodyState;

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
        BodyRotate.Angle = m.RotationDeg;
        // m.Alpha reserved for global fades; body cross-fade uses Image opacity.

        if (_useLayered)
        {
            HeadTilt.Angle = m.TiltDeg;
            // Body ScaleY about foot pivot lifts the neck; keep head seated on the neck.
            // WPF Y grows downward, so negative = visually upward.
            HeadTranslate.Y = -(_canvasH - _neckPivotY) * (m.ScaleY - 1.0);
        }
        else
        {
            HeadTilt.Angle = 0;
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
