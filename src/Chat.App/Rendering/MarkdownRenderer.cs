using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Chat.App.Rendering;

/// <summary>行内样式片段。强/斜/链接用 <see cref="Children"/> 保留嵌套样式。</summary>
public enum MarkdownInlineKind
{
    Text,
    Code,
    Strong,
    Emphasis,
    Link,
    LineBreak,
}

public sealed record MarkdownInline(
    MarkdownInlineKind Kind,
    string Text,
    string? Url = null,
    IReadOnlyList<MarkdownInline>? Children = null)
{
    public static MarkdownInline Plain(string text) => new(MarkdownInlineKind.Text, text);
}

/// <summary>渲染后的块级元素。视图用 DataTemplate 按类型选择控件，VM 只产出这一个列表。</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

public sealed record MarkdownListItem(string Marker, IReadOnlyList<MarkdownBlock> Blocks);

public sealed record MarkdownList(bool IsOrdered, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

public sealed record MarkdownCodeBlock(string Language, string Code) : MarkdownBlock;

public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

public sealed record MarkdownTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool HasHeader) : MarkdownBlock;

public sealed record MarkdownThematicBreak : MarkdownBlock;

public sealed record MarkdownHtml(string Text) : MarkdownBlock;

/// <summary>
/// Markdig AST → 与 UI 无关的块/行内模型。视图层只负责把模型映射成控件，
/// 这样 Markdown 解析可以被纯单测覆盖，流式重渲染也不会碰视图对象。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static IReadOnlyList<MarkdownBlock> Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var document = Markdown.Parse(markdown, Pipeline);
        return ConvertBlocks(document);
    }

    public static string ToPlainText(IReadOnlyList<MarkdownInline>? inlines)
    {
        if (inlines is null || inlines.Count == 0) return "";
        var builder = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline.Kind == MarkdownInlineKind.LineBreak)
            {
                builder.Append('\n');
                continue;
            }

            if (inline.Children is { Count: > 0 }) builder.Append(ToPlainText(inline.Children));
            else builder.Append(inline.Text);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<MarkdownBlock> ConvertBlocks(ContainerBlock container)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var child in container)
        {
            switch (child)
            {
                case HeadingBlock heading:
                    blocks.Add(new MarkdownHeading(heading.Level, ConvertInlines(heading.Inline)));
                    break;
                case ParagraphBlock paragraph:
                    blocks.Add(new MarkdownParagraph(ConvertInlines(paragraph.Inline)));
                    break;
                case FencedCodeBlock fenced:
                    blocks.Add(new MarkdownCodeBlock(FenceLanguage(fenced.Info), LinesOf(fenced)));
                    break;
                case CodeBlock code:
                    blocks.Add(new MarkdownCodeBlock("", LinesOf(code)));
                    break;
                case ListBlock list:
                    blocks.Add(ConvertList(list));
                    break;
                case QuoteBlock quote:
                    blocks.Add(new MarkdownQuote(ConvertBlocks(quote)));
                    break;
                case Table table:
                    blocks.Add(ConvertTable(table));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new MarkdownThematicBreak());
                    break;
                case HtmlBlock html:
                    blocks.Add(new MarkdownHtml(LinesOf(html)));
                    break;
                case ContainerBlock nested:
                    blocks.AddRange(ConvertBlocks(nested));
                    break;
                case LeafBlock leaf:
                    var text = LinesOf(leaf);
                    if (text.Length > 0) blocks.Add(new MarkdownParagraph([MarkdownInline.Plain(text)]));
                    break;
            }
        }

        return blocks;
    }

    private static MarkdownList ConvertList(ListBlock list)
    {
        var items = new List<MarkdownListItem>();
        var start = int.TryParse(list.OrderedStart, out var parsed) ? parsed : 1;
        var index = 0;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            var number = item.Order > 0 ? item.Order : start + index;
            var marker = list.IsOrdered ? number + "." : "•";
            items.Add(new MarkdownListItem(marker, ConvertBlocks(item)));
            index++;
        }

        return new MarkdownList(list.IsOrdered, items);
    }

    private static MarkdownTable ConvertTable(Table table)
    {
        var rows = new List<IReadOnlyList<string>>();
        var hasHeader = false;
        foreach (var child in table)
        {
            if (child is not TableRow row) continue;
            hasHeader |= row.IsHeader;
            var cells = new List<string>();
            foreach (var cell in row)
            {
                if (cell is TableCell tableCell) cells.Add(PlainText(tableCell));
            }

            rows.Add(cells);
        }

        return new MarkdownTable(rows, hasHeader);
    }

    private static IReadOnlyList<MarkdownInline> ConvertInlines(ContainerInline? container)
    {
        var inlines = new List<MarkdownInline>();
        for (var child = container?.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    inlines.Add(MarkdownInline.Plain(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    inlines.Add(new MarkdownInline(MarkdownInlineKind.Code, code.Content));
                    break;
                case EmphasisInline emphasis:
                    var children = ConvertInlines(emphasis);
                    inlines.Add(new MarkdownInline(
                        emphasis.DelimiterCount >= 2 ? MarkdownInlineKind.Strong : MarkdownInlineKind.Emphasis,
                        MarkdownRenderer.ToPlainText(children),
                        Children: children));
                    break;
                case LinkInline link:
                    var labelInlines = ConvertInlines(link);
                    var label = MarkdownRenderer.ToPlainText(labelInlines);
                    if (label.Length == 0) label = link.Url ?? "";
                    inlines.Add(new MarkdownInline(MarkdownInlineKind.Link, label, link.Url,
                        labelInlines.Count > 0 ? labelInlines : null));
                    break;
                case LineBreakInline lineBreak:
                    inlines.Add(new MarkdownInline(
                        lineBreak.IsHard ? MarkdownInlineKind.LineBreak : MarkdownInlineKind.Text,
                        lineBreak.IsHard ? "\n" : " "));
                    break;
                case ContainerInline nested:
                    var nestedInlines = ConvertInlines(nested);
                    if (nestedInlines.Count > 0)
                        inlines.Add(new MarkdownInline(MarkdownInlineKind.Text, MarkdownRenderer.ToPlainText(nestedInlines), Children: nestedInlines));
                    break;
            }
        }

        return inlines;
    }

    private static string PlainText(ContainerBlock container) =>
        string.Join(" ", ConvertBlocks(container).Select(BlockText)).Trim();

    private static string BlockText(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => MarkdownRenderer.ToPlainText(paragraph.Inlines),
        MarkdownHeading heading => MarkdownRenderer.ToPlainText(heading.Inlines),
        MarkdownCodeBlock code => code.Code,
        MarkdownQuote quote => string.Join(" ", quote.Blocks.Select(BlockText)),
        MarkdownList list => string.Join(" ", list.Items.Select(item => string.Join(" ", item.Blocks.Select(BlockText)))),
        MarkdownTable table => string.Join(" ", table.Rows.Select(row => string.Join(" ", row))),
        MarkdownHtml html => html.Text,
        _ => "",
    };

    private static string FenceLanguage(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return "";
        var trimmed = info.Trim();
        var space = trimmed.IndexOfAny([' ', '\t', '{']);
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static string LinesOf(LeafBlock block) => block.Lines.ToString().TrimEnd('\n', '\r');
}
