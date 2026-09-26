#!/bin/bash
# 发布独立可执行文件到 E:\gitcn-publish（自包含，用户机器不需要装 .NET）
set -u
export NUGET_PACKAGES='E:\dev-cache\nuget-packages'
export DOTNET_CLI_HOME='E:\dev-cache\dotnet-home'
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /d/Agents/Workfile/GitCN/wpf || exit 1
OUT=/e/gitcn-publish
mkdir -p $OUT

pub() {  # pub 项目 RID 输出子目录
  local proj=$1 rid=$2 name=$3
  echo "===== publish $name ($rid) ====="
  dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none -o "$OUT/$name" > "$OUT/$name.log" 2>&1
  local rc=$?
  tail -5 "$OUT/$name.log"
  if [ "$rc" -eq 0 ]; then echo "OK $name"; else echo "FAILED $name (rc=$rc)"; fi
}

pub src/GitCn.App/GitCn.App.csproj win-x64 win-gui
pub src/GitCn.Cli/GitCn.Cli.csproj win-x64 win-cli
pub src/GitCn.Cli/GitCn.Cli.csproj linux-x64 linux-cli
pub src/GitCn.Cli/GitCn.Cli.csproj osx-arm64 mac-arm64-cli
pub src/GitCn.Cli/GitCn.Cli.csproj osx-x64 mac-x64-cli

echo "===== 产物清单 ====="
for d in win-gui win-cli linux-cli mac-arm64-cli mac-x64-cli; do
  if [ -d "$OUT/$d" ]; then
    printf "%-14s %8s MB  " "$d" "$(du -sm "$OUT/$d" | cut -f1)"
    ls "$OUT/$d" | grep -E '^(git-cn|git-cn-gui)(\.exe)?$' | tr '\n' ' '
    echo
  else
    printf "%-14s 缺失\n" "$d"
  fi
done
echo "PUBLISH_DONE"
