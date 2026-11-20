#!/usr/bin/env bash
# 方案 B 端到端集成冒烟：真网关 + 前端数据层 + 假 DeepSeek（无真实网络/密钥）。
#
# 做法：独立 C# 探针（tests/WebGatewaySmoke/Program.cs）用假 HttpMessageHandler
# 在进程内启动真实 Chat.Gateway，注入冒烟专用 Key 并把上游请求记成 JSONL；
# Node 驱动（tests/WebGatewaySmoke/smoke.mjs）直接 import 前端 remoteDb.ts，
# 用真 fetch 驱动会话/附件 CRUD、静态托管、SSE 与缺 Key 场景，并用 node:sqlite
# 直接检查 SQLite 落库。脚本可重复运行，trap 清理临时目录与探针进程。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DEMO_ROOT="$ROOT/web/chat-demo/dist"
PROBE_PROJECT="tests/WebGatewaySmoke/WebGatewaySmoke.csproj"
PROBE_DLL="tests/WebGatewaySmoke/bin/Debug/net10.0/WebGatewaySmoke.dll"
NODE_SCRIPT="tests/WebGatewaySmoke/smoke.mjs"

if [ ! -f "$DEMO_ROOT/index.html" ]; then
  echo "[smoke] 前端 dist 未构建（缺少 $DEMO_ROOT/index.html）；请先构建 web/chat-demo。" >&2
  exit 2
fi
if ! command -v node >/dev/null 2>&1; then
  echo "[smoke] 找不到 node（需要 Node 24+ 的原生 TS 剥离）。" >&2
  exit 2
fi

echo "==> 构建 WebGatewaySmoke 探针"
if ! dotnet build "$PROBE_PROJECT" -v q; then
  echo "[smoke] 探针构建失败。" >&2
  exit 1
fi

WORKDIR="$(mktemp -d /tmp/llmchat-web-smoke.XXXXXX)"
GATEWAY_PID=""
cleanup() {
  if [ -n "$GATEWAY_PID" ]; then
    touch "$WORKDIR/stop" 2>/dev/null || true
    for _ in $(seq 1 50); do
      if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then break; fi
      sleep 0.1
    done
    kill "$GATEWAY_PID" 2>/dev/null || true
    wait "$GATEWAY_PID" 2>/dev/null || true
  fi
  rm -rf "$WORKDIR"
}
trap cleanup EXIT INT TERM

echo "==> 启动进程内网关（临时数据目录：$WORKDIR/data）"
env -u DISPLAY -u DEEPSEEK_APIKEY -u DEEPSEEK_API_KEY \
  dotnet "$PROBE_DLL" \
    --data-dir "$WORKDIR/data" \
    --demo-root "$DEMO_ROOT" \
    --urls "http://127.0.0.1:0" \
    --record "$WORKDIR/upstream.jsonl" \
    --stop-file "$WORKDIR/stop" \
    --url-file "$WORKDIR/url" \
    > "$WORKDIR/gateway.log" 2>&1 &
GATEWAY_PID=$!

for _ in $(seq 1 100); do
  if [ -s "$WORKDIR/url" ]; then break; fi
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    echo "[smoke] 网关提前退出，日志：" >&2
    cat "$WORKDIR/gateway.log" >&2
    exit 1
  fi
  sleep 0.1
done
if [ ! -s "$WORKDIR/url" ]; then
  echo "[smoke] 网关启动超时（未写出 URL），日志：" >&2
  cat "$WORKDIR/gateway.log" >&2
  exit 1
fi
BASE_URL="$(tr -d '\r\n[:space:]' < "$WORKDIR/url")"

echo "==> 运行端到端 Node 驱动（$BASE_URL）"
if ! SMOKE_BASE_URL="$BASE_URL" \
     SMOKE_DATA_DIR="$WORKDIR/data" \
     SMOKE_RECORD="$WORKDIR/upstream.jsonl" \
     SMOKE_DB="$WORKDIR/data/chat.db" \
     node "$NODE_SCRIPT"; then
  echo "[smoke] Node 驱动失败，网关日志：" >&2
  cat "$WORKDIR/gateway.log" >&2
  exit 1
fi

echo "[smoke] 端到端冒烟全部通过。"
exit 0
