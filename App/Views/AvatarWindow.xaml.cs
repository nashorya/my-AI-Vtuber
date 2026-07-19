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
/// </summary>
public partial class AvatarWindow : Window
{
    private readonly PixelAvatarDriver _driver;
    private readonly AvatarRuntimeConfig _runtimeCfg;
    private readonly Dictionary<string, ImageSource> _bodyFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _stickerFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageSource> _placeholderLoop = [];

    private bool _rendering;
    private TimeSpan _lastRenderTime = TimeSpan.Zero;
    private string? _lastBodyState;
    private string? _lastFadeFrom;
    private int _placeholderIndex;
    private double _placeholderAccumMs;
    private bool _usePlaceholderSheet;
    private BitmapScalingMode _scalingMode = BitmapScalingMode.Fant;

    public AvatarWindow(PixelAvatarDriver driver, AvatarRuntimeConfig runtimeCfg)
    {
        InitializeComponent();
        _driver = driver;
        _runtimeCfg = runtimeCfg;

        TitleText.Text = driver.Pack.Meta.Name;
        ApplyWindowChrome();
        PreloadAssets();
        ApplyScalingMode();

        Loaded += (_, _) => StartRendering();
        Closed += (_, _) => StopRendering();
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
        var pack = _driver.Pack;
        var dir = _driver.AssetsDirectory;

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
                var bmp = LoadFrozenBitmap(path);
                _bodyFrames[name] = bmp;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"[AvatarWindow] failed to load {path}: {ex.Message}");
            }
        }

        // If no HD frames but placeholder exists, use it.
        if (_bodyFrames.Count == 0)
        {
            var idle = AvatarConfigLoader.ResolveDevPlaceholderIdle(dir);
            if (idle is not null)
            {
                _usePlaceholderSheet = true;
                LoadPlaceholderSheet(idle);
            }
        }

        foreach (var (id, item) in pack.Stickers.Items)
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

        // Seed first frame
        if (_usePlaceholderSheet && _placeholderLoop.Count > 0)
            BodyImageA.Source = _placeholderLoop[0];
        else if (_bodyFrames.TryGetValue(AvatarStateMachine.Neutral, out var neutral))
            BodyImageA.Source = neutral;
        else if (_bodyFrames.Count > 0)
            BodyImageA.Source = _bodyFrames.Values.First();

        // Pivot: RenderTransformOrigin is already foot-center (0.5, 1).
        // Fine-tune if pack pivot is not dead-center horizontally.
        var canvasW = Math.Max(1, pack.Meta.Canvas.Width);
        var pivotX = pack.Meta.Pivot.X / canvasW;
        BodyLayer.RenderTransformOrigin = new Point(Math.Clamp(pivotX, 0, 1), 1.0);
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
        var pack = _driver.Pack;
        var canvasW = Math.Max(1.0, pack.Meta.Canvas.Width);
        var canvasH = Math.Max(1.0, pack.Meta.Canvas.Height);
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
