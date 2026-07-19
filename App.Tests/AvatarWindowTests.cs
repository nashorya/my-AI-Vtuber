using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIVTuber.App.Views;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Config;

namespace AIVTuber.App.Tests;

public sealed class AvatarWindowTests
{
    [Fact]
    public void HeadBreath_UsesSynchronizedOverlappingLayers()
    {
        RunOnSta(() =>
        {
            var assetsDir = FindAvatarAssets();
            var pack = AvatarConfigLoader.Load(assetsDir);
            var driver = new PixelAvatarDriver(
                pack,
                assetsDir,
                AvatarConfigLoader.ResolveAvailableStates(pack, assetsDir));
            var window = new AvatarWindow(driver, new AvatarRuntimeConfig
            {
                WindowWidth = 480,
                WindowHeight = 480,
            });

            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            root.Measure(new Size(480, 480));
            root.Arrange(new Rect(0, 0, 480, 480));
            root.UpdateLayout();

            var bodyLayer = Assert.IsType<Grid>(window.FindName("BodyClipLayer"));
            var headLayer = Assert.IsType<Grid>(window.FindName("HeadLayer"));
            var bodyImage = Assert.IsType<Image>(window.FindName("BodyImageA"));
            var headImage = Assert.IsType<Image>(window.FindName("HeadImageA"));
            var bodyClip = Assert.IsType<RectangleGeometry>(bodyLayer.Clip);
            var headClip = Assert.IsType<RectangleGeometry>(headLayer.Clip);

            Assert.Equal(Visibility.Visible, headLayer.Visibility);
            Assert.Same(bodyImage.Source, headImage.Source);
            Assert.True(bodyClip.Rect.Y < headClip.Rect.Bottom, "head/body clips must overlap");
            Assert.True(bodyClip.Rect.Height > 0);
            Assert.True(headClip.Rect.Height > 0);

            window.Close();
        });
    }

    [Fact]
    public void BodyBreath_RestoresSingleUnclippedBodyLayer()
    {
        RunOnSta(() =>
        {
            var assetsDir = FindAvatarAssets();
            var pack = AvatarConfigLoader.Load(assetsDir);
            pack.MotionLayer.Breath.Target = "body";
            var driver = new PixelAvatarDriver(
                pack,
                assetsDir,
                AvatarConfigLoader.ResolveAvailableStates(pack, assetsDir));
            var window = new AvatarWindow(driver, new AvatarRuntimeConfig());

            var bodyLayer = Assert.IsType<Grid>(window.FindName("BodyClipLayer"));
            var headLayer = Assert.IsType<Grid>(window.FindName("HeadLayer"));

            Assert.Null(bodyLayer.Clip);
            Assert.Equal(Visibility.Collapsed, headLayer.Visibility);

            window.Close();
        });
    }

    private static string FindAvatarAssets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "avatar");
            if (File.Exists(Path.Combine(candidate, "avatar.json")))
                return candidate;
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "assets", "avatar");
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
