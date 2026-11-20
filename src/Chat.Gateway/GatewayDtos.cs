using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Gateway;

public sealed class GatewayPutRequest
{
    public JsonElement? Session { get; set; }
    public List<GatewayFilePayload>? Files { get; set; }
}

public sealed class GatewayPatchRequest
{
    public string? Title { get; set; }
}

public sealed class GatewayFilePayload
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Mime { get; set; }
    public string? Base64 { get; set; }
}
