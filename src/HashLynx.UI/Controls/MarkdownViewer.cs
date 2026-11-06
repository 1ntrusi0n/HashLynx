using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HashLynx.UI.Controls;

/// <summary>
/// Selectable Markdown presented as native WPF text. Links copy their destination;
/// the document never navigates, loads images, or interprets HTML.
/// </summary>
public sealed class MarkdownViewer : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty BaseUriProperty = DependencyProperty.Register(
        nameof(BaseUri), typeof(Uri), typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(new Uri("https://github.com/1ntrusi0n/HashLynx/blob/main/"), OnContentChanged));

    private bool _initialized;

    public MarkdownViewer()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        IsDocumentEnabled = true;
        AcceptsTab = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Padding = new Thickness(12);
        BorderThickness = new Thickness(1);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty, "InputBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(SelectionBrushProperty, "AccentBrush");
        SetResourceReference(CaretBrushProperty, "AccentBrush");
        _initialized = true;
        RenderMarkdown();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Base for relative documentation links; resolving a URI does not access it.</summary>
    public Uri? BaseUri
    {
        get => (Uri?)GetValue(BaseUriProperty);
        set => SetValue(BaseUriProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_initialized && (e.Property == FontSizeProperty || e.Property == FontFamilyProperty))
            RenderMarkdown();
    }

    private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is MarkdownViewer { _initialized: true } viewer)
            viewer.RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        Document = MarkdownFlowRenderer.Render(Markdown ?? string.Empty, BaseUri, FontFamily, FontSize);
        ScrollToHome();
    }
}
