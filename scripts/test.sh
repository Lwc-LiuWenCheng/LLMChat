#!/usr/bin/env bash
# LLMChat 全量测试：先构建 solution，再依次直接运行各测试工程的可执行 dll。
#
# 为什么不用 `dotnet test`：本仓库用 .NET 10 + xunit.v3，VSTest 不再支持 xunit.v3，
# 因此跑法是 `dotnet tests/<Proj>/bin/Debug/net10.0/<Proj>.dll`（脚本已封装）。
# 无显示器环境（CI / 服务器）不需要 xvfb：App 测试走 Avalonia.Headless。
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SUITES=(
  "tests/Chat.Core.Tests"
  "tests/Chat.Storage.Tests"
  "tests/Chat.Kernel.Tests"
  "tests/Chat.App.Tests"
  "tests/Chat.Providers.OpenAiCompatible.Tests"
  "tests/Chat.Providers.OpenCodeGo.Tests"
)

echo "==> dotnet build Chat.slnx -v q"
if ! dotnet build Chat.slnx -v q; then
  echo "构建失败，测试中止。" >&2
  exit 1
fi

LOG_DIR="$(mktemp -d "${TMPDIR:-/tmp}/llmchat-tests.XXXXXX")"
trap 'rm -rf -- "${LOG_DIR:?}"' EXIT

failures=0
passed=0
rows=()

for project in "${SUITES[@]}"; do
  name="$(basename "$project")"
  dll="$project/bin/Debug/net10.0/$name.dll"

  echo
  echo "==> $name"

  if [[ ! -f "$dll" ]]; then
    echo "缺少测试程序集：$dll" >&2
    rows+=("$(printf '%-44s %6s %7s  %s' "$name" "-" "-" "MISSING")")
    failures=$((failures + 1))
    continue
  fi

  log="$LOG_DIR/$name.log"
  env -u DISPLAY -u DEEPSEEK_APIKEY dotnet "$dll" 2>&1 | tee "$log"
  code=${PIPESTATUS[0]}

  counts="$(grep -E 'Total: [0-9]+, Errors: [0-9]+, Failed: [0-9]+' "$log" | tail -1 || true)"
  if [[ -n "$counts" ]]; then
    total="$(sed -E 's/.*Total: ([0-9]+), Errors: ([0-9]+), Failed: ([0-9]+).*/\1/' <<<"$counts")"
    errors="$(sed -E 's/.*Total: ([0-9]+), Errors: ([0-9]+), Failed: ([0-9]+).*/\2/' <<<"$counts")"
    failed="$(sed -E 's/.*Total: ([0-9]+), Errors: ([0-9]+), Failed: ([0-9]+).*/\3/' <<<"$counts")"
  else
    total='?' errors='?' failed='?'
  fi

  if [[ "$code" -eq 0 && "$errors" == "0" && "$failed" == "0" && "$total" != "0" && "$total" != "?" ]]; then
    status="PASS"
    passed=$((passed + 1))
  else
    status="FAIL"
    failures=$((failures + 1))
  fi

  rows+=("$(printf '%-44s %6s %7s  %s' "$name" "$total" "$failed" "$status")")
done

echo
echo "==================== 测试汇总 ===================="
printf '%-44s %6s %7s  %s\n' "套件" "Total" "Failed" "结果"
for row in "${rows[@]}"; do
  echo "$row"
done
echo "--------------------------------------------------"
echo "套件：${#SUITES[@]}  通过：$passed  失败：$failures"

if [[ "$failures" -ne 0 ]]; then
  echo "存在失败的测试套件。" >&2
  exit 1
fi

echo "全部测试通过。"
