using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace HashLynx.UI.Infrastructure;

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string path ? Path.GetFileName(path) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
