using System.Net;
using System.Text;
using Chat.Gateway;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

var config = GatewayConfig.FromArgs(args);
var dataDir = config.DataDirectory;
var urls = config.Urls;
var demoRoot = config.DemoRoot;
var demoIndex = Path.Combine(demoRoot, "index.html");
var hasDemo = config.HasDemo;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(urls);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 256L * 1024 * 1024;
});

var app = builder.Build();
var store = new GatewayStore(dataDir);
var proxy = new DeepSeekProxy(store, app.Logger);
app.Logger.LogInformation("Chat.Gateway 启动：data={DataDir} urls={Urls} demo={Demo}", dataDir, urls, hasDemo ? demoRoot : "(缺失)");

GatewayHost.RegisterRoutes(app, store, proxy, config);

await using (proxy)
{
    app.Run();
}

public partial class Program { }