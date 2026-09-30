git-cn 把 Git 的中文与"少一点反人类"做到两层：**改过的 Git 源码**，和**开箱即用的中文客户端**。

## 你现在就能用的（不需要编译、不需要装 Python/.NET）

| 文件 | 是什么 | 平台 |
|---|---|---|
| `git-cn-gui-win-x64.zip` | 图形界面 `git-cn-gui.exe`：中文状态分组、一键提交/推送、危险操作确认对话框、体检与默认值 | Windows x64 |
| `git-cli-win-x64.zip` | 命令行 `git-cn.exe` | Windows x64 |
| `git-cli-linux-x64.zip` | 命令行 `git-cn` | Linux x64 |
| `git-cli-mac-arm64.zip` | 命令行 `git-cn` | Apple Silicon |
| `git-cli-mac-x64.zip` | 命令行 `git-cn` | Intel Mac |

解压后直接运行。首次建议顺序：

```
git-cn doctor      # 体检
git-cn fix         # 写入 21 项符合国内习惯的默认值（可 git-cn fix --revert 回退）
git-cn             # 中文向导菜单
```

图形版没有独立安装步骤，也不改你的 PATH。

## 想要 git 本体输出中文

`git-cn-linux-wsl.tar.gz` —— 在 WSL2/Ubuntu 26.04 上编译并验收过的 **git version 2.55.0.cn1**（解压后 `out/bin/git`）。
需要系统有 `zh_CN.UTF-8` locale（`sudo locale-gen zh_CN.UTF-8`），或 `git config --global locale.default zh_CN`。
源码与补丁在仓库里：`git-source/` + `cn/`，全部改动一个 diff 看完（279 行 / 6 文件）。

改动要点：
- `po/zh_CN.po`：改写 16 条最伤新手的官方直译 + 新增 2 条（只动 `msgstr`，不动 C 字符串）
- `refs.c`：默认分支直接 `main`，去掉 master→main 迁移提示
- `builtin/reset.c`：不带模式的 `git reset` 说清它动了提交/暂存/工作区哪三层
- `builtin/checkout.c`：`git checkout .` 说明这是丢改动而不是切分支
- `t/t0001-init.sh`：两处编码旧行为的上游断言同步改掉（有意分叉，不是掩盖失败）
- 版本号标记 `cn1`

## 验证过的程度

- C# 逻辑单元测试：**26 通过 / 0 失败**
- 汉化版 git 行为验收（对象是编译产物）：**15 / 15**
- 上游回归测试：**263 个测试文件，0 失败**
- Python 包装器端到端自检：**22 / 22**
- 图形界面：用**发布包里的单文件 EXE**真实点击走完全流程，每步以磁盘状态复核——写入并回退 21 项配置、提交、推送被拒时弹出翻译卡片、拉取撞出真冲突、解决并继续变基（历史线性、保留我方内容）、再推送至"已同步"、撤销提交后改动留在暂存区
- 独立 EXE：发布包里的 `git-cn.exe` 与 `git-cn-gui.exe` 都实测跑起来

## 已知边界（不藏）

- 图形界面是 WPF，**只有 Windows**；Linux/macOS 用命令行版（同一套逻辑）。
- **没有 Windows 原生的汉化 git.exe**（需要 MSYS2 工具链，本次未搭）。
- 汉化版 git 对 GitHub 的 https 访问**没在能出网的机器上验证过**（开发机把 `github.com` 指向本地加速程序）；传输层已确认编译进去，并用 `file://` 端到端克隆补齐真实读写验证。
- 上游 `zh_CN.po` 部分条目带 `fuzzy`，msgfmt 会跳过，所以少数句子仍可能是英文。

上游 [git/git](https://github.com/git/git) v2.55.0，GPL-2.0-only；中文翻译基线来自 [git-l10n/git-po](https://github.com/git-l10n/git-po)。
