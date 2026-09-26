#!/bin/bash
# 跑上游测试套件中与本次改动相关的用例，作为“没改坏功能”的证据。
#   ./run_tests.sh <git源码目录>          按主题筛选
#   ALL=1 ./run_tests.sh <git源码目录>    跑整个套件
set -u
SRC=${1:-$HOME/gitcn-build/git-2.55.0}
cd "$SRC/t" || exit 1
if [ -n "${ALL:-}" ]; then
  FILES=$(ls t[0-9]*.sh)
else
  FILES=$(ls t[0-9]*.sh | grep -E 'init|branch|reset|checkout|switch|restore|rebase|status|advice|remote|commit|merge')
fi
echo "候选 $(printf '%s\n' "$FILES" | wc -l) 个测试文件"
pass=0; fail=0; failed=""
for f in $FILES; do
  out=$(./"$f" --immediate 2>&1); rc=$?
  if [ "$rc" -eq 0 ]; then
    pass=$((pass+1))
    printf 'PASS  %s\n' "$f"
  else
    n=$(printf '%s' "$out" | grep -cE '^not ok')
    fail=$((fail+1)); failed="$failed $f"
    echo "FAIL  $f  (退出码 $rc, not ok x$n)"
    printf '%s\n' "$out" | grep -E '^not ok|^Bail out|^# failed' | head -4 | sed 's/^/        /'
  fi
done
echo
echo "测试：通过 $pass 个文件，失败 $fail 个文件"
[ "$fail" -eq 0 ] && echo "TESTS_OK" || { echo "FAILED_FILES:$failed"; exit 1; }
