# 开发环境：直接复用已有的 .NET 10（不再下载）

## 结论

- SDK 已存在：`/srv/workspace/env/dotnet`（**10.0.403**，运行时 `Microsoft.NETCore.App 10.0.14` / `Microsoft.AspNetCore.App 10.0.14`）。
- 已建立全局入口：`/usr/local/bin/dotnet → /srv/workspace/env/dotnet/dotnet`，新 shell 直接可用，**无需 source、无需重装**。
- `/srv/LLMChat/global.json` 固定 `10.0.403`（`rollForward: latestFeature`），`dotnet` 会选中这套 SDK。
- NuGet 复用工作区缓存：`/srv/LLMChat/NuGet.config` 把 `globalPackagesFolder` 指向 `/srv/workspace/env/cache/nuget`，已缓存的包（xunit、gRPC 等）不再下载。

## 实测证据

```console
$ command -v dotnet
/usr/local/bin/dotnet
$ dotnet --version
10.0.403
$ dotnet --list-sdks
10.0.403 [/srv/workspace/env/dotnet/sdk]
```

`/srv/LLMChat` 内用探针工程（Avalonia + CommunityToolkit.Mvvm + Microsoft.Data.Sqlite + Markdig）验证：

```console
Restored /srv/LLMChat/tmp-probe/Probe.csproj (in 2.52 sec).
Probe -> .../bin/Debug/net10.0/Probe.dll
Build succeeded. 0 Warning(s) 0 Error(s)
```

首次还原 Avalonia 12.1.6 并手写 XAML 编译约 4 分钟（探针工程），之后增量构建秒级。包源 `api.nuget.org` 会 302 到镜像 `nuget.azure.cn`，`dotnet restore` 自动跟随，不需要额外配置。

## 已验证的包版本（net10.0 实测可还原、可编译）

| 包 | 版本 | 说明 |
|---|---|---|
| Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter | 12.1.6 | 当前 feed 最新（12.x 已是主线，非 11.x） |
| CommunityToolkit.Mvvm | 8.4.2 | |
| Microsoft.Data.Sqlite | 10.0.0 | 11.0.0 的 native 依赖 GLIBC ≥ 2.34，宿主只有 2.31，勿升级 |
| Markdig | 1.6.0 | |
| xunit.v3 | 4.0.2 | 已在共享缓存中 |
| xunit.runner.visualstudio | 3.1.4 | 已在共享缓存中 |
| Microsoft.NET.Test.Sdk | 18.11.0 | 已在共享缓存中 |

版本集中在 [Directory.Packages.props](../Directory.Packages.props)（`ManagePackageVersionsCentrally`），工程里只写 `<PackageReference Include="Avalonia" />`。

## 日常命令

```bash
# 不需要 source 任何脚本
dotnet build Chat.slnx -v q
bash scripts/test.sh        # 全量测试（构建 + 六套测试 dll；别用 dotnet test，见下）
bash scripts/run.sh         # 启动应用
bash scripts/run.sh --selftest
dotnet publish src/Chat.App -c Release -r linux-x64 --self-contained false
```

> `.NET 10` 的 VSTest 不再支持 **xunit.v3**，所以 `dotnet test` 跑不了本仓库的测试；
> 正确跑法是直接执行测试 dll（`dotnet tests/<Proj>/bin/Debug/net10.0/<Proj>.dll`），
> `scripts/test.sh` 已封装全部六套并汇总计数。

## 无显示器环境（headless，CI/服务器）

- App 的 UI 测试基于 **Avalonia.Headless**（`HeadlessUnitTestSession`，每测试隔离）在进程内加载 XAML、
  挂载 `MainWindow`，**不需要 X server / DISPLAY**，也**不要安装或调用 xvfb**。
- 统一用 `bash scripts/test.sh`；它会给每套测试加 `env -u DISPLAY -u DEEPSEEK_APIKEY`，
  避免环境变量兜底导致「未配置 API Key」用例失败、也不让测试发真实网络请求。
- 只有需要真实窗口时才要求桌面环境（本地直接启动无 DISPLAY 会以 `XOpenDisplay failed` 退出）。

## 插件目录与 `--selftest`

插件是「manifest.json + 入口程序集」的目录，**不需要注册**，构建时由 `Chat.App.csproj` 的
`CopyBuiltinPlugins` 目标按 manifest 里的 `id`/`version` 自动复制：

```text
src/Chat.App/bin/Debug/net10.0/plugins/
  chat.provider.deepseek/1.0.0/{chat.provider.deepseek.dll,manifest.json}
  chat.provider.openai/1.0.0/{chat.provider.openai.dll,manifest.json}
  chat.provider.opencodego/1.0.0/{chat.provider.opencodego.dll,manifest.json}
```

新增内置插件工程（`src/Chat.Providers.*`，带 `manifest.json`）后无需改 csproj，重新构建即自动纳入；
用户级插件放在数据目录的 `plugins/` 下，覆盖内置版本。

`--selftest` 不启动窗口，只扫描目录并逐个加载/握手/校验 Descriptor（离线，不联网），适合打包后与升级前检查：

```bash
bash scripts/run.sh --selftest                                   # 默认: AppContext.BaseDirectory/plugins
bash scripts/run.sh --selftest src/Chat.App/bin/Debug/net10.0/plugins
dotnet src/Chat.App/bin/Debug/net10.0/LLMChat.dll --selftest /path/to/plugins
```

输出每个插件一行 `OK`/`FAIL`，末行 `plugin-selftest: N loaded, M failed`；**退出码 0 当且仅当
loaded > 0 且 failed = 0**。所以空目录或坏插件（例如 manifest 指向缺失的 dll）会以退出码 1 结束，
CI 可以直接用退出码判断。

## 备选（若 symlink 被环境重置）

```bash
source /srv/workspace/env/env.sh   # 设置 DOTNET_ROOT / PATH / NUGET_PACKAGES
```

或单条命令内联：

```bash
DOTNET_ROOT=/srv/workspace/env/dotnet /srv/workspace/env/dotnet/dotnet build
```

## 风险与注意事项

1. **Avalonia 12 与 11 的差异**：网上多数样例是 11.x。M0 先用官方模板 `dotnet new install Avalonia.Templates` 生成一次基线工程（模板版本需与 12.1.6 匹配），确认 `App.axaml` / `ApplicationLifetime` / 主题包名后再手写，避免照抄 11 的写法。
2. **共享缓存并发**：`/srv/workspace/env/cache/nuget` 同时被 `/srv/workspace/apps/eap` 使用。NuGet 的缓存写入是原子的，正常并行构建安全；不要手工删除该目录。
3. **不设 `NUGET_PACKAGES` 也能工作**：因为 `NuGet.config` 里的 `globalPackagesFolder` 优先于环境变量缺省值；改配置后请 `dotnet nuget locals all --list` 确认实际路径。
