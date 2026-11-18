using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Chat.App.Services;

public enum DeepSeekAccountStatus
{
    Verified,
    Unavailable,
    InvalidKey,
    RateLimited,
    NetworkError,
    ServiceError,
    InvalidResponse,
}

/// <summary>余额字段一律按官方返回的 decimal 字符串原样保存，禁止转 double。</summary>
public sealed record DeepSeekBalanceInfo(
    string Currency,
    string TotalBalance,
    string GrantedBalance,
    string ToppedUpBalance);

public sealed record DeepSeekAccountResult(
    DeepSeekAccountStatus Status,
    string Message,
    IReadOnlyList<DeepSeekBalanceInfo> Balances)
{
    public bool Saved => Status is DeepSeekAccountStatus.Verified or DeepSeekAccountStatus.Unavailable;
}

/// <summary>
/// DeepSeek 数据源向导（M-A1，宿主层）：跳转控制台 → 用户粘贴 Key → 官方余额接口验证 →
/// 写回本地设置。DeepSeek 官方没有自动抓取 Key 的协议，因此做诚实的半自动，不碰插件契约。
/// </summary>
public interface IDeepSeekAccountService
{
    Uri ApiKeysPage { get; }
    Uri TopUpPage { get; }
    void OpenApiKeysPage();
    void OpenTopUpPage();
    Task<DeepSeekAccountResult> VerifyAndSaveAsync(string apiKey, CancellationToken cancellationToken = default);
}

public sealed class DeepSeekAccountService : IDeepSeekAccountService
{
    /// <summary>全应用唯一的 secret key 设置名；MainWindowViewModel.SecretKey 复用它。</summary>
    public const string SecretKey = "deepseek.apiKey";

    public static readonly Uri ApiKeysPage = new("https://platform.deepseek.com/api_keys");
    public static readonly Uri TopUpPage = new("https://platform.deepseek.com/top_up");
    public static readonly Uri BalanceEndpoint = new("https://api.deepseek.com/user/balance");

    private readonly HttpMessageInvoker _http;
    private readonly Chat.Storage.SettingsRepository _settings;
    private readonly IUrlLauncher _launcher;

    public DeepSeekAccountService(
        HttpMessageInvoker http,
        Chat.Storage.SettingsRepository settings,
        IUrlLauncher launcher)
    {
        _http = http;
        _settings = settings;
        _launcher = launcher;
    }

    Uri IDeepSeekAccountService.ApiKeysPage => ApiKeysPage;
    Uri IDeepSeekAccountService.TopUpPage => TopUpPage;

    public void OpenApiKeysPage() => _launcher.Launch(ApiKeysPage);

    public void OpenTopUpPage() => _launcher.Launch(TopUpPage);

    public async Task<DeepSeekAccountResult> VerifyAndSaveAsync(
        string apiKey, CancellationToken cancellationToken = default)
    {
        var key = (apiKey ?? "").Trim();
        if (key.Length == 0)
            return new DeepSeekAccountResult(DeepSeekAccountStatus.InvalidKey, "请先粘贴 API Key。", []);

        using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Fail(DeepSeekAccountStatus.InvalidKey, "API Key 无效，请重新创建或复制后重试。", key);
            if (response.StatusCode == (HttpStatusCode)429)
                return Fail(DeepSeekAccountStatus.RateLimited, "请求过于频繁，请稍后重试。", key);
            if (!response.IsSuccessStatusCode)
                return Fail(DeepSeekAccountStatus.ServiceError,
                    $"DeepSeek 服务返回错误（HTTP {(int)response.StatusCode}），请稍后重试。", key);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Fail(DeepSeekAccountStatus.InvalidResponse, "DeepSeek 返回的数据格式异常，请稍后重试。", key);

            var root = document.RootElement;
            var available = root.TryGetProperty("is_available", out var flag) && flag.ValueKind == JsonValueKind.True;
            var balances = new List<DeepSeekBalanceInfo>();
            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object) continue;
                    balances.Add(new DeepSeekBalanceInfo(
                        ReadString(info, "currency"),
                        ReadString(info, "total_balance"),
                        ReadString(info, "granted_balance"),
                        ReadString(info, "topped_up_balance")));
                }
            }

            // 只在官方接口明确返回成功（2xx + 可解析）后才写库；失败响应绝不覆盖已有 Key。
            _settings.Set(SecretKey, key);
            var mask = Mask(key);
            if (!available)
                return new DeepSeekAccountResult(DeepSeekAccountStatus.Unavailable,
                    $"API Key（末4位 {mask}）已验证并保存，但账户余额信息暂不可用，请稍后重试。", balances);
            return new DeepSeekAccountResult(DeepSeekAccountStatus.Verified,
                $"API Key（末4位 {mask}）已验证并保存。", balances);
        }
        catch (JsonException)
        {
            return Fail(DeepSeekAccountStatus.InvalidResponse, "DeepSeek 返回的数据格式异常，请稍后重试。", key);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(DeepSeekAccountStatus.NetworkError, "网络连接失败或请求超时，请检查网络后重试。", key);
        }
        catch (HttpRequestException)
        {
            return Fail(DeepSeekAccountStatus.NetworkError, "网络连接失败，请检查网络后重试。", key);
        }
    }

    private static DeepSeekAccountResult Fail(DeepSeekAccountStatus status, string message, string key) =>
        new(status, message, []);

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>UI 与日志最多展示末 4 位，绝不输出完整 Key。</summary>
    public static string Mask(string key)
    {
        var value = (key ?? "").Trim();
        return value.Length <= 4 ? "****" : value[^4..];
    }
}
