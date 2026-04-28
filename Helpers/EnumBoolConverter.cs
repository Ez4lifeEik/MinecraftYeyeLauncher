using System.Globalization;
using System.Windows.Data;

namespace ArclightLauncher.Helpers;

/// <summary>
/// 枚举值 ↔ bool 转换器，用于 RadioButton.IsChecked 绑定枚举属性。
/// ConverterParameter 传目标枚举值，若当前值等于目标则返回 true。
/// </summary>
[ValueConversion(typeof(object), typeof(bool))]
public class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.Equals(parameter) == true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
