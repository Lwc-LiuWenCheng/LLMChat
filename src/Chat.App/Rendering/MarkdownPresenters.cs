using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Chat.App.Rendering;

/// <summary>把 <see cref="MarkdownInline"/> 列表映射成 Avalonia 的 Inlines（Run/Span/LineBreak）。</summary>
internal static class MarkdownInlineBuilder
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Menlo,monospace");
    private static readonly IBrush CodeForeground = new SolidColorBrush(Color.Parse("#E6C07B"));
    private static readonly IBrush LinkForeground = new SolidColorBrush(Color.Parse("#6EA8FE"));

    public static void AddRange(InlineCollection target, IReadOnlyList<MarkdownInline>? inlines,
        FontWeight? weight = null, FontStyle? style = null)
    {
        if (inlines is null) return;
        foreach (var inline in inlines) AddOne(target, inline, weight, style);
    }

    private static void AddOne(InlineCollection target, MarkdownInline inline, FontWeight? weight, FontStyle? style)
    {
        switch (inline.Kind)
        {
            case MarkdownInlineKind.Text:
                target.Add(CreateRun(inline.Text, weight, style));
                break;
            case MarkdownInlineKind.Code:
                target.Add(new Run(inline.Text) { FontFamily = MonoFont, Foreground = CodeForeground });
                break;
            case MarkdownInlineKind.LineBreak:
                target.Add(new LineBreak());
                break;
            case MarkdownInlineKind.Strong:
                target.Add(CreateSpan(inline, FontWeight.Bold, style, null));
                break;
            case MarkdownInlineKind.Emphasis:
                target.Add(CreateSpan(inline, weight, FontStyle.Italic, null));
                break;
            case MarkdownInlineKind.Link:
                target.Add(CreateSpan(inline, weight, style, LinkForeground));
                break;
        }
    }

    private static Run CreateRun(string text, FontWeight? weight, FontStyle? style)
    {
        var run = new Run(text);
        if (weight is { } w) run.FontWeight = w;
        if (style is { } s) run.FontStyle = s;
        return run;
    }

    private static Span CreateSpan(MarkdownInline inline, FontWeight? weight, FontStyle? style, IBrush? foreground)
    {
        var span = new Span();
        if (weight is { } w) span.FontWeight = w;
        if (style is { } s) span.FontStyle = s;
        if (foreground is not null)
        {
            span.Foreground = foreground;
            span.TextDecorations = TextDecorations.Underline;
        }

        span.Inlines ??= new InlineCollection();
        AddRange(span.Inlines, inline.Children, weight, style);
        if (span.Inlines.Count == 0)
        {
            if (weight is not null || style is not null)
                span.Inlines.Add(CreateRun(inline.Text, null, null));
            else
                span.Inlines.Add(new Run(inline.Text));
        }

        return span;
    }
}

/// <summary>段落的富文本呈现：一个 SelectableTextBlock，Inlines 由 <see cref="InlinesSource"/> 重建。</summary>
public class MarkdownParagraphPresenter : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownInline>?> InlinesSourceProperty =
        AvaloniaProperty.Register<MarkdownParagraphPresenter, IReadOnlyList<MarkdownInline>?>(nameof(InlinesSource));

    public MarkdownParagraphPresenter()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public IReadOnlyList<MarkdownInline>? InlinesSource
    {
        get => GetValue(InlinesSourceProperty);
        set => SetValue(InlinesSourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InlinesSourceProperty) Rebuild();
    }

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        MarkdownInlineBuilder.AddRange(Inlines, InlinesSource);
    }
}

/// <summary>标题：按级别设置字号，行内样式与段落共用构建器。</summary>
public class MarkdownHeadingPresenter : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownInline>?> InlinesSourceProperty =
        AvaloniaProperty.Register<MarkdownHeadingPresenter, IReadOnlyList<MarkdownInline>?>(nameof(InlinesSource));

    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<MarkdownHeadingPresenter, int>(nameof(Level), 1);

    public MarkdownHeadingPresenter()
    {
        TextWrapping = TextWrapping.Wrap;
        FontWeight = FontWeight.Bold;
        FontSize = 22;
    }

    public IReadOnlyList<MarkdownInline>? InlinesSource
    {
        get => GetValue(InlinesSourceProperty);
        set => SetValue(InlinesSourceProperty, value);
    }

    public int Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty) FontSize = Level switch
        {
            <= 1 => 22,
            2 => 19,
            3 => 17,
            4 => 15,
            _ => 14,
        };
        if (change.Property is null || change.Property == InlinesSourceProperty) Rebuild();
    }

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        MarkdownInlineBuilder.AddRange(Inlines, InlinesSource);
    }
}

/// <summary>表格：按行/列生成 Border + SelectableTextBlock，首行按表头加粗。</summary>
public class MarkdownTablePresenter : Grid
{
    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<string>>?> RowsProperty =
        AvaloniaProperty.Register<MarkdownTablePresenter, IReadOnlyList<IReadOnlyList<string>>?>(nameof(Rows));

    public static readonly StyledProperty<bool> HasHeaderProperty =
        AvaloniaProperty.Register<MarkdownTablePresenter, bool>(nameof(HasHeader));

    public IReadOnlyList<IReadOnlyList<string>>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public bool HasHeader
    {
        get => GetValue(HasHeaderProperty);
        set => SetValue(HasHeaderProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty || change.Property == HasHeaderProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        var rows = Rows;
        if (rows is null || rows.Count == 0) return;

        var columns = rows.Max(row => row.Count);
        for (var column = 0; column < columns; column++) ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < rows.Count; row++) RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var borderBrush = new SolidColorBrush(Color.Parse("#2A2B31"));
        var headerBackground = new SolidColorBrush(Color.Parse("#23242A"));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var column = 0; column < row.Count; column++)
            {
                var text = new SelectableTextBlock
                {
                    Text = row[column],
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = HasHeader && rowIndex == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                };
                var border = new Border
                {
                    Child = text,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4),
                    Background = HasHeader && rowIndex == 0 ? headerBackground : null,
                };
                SetRow(border, rowIndex);
                SetColumn(border, column);
                Children.Add(border);
            }
        }
    }
}
