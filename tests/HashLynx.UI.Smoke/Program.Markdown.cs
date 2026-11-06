using HashLynx.UI.Controls;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.UI.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckMarkdownAsync(ShellViewModel shell, Window parent)
    {
        Find<Expander>(VisualTreeHelper.GetParent(Find<TextBlock>(parent, "UpdateStatusText")), "").IsExpanded = true;
        await parent.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var changelog = Find<MarkdownViewer>(parent, "ChangelogText");
        Require(changelog.Markdown == shell.Settings.Changelog && MarkdownText(changelog.Document).Contains(ApplicationInfo.Version, StringComparison.Ordinal),
            "The real changelog binds to the formatted document.");
        Require(MarkdownElements(changelog.Document).OfType<System.Windows.Documents.List>().Any(),
            "The shipped Markdown changelog renders native lists.");
        var notes = Find<MarkdownViewer>(parent, "ReleaseNotesText");
        Require(notes.Markdown == shell.Settings.Updates.ReleaseNotes && MarkdownText(notes.Document).Contains("Fixture release", StringComparison.Ordinal),
            "Downloaded release notes use the same Markdown viewer and binding.");

        const string fixture = """
            # Release fixture

            Read **important changes**, *helpful details*, ~~retired behavior~~ and `--help`.

            Entities: AT&amp;T and &lt;tag&gt; remain text.

            ## Recovery improvements

            - First bullet
              - Nested bullet
            - Second bullet

            1. Download explicitly
            2. Restart when idle

            > A quoted release note.

            ```text
            hashcat --help
            ```

            | Feature | Status |
            | --- | --- |
            | Markdown | Available |

            [Project][project] · [Relative guide](README.md) · [Contact](mailto:maintainer@example.invalid)

            [project]: https://github.com/1ntrusi0n/HashLynx
            """;
        var source = new MarkdownSmokeSource { Markdown = fixture };
        var viewer = new MarkdownViewer { Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        viewer.SetBinding(MarkdownViewer.MarkdownProperty, new Binding(nameof(MarkdownSmokeSource.Markdown)) { Source = source, Mode = BindingMode.OneWay });
        var frame = new Border { Child = viewer };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var window = new Window
        {
            Owner = parent, Content = frame, Width = 640, Height = 740, ShowInTaskbar = false,
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            Title = "Markdown smoke fixture"
        };
        window.Show();
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var elements = MarkdownElements(viewer.Document).ToArray();
            var text = MarkdownText(viewer.Document);
            Require(text.Contains("Release fixture", StringComparison.Ordinal) && !text.Contains("# Release fixture", StringComparison.Ordinal),
                "Headings display their content without Markdown syntax.");
            Require(text.Contains("AT&T and <tag> remain text.", StringComparison.Ordinal),
                "Markdown character entities decode into inert selectable text.");
            Require(elements.OfType<Paragraph>().Any(paragraph => MarkdownText(paragraph).Trim() == "Release fixture" && paragraph.FontSize > viewer.Document.FontSize),
                "A Markdown heading has a native heading-sized paragraph.");
            Require(elements.OfType<TextElement>().Any(element => MarkdownText(element).Contains("important changes", StringComparison.Ordinal) && element.FontWeight >= FontWeights.SemiBold),
                "Strong emphasis is bold in the document.");
            Require(elements.OfType<TextElement>().Any(element => MarkdownText(element).Contains("helpful details", StringComparison.Ordinal) && element.FontStyle == FontStyles.Italic),
                "Emphasis is italic in the document.");
            Require(elements.OfType<Inline>().Any(element => MarkdownText(element).Contains("retired behavior", StringComparison.Ordinal) &&
                element.TextDecorations?.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough) == true),
                "Strikethrough renders as a native text decoration.");
            Require(elements.OfType<TextElement>().Any(element => MarkdownText(element).Trim() == "--help" && element.FontFamily.Source != viewer.Document.FontFamily.Source),
                "Inline code uses a distinct code font.");
            Require(elements.OfType<Paragraph>().Any(element => MarkdownText(element).Contains("hashcat --help", StringComparison.Ordinal) && element.FontFamily.Source != viewer.Document.FontFamily.Source),
                "Fenced code renders as code text without Markdown fences.");
            var lists = elements.OfType<System.Windows.Documents.List>().ToArray();
            Require(lists.Length >= 3 && lists.Any(list => list.MarkerStyle == TextMarkerStyle.Decimal),
                "Nested bullets and numbered lists render as native lists.");
            Require(elements.OfType<Table>().Any(table => table.RowGroups.SelectMany(group => group.Rows).Count() >= 2),
                "Markdown tables preserve header and body rows.");
            var links = elements.OfType<Hyperlink>().ToArray();
            Require(links.Length == 3 && links.All(link => link.NavigateUri is null && link.Tag is Uri),
                "Links retain safe destinations without an automatic navigation URI.");
            Require(links.Any(link => link.Tag is Uri uri && uri.AbsoluteUri == "https://github.com/1ntrusi0n/HashLynx") &&
                links.Any(link => link.Tag is Uri uri && uri.AbsoluteUri.EndsWith("/blob/main/README.md", StringComparison.Ordinal)),
                "Reference and repository-relative links resolve correctly.");
            Require(viewer.IsReadOnly, "Formatted release information remains read-only.");
            viewer.SelectAll();
            Require(viewer.Selection.Text.Contains("important changes", StringComparison.Ordinal) && viewer.Selection.Text.Contains("Available", StringComparison.Ordinal),
                "Rendered Markdown text remains selectable, including table cells.");
            viewer.Selection.Select(viewer.Document.ContentStart, viewer.Document.ContentStart);

            foreach (var theme in new[] { "Light", "Dark" })
            {
                AppServices.ApplyTheme(theme);
                await RenderAsync(window, "Markdown-" + theme + "-narrow");
                Require(viewer.Foreground is SolidColorBrush foreground && Resources["TextBrush"] is SolidColorBrush expected && foreground.Color == expected.Color,
                    "The Markdown viewer follows the active theme text color.");
            }

            source.Markdown = "## Changed release\n\nUpdated **binding** content.";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(BindingOperations.IsDataBound(viewer, MarkdownViewer.MarkdownProperty) && MarkdownText(viewer.Document).Contains("Changed release", StringComparison.Ordinal) &&
                !MarkdownText(viewer.Document).Contains("Release fixture", StringComparison.Ordinal),
                "An asynchronous release-notes binding update replaces the old rendered content.");

            source.Markdown = """
                [Danger](javascript:alert%281%29) [Local](file:///C:/private.txt) [Shell](shell:Downloads) [Data](data:text/html,test)

                ![External image](https://example.invalid/image.png)

                <img src="https://example.invalid/raw-image.png" onerror="alert(1)">
                <iframe src="https://example.invalid/embedded"></iframe>
                <script>arbitrary_html()</script>

                Still readable.
                """;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var inertElements = MarkdownElements(viewer.Document).ToArray();
            Require(!inertElements.OfType<Hyperlink>().Any(), "Dangerous URL schemes render as inert text.");
            Require(!inertElements.Any(element => element is InlineUIContainer or BlockUIContainer or Image or WebBrowser),
                "Remote images and raw HTML cannot create active or fetching document elements.");
            Require(MarkdownText(viewer.Document).Contains("External image", StringComparison.Ordinal) && MarkdownText(viewer.Document).Contains("Still readable.", StringComparison.Ordinal),
                "Image alternatives and surrounding text stay readable without fetching remote content.");

            foreach (var malformed in new[]
            {
                (Markdown: "", Visible: ""),
                (Markdown: "[unfinished](\n**bold\n```\n<broken", Visible: "**bold"),
                (Markdown: "| broken | table\n|-\n\u2603 **", Visible: "\u2603 **"),
                (Markdown: "[x][missing]\n\n    literal <tag>", Visible: "[x][missing]")
            })
            {
                source.Markdown = malformed.Markdown;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(viewer.Document is not null && viewer.IsReadOnly, "Empty or malformed Markdown keeps a usable read-only document.");
                Require(MarkdownText(viewer.Document!).Contains(malformed.Visible, StringComparison.Ordinal),
                    "Malformed and unresolved Markdown preserves its literal fixture marker: " + malformed.Visible);
            }
        }
        finally
        {
            window.Close();
            AppServices.ApplyTheme(shell.Services.Settings.Theme);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "markdown-result.txt"),
            "PASS: actual changelog/release-note bindings; headings; bold/italic/strikethrough; inline/fenced code; nested/numbered lists; tables; reference/relative links; read-only selection; dynamic binding and Light/Dark theme; inert unsafe URLs, external images and HTML; empty/malformed input. Screenshots: Markdown-Light-narrow.png and Markdown-Dark-narrow.png.");
    }

    private static string MarkdownText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;
    private static string MarkdownText(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text;

    private static IEnumerable<DependencyObject> MarkdownElements(DependencyObject element)
    {
        yield return element;
        IEnumerable<DependencyObject> children = element switch
        {
            FlowDocument document => document.Blocks.Cast<DependencyObject>(),
            Section section => section.Blocks.Cast<DependencyObject>(),
            Paragraph paragraph => paragraph.Inlines.Cast<DependencyObject>(),
            Span span => span.Inlines.Cast<DependencyObject>(),
            System.Windows.Documents.List list => list.ListItems.Cast<DependencyObject>(),
            ListItem item => item.Blocks.Cast<DependencyObject>(),
            Table table => table.RowGroups.Cast<DependencyObject>(),
            TableRowGroup group => group.Rows.Cast<DependencyObject>(),
            TableRow row => row.Cells.Cast<DependencyObject>(),
            TableCell cell => cell.Blocks.Cast<DependencyObject>(),
            InlineUIContainer inline when inline.Child is not null => [inline.Child],
            BlockUIContainer block when block.Child is not null => [block.Child],
            _ => []
        };
        foreach (var child in children)
            foreach (var descendant in MarkdownElements(child)) yield return descendant;
    }

    private sealed class MarkdownSmokeSource : ObservableObject
    {
        private string _markdown = "";
        public string Markdown { get => _markdown; set => Set(ref _markdown, value); }
    }
}
