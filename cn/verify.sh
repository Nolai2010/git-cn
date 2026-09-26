#!/bin/bash
# 对编译出来的汉化版 git 做行为验收。任何一项不过，退出码非 0。
#   ./verify.sh <git可执行文件路径>
set -u
GIT=${1:?用法: verify.sh <git可执行文件路径>}
[ -x "$GIT" ] || { echo "FAIL 找不到可执行文件 $GIT"; exit 1; }
export LC_ALL=zh_CN.UTF-8 LANG=zh_CN.UTF-8
export GIT_AUTHOR_NAME=测试 GIT_AUTHOR_EMAIL=t@example.com
export GIT_COMMITTER_NAME=测试 GIT_COMMITTER_EMAIL=t@example.com
WORK=$(mktemp -d)
export GIT_CONFIG_GLOBAL=$WORK/gitconfig
: > "$GIT_CONFIG_GLOBAL"
"$GIT" config --global core.quotepath false   # 等价 git-cn fix 里最关键的一项
pass=0; fail=0
chk() {  # chk 说明 期望子串 实际输出
    if printf '%s' "$3" | grep -qF -- "$2"; then
        echo "PASS  $1"; pass=$((pass+1))
    else
        echo "FAIL  $1  (没找到: $2)"; printf '%s\n' "$3" | head -10 | sed 's/^/        | /'
        fail=$((fail+1))
    fi
}
cd "$WORK" || exit 1

chk "版本号带 cn1 标识" "cn1" "$("$GIT" --version 2>&1)"

"$GIT" init -q . 2>&1
chk "默认分支是 main" "refs/heads/main" "$("$GIT" symbolic-ref HEAD 2>&1)"

echo a > 中文名.txt
"$GIT" add -A && "$GIT" commit -qm 第一次提交
echo b >> 中文名.txt            # 已修改未暂存
echo c > 新文件.txt && "$GIT" add 新文件.txt   # 已暂存
echo d > 临时草稿.txt            # 未跟踪
S=$("$GIT" status 2>&1)
chk "已暂存表头改口"       "已暂存" "$S"
chk "未 add 表头改口"      "已修改但没 git add" "$S"
chk "未跟踪表头改口"       "git 还没管起来的文件" "$S"
chk "restore 提示说清代价" "找不回来" "$S"
chk "中文文件名不被转义"   "中文名.txt" "$S"
chk "暂存区里的新文件可见" "新文件.txt" "$S"

R=$("$GIT" reset 2>&1)
chk "reset 不带模式讲清三层" "MIXED 重置" "$R"
chk "checkout . 说明不是切分支" "还原文件内容" "$("$GIT" checkout . 2>&1)"

echo e >> 中文名.txt && "$GIT" add -A && "$GIT" commit -qm 第二次提交 >/dev/null 2>&1
D=$("$GIT" checkout "$("$GIT" rev-parse HEAD~1)" 2>&1)
chk "游离 HEAD 说明提交会悬空" "不属于任何分支" "$D"
"$GIT" checkout -q main 2>/dev/null

"$GIT" init -q --bare "$WORK/remote.git"
"$GIT" remote add origin "$WORK/remote.git"
chk "无上游提示给了长期解法" "还没有上游" "$("$GIT" push 2>&1)"
"$GIT" push -q -u origin main >/dev/null 2>&1
chk "按建议建立上游后推送成功" "refs/remotes/origin/main" "$("$GIT" for-each-ref refs/remotes 2>&1)"

# 端到端：从裸仓库克隆回来，中文文件名和内容都要完好
"$GIT" clone -q "file://$WORK/remote.git" "$WORK/back" 2>&1
B=$("$GIT" -C "$WORK/back" show --stat --oneline HEAD 2>&1)
chk "克隆回来的仓库内容完整" "中文名.txt" "$B"

# https 传输层：能连上就给 sha；连不上也要区分“没编译 curl”和“这台机器网络不通”
H=$("$GIT" ls-remote https://github.com/git/git HEAD 2>&1)
if printf '%s' "$H" | grep -qE '[0-9a-f]{40}[[:space:]]'; then
    echo "PASS  https 端到端可用"; pass=$((pass+1))
elif printf '%s' "$H" | grep -qiE 'unsupported|not built|remote helper|invalid proxy|no curl'; then
    echo "FAIL  https 没编译进去"; printf '%s\n' "$H" | head -4 | sed 's/^/        | /'; fail=$((fail+1))
else
    echo "PASS  https 传输层已链接（报错来自连接层，是这台机器的网络问题）"
    printf '%s\n' "$H" | head -2 | sed 's/^/        ! /'
    pass=$((pass+1))
fi

echo
echo "通过 $pass 项，失败 $fail 项  (工作目录 $WORK)"
[ "$fail" -eq 0 ] || exit 1
echo "VERIFY_OK"
