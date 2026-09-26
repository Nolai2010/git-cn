# 验收记录（2026-09-27）

环境：Windows 11 + WSL2 Ubuntu 26.04（内核 6.18.40.1），gcc 15.2 / GNU Make 4.4.1 / gettext 0.23.2，
libcurl 8.18（openssl 后端）、zlib、expat、openssl 头文件齐全。上游基线 `git/git` **v2.55.0**。

## 1. 汉化版 git 构建

```
cn/build_git.sh → 解压 → cn/cn_patch.py --apply（含 msgfmt -c 校验）→ make -j4 → make install
产物：git version 2.55.0.cn1
```

补丁命中情况（`cn_patch.py --check-only` 可复跑校验）：

- C 补丁 4 处 + 上游断言修正 2 处，全部**唯一命中**（命中 0 次或多次都会报错退出）
- 译文改写 16 处 + 新译文 2 条，全部通过占位符与换行结尾一致性检查
- `msgfmt -c` 校验通过（这一层是编译期发现问题的关键：第一次就是它抓到 `msgid`/`msgstr` 换行不对称）
- 生成的单文件 diff：`cn/git-cn-src.patch`，279 行 / 6 个文件，能在干净源码上 `patch -p2` 重放（已 dry-run 验证）

## 2. 行为验收 `cn/verify.sh`：15 项全过

对**安装后的二进制**逐项断言，不是对源码静态检查：

| 断言 | 结果 |
|---|---|
| 版本号含 `cn1` | PASS |
| 默认分支为 `main` | PASS |
| 已暂存 / 未 add / 未跟踪 三类表头改口 | PASS ×3 |
| `restore` 提示说明"丢出去找不回来" | PASS |
| 中文文件名不被转义成八进制 | PASS |
| 暂存区里的中文新文件可见 | PASS |
| `git reset` 不带模式讲清三层 | PASS |
| `git checkout .` 说明不是切分支 | PASS |
| 游离 HEAD 说明"提交会悬空" | PASS |
| 无上游提示给长期解法 | PASS |
| 建立上游后推送成功 | PASS |
| `file://` 克隆回来中文文件名完好 | PASS |
| https 传输层已链接 | PASS（见下方说明） |

https 一项的诚实说明：这台机器把 `github.com` 解析到 `127.0.0.1`（Windows 侧有本地 GitHub 加速程序接管），
WSL 的 `127.0.0.1` 是虚拟机本身，所以真实外网请求连不上（`Failed to connect ... port 443 after 4 ms`）。
该报错来自连接层而非"没有 curl 支持"，因此判定 https 支持已编译进去，并用 `file://` 端到端克隆补齐了真实读写路径的验证。
**没有在能出网的机器上验证过对 GitHub 的 https 访问**，这是遗留项。

## 3. 上游回归测试 `cn/run_tests.sh`

按主题筛选（init / branch / reset / checkout / switch / restore / rebase / status / advice / remote / commit / merge）：

```
测试：通过 263 个文件，失败 0 个文件      TESTS_OK
```

判定用的是测试脚本**退出码**。这个判据不是想当然：第一轮跑出 2 个真失败，正是退出码抓出来的——

- `t0001-init` 的 `default branch name` 断言默认分支为 `master`
- `t0001-init` 的 `advice on unconfigured init.defaultBranch` 断言还有 `master→main` 迁移提示

两者都是本 fork **有意的行为分叉**，因此把断言一并改掉（写进 `cn_patch.py`，可复现），
第二轮 263 个文件全绿。

注意：这批 263 个文件里有一部分是因为缺 svn/p4/tcl 等依赖而整体跳过（退出码 0），
所以"263 全过"要理解为"没有任何一个能跑起来的用例失败"，而不是"263 个文件都真跑了断言"。

## 4. 包装器自检 `wrapper/selftest.sh`：22 项全过

在临时仓库里跑，`GIT_CONFIG_GLOBAL` 指向临时文件，**不会碰到真实 `~/.gitconfig`**：

- `doctor` 能报出缺失项，`fix` 后复检为"配置全部到位"；`fix --dry` 明确不落盘
- **试值护栏被正反两个方向证明**：`_probe diff.algorithm=meyers` → "被 git 拒绝"（退出码 1），
  `_probe diff.algorithm=histogram` → "可接受"。会真解析配置值的命令是 `git -c k=v diff --quiet`
  （在一次性临时仓库里跑）；`rev-parse`/`config --get`/`--version` 都不会解析值，拿它们当护栏等于没护栏
- 危险闸：回答"否"时 `restore` 未执行、改动仍在；回答"是"时先自动 `git stash push -u` 备份且能从 `stash list` 找回
- `git restore --staged` 不触发闸门（可逆操作）
- 报错翻译卡片在真实失败路径上出现；拼错命令给出相近命令
- `checkout` 对已跟踪文件路由到还原、对未跟踪文件说明"从未被 git add 过所以无法还原"
- `reset` 拒绝非数字的回退个数
- 向导菜单能进能出、帮助可读

## 5. 开发过程中被测试挡住的真实缺陷（保留记录）

1. `diff.algorithm=meyers` 是错拼，git 会 `fatal: bad config variable`，让用户**所有** git 命令失效。
   第一版护栏（`git -c k=v rev-parse --git-dir`）后来被实测证伪：好坏值输出一模一样，因为 `rev-parse`
   根本不解析配置值——**等于没护栏**。换成在临时仓库里跑 `git -c k=v diff --quiet`，并用 `_probe`
   加了两条正反断言，才真拦住。
2. `help.autocorrect=10` 实测会**自动执行猜出来的命令**（`git lgo` 跑了 `git log`）→ 钉到 `0`。
3. `po` 里 `git push` 那条提示的 `msgid` 以 `\n` 结尾，改写时漏了 → `msgfmt` 编译期致命错误 → 把换行对称规则前置成补丁期校验。
4. porcelain 输出里中文路径是八进制转义串，回传给 `git add` 必然失败 → 改用 `-z` + `core.quotepath=false`。
5. `restore <单个文件>` 完全没进危险闸（旧正则只匹配 `.` / `-f` / `--`），恰恰是最常见的丢改动动作 → 补规则。
6. stdin 没设 UTF-8，管道输入中文仓库名落成乱码目录 → 一并重配 `sys.stdin`；同时改成**只在非 tty 时**强制 UTF-8，
   因为真控制台走 Python 宽字符通道，硬设 utf-8 反而会在 GBK 码页控制台上乱码。
7. `git-cn init` 在父仓库目录里执行 `git add -A` → 改为对新仓库 `-C` 定向。
8. 重写子命令表时漏掉 `checkout/restore/reset/log`，导致它们走"未知子命令"分支 → 自检脚本当场抓到。
9. `git-cn.cmd` 里写了中文注释：cmd.exe 用 OEM 码页读批处理，UTF-8 中文变垃圾字节后**把后面几行都啃坏**
   （表现为 `'orlevel' 不是内部或外部命令`），并且 `%errorlevel%` 写在括号块里会在解析期展开、传错退出码
   → 批处理全 ASCII + `if not errorlevel 1 goto` 结构。
10. Git Bash 里 `python3` 会命中 Microsoft Store 的空壳别名，`exec` 之后**静默无输出** →
    shim 改为先 `-c 'import sys'` 试跑，能真跑起来才用它。
