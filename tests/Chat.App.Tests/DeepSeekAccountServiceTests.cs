using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chat.App.Services;
using Xunit;

namespace Chat.App.Tests;

internal sealed class FakeUrlLauncher : IUrlLauncher
{
    public List<Uri> Launched { get; } = [];
    public void Launch(Uri uri) => Launched.Add(uri);
}

internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
    public List<HttpRequestMessage> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _handler(request, cancellationToken);
    }
}

/// <summary>
/// M-A1 账户向导服务单测：假 HttpMessageHandler + 假 launcher，不触真实网络/浏览器。
/// </summary>
public class DeepSeekAccountServiceTests
{
    private const string TestKey = "sk-wizard-test-1234";

    private static (DeepSeekAccountService Service, FakeUrlLauncher Launcher, StubHttpHandler Handler, TempDataDirectory Temp)
        Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var temp = new TempDataDirectory();
        var database = new Chat.Storage.ChatDatabase(
            System.IO.Path.Combine(temp.Path, "app.db"));
        database.Initialize();
        var launcher = new FakeUrlLauncher();
        var handler = new StubHttpHandler((request, _) => Task.FromResult(respond(request)));
        var http = new HttpMessageInvoker(handler);
        var settings = new Chat.Storage.SettingsRepository(database);
        var service = new DeepSeekAccountService(http, settings, launcher);
        return (service, launcher, handler, temp);
    }

    [Fact]
    public void Launchers_UseExpectedPages()
    {
        var (service, launcher, _, temp) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using (temp)
        {
            Assert.Equal("https://platform.deepseek.com/api_keys", DeepSeekAccountService.ApiKeysPage.AbsoluteUri);
            Assert.Equal("https://platform.deepseek.com/top_up", DeepSeekAccountService.TopUpPage.AbsoluteUri);
            service.OpenApiKeysPage();
            service.OpenTopUpPage();
            Assert.Equal(new[] { DeepSeekAccountService.ApiKeysPage, DeepSeekAccountService.TopUpPage }, launcher.Launched);
        }
    }

    [Fact]
    public async Task Verify_Success_ParsesBalancesAndSavesKey()
    {
        const string body = """
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"123.450000","granted_balance":"10.000000","topped_up_balance":"113.450000"},{"currency":"USD","total_balance":"0.000000","granted_balance":"0.000000","topped_up_balance":"0.000000"}]}
            """;
        var (service, launcher, handler, temp) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync(TestKey, TestContext.Current.CancellationToken);
            Assert.Equal(DeepSeekAccountStatus.Verified, result.Status);
            Assert.Equal(2, result.Balances.Count);
            Assert.Equal("123.450000", result.Balances[0].TotalBalance);
            Assert.Equal("CNY", result.Balances[0].Currency);
            Assert.Equal("10.000000", result.Balances[0].GrantedBalance);
            Assert.Equal("113.450000", result.Balances[0].ToppedUpBalance);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.deepseek.com/user/balance", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(TestKey, request.Headers.Authorization.Parameter);
            Assert.Empty(launcher.Launched);
            Assert.DoesNotContain(TestKey, result.Message);
            Assert.Contains("1234", result.Message);
        }
    }

    [Fact]
    public async Task Verify_IsAvailableFalse_SavesButReportsUnavailable()
    {
        var (service, _, _, temp) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"is_available":false,"balance_infos":[]}""", Encoding.UTF8, "application/json"),
        });
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync(TestKey, TestContext.Current.CancellationToken);
            Assert.Equal(DeepSeekAccountStatus.Unavailable, result.Status);
            Assert.Contains("余额信息暂不可用", result.Message);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DeepSeekAccountStatus.InvalidKey, "无效")]
    [InlineData((HttpStatusCode)429, DeepSeekAccountStatus.RateLimited, "频繁")]
    [InlineData(HttpStatusCode.InternalServerError, DeepSeekAccountStatus.ServiceError, "服务返回错误")]
    public async Task Verify_HttpErrors_AreClassified(HttpStatusCode code, DeepSeekAccountStatus expected, string messageHint)
    {
        var (service, _, _, temp) = Create(_ => new HttpResponseMessage(code));
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync(TestKey, TestContext.Current.CancellationToken);
            Assert.Equal(expected, result.Status);
            Assert.Contains(messageHint, result.Message);
            Assert.DoesNotContain(TestKey, result.Message);
        }
    }

    [Fact]
    public async Task Verify_NetworkFailure_IsClassifiedAndKeyNotSaved()
    {
        var (service, _, _, temp) = Create(_ => throw new HttpRequestException("offline"));
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync(TestKey, TestContext.Current.CancellationToken);
            Assert.Equal(DeepSeekAccountStatus.NetworkError, result.Status);
            Assert.DoesNotContain(TestKey, result.Message);
        }
    }

    [Fact]
    public async Task Verify_BadJson_IsClassified()
    {
        var (service, _, _, temp) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        });
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync(TestKey, TestContext.Current.CancellationToken);
            Assert.Equal(DeepSeekAccountStatus.InvalidResponse, result.Status);
        }
    }

    [Fact]
    public async Task Verify_EmptyKey_DoesNotSendRequest()
    {
        var (service, _, handler, temp) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using (temp)
        {
            var result = await service.VerifyAndSaveAsync("  ", TestContext.Current.CancellationToken);
            Assert.Equal(DeepSeekAccountStatus.InvalidKey, result.Status);
            Assert.Empty(handler.Requests);
        }
    }
}
