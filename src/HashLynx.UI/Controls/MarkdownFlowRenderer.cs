using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace HashLynx.UI.Controls;

/// <summary>Presentation-only Markdown AST renderer with no HTML, image, or navigation surface.</summary>
internal sealed class MarkdownFlowRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .Build();
    private static readonly FontFamily CodeFont = new("Consolas");
    private readonly string _source;
    private readonly Uri? _baseUri;
    private readonly double _fontSize;
    private int _elementCount;

    private MarkdownFlowRenderer(string source, Uri? baseUri, double fontSize)
    {
        _source = source;
        _baseUri = baseUri;
        _fontSize = fontSize;
    }

    public static FlowDocument Render(string source, Uri? baseUri, FontFamily fontFamily, double fontSize)
    {
        var document = new FlowDocument
        {
            FontFamily = fontFamily,
            FontSize = fontSize,
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");
        try
        {
            var renderer = new MarkdownFlowRenderer(source, baseUri, fontSize);
            renderer.AddBlocks(Markdig.Markdown.Parse(source, Pipeline), document.Blocks, 0);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            FormatException or OverflowException or InvalidDataException or NotSupportedException)
        {
            // Release text must remain readable even when an unusual construct cannot be rendered.
            // Preserve the original text without executing or logging it.
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(source)) { Margin = new Thickness(0) });
        }
        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        return document;
    }

    private void AddBlocks(ContainerBlock source, BlockCollection destination, int depth)
    {
        foreach (var block in source)
        {
            var rendered = RenderBlock(block, depth + 1);
            if (rendered is not null) destination.Add(rendered);
        }
    }

    private WpfBlock? RenderBlock(MarkdownBlock block, int depth)
    {
        CheckBudget(depth);
        switch (block)
        {
            case HeadingBlock heading:
            {
                var paragraph = RenderParagraph(heading.Inline, depth);
                paragraph.FontSize = _fontSize * (heading.Level switch { 1 => 1.8, 2 => 1.5, 3 => 1.25, 4 => 1.1, _ => 1 });
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 12, 0, 8);
                paragraph.KeepWithNext = true;
                return paragraph;
            }
            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph.Inline, depth);
            case CodeBlock code:
                return CodeParagraph(code.Lines.ToString());
            case ListBlock list:
                return RenderList(list, depth);
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 3, 0, 0),
                    Margin = new Thickness(0, 4, 0, 10)
                };
                section.SetResourceReference(Section.BorderBrushProperty, "AccentBrush");
                AddBlocks(quote, section.Blocks, depth);
                return section;
            }
            case ThematicBreakBlock:
            {
                var separator = new Paragraph
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 10, 0, 10),
                    FontSize = 1,
                    LineHeight = 1
                };
                separator.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
                return separator;
            }
            case Markdig.Extensions.Tables.Table table:
                return RenderTable(table, depth);
            case HtmlBlock html:
                return CodeParagraph(html.Lines.ToString());
            case LinkReferenceDefinitionGroup:
                return null;
            case ContainerBlock container:
            {
                var section = new Section { Margin = new Thickness(0) };
                AddBlocks(container, section.Blocks, depth);
                return section;
            }
            case LeafBlock leaf:
                return leaf.Inline is not null ? RenderParagraph(leaf.Inline, depth) : new Paragraph(new Run(leaf.Lines.ToString()));
            default:
                return new Paragraph(new Run(SourceText(block)));
        }
    }

    private Paragraph RenderParagraph(ContainerInline? inline, int depth)
    {
        var result = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
        if (inline is not null) AddInlines(inline, result.Inlines, depth);
        return result;
    }

    private Paragraph CodeParagraph(string content)
    {
        var paragraph = new Paragraph(new Run(content.TrimEnd('\r', '\n')))
        {
            FontFamily = CodeFont,
            FontSize = Math.Max(11, _fontSize - 1),
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 3, 0, 10)
        };
        paragraph.SetResourceReference(Paragraph.BackgroundProperty, "SurfaceBrush");
        paragraph.SetResourceReference(Paragraph.BorderBrushProperty, "BorderBrush");
        return paragraph;
    }

    private WpfList RenderList(ListBlock source, int depth)
    {
        var start = int.TryParse(source.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var result = new WpfList
        {
            MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Padding = new Thickness(24, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 9),
            MarkerOffset = 7,
            StartIndex = Math.Max(1, start)
        };
        // WPF requires StartIndex >= 1, while CommonMark also permits zero.
        if (source.IsOrdered && start == 0) result.MarkerStyle = TextMarkerStyle.None;
        foreach (var item in source.OfType<ListItemBlock>())
        {
            var rendered = new ListItem { Margin = new Thickness(0, 0, 0, source.IsLoose ? 5 : 1) };
            AddBlocks(item, rendered.Blocks, depth);
            if (rendered.Blocks.Count == 0) rendered.Blocks.Add(new Paragraph());
            if (!source.IsLoose)
                foreach (var paragraph in rendered.Blocks.OfType<Paragraph>()) paragraph.Margin = new Thickness(0, 0, 0, 2);
            if (source.IsOrdered && start == 0)
            {
                if (rendered.Blocks.FirstBlock is not Paragraph first)
                {
                    first = new Paragraph { Margin = new Thickness(0) };
                    rendered.Blocks.InsertBefore(rendered.Blocks.FirstBlock, first);
                }
                first.Inlines.InsertBefore(first.Inlines.FirstInline ?? AddEmptyRun(first), new Run($"{result.ListItems.Count}. "));
            }
            result.ListItems.Add(rendered);
        }
        return result;
    }

    private static Run AddEmptyRun(Paragraph paragraph)
    {
        var run = new Run();
        paragraph.Inlines.Add(run);
        return run;
    }

    private WpfTable RenderTable(Markdig.Extensions.Tables.Table source, int depth)
    {
        var result = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 3, 0, 12) };
        var columns = Math.Max(1, Math.Max(source.ColumnDefinitions.Count,
            source.OfType<Markdig.Extensions.Tables.TableRow>().Select(row => row.Count).DefaultIfEmpty(1).Max()));
        for (var i = 0; i < columns; i++) result.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new TableRowGroup();
        result.RowGroups.Add(rows);
        foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            CheckBudget(depth);
            var renderedRow = new WpfTableRow();
            rows.Rows.Add(renderedRow);
            if (row.IsHeader)
            {
                renderedRow.FontWeight = FontWeights.SemiBold;
                renderedRow.SetResourceReference(WpfTableRow.BackgroundProperty, "HoverBrush");
            }
            var column = 0;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var rendered = new WpfTableCell
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderThickness = new Thickness(0.5),
                    ColumnSpan = Math.Clamp(cell.ColumnSpan, 1, columns),
                    RowSpan = Math.Max(1, cell.RowSpan)
                };
                rendered.SetResourceReference(WpfTableCell.BorderBrushProperty, "BorderBrush");
                var index = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                if (index < source.ColumnDefinitions.Count)
                    rendered.TextAlignment = source.ColumnDefinitions[index].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    };
                AddBlocks(cell, rendered.Blocks, depth);
                if (rendered.Blocks.Count == 0) rendered.Blocks.Add(new Paragraph());
                foreach (var paragraph in rendered.Blocks.OfType<Paragraph>()) paragraph.Margin = new Thickness(0);
                renderedRow.Cells.Add(rendered);
                column += rendered.ColumnSpan;
            }
        }
        return result;
    }

    private void AddInlines(ContainerInline source, InlineCollection destination, int depth)
    {
        for (var inline = source.FirstChild; inline is not null; inline = inline.NextSibling)
            destination.Add(RenderInline(inline, depth + 1));
    }

    private WpfInline RenderInline(MarkdownInline inline, int depth)
    {
        CheckBudget(depth);
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = CodeFont };
                run.SetResourceReference(Run.BackgroundProperty, "SurfaceBrush");
                return run;
            }
            case EmphasisInline emphasis:
            {
                var span = new Span();
                if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                else span.FontStyle = FontStyles.Italic;
                AddInlines(emphasis, span.Inlines, depth);
                return span;
            }
            case LineBreakInline line:
                return line.IsHard ? new LineBreak() : new Run(" ");
            case LinkInline { IsImage: true } image:
            {
                var span = new Span(new Run("[Image: "));
                AddInlines(image, span.Inlines, depth);
                span.Inlines.Add(new Run("]"));
                return span;
            }
            case LinkInline link:
            {
                var span = CreateLink(link.Url) ?? new Span();
                AddInlines(link, span.Inlines, depth);
                return span;
            }
            case AutolinkInline link:
            {
                var span = CreateLink(link.IsEmail ? "mailto:" + link.Url : link.Url) ?? new Span();
                span.Inlines.Add(new Run(link.Url));
                return span;
            }
            case HtmlInline html:
                return new Run(html.Tag);
            case TaskList task:
            {
                var run = new Run(task.Checked ? "☑ " : "☐ ");
                AutomationProperties.SetName(run, task.Checked ? "Completed task" : "Incomplete task");
                return run;
            }
            case DelimiterInline delimiter:
            {
                // Unmatched emphasis/link markers remain literal text in CommonMark.
                var span = new Span(new Run(delimiter.ToLiteral()));
                AddInlines(delimiter, span.Inlines, depth);
                return span;
            }
            case ContainerInline container:
            {
                var span = new Span();
                AddInlines(container, span.Inlines, depth);
                return span;
            }
            default:
                return new Run(SourceText(inline));
        }
    }

    private Hyperlink? CreateLink(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination.Any(char.IsControl)) return null;
        Uri? uri;
        if (Uri.TryCreate(destination, UriKind.Absolute, out var absolute)) uri = absolute;
        else if (_baseUri is { IsAbsoluteUri: true } && Uri.TryCreate(_baseUri, destination, out var relative)) uri = relative;
        else return null;
        if (uri.Scheme is not ("https" or "http" or "mailto")) return null;
        var link = new Hyperlink
        {
            // Do not set NavigateUri: no browser, shell, or navigation host is ever invoked.
            Tag = uri,
            ToolTip = "Click to copy link\n" + uri.AbsoluteUri,
            Cursor = Cursors.Hand,
            Focusable = true
        };
        link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetHelpText(link, "Click to copy link: " + uri.AbsoluteUri);
        link.Click += CopyLink;
        return link;
    }

    private static void CopyLink(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not Hyperlink { Tag: Uri uri } link) return;
        try
        {
            Clipboard.SetText(uri.AbsoluteUri);
            link.ToolTip = "Link copied\n" + uri.AbsoluteUri;
        }
        catch (ExternalException)
        {
            link.ToolTip = "Clipboard is busy. Click to try copying this link again.\n" + uri.AbsoluteUri;
        }
    }

    private string SourceText(MarkdownObject node)
    {
        var start = node.Span.Start;
        var end = node.Span.End;
        return start >= 0 && end >= start && end < _source.Length ? _source[start..(end + 1)] : string.Empty;
    }

    private void CheckBudget(int depth)
    {
        if (depth > 96 || ++_elementCount > 30000)
            throw new InvalidDataException("Markdown exceeds the native document layout limit.");
    }
}
