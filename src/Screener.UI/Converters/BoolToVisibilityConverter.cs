using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Screener.UI.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Visible;
        }
        return false;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v != Visibility.Visible;
        }
        return true;
    }
}

public class PercentToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            // Return GridLength for star sizing (value should be 0-1)
            var starValue = Math.Clamp(percent, 0, 1) * 100;
            return new GridLength(Math.Max(starValue, 0.01), GridUnitType.Star);
        }
        return new GridLength(0.01, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class InversePercentToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent)
        {
            // Return GridLength for the inverse (empty space above the level)
            var starValue = (1.0 - Math.Clamp(percent, 0, 1)) * 100;
            return new GridLength(Math.Max(starValue, 0.01), GridUnitType.Star);
        }
        return new GridLength(100, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null && !string.IsNullOrEmpty(value.ToString())
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class PercentToPixelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent && parameter is string maxHeightStr && double.TryParse(maxHeightStr, out var maxHeight))
        {
            return Math.Clamp(percent, 0, 1) * maxHeight;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class PercentToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double percent && parameter is string maxHeightStr && double.TryParse(maxHeightStr, out var maxHeight))
        {
            var bottomOffset = Math.Clamp(percent, 0, 1) * maxHeight;
            return new Thickness(0, 0, 0, bottomOffset);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// MultiValueConverter: takes (level 0-1, actualHeight) and returns pixel height.
/// Used for meter overlay to cover unused portion from top.
/// </summary>
public class LevelToPixelHeightMultiConverter : IMultiValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is double level && values[1] is double actualHeight)
        {
            var clamped = Math.Clamp(level, 0, 1);
            var height = Inverse ? (1.0 - clamped) * actualHeight : clamped * actualHeight;
            return Math.Max(0, height);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// MultiValueConverter: takes (level 0-1, actualHeight) and returns Thickness with bottom margin.
/// Used for positioning peak hold line from the bottom.
/// </summary>
public class LevelToBottomMarginMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is double level && values[1] is double actualHeight)
        {
            var bottom = Math.Clamp(level, 0, 1) * actualHeight;
            return new Thickness(0, 0, 0, bottom);
        }
        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an enum value to bool for RadioButton binding.
/// ConverterParameter specifies the enum value name to compare against.
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}

public class MutedToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isMuted)
        {
            return isMuted ? 0.3 : 1.0;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
