using Chat.App.Rendering;
using Xunit;

namespace Chat.App.Tests;

/// <summary>
/// Markdig AST → 展示模型的纯单元测试，不依赖 Avalonia，也不涉及视图。
/// </summary>
public class MarkdownRendererTests
{
    [Fact]
    public void Render_EmptyInput_ReturnsNoBlocks()
    {
        Assert.Empty(MarkdownRenderer.Render(null));
        Assert.Empty(MarkdownRenderer.Render(""));
        Assert.Empty(MarkdownRenderer.Render("   \n\n "));
    }

    [Fact]
    public void Render_Headings_KeepLevelsAndText()
    {
        var blocks = MarkdownRenderer.Render("# 一级\n\n## 二级\n\n### 三级");

        var headings = blocks.OfType<MarkdownHeading>().ToList();
        Assert.Equal([1, 2, 3], headings.Select(heading => heading.Level));
        Assert.Equal(["一级", "二级", "三级"], headings.Select(heading => MarkdownRenderer.ToPlainText(heading.Inlines)));
    }

    [Fact]
    public void Render_Paragraph_ProducesTextStrongEmphasisAndCodeInlines()
    {
        var blocks = MarkdownRenderer.Render("普通 **粗体** 与 *斜体* 以及 `代码`。");

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks));
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Text && inline.Text.Contains("普通"));
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Strong && inline.Text == "粗体");
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Emphasis && inline.Text == "斜体");
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Code && inline.Text == "代码");
        Assert.Equal("普通 粗体 与 斜体 以及 代码。", MarkdownRenderer.ToPlainText(paragraph.Inlines));
    }

    [Fact]
    public void Render_Link_KeepsUrlAndLabel()
    {
        var blocks = MarkdownRenderer.Render("见 [文档](https://example.com/docs)。");

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks));
        var link = Assert.Single(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Link);
        Assert.Equal("文档", link.Text);
        Assert.Equal("https://example.com/docs", link.Url);
        Assert.Equal("见 文档。", MarkdownRenderer.ToPlainText(paragraph.Inlines));
    }

    [Fact]
    public void Render_HardLineBreak_BecomesLineBreakInline()
    {
        var blocks = MarkdownRenderer.Render("第一行  \n第二行");

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks));
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.LineBreak);
        Assert.Equal("第一行\n第二行", MarkdownRenderer.ToPlainText(paragraph.Inlines));
    }

    [Fact]
    public void Render_UnorderedList_UsesBulletMarkers()
    {
        var blocks = MarkdownRenderer.Render("- 甲\n- 乙");

        var list = Assert.IsType<MarkdownList>(Assert.Single(blocks));
        Assert.False(list.IsOrdered);
        Assert.Equal(["•", "•"], list.Items.Select(item => item.Marker));
        Assert.Equal(["甲", "乙"], list.Items.Select(item => MarkdownRenderer.ToPlainText(
            Assert.IsType<MarkdownParagraph>(Assert.Single(item.Blocks)).Inlines)));
    }

    [Fact]
    public void Render_OrderedList_StartsAtFirstNumber()
    {
        var blocks = MarkdownRenderer.Render("3. 三\n4. 四");

        var list = Assert.IsType<MarkdownList>(Assert.Single(blocks));
        Assert.True(list.IsOrdered);
        Assert.Equal(["3.", "4."], list.Items.Select(item => item.Marker));
    }

    [Fact]
    public void Render_FencedCodeBlock_KeepsLanguageAndCode()
    {
        var blocks = MarkdownRenderer.Render("\u0060\u0060\u0060cs\nvar x = 1;\nConsole.WriteLine(x);\n\u0060\u0060\u0060");

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(blocks));
        Assert.Equal("cs", code.Language);
        Assert.Equal("var x = 1;\nConsole.WriteLine(x);", code.Code);
    }

    [Fact]
    public void Render_IndentedCodeBlock_HasNoLanguage()
    {
        var blocks = MarkdownRenderer.Render("    缩进代码");

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(blocks));
        Assert.Equal("", code.Language);
        Assert.Equal("缩进代码", code.Code);
    }

    [Fact]
    public void Render_Quote_ContainsNestedBlocks()
    {
        var blocks = MarkdownRenderer.Render("> 引用 **重点**\n> 第二行");

        var quote = Assert.IsType<MarkdownQuote>(Assert.Single(blocks));
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(quote.Blocks));
        Assert.Contains(paragraph.Inlines, inline => inline.Kind == MarkdownInlineKind.Strong);
        // 引用内是软换行，渲染成空格而不是硬换行。
        Assert.Equal("引用 重点 第二行", MarkdownRenderer.ToPlainText(paragraph.Inlines));
    }

    [Fact]
    public void Render_Table_KeepsHeaderFlagAndCells()
    {
        var blocks = MarkdownRenderer.Render("| 列一 | 列二 |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |");

        var table = Assert.IsType<MarkdownTable>(Assert.Single(blocks));
        Assert.True(table.HasHeader);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["列一", "列二"], table.Rows[0]);
        Assert.Equal(["1", "2"], table.Rows[1]);
        Assert.Equal(["3", "4"], table.Rows[2]);
    }

    [Fact]
    public void Render_ThematicBreak_ProducesSeparator()
    {
        var blocks = MarkdownRenderer.Render("上\n\n---\n\n下");

        Assert.Equal(3, blocks.Count);
        Assert.IsType<MarkdownParagraph>(blocks[0]);
        Assert.IsType<MarkdownThematicBreak>(blocks[1]);
        Assert.IsType<MarkdownParagraph>(blocks[2]);
    }

    [Fact]
    public void Render_RawHtml_IsKeptAsText()
    {
        var blocks = MarkdownRenderer.Render("<div>原样输出</div>");

        var html = Assert.IsType<MarkdownHtml>(Assert.Single(blocks));
        Assert.Contains("原样输出", html.Text);
    }

    [Fact]
    public void ToPlainText_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MarkdownRenderer.ToPlainText(null));
        Assert.Equal("", MarkdownRenderer.ToPlainText([]));
    }
}
