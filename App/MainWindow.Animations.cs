using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace HonorControl;

public sealed partial class MainWindow
{
    private readonly UISettings _animationSettings = new();

    private void AnimatePage(UIElement page)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(page, true);
        var visual = ElementCompositionPreview.GetElementVisual(page);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!_animationSettings.AnimationsEnabled) return;

        var compositor = visual.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1);
        opacity.Duration = TimeSpan.FromMilliseconds(320);
        using var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(14, 0, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }
}
