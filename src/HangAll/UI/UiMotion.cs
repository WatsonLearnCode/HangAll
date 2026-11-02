using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HangAll.UI;

internal static class UiMotion
{
    private const int QuickMilliseconds = 120;
    private const int StandardMilliseconds = 160;

    public static void RevealPage(FrameworkElement page)
    {
        var offset = page.RenderTransform as TranslateTransform;
        if (offset is null)
        {
            offset = new TranslateTransform();
            page.RenderTransform = offset;
            page.RenderTransformOrigin = new Point(0.5, 0);
        }

        page.BeginAnimation(UIElement.OpacityProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        page.Opacity = 1;
        offset.Y = 0;

        if (!SystemParameters.ClientAreaAnimation) return;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        page.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        offset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>
    /// Menu entrance: a short fade, a 2 pt rise and a faint scale-up, all from
    /// one transform group, so a menu grows out of the pointer instead of
    /// appearing at full size. Called on every open, so the group is rebuilt
    /// instead of being carried over with stale animation clocks.
    /// </summary>
    public static void PopIn(FrameworkElement element, int milliseconds = 115, double rise = 2, double fromScale = 0.97)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!SystemParameters.ClientAreaAnimation) return;

        var scale = new ScaleTransform(1, 1);
        var lift = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(lift);
        element.RenderTransform = group;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScale, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScale, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
        lift.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(rise, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>
    /// Brings a page's sections in one after another, 22 ms apart. Cheap enough
    /// to run on every category switch and it makes the page read top to bottom.
    /// </summary>
    public static void StaggerIn(Panel panel, int stepMilliseconds = 22, int durationMilliseconds = 150)
    {
        int staggered = Math.Min(panel.Children.Count, 10);
        for (int index = 0; index < panel.Children.Count; index++)
        {
            if (panel.Children[index] is not FrameworkElement child) continue;
            child.BeginAnimation(UIElement.OpacityProperty, null);
            if (child.RenderTransform is TranslateTransform offset)
                offset.BeginAnimation(TranslateTransform.YProperty, null);
            child.Opacity = 1;
            if (!SystemParameters.ClientAreaAnimation || index >= staggered) continue;

            if (child.RenderTransform is not TranslateTransform lift)
            {
                lift = new TranslateTransform();
                child.RenderTransform = lift;
            }
            lift.Y = 0;

            // Base values stay at the start of the motion while the delay runs,
            // then the animation holds its end value long enough to write the
            // real values back and hand the element over untouched.
            child.Opacity = 0;
            lift.Y = 4;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
            var delay = TimeSpan.FromMilliseconds(stepMilliseconds * index);
            var liftMotion = new DoubleAnimation(4, 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            };
            liftMotion.Completed += (_, _) =>
            {
                child.BeginAnimation(UIElement.OpacityProperty, null);
                lift.BeginAnimation(TranslateTransform.YProperty, null);
                child.Opacity = 1;
                lift.Y = 0;
            };
            child.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration)
                {
                    BeginTime = delay,
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd,
                });
            lift.BeginAnimation(TranslateTransform.YProperty, liftMotion);
        }
    }

    /// <summary>
    /// Lifts a surface a couple of points and deepens its shadow while the
    /// pointer is on it. The card keeps its place in the grid: only the render
    /// transform moves, so nothing below it reflows.
    /// </summary>
    public static void HoverLift(FrameworkElement element, double rise = 2, double shadowBoost = 0.08)
    {
        var shadow = element.Effect as System.Windows.Media.Effects.DropShadowEffect;
        double restShadow = shadow?.Opacity ?? 0;
        TranslateTransform? lift = element.RenderTransform as TranslateTransform;
        bool entered = false;

        void Move(bool up)
        {
            if (entered == up) return;
            entered = up;
            // Created on first use, so a card that is never hovered is rendered
            // exactly as before and keeps its sharp resting pixels.
            lift ??= new TranslateTransform();
            if (element.RenderTransform != lift) element.RenderTransform = lift;
            if (!SystemParameters.ClientAreaAnimation)
            {
                lift.Y = up ? -rise : 0;
                if (shadow is not null) shadow.Opacity = up ? restShadow + shadowBoost : restShadow;
                return;
            }
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(120);
            lift.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(lift.Y, up ? -rise : 0, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                },
                HandoffBehavior.SnapshotAndReplace);
            lift.Y = up ? -rise : 0;
            if (shadow is not null)
            {
                shadow.BeginAnimation(
                    System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(shadow.Opacity, up ? restShadow + shadowBoost : restShadow, duration)
                    {
                        EasingFunction = easing,
                        FillBehavior = FillBehavior.Stop,
                    },
                    HandoffBehavior.SnapshotAndReplace);
                shadow.Opacity = up ? restShadow + shadowBoost : restShadow;
            }
        }

        element.MouseEnter += (_, _) => Move(true);
        element.MouseLeave += (_, _) => Move(false);
    }

    public static void Fade(FrameworkElement element, bool visible, int milliseconds = QuickMilliseconds)
    {
        double from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = visible ? 1 : 0;
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (visible) element.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(from, visible ? 1 : 0, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (!visible)
        {
            animation.Completed += (_, _) =>
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 0;
                element.Visibility = Visibility.Collapsed;
            };
        }
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void MoveAndResize(
        FrameworkElement element,
        double x,
        double width,
        bool animate,
        int milliseconds = StandardMilliseconds)
    {
        var offset = element.RenderTransform as TranslateTransform;
        if (offset is null)
        {
            offset = new TranslateTransform();
            element.RenderTransform = offset;
        }

        double fromX = offset.X;
        double fromWidth = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        offset.BeginAnimation(TranslateTransform.XProperty, null);
        element.BeginAnimation(FrameworkElement.WidthProperty, null);

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            offset.X = x;
            element.Width = width;
            return;
        }

        element.Width = width;
        offset.X = x;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        offset.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(fromX, x, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
        element.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(fromWidth, width, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    public static void Scale(FrameworkElement element, double scale, int milliseconds = QuickMilliseconds)
    {
        var transform = element.RenderTransform as ScaleTransform;
        if (transform is null)
        {
            transform = new ScaleTransform();
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        double fromX = transform.ScaleX;
        double fromY = transform.ScaleY;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            transform.ScaleX = transform.ScaleY = scale;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromX, scale, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromY, scale, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd,
            },
            HandoffBehavior.SnapshotAndReplace);
    }
}
