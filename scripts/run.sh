#!/usr/bin/env bash
# 启动 LLMChat（Debug 构建）。
#   bash scripts/run.sh                 # 启动主窗口
#   bash scripts/run.sh --selftest      # 插件自检（默认扫描 bin 下 plugins）
#   bash scripts/run.sh --selftest DIR  # 指定插件目录
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLL="$ROOT/src/Chat.App/bin/Debug/net10.0/LLMChat.dll"

if [[ ! -f "$DLL" ]]; then
  echo "找不到 $DLL" >&2
  echo "先构建：dotnet build Chat.slnx -v q" >&2
  exit 1
fi

exec dotnet "$DLL" "$@"
