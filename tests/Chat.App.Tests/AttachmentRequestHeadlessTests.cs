using System.Text;
using System.Text.Json;
using Chat.Abstractions;
using Chat.Core;
using Xunit;

namespace Chat.App.Tests;

/// <summary>
/// 附件全链路 headless 验证：把既有会话（含图片/文件片段）种进 SQLite 并写入真实附件字节，
/// 再在真实 MainWindowViewModel 里发一条消息，断言最终交给 provider 的 ChatRequest 携带附件。
/// </summary>
public class AttachmentRequestHeadlessTests
{
    private static string Meta(PartKind kind, string name, string mime, string relPath, long size) =>
        JsonSerializer.Serialize(new
        {
            fragmentType = kind == PartKind.Image ? "image" : "file",
            name,
            mime,
            relPath,
            size,
            sha256 = "sha",
            isImage = kind == PartKind.Image,
        });

    private static async Task<ChatRequest> RunSeededAsync(byte[] imageBytes, byte[] fileBytes)
    {
        var fake = new FakeChatProvider().Text("收到");
        ChatRequest? captured = null;
        fake.OnStream = request => captured = request;

        await MainWindowHeadlessTests.RunWindowAsync(async (services, vm, _) =>
        {
            var imagePath = System.IO.Path.Combine("data", "files", "image.bin");
            var textPath = System.IO.Path.Combine("data", "files", "note.txt");
            Directory.CreateDirectory(System.IO.Path.Combine(services.DataDirectory, "data", "files"));
            await File.WriteAllBytesAsync(System.IO.Path.Combine(services.DataDirectory, imagePath), imageBytes);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(services.DataDirectory, textPath), fileBytes);

            var session = new Session
            {
                Id = "attach-1",
                ProviderId = fake.Descriptor.Id,
                Model = "fake-model",
                CurrentMessageId = 1,
            };
            services.Sessions.Upsert(session);
            services.Messages.Insert(session.Id, new MessageNode
            {
                Id = 1,
                ParentId = null,
                Role = MessageRole.User,
                Parts =
                [
                    ContentPart.OfText("看附件"),
                    new ContentPart(PartKind.Image, null, Meta(PartKind.Image, "shot.png", "image/png", imagePath, imageBytes.Length)),
                    new ContentPart(PartKind.File, null, Meta(PartKind.File, "note.txt", "text/plain", textPath, fileBytes.Length)),
                ],
            });

            vm.RefreshSessions(session.Id);
            vm.Composer = "继续";
            await vm.SendCommand.ExecuteAsync(null);
            HeadlessSession.Pump();
            HeadlessSession.Pump();
        }, [fake]);

        Assert.NotNull(captured);
        return captured!;
    }

    private static ChatMessage FindHistory(ChatRequest request) => Assert.Single(request.Messages,
        m => m.Role == ChatRole.User && m.Parts.Any(p => p is ChatPart.Text t && t.Value == "看附件"));

    [Fact]
    public async Task SeededImageAndTextFile_AreIncludedInProviderRequest()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var text = "会议纪要第一行" + Environment.NewLine + "第二行";
        var request = await RunSeededAsync(imageBytes, Encoding.UTF8.GetBytes(text));

        var history = FindHistory(request);
        var image = Assert.Single(history.Parts.OfType<ChatPart.Image>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(imageBytes, image.Data);
        var attachmentText = Assert.Single(history.Parts.OfType<ChatPart.Text>(), p => p.Value.StartsWith("[note.txt]"));
        Assert.Contains(text, attachmentText.Value);
    }

    [Fact]
    public async Task BinaryFileAndUnreadableImage_ProduceVisiblePlaceholders()
    {
        var request = await RunSeededAsync([], new byte[] { 0, 1, 2, 3 });

        var history = FindHistory(request);
        Assert.Contains(history.Parts.OfType<ChatPart.Text>(), p => p.Value.Contains("图片") && p.Value.Contains("附件字节不可用"));
        Assert.Contains(history.Parts.OfType<ChatPart.Text>(), p => p.Value.Contains("二进制附件"));
    }
}
