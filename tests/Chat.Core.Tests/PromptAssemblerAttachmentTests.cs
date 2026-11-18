using System.Text;
using System.Text.Json;
using Chat.Abstractions;
using Xunit;

namespace Chat.Core.Tests;

public class PromptAssemblerAttachmentTests
{
    private static MessageNode Node(params ContentPart[] parts) =>
        new() { Id = 1, ParentId = null, Role = MessageRole.User, Parts = parts };

    private static Session Session() => new() { Id = "s1", Model = "model" };

    private static string ImageMeta(string path, string? mime = "image/png", string? name = "photo.png") =>
        JsonSerializer.Serialize(new { name, mime, relPath = path, isImage = true });

    private static string FileMeta(string path, string name, string? mime, long? size = null) =>
        JsonSerializer.Serialize(new { name, mime, relPath = path, size });

    private static ChatPart.Text TextPart(ChatPart part) => Assert.IsType<ChatPart.Text>(part);

    [Fact]
    public void Image_ReaderAvailable_MapsToImagePartWithMimeAndBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        AttachmentReader reader = path =>
        {
            Assert.Equal("data/files/abc.png", path);
            return bytes;
        };
        var node = Node(new ContentPart(PartKind.Image, null, ImageMeta("data/files/abc.png", "image/jpeg; charset=binary")));

        var parts = PromptAssembler.ToParts(node, reader);

        var image = Assert.IsType<ChatPart.Image>(Assert.Single(parts));
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Same(bytes, image.Data);
    }

    [Fact]
    public void Image_UnreadableOrNullReader_DegradesToVisiblePlaceholder()
    {
        var node = Node(new ContentPart(PartKind.Image, null, ImageMeta("missing.png", name: "风景.jpg")));

        var withReader = PromptAssembler.ToParts(node, _ => null);
        var withoutReader = PromptAssembler.ToParts(node);

        Assert.Contains("风景.jpg", TextPart(Assert.Single(withReader)).Value);
        Assert.Contains("风景.jpg", TextPart(Assert.Single(withoutReader)).Value);
    }

    [Fact]
    public void Image_ReaderThrows_DegradesToPlaceholderWithoutThrowing()
    {
        var node = Node(new ContentPart(PartKind.Image, null, ImageMeta("boom.png")));

        var parts = PromptAssembler.ToParts(node, _ => throw new IOException("boom"));

        Assert.Contains("图片", TextPart(Assert.Single(parts)).Value);
    }

    [Fact]
    public void File_TextMime_InjectsUtf8Content()
    {
        var text = "第一行" + Environment.NewLine + "第二行";
        var node = Node(new ContentPart(PartKind.File, null, FileMeta("f.txt", "notes.txt", "text/plain")));

        var parts = PromptAssembler.ToParts(node, _ => Encoding.UTF8.GetBytes(text));

        var value = TextPart(Assert.Single(parts)).Value;
        Assert.StartsWith("[notes.txt]", value);
        Assert.Contains(text, value);
    }

    [Fact]
    public void File_BinaryMime_ProducesExplicitPlaceholder()
    {
        var node = Node(new ContentPart(PartKind.File, null, FileMeta("f.bin", "data.bin", "application/octet-stream", 12)));

        var parts = PromptAssembler.ToParts(node, _ => new byte[] { 0, 1, 2 });

        var value = TextPart(Assert.Single(parts)).Value;
        Assert.Contains("data.bin", value);
        Assert.Contains("二进制附件", value);
        Assert.Contains("12 字节", value);
    }

    [Fact]
    public void File_TextExtensionWithNulBytes_FallsBackToBinaryPlaceholder()
    {
        var node = Node(new ContentPart(PartKind.File, null, FileMeta("f.log", "mixed.log", "text/plain")));

        var parts = PromptAssembler.ToParts(node, _ => new byte[] { (byte)'a', 0, (byte)'b' });

        Assert.Contains("二进制附件", TextPart(Assert.Single(parts)).Value);
    }

    [Fact]
    public void File_Unreadable_KeepsPlaceholderRatherThanDropping()
    {
        var node = Node(new ContentPart(PartKind.File, null, FileMeta("missing.md", "missing.md", "text/markdown")));

        var parts = PromptAssembler.ToParts(node, _ => null);

        Assert.Contains("missing.md", TextPart(Assert.Single(parts)).Value);
    }

    [Fact]
    public void File_BadMetaJson_DegradesToPlaceholderWithoutThrowing()
    {
        var node = Node(new ContentPart(PartKind.File, null, "{not json"));

        var parts = PromptAssembler.ToParts(node, _ => throw new InvalidOperationException("never called"));

        Assert.Contains("附件", TextPart(Assert.Single(parts)).Value);
    }

    [Fact]
    public void File_MissingMimeInfersTextFromFilename()
    {
        var node = Node(new ContentPart(PartKind.File, null, FileMeta("f.md", "readme.md", null)));

        var parts = PromptAssembler.ToParts(node, _ => Encoding.UTF8.GetBytes("markdown body"));

        var value = TextPart(Assert.Single(parts)).Value;
        Assert.StartsWith("[readme.md]", value);
        Assert.Contains("markdown body", value);
    }

    [Fact]
    public void File_LegacyMetaAliases_AreUnderstood()
    {
        var meta = JsonSerializer.Serialize(new
        {
            file_id = "x",
            file_name = "legacy.md",
            mime_type = "text/markdown",
            rel_path = "legacy/path.md",
        });
        var node = Node(new ContentPart(PartKind.File, null, meta));

        var parts = PromptAssembler.ToParts(node, path =>
        {
            Assert.Equal("legacy/path.md", path);
            return Encoding.UTF8.GetBytes("legacy content");
        });

        var value = TextPart(Assert.Single(parts)).Value;
        Assert.Contains("legacy.md", value);
        Assert.Contains("legacy content", value);
    }

    [Fact]
    public void Image_NestedOriginalMeta_IsUnderstood()
    {
        var meta = JsonSerializer.Serialize(new
        {
            original = new { name = "nested.png", mime = "image/webp", relPath = "nested.webp" },
        });
        var node = Node(new ContentPart(PartKind.Image, null, meta));

        var parts = PromptAssembler.ToParts(node, path =>
        {
            Assert.Equal("nested.webp", path);
            return new byte[] { 9, 8, 7 };
        });

        var image = Assert.IsType<ChatPart.Image>(Assert.Single(parts));
        Assert.Equal("image/webp", image.MediaType);
    }

    [Fact]
    public void DefaultReaderNull_KeepsExistingPartsAndDropsAttachmentsOnly()
    {
        var node = Node(
            ContentPart.OfText("hello"),
            ContentPart.OfThinking("hidden"),
            new ContentPart(PartKind.Image, null, ImageMeta("a.png")),
            new ContentPart(PartKind.File, null, FileMeta("b.txt", "b.txt", "text/plain")));

        var parts = PromptAssembler.ToParts(node);

        Assert.Equal(3, parts.Count); // Text + 两个可见附件占位；Thinking 仍被过滤。
        Assert.IsType<ChatPart.Text>(parts[0]);
    }

    [Fact]
    public void Build_WithAttachmentReader_KeepsMultiTurnSemantics()
    {
        var session = Session();
        MessageNode[] path =
        [
            new() { Id = 1, ParentId = null, Role = MessageRole.User, Parts = [ContentPart.OfText("q1"), new ContentPart(PartKind.Image, null, ImageMeta("i.png"))] },
            new() { Id = 2, ParentId = 1, Role = MessageRole.Assistant, Parts = [ContentPart.OfText("a1")] },
            new() { Id = 3, ParentId = 2, Role = MessageRole.User, Parts = [ContentPart.OfText("q2")] },
        ];

        var request = PromptAssembler.Build(session, path, attachmentReader: _ => new byte[] { 1 });

        Assert.Equal(3, request.Messages.Count);
        Assert.IsType<ChatPart.Image>(request.Messages[0].Parts[1]);
        Assert.Equal("q2", ((ChatPart.Text)request.Messages[2].Parts[0]).Value);
    }
}
