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

    private DispatcherTimer? _waveTimer;
    private DoubleAnimation? _scrollAnimation;
    private double _waveOffset;
    private double _segmentWidth = 40;       // px per EKG beat segment
    private double _amplitude = 8;            // base wave amplitude
    private double _spikeAmplitude = 18;      // spike peak height
    private PulseState _currentState = PulseState.Calm;
    private int _spikeCountdown;               // remaining spikes to inject
    private readonly Random _rng = new();

    // Wave generation buffer — we build path geometry from these points
    private double _lastY = 20;

    /// <summary>
    /// Sets the market pulse state. Call this when market data changes
    /// (e.g., on data refresh, connection loss, price alerts).
    /// </summary>
    public void SetPulseState(PulseState state)
    {
        _currentState = state;
        _spikeCountdown = state == PulseState.Spike ? 3 : 0;

        // Adjust wave parameters based on state
        (_amplitude, _segmentWidth) = state switch
        {
            PulseState.Calm      => (6, 50),     // slow, gentle
            PulseState.Active    => (12, 30),    // faster, taller
            PulseState.Spike     => (14, 28),    // tall spikes
            PulseState.Flatline  => (0.5, 80),   // nearly flat
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
        InitializeWave();
        StartAnimation();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _waveTimer?.Stop();
        _waveTimer = null;
    }

    private void InitializeWave()
    {
        _waveOffset = 0;
        _lastY = ActualHeight / 2;
        GenerateWaveSegments();

        // Timer to append new wave segments and cycle the scroll
        _waveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // 20fps — smooth enough, light on CPU
        };
        _waveTimer.Tick += OnWaveTick;
        _waveTimer.Start();
    }

    private void StartAnimation()
    {
        // Continuous scroll animation — move wave left at constant speed
        // The path is wider than the canvas, so we scroll it and regenerate
        var scrollSpeed = _currentState switch
        {
            PulseState.Calm     => 40,   // px/sec
            PulseState.Active   => 80,
            PulseState.Spike    => 100,
            PulseState.Flatline => 10,
            _ => 40
        };

        _scrollAnimation = new DoubleAnimation
        {
            From = 0,
            To = -_segmentWidth,
            Duration = TimeSpan.FromSeconds(_segmentWidth / scrollSpeed),
            RepeatBehavior = RepeatBehavior.Forever
        };

        WaveTranslate.BeginAnimation(TranslateTransform.XProperty, _scrollAnimation);
        GlowTranslate.BeginAnimation(TranslateTransform.XProperty, _scrollAnimation);

        // Status dot heartbeat pulse (1.2s cycle — lub-dub... ...lub-dub)
        StartHeartbeatPulse();
    }

    private void StartHeartbeatPulse()
    {
        // Mimics a heartbeat: quick scale-up + scale-down, then rest
        var scaleUp = new DoubleAnimation(1, 1.4, TimeSpan.FromMilliseconds(150))
        {
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleDown = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

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

    private void OnWaveTick(object? sender, EventArgs e)
    {
        // Regenerate wave geometry periodically to create the scrolling effect
        _waveOffset += _segmentWidth;

        if (_waveOffset >= _segmentWidth * 2)
        {
            _waveOffset = 0;
            GenerateWaveSegments();
        }

        // Reset scroll animation position when we regenerate
        if (WaveTranslate.X <= -_segmentWidth)
        {
            WaveTranslate.X = 0;
        }
    }

    /// <summary>
    /// Generates EKG-shaped path geometry. Creates a wide path that scrolls
    /// across the canvas, with new segments appended as old ones scroll off.
    /// </summary>
    private void GenerateWaveSegments()
    {
        var width = ActualWidth > 0 ? ActualWidth : 400;
        var height = ActualHeight > 0 ? ActualHeight : 40;
        var centerY = height / 2;

        // Build a path that's 2x the visible width so it can scroll seamlessly
        var totalWidth = width * 2;
        var figures = new PathFigureCollection();
        var figure = new PathFigure
        {
            StartPoint = new Point(0, centerY),
            IsClosed = false
        };

        _lastY = centerY;
        var x = 0.0;

        while (x < totalWidth)
        {
            // Each EKG "beat" consists of:
            // 1. Flat baseline segment (P wave area — mostly flat)
            // 2. Quick downward dip (Q wave)
            // 3. Sharp upward spike (R wave — the main beat)
            // 4. Sharp downward spike (S wave)
            // 5. Return to baseline (T wave — gentle bump)
            // 6. Flat baseline until next beat

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

            // Add small random jitter for organic feel
            if (_currentState == PulseState.Active)
            {
                x += _rng.NextDouble() * 4 - 2; // ±2px jitter
            }
        }

        figures.Add(figure);

        var geometry = new PathGeometry { Figures = figures };
        EkgGeometry = geometry;
        EkgGlowGeometry = geometry.Clone();
    }

    private static void AddLineSegment(PathFigure figure, double x, double y)
    {
        figure.Segments.Add(new LineSegment(new Point(x, y), true));
    }

    /// <summary>
    /// Updates the scroll speed based on the current pulse state.
    /// Call after SetPulseState to re-sync the animation.
    /// </summary>
    public void RefreshAnimation()
    {
        StartAnimation();
    }
}
