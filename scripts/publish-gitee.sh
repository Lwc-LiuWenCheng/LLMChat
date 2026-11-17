#!/usr/bin/env bash
# 把 dist/ 下的构建产物发布到 Gitee Release（国内加速下载）。
# 由 .github/workflows/build.yml 的 release job 调用，需以下环境变量：
#   GITEE_TOKEN        Gitee 私人令牌（projects 权限）
#   GITEE_OWNER        Gitee 用户名
#   GITEE_REPO         Release 仓库名
#   RELEASE_TAG        Release 标签，如 v0.1.0
#   RELEASE_VERSION    版本号，如 0.1.0（默认从 tag 去掉 v）
#   DIST_DIR           产物目录（含 *.zip 与 SHA256SUMS.txt）
set -euo pipefail

: "${GITEE_TOKEN:?GITEE_TOKEN is required}"
: "${GITEE_OWNER:?GITEE_OWNER is required}"
: "${GITEE_REPO:?GITEE_REPO is required}"
: "${RELEASE_TAG:?RELEASE_TAG is required}"
: "${RELEASE_VERSION:=${RELEASE_TAG#v}}"
: "${DIST_DIR:?DIST_DIR is required}"

API="https://gitee.com/api/v5/repos/${GITEE_OWNER}/${GITEE_REPO}"
NOTE="LLMChat ${RELEASE_VERSION} (Windows x64)。-full.zip 自包含开箱即用；-small.zip 框架依赖需装 .NET 10 Desktop Runtime。校验见 SHA256SUMS.txt。"

# 统一 API 调用：打印 HTTP 状态与响应体，失败时可见真实错误（不再被 --fail 吞掉）。
# 用法：req <METHOD> <url> [curl 额外参数...]；成功时 stdout 仅输出响应体。
req() {
  local method="$1"; shift
  local url="$1"; shift
  local resp status body
  resp="$(curl -sS --retry 3 --retry-delay 3 -X "$method" \
    "$@" "$url" -w $'\n%{http_code}')" || {
      echo "请求失败（网络层）：$method $url" >&2
      return 1
    }
  status="${resp##*$'\n'}"
  body="${resp%$'\n'*}"
  if [[ "$status" -lt 200 || "$status" -ge 300 ]]; then
    echo "HTTP $status :: $method $url" >&2
    echo "响应体：${body:0:500}" >&2
    return 1
  fi
  printf '%s' "$body"
}

# token 同时放 query（Gitee 部分接口只认 query）与表单。
gget() { req GET "$1?access_token=${GITEE_TOKEN}"; }
gpost() {
  local url="$1"; shift
  req POST "${url}?access_token=${GITEE_TOKEN}" -F "access_token=${GITEE_TOKEN}" "$@"
}

echo "==> 确保 Gitee 仓库有初始 commit（空仓库无法建 Release）"
branch="$(gget "$API" 2>/dev/null | python3 -c 'import sys,json;print(json.load(sys.stdin).get("default_branch") or "")' 2>/dev/null || true)"
if [[ -z "$branch" ]]; then
  echo "仓库为空，创建初始 README..."
  gpost "$API/contents/README.md" \
    -F "content=$(printf '# LLMChat Releases\n\n国内下载加速仓库，构建产物由 GitHub Actions 自动发布。\n' | base64 -w0)" \
    -F "message=init releases repo" \
    -F "branch=master" >/dev/null
  branch="master"
fi
echo "默认分支：$branch"

echo "==> 查找或创建 Release $RELEASE_TAG"
release_id="$(gget "$API/releases/tags/$RELEASE_TAG" 2>/dev/null \
  | python3 -c 'import sys,json
try:
    print(json.load(sys.stdin).get("id") or "")
except Exception:
    print("")' 2>/dev/null || true)"

if [[ -z "$release_id" ]]; then
  echo "创建 Release..."
  created="$(gpost "$API/releases" \
    -F "tag_name=$RELEASE_TAG" \
    -F "name=LLMChat $RELEASE_VERSION" \
    -F "body=$NOTE" \
    -F "target_commitish=$branch" \
    -F "prerelease=false" \
    -F "draft=false")"
  release_id="$(echo "$created" | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')"
fi
echo "Release id：$release_id"

echo "==> 上传附件"
shopt -s nullglob
uploaded=0
for f in "$DIST_DIR"/*.zip "$DIST_DIR"/SHA256SUMS.txt; do
  [[ -f "$f" ]] || continue
  name="$(basename "$f")"
  # 查询已有附件（容错：查询失败当作"没有"，直接尝试上传）
  existing="$(gget "$API/releases/$release_id/attach_files" 2>/dev/null \
    | N="$name" python3 -c 'import sys,json,os
try:
    names=[a.get("name") for a in json.load(sys.stdin)]
    print("yes" if os.environ["N"] in names else "no")
except Exception:
    print("no")' 2>/dev/null || echo "no")"
  if [[ "$existing" == "yes" ]]; then
    echo "  跳过已存在：$name"
    continue
  fi
  echo "  上传：$name ($(du -h "$f" | cut -f1))"
  gpost "$API/releases/$release_id/attach_files" -F "file=@$f" >/dev/null
  uploaded=$((uploaded + 1))
done

echo "==> 完成，新上传 $uploaded 个附件"
echo "下载地址：https://gitee.com/${GITEE_OWNER}/${GITEE_REPO}/releases/tag/${RELEASE_TAG}"
