using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Polaris.Services;

/// <summary>
/// Shared factory for the macOS-dock-style "launch bounce": the icon hops up
/// off the dock and falls back with a couple of small bounces. Used by both the
/// pinned icons and the running-strip tiles so clicking any dock icon makes it
/// jump once before the dock dismisses.
/// </summary>
internal static class DockBounce
{
    private static readonly Duration Total = new(TimeSpan.FromMilliseconds(520));
    private static readonly TimeSpan ApexAt = TimeSpan.FromMilliseconds(170);

    /// <summary>Translation bounce for one axis: rest(0) → <paramref name="lift"/>
    /// (the apex, eased out) → back to 0 with a landing bounce. Apply to a
    /// TranslateTransform's X/Y. Reverts to the base value when it ends.</summary>
    public static DoubleAnimationUsingKeyFrames BuildTranslate(double lift)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = Total,
            FillBehavior = FillBehavior.Stop,
        };
        // Quick leap up to the apex...
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            lift, KeyTime.FromTimeSpan(ApexAt),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        // ...then fall back down and bounce on landing.
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.0, KeyTime.FromTimeSpan(Total.TimeSpan),
            new BounceEase { Bounces = 2, Bounciness = 2.4, EasingMode = EasingMode.EaseOut }));
        return anim;
    }

    /// <summary>A subtle scale "pop" synced to the hop apex (1 → peak → 1) so the
    /// icon swells slightly as it jumps. Apply to a ScaleTransform's ScaleX/Y.</summary>
    public static DoubleAnimationUsingKeyFrames BuildScale(double peak = 1.2)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = Total,
            FillBehavior = FillBehavior.Stop,
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            peak, KeyTime.FromTimeSpan(ApexAt),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0, KeyTime.FromTimeSpan(Total.TimeSpan),
            new BounceEase { Bounces = 2, Bounciness = 2.4, EasingMode = EasingMode.EaseOut }));
        return anim;
    }
}
