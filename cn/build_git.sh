#!/bin/bash
# 从上游源码 tar 包构建汉化+改语义版 git（git-cn）。
#
#   ./build_git.sh <git源码tar包> <安装前缀>
#
# tar 包取法：https://github.com/git/git/archive/refs/tags/v2.55.0.tar.gz
# 依赖：gcc make gettext msgfmt libcurl/openssl/zlib/expat 头文件
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
TARBALL=${1:?用法: build_git.sh <git源码tar包> <安装前缀>}
PREFIX=${2:?需要安装前缀}
WORK=${WORK:-$HOME/gitcn-build}
JOBS=${JOBS:-$(nproc)}
MAKEFLAGS_COMMON="LIBC_CONTAINS_LIBINTL=1 NO_TCLTK=1 NO_PYTHON=1 NO_MAN=1 NO_INFO=1 -j$JOBS"

[ -f "$TARBALL" ] || { echo "!! 找不到 tar 包: $TARBALL" >&2; exit 1; }
mkdir -p "$WORK"
DIR=$(tar tf "$TARBALL" | head -1 | cut -d/ -f1)
SRC="$WORK/$DIR"
case "$SRC" in
  "$WORK"/*/*) ;;  # 只允许 WORK 下一层
  "$WORK"/*) ;;
  *) echo "!! 解压目录异常: $SRC" >&2; exit 1 ;;
esac

echo "== 1/4 解压 $DIR =="
rm -rf "$SRC"
tar xf "$TARBALL" -C "$WORK"

echo "== 2/4 打补丁 =="
python3 "$HERE/cn_patch.py" --src "$SRC" --apply

echo "== 3/4 编译 =="
cd "$SRC"
make $MAKEFLAGS_COMMON prefix="$PREFIX" all

echo "== 4/4 安装到 $PREFIX =="
make $MAKEFLAGS_COMMON prefix="$PREFIX" install
"$PREFIX/bin/git" --version
echo "BUILD_OK $PREFIX/bin/git"
