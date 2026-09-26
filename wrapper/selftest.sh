#!/bin/bash
# git-cn 包装器的端到端自检。在临时仓库里跑，用独立的 GIT_CONFIG_GLOBAL，
# 不会碰你真实的 ~/.gitconfig。
#   bash wrapper/selftest.sh
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
PY=${PYTHON:-python3}
command -v "$PY" >/dev/null 2>&1 || PY=python
WORK=$(mktemp -d)
export GIT_CONFIG_GLOBAL="$WORK/gitconfig" GIT_CONFIG_SYSTEM=/dev/null
: > "$GIT_CONFIG_GLOBAL"
pass=0; fail=0
run() { (cd "$1" && shift && "$PY" "$HERE/git_cn.py" "$@" 2>&1); }
chk() {  # chk 说明 期望子串 实际输出
    if printf '%s' "$3" | grep -qF -- "$2"; then
        echo "PASS  $1"; pass=$((pass+1))
    else
        echo "FAIL  $1  (没找到: $2)"; printf '%s\n' "$3" | head -10 | sed 's/^/        | /'
        fail=$((fail+1))
    fi
}

mkdir -p "$WORK/repo" && cd "$WORK/repo"
git init -q -b main . && git config user.email t@e.com && git config user.name 测试
printf 'a\n' > 说明.md && git add -A && git commit -qm 初始提交 >/dev/null
printf 'b\n' >> 说明.md && printf 'c\n' > 草稿.txt

chk "doctor 报出缺失项"     "建议修复" "$(run "$WORK/repo" doctor)"
chk "fix 试算不改配置"      "试算模式" "$(run "$WORK/repo" fix --dry)"
F=$(run "$WORK/repo" status)
chk "status 中文分类"       "改了但没暂存" "$F"
chk "status 显示中文文件名" "说明.md" "$F"
chk "fix 真实写入并给出回退方式" "git-cn fix --revert" "$(run "$WORK/repo" fix)"
chk "doctor 复检通过"       "配置全部到位" "$(run "$WORK/repo" doctor)"
# 试值护栏必须被证明有效：坏值要被拒、好值不能误杀
chk "护栏拦下 git 不认的值" "被 git 拒绝" "$(run "$WORK/repo" _probe diff.algorithm=meyers)"
chk "护栏不误杀合法值"     "可接受"     "$(run "$WORK/repo" _probe diff.algorithm=histogram)"

# 危险闸：回答“否”必须不执行
printf 'n\n' | run "$WORK/repo" restore 说明.md > "$WORK/g1.log" 2>&1
chk "危险闸拦下 restore"     "危险操作确认" "$(cat "$WORK/g1.log")"
chk "被拦下后改动仍在"       "b" "$(cat 说明.md)"
# 回答“是”并同意自动备份：改动必须能找回
printf 'y\ny\n' | run "$WORK/repo" restore 说明.md > "$WORK/g2.log" 2>&1
chk "确认后执行并自动备份"   "已备份" "$(cat "$WORK/g2.log")"
chk "备份进 stash 可找回"    "git-cn 自动备份" "$(git stash list)"

chk "拼错命令给出相近建议"   "你是不是想用" "$(run "$WORK/repo" rest --hard)"
chk "无远端时给出建立上游的出路" "还没有对应远端" "$(run "$WORK/repo" push)"
chk "reset 表讲清三层差别"   "唯一会丢代码的" "$(printf '2\nmixed\n' | run "$WORK/repo" reset)"
chk "reset 拒绝非数字计数"   "不是正整数" "$(printf 'abc\n' | run "$WORK/repo" reset)"
printf 'f\n' >> 说明.md
chk "checkout 已跟踪文件路由到还原" "还原文件" "$(printf 'n\n' | run "$WORK/repo" checkout 说明.md)"
chk "checkout 未跟踪文件讲明无法还原" "从未被 git add" "$(run "$WORK/repo" checkout 草稿.txt)"
chk "菜单可正常退出"         "git-cn  中文 Git 工具箱" "$(printf '0\n' | run "$WORK/repo")"
chk "帮助可读"               "写入符合国内习惯的默认配置" "$(run "$WORK/repo" help)"
chk "报错被人话翻译出来"     "为什么失败" "$(printf 'y\nn\n' | run "$WORK/repo" restore 不存在的文件.txt)"
chk "撤销提交给出回退哈希"   "git reset --hard" "$(printf '2\n' | run "$WORK/repo" undo)"

echo
echo "通过 $pass 项，失败 $fail 项  (工作目录 $WORK)"
[ "$fail" -eq 0 ] || exit 1
echo "SELFTEST_OK"
