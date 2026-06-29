using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LootPulse.Controls
{
    /// <summary>
    /// Toast notification type — determines the accent color.
    /// </summary>
    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Danger
    }

    /// <summary>
    /// A slide-in toast notification with semantic-colored accent border.
    /// Auto-dismisses after 4 seconds. Stackable.
    /// </summary>
    public partial class ToastNotification : UserControl
    {
        private DispatcherTimer? _autoDismissTimer;

        public ToastNotification(string title, string message, ToastType type = ToastType.Info, int autoDismissMs = 4000)
        {
            InitializeComponent();

            TitleText.Text = title;
            MessageText.Text = message;

            // Set accent color based on type
            Color accentColor = type switch
            {
                ToastType.Success => Color.FromRgb(0x4A, 0xDE, 0x80), // #4ADE80
                ToastType.Warning => Color.FromRgb(0xFB, 0xBF, 0x24), // #FBBF24
                ToastType.Danger => Color.FromRgb(0xF8, 0x71, 0x71),  // #F87171
                _ => Color.FromRgb(0x60, 0xA5, 0xFA)                   // #60A5FA (Info)
            };
            AccentBorder.Background = new SolidColorBrush(accentColor);

            // Start auto-dismiss timer
            _autoDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(autoDismissMs)
            };
            _autoDismissTimer.Tick += (s, e) => Dismiss();
            _autoDismissTimer.Start();

            // Play slide-in animation on load
            Loaded += (s, e) => AnimateIn();
        }

        /// <summary>
        /// Event raised when the toast is dismissed (either by timer, close button, or programmatic call).
        /// </summary>
        public event EventHandler? Dismissed;

        private void AnimateIn()
        {
            var sb = new Storyboard();

            // Slide up from bottom
            var slide = new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, ToastTransform);
            Storyboard.SetTargetProperty(slide, new PropertyPath("(TranslateTransform.Y)"));
            sb.Children.Add(slide);

            // Fade in
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            sb.Begin();
        }

        private void AnimateOut(Action onComplete)
        {
            _autoDismissTimer?.Stop();

            var sb = new Storyboard();

            // Slide down
            var slide = new DoubleAnimation(0, 40, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(slide, ToastTransform);
            Storyboard.SetTargetProperty(slide, new PropertyPath("(TranslateTransform.Y)"));
            sb.Children.Add(slide);

            // Fade out
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            sb.Completed += (s, e) => onComplete();
            sb.Begin();
        }

        public void Dismiss()
        {
            AnimateOut(() =>
            {
                Dismissed?.Invoke(this, EventArgs.Empty);
                var parent = Parent as Panel;
                parent?.Children.Remove(this);
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }
    }
}
