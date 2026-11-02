using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HangAll.UI;

/// <summary>
/// One wheel notch glides to its offset instead of jumping, so a long settings
/// page or clipboard list reads as one continuous surface. The platform
/// behaviour is kept when Windows animations are off, when there is nothing to
/// scroll, or when the wheel event was not for this scroller.
/// </summary>
internal static class SmoothScroll
{
    private const int DurationMilliseconds = 150;
    private const double NotchPoints = 56;

    public static void Attach(ScrollViewer scroll)
    {
        if (scroll.Tag is SmoothState) return;
        var state = new SmoothState(scroll);
        scroll.Tag = state;
        scroll.PreviewMouseWheel += state.OnWheel;
        scroll.Unloaded += (_, _) => state.Stop();
    }

    private sealed class SmoothState
    {
        private readonly ScrollViewer _scroll;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();
        private double _from;
        private double _to;

        public SmoothState(ScrollViewer scroll)
        {
            _scroll = scroll;
            _timer = new DispatcherTimer(DispatcherPriority.Render, scroll.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(15),
            };
            _timer.Tick += (_, _) => Step();
        }

        public void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0 || _scroll.ScrollableHeight <= 0) return;
            if (!SystemParameters.ClientAreaAnimation)
            {
                Stop();
                return;
            }

            // Scrolling up gives a negative delta, and moves the offset down.
            double step = -e.Delta / 120.0 * NotchPoints;
            double target = Math.Clamp(_scroll.VerticalOffset + step, 0, _scroll.ScrollableHeight);
            if (Math.Abs(target - _scroll.VerticalOffset) < 0.5) return;

            e.Handled = true;
            _from = _scroll.VerticalOffset;
            _to = target;
            _clock.Restart();
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void Step()
        {
            double k = Math.Clamp(_clock.Elapsed.TotalMilliseconds / DurationMilliseconds, 0, 1);
            _scroll.ScrollToVerticalOffset(_from + (_to - _from) * Ease.OutCubic(k));
            if (k >= 1) Stop();
        }

        public void Stop() => _timer.Stop();
    }
}
