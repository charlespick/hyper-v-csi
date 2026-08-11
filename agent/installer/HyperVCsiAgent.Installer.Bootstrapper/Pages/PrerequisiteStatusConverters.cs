using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

internal sealed class PrerequisiteStatusGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PrerequisiteStatus.Pass ? "OK" : "!";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class PrerequisiteStatusBrushConverter : IValueConverter
{
    private static readonly Brush PassBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x7E, 0x34));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x0B));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PrerequisiteStatus.Pass ? PassBrush : WarnBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
