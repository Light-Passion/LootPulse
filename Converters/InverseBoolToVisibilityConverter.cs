using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LootPulse
{
    /// <summary>Visible when the bound bool is false, Collapsed when true (inverse of the built-in converter).</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v != Visibility.Visible;
        }
    }
}
