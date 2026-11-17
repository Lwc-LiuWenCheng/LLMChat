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

# GET：token 走 query string（Gitee 要求 access_token 参数）
gget() {
  local url="$1"
  curl -sS --fail --retry 3 --retry-delay 3 "${url}?access_token=${GITEE_TOKEN}"
}

# POST：token 走表单
gpost() {
  local url="$1"; shift
  curl -sS --fail --retry 3 --retry-delay 3 -X POST \
    -F "access_token=${GITEE_TOKEN}" "$@" "$url"
}

echo "==> 确保 Gitee 仓库有初始 commit（空仓库无法建 Release）"
branch="$(gget "$API" | python3 -c 'import sys,json;print(json.load(sys.stdin).get("default_branch") or "")')"
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
    print("")' || true)"

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
  existing="$(gget "$API/releases/$release_id/assets" 2>/dev/null \
    | N="$name" python3 -c 'import sys,json,os
try:
    names=[a.get("name") for a in json.load(sys.stdin)]
    print("yes" if os.environ["N"] in names else "no")
except Exception:
    print("no")')"
  if [[ "$existing" == "yes" ]]; then
    echo "  跳过已存在：$name"
    continue
  fi
  echo "  上传：$name ($(du -h "$f" | cut -f1))"
  gpost "$API/releases/$release_id/assets" -F "file=@$f" >/dev/null
  uploaded=$((uploaded + 1))
done

echo "==> 完成，新上传 $uploaded 个附件"
echo "下载地址：https://gitee.com/${GITEE_OWNER}/${GITEE_REPO}/releases/tag/${RELEASE_TAG}"
