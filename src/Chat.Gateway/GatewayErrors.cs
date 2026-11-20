using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Gateway;

public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class GatewayBadRequestException(string message) : Exception(message);

public static class GatewayErrors
{
    public static IResult NotFound(string message) => Error("not_found", message, StatusCodes.Status404NotFound);
    public static IResult InvalidSession(string message) => Error("invalid_session", message, StatusCodes.Status400BadRequest);
    public static IResult IoError(string message) => Error("io_error", message, StatusCodes.Status500InternalServerError);
    public static IResult NotImplemented(string message) => Error("not_implemented", message, StatusCodes.Status501NotImplemented);
    public static IResult MissingKey(string message) => Error("missing_api_key", message, StatusCodes.Status428PreconditionRequired);

    public static IResult Error(string code, string message, int status) => Results.Json(
        new { error = new { code, message } },
        statusCode: status);
}
