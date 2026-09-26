# git-cn —— 汉化 + 少一点反人类设计的 Git

两件事分开做，因为它们解决的是两个不同的问题：

| 组成 | 是什么 | 现在能在哪用 |
|---|---|---|
| `git-source/` + `cn/` | 上游 Git 2.55.0 源码，打上中文与语义补丁后编译出的 **git 2.55.0.cn1** | Linux / WSL2（已实测） |
| `wrapper/` | `git-cn` 命令行工具箱：安全闸、报错人话翻译、中文向导菜单、一键合理默认值 | **Windows 直接用，不需要编译** |

> 先说清楚一件事：Git 是命令行工具，没有界面可"汉化"。它的"反人类"在于**命令语义和默认值**。
> 所以这个项目同时做了两层：把 Git 本体输出改成中文并改写最伤新手的提示；再在外面包一层把危险动作拦住、把报错翻译成人话。

---

## 一、早上起来就能用的部分：`git-cn` 工具箱（Windows）

```powershell
# 在 wrapper 目录里，双击或运行：
powershell -ExecutionPolicy Bypass -File .\install.ps1
# 重开终端后：
git-cn doctor      # 体检：git 版本 / 配置 / 编码 / 是否汉化版
git-cn fix         # 写入符合国内习惯的默认值（可 --revert 回退）
git-cn             # 中文向导菜单
```

`install.ps1` 只做两件事：把文件复制到 `%LOCALAPPDATA%\git-cn`，并把该目录加入**用户** PATH（改 PATH 前会把原值备份到 `PATH-backup.txt`）。不想动 PATH 也可以直接在 wrapper 目录里跑 `git-cn.cmd doctor`。

### 1. `git-cn fix`：干掉一批反人类默认值

21 项，写全局配置前先拿 `git -c key=value` 试一遍，git 不认的值直接跳过并说明——**不会把配置写坏**（这个护栏是踩过一次坑加的：`diff.algorithm` 写成错拼值会让所有 git 命令 fatal）。

最关键的几项：

- `core.quotepath=false` —— 中文文件名不再显示成 `"\346\226\207\344\273\266"`
- `push.autoSetupRemote=true` —— 新分支第一次 `git push` 不再报 `no upstream branch`
- `pull.rebase=true` + `rebase.autoStash=true` —— 历史一条直线，且不再"你有未提交改动所以不能变基"
- `core.autocrlf=input` + 模板 `.gitattributes` —— 不再被 CRLF 反复咬
- `core.longpaths=true` —— Windows 260 字符路径上限
- `rerere.enabled=true` —— 同样的冲突只解决一次
- `help.autocorrect=0` —— 拼错只提示相近命令、**不自动执行**。实测 `=10` 会在 1 秒后真的把猜出来的命令跑掉（`git lgo` 直接执行了 `git log`），和本工具"危险动作先确认"的原则冲突，所以显式钉在 0
- `i18n.*Encoding=utf-8`、`locale.default=zh_CN` —— 中文提交信息不乱码，配合汉化版直接出中文界面

### 2. 危险动作先讲后果，再问一句

`reset --hard`、`clean -fd`、`push -f`、`checkout .`、`restore <文件>`、`branch -D`、`stash drop/clear`、`filter-branch`、`gc --prune` 等命中时，会打印**会造成什么后果 / 更安全的做法**，并且对会丢代码的那几个主动提出"先帮你 `git stash push -u` 备份一份"。默认回答是"否"。可逆的 `restore --staged`（只是取消暂存）刻意不拦，避免每次都问一遍。

```
!! 危险操作确认：强制推送
  会造成什么后果：会用你本地的历史覆盖远端历史，远端上你没有的提交会立即对其他人和另一台机器消失。
  更安全的做法：先 git pull --rebase 再普通 push；只有确认远端那些提交是垃圾时才强推。
```

### 3. 报错翻译成人话（含大陆网络那几条）

`git-cn push` / `git-cn pull` / 任何 `git-cn <git子命令>` 失败时，除了原样输出，还会给"为什么失败 + 怎么办"：

- `no upstream branch` → 给 `-u` 的具体命令，并提示 `git-cn fix` 能永久解决
- `rejected / fetch first` → 说明为什么不能直接 `-f`，给 `pull --rebase` 路径
- `Would be overwritten` / `cannot rebase: unstaged changes` → 给 stash 出路
- `detached HEAD` → 说清"提交会悬空"以及怎么存下来
- `Filename too long` → 长路径开关
- `detected dubious ownership` → `safe.directory` 具体命令
- `Permission denied (publickey)` / `Failed to connect ... 443` / `RPC failed; early EOF` → 按大陆网络情况给 SSH-over-443、`http.version=HTTP/1.1`、`git-cn net` 等方案
- 命令拼错 → `did you mean` 之外还给相近命令列表

`git-cn net` 专门测 GitHub 连通性并可 `--apply` 常用修复项。

### 4. 语义重排（包装层）

- `git-cn checkout <东西>` —— 判断你是要**还原文件**还是**切分支**，分别走 `git restore` / `git switch`；两者同名时问你
- `git-cn reset` —— 先用一张表讲清 soft/mixed/hard 各自动了提交记录/暂存区/工作区的哪一层，再执行
- `git-cn undo` —— 撤销上次提交，四种方式讲清区别，并打印"万一改坏了怎么回去"的哈希
- `git-cn conflict` —— 逐个冲突文件引导，并明确提醒 **变基时 `--ours`/`--theirs` 的含义和合并时正好相反**（Git 最坑的点之一）

---

## 二、编译出来的汉化版 Git（`git-source/` + `cn/`）

### 改了什么

**中文界面**：上游 `po/zh_CN.po` 本来就存在且是活的。这里没有从零翻译，而是**在官方译文上改写 16 条最伤新手的措辞**，加上 2 条新增提示。改写全部在 `msgstr` 层，不动 C 字符串，所以不会造成消息 ID 漂移。

典型对比（左为官方直译，右为本 fork）：

```
Changes not staged for commit:
  尚未暂存以备提交的变更：            →  已修改但没 git add：这些内容不会进入下一次提交
  (use "git restore ..." to discard) →  （想丢掉这些未提交改动用 git restore <文件>...，丢出去找不回来）
You are in 'detached HEAD' state...  →  你现在处于"游离 HEAD"：不在任何分支上。此时的提交不属于任何分支，
                                         切走之后只能靠 reflog 找回，看起来就像代码丢了。
```

**C 层 4 处语义补丁**（尽量小，可单独回退）：

| 文件 | 改动 |
|---|---|
| `refs.c` | 未配置 `init.defaultBranch` 时默认建 `main`，并去掉 `master→main` 迁移提示 |
| `builtin/reset.c` | `git reset` 不带模式时，打印它到底动了哪三层 |
| `builtin/checkout.c` | `git checkout .` 明确告知"这是丢弃未提交改动，不是切分支"，并指向 `git restore` |
| `GIT-VERSION-GEN` | 版本号 `2.55.0.cn1`，便于 `git --version` 和体检脚本识别 |

另有 2 处**上游断言**被同步改掉（`t/t0001-init.sh`），因为本 fork 有意偏离：默认分支已是 `main`、迁移提示已去掉。这是有意的分叉，不是掩盖失败。

全部改动都能用 `cn/git-cn-src.patch` 一个文件看完，也能用 `cn/cn_patch.py` 对任意干净源码重放（它会校验占位符数量、换行结尾一致性、以及"C 里的字符串确实等于 po 的 msgid"，不匹配就拒绝写）。

### 怎么编译（WSL2 / Linux）

```bash
# 依赖（Debian/Ubuntu）
sudo apt-get install -y build-essential gettext libcurl4-openssl-dev \
    zlib1g-dev libexpat1-dev libssl-dev locales
sudo locale-gen zh_CN.UTF-8

# 取上游源码（国内网络建议在 Windows 侧下好再放进 WSL）
curl -L -o /tmp/git.tar.gz https://codeload.github.com/git/git/tar.gz/refs/tags/v2.55.0

cn/build_git.sh /tmp/git.tar.gz "$HOME/gitcn/out"   # 解压→打补丁→msgfmt 校验→编译→安装
cn/verify.sh "$HOME/gitcn/out/bin/git"              # 行为验收
cn/run_tests.sh "$HOME/gitcn-build/git-2.55.0"      # 上游回归测试
```

装完后设 `locale.default=zh_CN`（`git-cn fix` 会自动写）或 `LC_ALL=zh_CN.UTF-8`，Git 输出即为中文。

---

## 三、验证到什么程度（不吹）

- `cn/verify.sh` 对**编译产物**做 15 项行为断言：版本号、默认分支 `main`、三类 status 表头改口、中文文件名不被转义、`reset` 三层解释、`checkout .` 提示、游离 HEAD 中文说明、无上游提示、推送成功、`file://` 克隆回来中文文件名完好、https 传输层已链接 —— **15/15 通过**。
- `cn/run_tests.sh` 跑上游套件里 init/branch/reset/checkout/switch/restore/rebase/status/advice/remote/commit/merge 相关用例，结果见 `docs/`（同目录日志）。
- 包装器在真实仓库上跑过：`doctor`、`fix`/`fix --revert`、`status`、危险闸取消路径、`checkout` 路由、菜单进出、拼错命令翻译。

**没做到 / 已知边界**：

1. **没有 Windows 原生 git.exe**。编译是在 WSL2 里做的，产物是 Linux 版。要 Windows 版得先装 MSYS2 整套工具链（本仓库没做）。`git-source/` 里的补丁理论上也能在 MSYS2 下重放。
2. WSL 里访问 GitHub 失败：这台机器的 hosts 把 `github.com` 指到 `127.0.0.1`（Windows 侧有个本地加速程序在接管），WSL 的 `127.0.0.1` 是虚拟机自己，所以连不通。这是环境问题，`verify.sh` 因此把 https 检查降级为"传输层已链接"，并补了 `file://` 端到端克隆测试来覆盖真实读写路径。
3. `checkout` 的语义重排只是**加提示**，没有禁用旧用法——那会破坏上游行为和大量脚本。真正的路由在包装层。
4. 上游 `zh_CN.po` 条目很多带 `fuzzy` 标记，msgfmt 会跳过它们，所以**少数句子仍可能是英文**。这是上游翻译进度，不是本 fork 的取舍。
5. 中文改写只覆盖高频界面语句，不是全量重译。

## 许可与出处

- 上游：[git/git](https://github.com/git/git) **v2.55.0**，GPL-2.0-only。本仓库 `git-source/` 是其打过补丁的完整源码快照，许可与版权说明随源码保留（`git-source/COPYING`）。
- 中文翻译基线：[git-l10n/git-po](https://github.com/git-l10n/git-po) 的 `po/zh_CN.po`，译文版权归各自作者。
- `wrapper/`、`cn/` 里的原创脚本同样按 GPL-2.0 提供。
