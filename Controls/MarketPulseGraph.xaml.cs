using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LootPulse.Controls;

/// <summary>
/// Market Pulse Graph — an animated EKG/heartbeat line that scrolls continuously.
/// Its rhythm reflects market volatility states (Calm, Active, Spike, Flatline).
/// "The Heartbeat of the Economy."
/// </summary>
public partial class MarketPulseGraph
{
    /// <summary>Market volatility state — controls wave amplitude and frequency.</summary>
    public enum PulseState
    {
        /// <summary>Steady, low-amplitude waves. Market is stable.</summary>
        Calm,
        /// <summary>Faster, taller spikes. Prices are moving.</summary>
        Active,
        /// <summary>Single major peak. A price alert just fired.</summary>
        Spike,
        /// <summary>Flat line. No data or disconnected.</summary>
        Flatline
    }

    private DispatcherTimer? _renderTimer;
    private PulseState _currentState = PulseState.Calm;
    private double _segmentWidth = 50;       // px per EKG beat segment
    private double _amplitude = 6;            // base wave amplitude
    private double _spikeAmplitude = 18;      // spike peak height
    private int _spikeCountdown;               // remaining spikes to inject
    private readonly Random _rng = new();

    // Scroll state — we advance this each frame and regenerate the path
    private double _scrollX;
    private const double ScrollSpeed = 50;     // px per second
    private const double RenderFps = 30;       // frames per second
    private const double FrameInterval = 1000.0 / RenderFps;

    // Pre-generated wave data — one long path that we scroll and regenerate
    private PathGeometry _waveGeometry = new();

    /// <summary>
    /// Sets the market pulse state. Call this when market data changes
    /// (e.g., on data refresh, connection loss, price alerts).
    /// </summary>
    public void SetPulseState(PulseState state)
    {
        _currentState = state;
        _spikeCountdown = state == PulseState.Spike ? 3 : 0;

        (_amplitude, _segmentWidth) = state switch
        {
            PulseState.Calm      => (6, 50),
            PulseState.Active    => (12, 30),
            PulseState.Spike     => (14, 28),
            PulseState.Flatline  => (0.5, 80),
            _ => (6, 50)
        };
    }

    /// <summary>Triggers a single spike (price alert moment).</summary>
    public void TriggerSpike()
    {
        _spikeCountdown = Math.Max(_spikeCountdown, 2);
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // Generate the initial wave
        GenerateWave();

        // Start the render loop
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FrameInterval)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        // Start the heartbeat on the status dot
        StartHeartbeatPulse();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer?.Stop();
        _renderTimer = null;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        // Advance the scroll position
        _scrollX += ScrollSpeed / RenderFps;

        // When we've scrolled one full segment width, regenerate the wave
        // and reset the scroll position so it wraps seamlessly
        if (_scrollX >= _segmentWidth)
        {
            _scrollX -= _segmentWidth;
            GenerateWave();
        }

        // Apply the scroll offset directly to the render transforms
        WaveTranslate.X = -_scrollX;
        GlowTranslate.X = -_scrollX;
    }

    /// <summary>
    /// Generates EKG-shaped path geometry directly on the Path elements.
    /// Creates a path that's 2x the visible width for seamless scrolling.
    /// </summary>
    private void GenerateWave()
    {
        var width = ActualWidth > 0 ? ActualWidth : 200;
        var height = ActualHeight > 0 ? ActualHeight : 24;
        var centerY = height / 2;

        var totalWidth = width + _segmentWidth * 4; // extra width for scroll buffer
        var figure = new PathFigure
        {
            StartPoint = new Point(0, centerY),
            IsClosed = false
        };

        var x = 0.0;

        while (x < totalWidth)
        {
            var beatWidth = _segmentWidth;
            var amp = _amplitude;
            var hasSpike = _spikeCountdown > 0;
            if (hasSpike)
            {
                amp = _spikeAmplitude;
                _spikeCountdown--;
            }

            // 1. Flat baseline (30% of beat)
            var flatWidth = beatWidth * 0.3;
            AddLineSegment(figure, x + flatWidth, centerY);
            x += flatWidth;

            // 2. Q wave — small downward dip (5%)
            var qWidth = beatWidth * 0.05;
            AddLineSegment(figure, x + qWidth, centerY + amp * 0.4);
            x += qWidth;

            // 3. R wave — sharp upward spike (10%)
            var rWidth = beatWidth * 0.1;
            AddLineSegment(figure, x + rWidth * 0.5, centerY - amp);
            x += rWidth;

            // 4. S wave — sharp downward (10%)
            var sWidth = beatWidth * 0.1;
            AddLineSegment(figure, x + sWidth * 0.5, centerY + amp * 0.6);
            x += sWidth;

            // 5. Return to baseline (10%)
            var returnWidth = beatWidth * 0.1;
            AddLineSegment(figure, x + returnWidth, centerY);
            x += returnWidth;

            // 6. T wave — gentle bump (15%) — only on non-flatline states
            if (_currentState != PulseState.Flatline)
            {
                var tWidth = beatWidth * 0.15;
                AddLineSegment(figure, x + tWidth * 0.5, centerY - amp * 0.3);
                x += tWidth;
            }

            // 7. Flat to end of beat
            AddLineSegment(figure, x + (beatWidth * 0.2), centerY);
            x += beatWidth * 0.2;

            // Add small random jitter for organic feel in Active state
            if (_currentState == PulseState.Active)
            {
                x += _rng.NextDouble() * 4 - 2;
            }
        }

        _waveGeometry = new PathGeometry();
        _waveGeometry.Figures.Add(figure);

        // CRITICAL: Set Data directly on the Path elements, not on the
        // PathGeometry field reference — WPF doesn't propagate field reassignment
        EkgPath.Data = _waveGeometry;
        EkgGlowPath.Data = _waveGeometry;
    }

    private static void AddLineSegment(PathFigure figure, double x, double y)
    {
        figure.Segments.Add(new LineSegment(new Point(x, y), true));
    }

    private void StartHeartbeatPulse()
    {
        // Mimics a heartbeat: lub-dub... ...lub-dub...
        var heartbeat = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.2),
            RepeatBehavior = RepeatBehavior.Forever
        };

        // lub (quick beat)
        heartbeat.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        var lubUp = new EasingDoubleKeyFrame(1.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)));
        lubUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        heartbeat.KeyFrames.Add(lubUp);

        var lubDown = new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)));
        lubDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        heartbeat.KeyFrames.Add(lubDown);

        // dub (second beat, smaller)
        var dubUp = new EasingDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)));
        dubUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        heartbeat.KeyFrames.Add(dubUp);

        var dubDown = new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)));
        dubDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        heartbeat.KeyFrames.Add(dubDown);

        // rest (flatline until next cycle at 1200ms)
        heartbeat.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));

        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, heartbeat);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, heartbeat);

        // Sync the glow opacity with the heartbeat
        var glowPulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.2),
            RepeatBehavior = RepeatBehavior.Forever
        };
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        glowPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));

        StatusDotGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glowPulse);
    }

    /// <summary>
    /// Updates the scroll speed based on the current pulse state.
    /// Call after SetPulseState to re-sync the animation.
    /// </summary>
    public void RefreshAnimation()
    {
        // Timer-driven approach doesn't need animation restart
        GenerateWave();
    }
}
