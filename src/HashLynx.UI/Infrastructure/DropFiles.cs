using System.Windows;
using System.Windows.Input;

namespace HashLynx.UI.Infrastructure;

public static class DropFiles
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(DropFiles), new PropertyMetadata(null, Changed));
    public static void SetCommand(DependencyObject element, ICommand value) => element.SetValue(CommandProperty, value);
    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not UIElement element) return;
        element.AllowDrop = args.NewValue is not null;
        element.PreviewDragOver -= DragOver;
        element.PreviewDrop -= Drop;
        if (args.NewValue is not null) { element.PreviewDragOver += DragOver; element.PreviewDrop += Drop; }
    }
    private static void DragOver(object sender, DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }
    private static void Drop(object sender, DragEventArgs args)
    {
        if (args.Data.GetData(DataFormats.FileDrop) is string[] files && GetCommand((DependencyObject)sender) is { } command && command.CanExecute(files)) command.Execute(files);
        args.Handled = true;
    }
}
