using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LootPulse.Controls;

/// <summary>
/// Market Pulse Graph — an animated EKG/heartbeat line that scrolls continuously.
/// "The Heartbeat of the Economy."
/// </summary>
public partial class MarketPulseGraph
{
    public enum PulseState { Calm, Active, Spike, Flatline }

    private DispatcherTimer? _renderTimer;
    private PulseState _currentState = PulseState.Calm;
    private double _segmentWidth = 50;
    private double _amplitude = 6;
    private double _spikeAmplitude = 18;
    private int _spikeCountdown;
    private readonly Random _rng = new();

    private double _scrollX;
    private const double ScrollSpeed = 50;
    private const double RenderFps = 30;
    private const double FrameInterval = 1000.0 / RenderFps;

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

    public void TriggerSpike() => _spikeCountdown = Math.Max(_spikeCountdown, 2);

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // Wait for layout to complete, then generate and start
        GenerateWave();
        StartRenderLoop();
        StartHeartbeatPulse();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer?.Stop();
        _renderTimer = null;
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Regenerate wave when the control gets real dimensions
        GenerateWave();
    }

    private void StartRenderLoop()
    {
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FrameInterval)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        _scrollX += ScrollSpeed / RenderFps;

        if (_scrollX >= _segmentWidth)
        {
            _scrollX -= _segmentWidth;
            GenerateWave();
        }

        WaveTranslate.X = -_scrollX;
        GlowTranslate.X = -_scrollX;
    }

    private void GenerateWave()
    {
        // Use ActualWidth/Height, fall back to design values if zero
        var width = ActualWidth > 0 ? ActualWidth : 400;
        var height = ActualHeight > 0 ? ActualHeight : 40;
        var centerY = height / 2;

        var totalWidth = width + _segmentWidth * 4;
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

            if (_spikeCountdown > 0)
            {
                amp = _spikeAmplitude;
                _spikeCountdown--;
            }

            // 1. Flat baseline (30%)
            var w = beatWidth * 0.3;
            AddSeg(figure, x + w, centerY);
            x += w;

            // 2. Q wave — downward dip (5%)
            w = beatWidth * 0.05;
            AddSeg(figure, x + w, centerY + amp * 0.4);
            x += w;

            // 3. R wave — sharp upward spike (10%)
            w = beatWidth * 0.1;
            AddSeg(figure, x + w * 0.5, centerY - amp);
            x += w;

            // 4. S wave — sharp downward (10%)
            w = beatWidth * 0.1;
            AddSeg(figure, x + w * 0.5, centerY + amp * 0.6);
            x += w;

            // 5. Return to baseline (10%)
            w = beatWidth * 0.1;
            AddSeg(figure, x + w, centerY);
            x += w;

            // 6. T wave — gentle bump (15%)
            if (_currentState != PulseState.Flatline)
            {
                w = beatWidth * 0.15;
                AddSeg(figure, x + w * 0.5, centerY - amp * 0.3);
                x += w;
            }

            // 7. Flat to end (20%)
            w = beatWidth * 0.2;
            AddSeg(figure, x + w, centerY);
            x += w;

            if (_currentState == PulseState.Active)
                x += _rng.NextDouble() * 4 - 2;
        }

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();

        EkgPath.Data = geo;
        EkgGlowPath.Data = geo;
    }

    private static void AddSeg(PathFigure f, double x, double y)
        => f.Segments.Add(new LineSegment(new Point(x, y), true));

    private void StartHeartbeatPulse()
    {
        var heartbeat = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.2),
            RepeatBehavior = RepeatBehavior.Forever
        };

        heartbeat.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        var lubUp = new EasingDoubleKeyFrame(1.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)));
        lubUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        heartbeat.KeyFrames.Add(lubUp);

        var lubDown = new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)));
        lubDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        heartbeat.KeyFrames.Add(lubDown);

        var dubUp = new EasingDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)));
        dubUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        heartbeat.KeyFrames.Add(dubUp);

        var dubDown = new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)));
        dubDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        heartbeat.KeyFrames.Add(dubDown);

        heartbeat.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));

        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, heartbeat);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, heartbeat);

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

    public void RefreshAnimation() => GenerateWave();
}
