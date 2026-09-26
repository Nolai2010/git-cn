#!/usr/bin/env python3
"""git-cn: 中文优先的 Git 前端。安全闸 + 人话报错 + 向导菜单 + 合理默认值。"""
import difflib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

GIT_CMDS = set("""
init clone add am apply blame branch bisect cat-file cherry-pick checkout clean cli
commit describe diff difftool fetch fsck gc gitk grep hash-object help index-pack
instaweb ls-files ls-remote maintenance merge mergetool mv notes prune prune-rebase
pull push range-diff rebase reflog reset restore revert rm shortlog show show-ref
stash status submodule switch tag unpack-file verify-pack worktree whatchanged
for-each-ref update-index update-ref verify-commit verify-tag rev-list rev-parse
send-email request-pull interpret-trailers filter-branch filter-repo config remote
credential gui citool difftool
""".split())

BOLD, DIM, RED, GREEN, YELLOW, CYAN, RESET = (
    "\033[1m", "\033[2m", "\033[31m", "\033[32m", "\033[33m", "\033[36m", "\033[0m"
)


def init_console():
    if os.name == "nt":
        try:
            import ctypes
            k32 = ctypes.windll.kernel32
            for std in (-11, -12):  # stdout / stderr
                h = k32.GetStdHandle(std)
                mode = ctypes.c_uint32()
                if k32.GetConsoleMode(h, ctypes.byref(mode)):
                    k32.SetConsoleMode(h, mode.value | 0x0004)  # ENABLE_VIRTUAL_TERMINAL_PROCESSING
        except Exception:
            pass
    for stream in (sys.stdout, sys.stderr, sys.stdin):
        try:
            # 真控制台走 Python 的宽字符通道（GBK 码页也能正确显示中文），
            # 只有管道/重定向时才强制按 UTF-8 读写字节。
            if not stream.isatty():
                stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


def say(msg=""):
    print(msg, flush=True)


def head(msg):
    say(f"\n{BOLD}{CYAN}== {msg} =={RESET}")


def ok(msg):
    say(f"{GREEN}[完成]{RESET} {msg}")


def warn(msg):
    say(f"{YELLOW}[注意]{RESET} {msg}")


def bad(msg):
    say(f"{RED}[出错]{RESET} {msg}")


def ask(prompt, default=""):
    try:
        raw = input(f"{prompt}" + (f" [{default}]" if default else "") + ": ")
    except (EOFError, KeyboardInterrupt):
        say("\n(已取消：输入流已结束，无法继续交互)")
        raise SystemExit(130)
    raw = raw.strip()
    return raw or default


def confirm(prompt, default_no=True):
    hint = "yes/否" if default_no else "Y/否"
    ans = ask(f"{prompt} ({hint})").lower()
    if not ans:
        return not default_no
    return ans in ("y", "yes", "是", "1")


# ---------------------------------------------------------------- git 调用
def git(*args, cwd=None, capture=True, check=False):
    cmd = ["git", *args]
    if capture:
        p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        return p.returncode, (p.stdout or ""), (p.stderr or "")
    p = subprocess.run(cmd, cwd=cwd)
    return p.returncode, "", ""


def git_out(*args, cwd=None):
    rc, out, _ = git(*args, cwd=cwd)
    return out.strip() if rc == 0 else ""


def have_git():
    try:
        subprocess.run(["git", "--version"], capture_output=True, check=False)
        return True
    except FileNotFoundError:
        return False


def repo_root():
    return git_out("rev-parse", "--show-toplevel") or ""


def in_repo_or_exit():
    root = repo_root()
    if not root:
        bad("当前目录不在 Git 仓库里。")
        say("  要在当前位置新建仓库：git-cn init")
        say("  已有仓库：先 cd 进项目根目录再运行")
        raise SystemExit(128)
    return root


def current_branch():
    return git_out("rev-parse", "--abbrev-ref", "HEAD") or "(未知)"


def upstream_of(branch):
    return git_out("rev-parse", "--abbrev-ref", f"{branch}@{{upstream}}")


# ---------------------------------------------------------------- 报错翻译
ERROR_RULES = [
    (r"not a git repository",
     "这里没有 Git 仓库：当前目录和它的父目录里都没有 .git 文件夹。",
     ["在项目根目录执行 git-cn init 新建仓库",
      "或先 cd 到真正的仓库目录再运行 git 命令"]),
    (r"has no upstream branch|no upstream branch|has no upstream configured",
     "这个分支是第一次推送，Git 不知道要推到远端的哪个分支。",
     ["git push -u origin <分支名>    # -u 记住对应关系，以后直接 git push",
      "git-cn fix    # 长期解决：打开 push.autoSetupRemote，以后所有新分支不用再 -u"]),
    (r"(failed to push some refs|non-fast-forward|Updates were rejected)"
     r"|(fetch first|tip of your current branch is behind)",
     "远端分支上有你本地没有的提交，直接推会覆盖别人（或你在另一台机器上）的工作，所以被拒绝。",
     ["git pull --rebase    # 把你的提交挪到远端最新提交之后，保持一条直线",
      "git push             # 再推",
      "不要用 git push -f 硬推，那会真的删掉远端的提交"]),
    (r"Your local changes to the following files would be overwritten"
     r"|Please commit your changes or stash them|would be overwritten by (merge|checkout)",
     "你有没提交的改动会被这次操作覆盖，Git 选择停下来保护你。",
     ["git-cn stash    # 先临时收起来（含未跟踪文件）",
      "然后重跑刚才的命令，最后 git stash pop 取回改动"]),
    (r"untracked working tree files would be (overwritten|removed)",
     "本地有未跟踪文件挡路了，继续会直接删掉它们。",
     ["把提示里列出的文件改名或移走，再重跑命令",
      "确认不需要这些文件了：git clean -nd 先看清单，再用 git-cn clean 处理"]),
    (r"cannot rebase: (You have unstaged changes|your local working directory|with a dirty)",
     "工作区有未保存的改动时禁止 rebase，避免 rebase 中途冲突时你的改动无处可回。",
     ["git stash push -u && git rebase origin/main && git stash pop",
      "git-cn fix    # 打开 rebase.autoStash，以后自动帮你收/放"]),
    (r"divergent branches|Need to specify how to reconcile",
     "本地和远端各有对方没有的提交。Git 2.27 起强制你选 merge 还是 rebase，所以报错。",
     ["git pull --rebase    # 推荐：历史线性、好读",
      "git-cn fix    # 打开 pull.rebase，以后不用每次带参数"]),
    (r"HEAD detached",
     "你现在处于“游离 HEAD”：不在任何分支上，此时的提交不属于任何分支，切走就可能找不到。",
     ["git switch -c save-my-work    # 把当前状态存成新分支（最稳）",
      "git switch <分支名>           # 或者放弃回到正轨"]),
    (r"did not match any file\(s\)|pathspec '\S+' did not match",
     "这个路径 Git 不认识：可能是拼错、不在仓库根目录、或者该文件从来没被 git add 过。",
     ["git status --short    # 用真实路径",
      "git-cn fix    # 打开 core.quotepath，中文文件名不再显示成 \\346\\226\\207 乱码，便于核对"]),
    (r"Author identity unknown|Please tell me who you are|unable to auto-detect",
     "Git 还不知道你是谁（缺 user.name / user.email），所以拒绝写提交。",
     ["git config --global user.name  \"你的名字\"",
      "git config --global user.email \"你的邮箱@example.com\""]),
    (r"Filename too long|cannot create directory.*No such file|Invalid path|too long",
     "Windows 默认路径长度上限 260 字符，深层目录会被砍断。",
     ["git-cn fix    # 打开 core.longpaths，Windows 下几乎必开"]),
    (r"detected dubious ownership",
     "Git 认为仓库目录的所有者不是当前用户（Windows 上管理员/普通权限混用很常见），拒绝操作。",
     ["git config --global --add safe.directory \"<仓库绝对路径>\""]),
    (r"is not a git command",
     "这个命令不存在，八成是拼错了。",
     []),
    (r"Permission denied \(publickey\)|SSH authentication|kex_exchange_identification"
     r"|Connection closed by .*port 22",
     "SSH 连不上 GitHub。大陆网络干扰 22 端口是常见原因，不一定是你没配 key。",
     ["ssh -T git@ssh.github.com -p 443    # 先测 443 端口的 SSH 是否通",
      "把 remote 换成 https 也能绕过：git remote set-url origin https://github.com/用户/仓库.git",
      "git-cn net    # 一键体检并套用 GitHub 常用网络修复"]),
    (r"Could not resolve host|Failed to connect to|Connection reset by peer"
     r"|Empty reply from server|curl \d+|RPC failed|early EOF|TLS connect error"
     r"|gnutls|The requested URL returned error: (408|502|503|504)|port 443",
     "网络没走到 GitHub：DNS、连接被重置或超时。这类失败通常是瞬时的，重试往往就成功。",
     ["先重跑一次同样的命令（Git 支持断点续传，clone 失败可 rm -rf 后重试）",
      "git-cn net    # 自动切 HTTP/1.1、加大 postBuffer，并测 GitHub 连通",
      "git-cn fix    # 需要时走镜像或 SSH over 443，这里给现成配置"]),
    (r"Authentication failed|Invalid username or password|returned error: 40[13]"
     r"|could not read Username",
     "身份验证没过。GitHub 从 2021 年起不接受账号密码，只接受 token 或 SSH key 或凭据管理器。",
     ["用 gh 登录最省事：gh auth login",
      "Windows 凭据残留时清理：control keymgr.dll 删掉 github.com 条目后重试"]),
    (r"index\.lock: File exists|cannot lock ref|Unable to create .*\.lock",
     "有 .lock 文件残留，说明上次 git 进程被强杀了。Git 不敢并发写，所以停住。",
     ["确认没有 git 命令在跑，然后删除 .git/index.lock",
      "rm -f .git/index.lock    # 只删提示里那个 lock 文件，不要删 .git 下别的"]),
    (r"nothing added to commit|no changes added to commit",
     "改动没进暂存区，所以这次 commit 是空的。这是新手最常踩的一步：改了文件 ≠ 提交了文件。",
     ["git add -A    # 全部加入暂存区",
      "git-cn commit    # 或者直接走向导，自动列文件让你挑"]),
    (r"nothing to commit, working tree clean",
     "没有需要提交的改动。可能是改动已经提交过了，也可能你在错的目录。",
     ["git-cn status    # 中文解读当前状态",
      "git log --oneline -5    # 看最近提交，确认改动是不是已经在里面"]),
    (r"CONFLICT \(content\)|Merge conflict in |both modified:",
     "出现了合并冲突，同一个文件里有两方改动的版本，Git 不替你决定。",
     ["git-cn conflict    # 中文逐个文件引导处理",
      "记住坑点：rebase 过程中 --ours 是“远端/目标分支”，--theirs 才是“你自己的提交”，和 merge 时相反"]),
    (r"Your branch is ahead of|have diverged",
     "本地有远端还没有的提交（或两边分叉了），需要 push 或先 pull --rebase。",
     ["git push",
      "git pull --rebase && git push"]),
    (r"gpg failed to sign|secret key not available|error writing the gpg file",
     "提交签名失败：GPG 没配好，但仓库却开着自动签名。",
     ["git-cn fix    # 关掉 commit.gpgsign，先把提交跑通"]),
    (r"problem with the editor|Waiting for your editor to close|error: There was a problem with",
     "Git 调不出编辑器（Windows 上常见于默认 vim 不存在，或编辑器不等待就返回）。",
     ["git config --global core.editor \"notepad -w\"    # -w 让记事本等关闭后再继续",
      "装了 VSCode 可以用：git config --global core.editor \"code --wait\""]),
    (r"bad config file line",
     "Git 配置文件语法坏了，所有 git 命令都会失败。",
     ["按提示的行号修 ~/.gitconfig（Windows: C:\\Users\\你\\.gitconfig）",
      "git-cn fix    # 也可以直接重置常用项"]),
    (r"remote origin already exists",
     "远端 origin 已经存在，不能再 add，但可以直接改地址。",
     ["git remote set-url origin <新地址>"]),
    (r"Repository is not large enough|pack is corrupt|unable to read sha1 file|invalid index file",
     "仓库对象或索引疑似损坏，本地继续写可能有风险。",
     ["最稳：另存一份工作副本后重新 clone",
      "只坏索引时：rm -f .git/index && git reset  # 重新生成索引，不动工作区"]),
    (r"could not read Repository format|does not appear to be a git repository",
     "地址不对，或者这个仓库在你没有权限的账号下（私有仓库匿名访问就是这个报错）。",
     ["核对地址：git remote -v",
      "私有仓库用 gh 登录后再试：gh auth status"]),
]


def translate_error(text, argv=None):
    hits = []
    low = text
    for pat, why, fixes in ERROR_RULES:
        if re.search(pat, low, re.I):
            hits.append((why, fixes))
    m = re.search(r"git: '([^']+)' is not a git command", low)
    if m:
        name = m.group(1)
        near = difflib.get_close_matches(name, sorted(GIT_CMDS), n=3, cutoff=0.5)
        hits = [(w, f) for w, f in hits if "不存在" not in w]
        hits.insert(0, (f"Git 里没有 “{name}” 这个命令。",
                        [f"你是不是想用：{' / '.join(near)}" if near else "git help -a  # 查看全部命令"]))
    if not hits:
        return False
    say(f"\n{BOLD}{RED}--- git-cn 帮你翻译这份报错 ---{RESET}")
    for why, fixes in hits:
        say(f"  {BOLD}为什么失败：{RESET}{why}")
        if fixes:
            say(f"  {BOLD}怎么办：{RESET}")
            for f in fixes:
                say(f"    {GREEN}#{RESET} {f}")
        say("")
    say(f"{DIM}（原始输出在上面，未做任何删改）{RESET}")
    return True


# ---------------------------------------------------------------- 危险操作闸
DANGER = [
    (r"^push\b.*(\s--force(-with-lease)?|\s-f)(\s|$)", "强制推送",
     "会用你本地的历史覆盖远端历史，远端上你没有的提交会立即对其他人和另一台机器消失。",
     "先 git pull --rebase 再普通 push；只有确认远端那些提交是垃圾时才强推。"),
    (r"^reset\b.*--(hard|keep|merge)\b", "硬重置",
     "工作区里所有未提交的改动会被直接抹掉，git 不保留副本，reflog 也救不回未提交内容。",
     "先 git stash push -u 备份，再 reset。git-cn 可以在确认时自动帮你备份。"),
    (r"^clean\b.*-[a-z]*f", "清理未跟踪文件",
     "未跟踪文件不在版本控制里，删了就永久没了——包括你没提交的新建文件、.env、本地笔记。",
     "先 git clean -nd 看清单；有 .gitignore 里的构建产物时不要加 -x。"),
    (r"^restore\b(?!.*--staged)", "还原文件内容",
     "restore 只作用于文件：这些文件未提交的修改会被丢掉，Git 里没有它们的另一份副本。",
     "先保住改动：git stash push -u，或 git diff > 我的改动.patch；确认可丢再执行。"),
    (r"^(checkout|restore|switch)\b.*(\s--|\s-f\b|\s\.\s*$|\s\.$|\s\*\s*$)", "丢弃本地修改",
     "会把文件内容回退到上一次提交的状态，未提交的修改不可恢复。",
     "先 git stash push -u，或者只回退指定文件而不是整个目录。"),
    (r"^branch\b.*\s-D\b", "删除未合并分支",
     "-D 会连未合并进主干的提交一起删掉，那些提交只能靠 reflog 短期找回。",
     "先确认分支内容已合并：git log main..<分支> --oneline"),
    (r"^stash\b.*\s(drop|clear)\b", "丢弃 stash",
     "stash 是 Git 里唯一的“临时抽屉”，drop/clear 后里面内容只能靠 git fsck --unreachable 捞。",
     "改用 git stash list + git stash apply <编号>，保留抽屉。"),
    (r"^push\b.*(--delete|\s:\S)", "删除远端分支",
     "远端分支和它独有的提交对所有人消失。",
     "确认合并请求已合并，再删。"),
    (r"^(filter-branch|filter-repo)", "重写全部历史",
     "所有提交的哈希会变，别人和远端会彻底分叉，且不可逆。",
     "先在临时克隆里试，并且必须通知所有协作者。"),
    (r"^gc\b.*--prune", "立即清除悬空对象",
     "被 reflog 之外的“悬空”提交会被物理删除，未合并的救命提交可能就此消失。",
     "一般不用手动 gc，交给 git 自动维护。"),
    (r"^tag\b.*\s-d\b|^push\b.*--delete.*tags", "删除标签",
     "发布版本标签删除后，别人按标签拉的构建会失败。",
     "确认这是你自己打错的标签。"),
]

AUTO_BACKUP = [r"^reset\b.*--(hard|keep)", r"^clean\b.*-[a-z]*f", r"^restore\b(?!.*--staged)",
               r"^(checkout|restore|switch)\b.*(\s\.|\s--\s)"]


def danger_check(argv, assume_yes):
    joined = " ".join(argv)
    if re.match(r"^(checkout|restore)\b.*--staged\b", joined):
        return True  # 取消暂存是可逆操作，不动文件内容
    matched = None
    for pat, label, harm, safer in DANGER:
        if re.search(pat, joined, re.I):
            matched = (label, harm, safer)
            auto_pat = next((p for p in AUTO_BACKUP if re.match(p, joined, re.I)), None)
            break
    if not matched:
        return True
    label, harm, safer = matched
    say(f"\n{BOLD}{YELLOW}!! 危险操作确认：{label}{RESET}")
    say(f"  {BOLD}会造成什么后果：{RESET}{harm}")
    say(f"  {BOLD}更安全的做法：{RESET}{safer}")
    if assume_yes:
        warn("你传了 --yes，跳过确认继续执行。")
        return True
    if confirm("  仍然要执行吗？", default_no=True):
        if auto_pat and repo_root() and confirm("  要不要我先把未提交改动 stash 备份一份？（推荐）"):
            rc, out, err = git("stash", "push", "-u", "-m", f"git-cn 自动备份 在执行「{label}」之前")
            if rc == 0 and "No local changes" not in out + err:
                ok("已备份。恢复：git stash pop")
            else:
                warn("没有可备份的改动，继续。")
        return True
    say("  已取消，未执行任何操作。")
    return False


# ---------------------------------------------------------------- 子命令
TTY_COMMANDS = {
    "commit", "am", "tag", "bisect", "merge", "rebase", "cherry-pick", "revert",
    "gui", "citool", "mergetool", "difftool", "instaweb", "request-pull",
    "help", "log", "show", "diff", "blame", "annotate", "grep", "interactive",
}


def needs_terminal(argv):
    if not argv:
        return False
    if argv[0] in TTY_COMMANDS:
        return True
    return any(a in ("-p", "-i", "--patch", "--interactive") for a in argv)


def run_git_tee(argv):
    """边转发边留一份输出副本，这样失败时还能翻译。"""
    proc = subprocess.Popen(["git", *argv], stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True,
                            encoding="utf-8", errors="replace", bufsize=1)
    buf = []
    assert proc.stdout is not None
    for line in proc.stdout:
        buf.append(line)
        sys.stdout.write(line)
    proc.stdout.close()
    rc = proc.wait()
    return rc, "".join(buf)


def cmd_run(argv, assume_yes):
    if not argv:
        return cmd_menu()
    sub = argv[0]
    if sub in GIT_CMDS or sub == "git":
        argv = argv[1:] if sub == "git" else argv
        if not danger_check(argv, assume_yes):
            return 130
        if needs_terminal(argv):
            rc = subprocess.run(["git", *argv]).returncode
            if rc != 0:
                say(f"{DIM}（这条命令走交互/分页通道，未做报错翻译）{RESET}")
            return rc
        rc, out = run_git_tee(argv)
        if rc != 0:
            if not translate_error(out or "", argv):
                bad(f"git {' '.join(argv)} 失败（退出码 {rc}）。")
        elif re.search(r"nothing to commit|no changes added", out or "", re.I):
            translate_error(out, argv)
        return rc
    say(f"{DIM}未知子命令 {sub}，按原始 git 命令透传。{RESET}")
    return cmd_run(["git", *argv], assume_yes)


_PROBE_DIR = None


def probe_repo():
    """一次性建个空仓库当试值台：diff 系列配置只有在工作区里才会被真正解析。"""
    global _PROBE_DIR
    if _PROBE_DIR:
        return _PROBE_DIR
    import tempfile
    d = tempfile.mkdtemp(prefix="git-cn-probe-")
    rc, _, _ = git("init", "-q", "--", d)
    if rc != 0:
        return None
    git("-c", "user.email=probe@local", "-c", "user.name=probe",
        "commit", "-q", "--allow-empty", "-m", "probe", "-C", d)
    _PROBE_DIR = d
    return d


def value_is_acceptable(key, value):
    d = probe_repo()
    if not d:
        return True
    _, out, err = git("-C", d, "-c", f"{key}={value}", "diff", "--quiet")
    t = out + err
    return not re.search(r"unknown value for config|bad config|invalid.*config", t, re.I)


def cmd_probe_value(argv):
    bad_any = False
    for item in argv:
        if "=" not in item:
            say(f"用法：git-cn _probe key=value ...")
            return 2
        key, val = item.split("=", 1)
        good = value_is_acceptable(key, val)
        bad_any = bad_any or not good
        say(f"{key}={val}  ->  {'可接受' if good else '被 git 拒绝'}")
    return 1 if bad_any else 0


def cmd_fix(dry=False, revert=False):
    in_repo = bool(repo_root())
    want = [
        ("core.quotepath", "false", "中文文件名不再显示成 \\346\\226\\207 八进制乱码"),
        ("i18n.commitEncoding", "utf-8", "中文提交信息按 UTF-8 存，别人不会看到乱码"),
        ("i18n.logOutputEncoding", "utf-8", "读日志也按 UTF-8 输出"),
        ("locale.default", "zh_CN", "启用 git-cn 汉化版的中文界面（上游 git 会忽略此项）"),
        ("init.defaultBranch", "main", "新仓库默认分支叫 main，不用 master"),
        ("pull.rebase", "true", "pull 用变基：历史一条直线，没有 “Merge branch 'main'” 噪音"),
        ("rebase.autoStash", "true", "变基前自动收起未提交改动，结束后自动放回"),
        ("push.autoSetupRemote", "true", "新分支第一次 git push 不再报 no upstream branch"),
        ("push.default", "current", "只推当前分支，不会误推别的分支"),
        ("push.followTags", "true", "推送时自动带上对应标签，发版少一步"),
        ("help.autocorrect", "0", "拼错命令时只提示相近命令，绝不自动执行（实测 10 会真的把猜出来的命令跑掉）"),
        ("rerere.enabled", "true", "同样的冲突解决一次就记住，rebase 重来时不用重做"),
        ("merge.conflictstyle", "zdiff3", "冲突块多显示基准内容，看得懂谁改了什么"),
        ("diff.algorithm", "histogram", "diff 用 histogram 算法，改动块显示更接近人眼看到的最小集合"),
        ("diff.indentHeuristic", "true", "缩进变化不被误判成代码改动"),
        ("color.ui", "auto", "彩色输出（重定向时自动关）"),
        ("status.relativePaths", "true", "status 显示相对路径，短而好读"),
        ("core.autocrlf", "input", "提交时统一存 LF，检出时不改成 CRLF，跨平台不打架"),
        ("core.longpaths", "true", "Windows 突破 260 字符路径限制"),
        ("commit.gpgsign", "false", "先关掉提交签名，避免 GPG 未配置导致提交不了"),
    ]
    if os.name == "nt":
        want.append(("core.editor", "notepad -w", "调编辑器时用记事本，-w 保证等你保存关闭"))
    bk = Path.home() / ".git-cn-backup.json"
    if revert:
        if not bk.exists():
            warn("没有备份文件，无法回退。")
            return 1
        saved = json.loads(bk.read_text(encoding="utf-8"))
        head("按备份恢复原配置")
        for k, v in saved.items():
            if v is None:
                git("config", "--global", "--unset", k)
                say(f"  移除 {DIM}{k}{RESET}")
            else:
                git("config", "--global", k, v)
                say(f"  恢复 {k} = {v}")
        ok("已恢复。")
        return 0
    head("写入符合国内使用习惯的默认值（全局）")
    changes, backup = [], {}
    for key, val, why in want:
        rc, cur, _ = git("config", "--global", "--get", key)
        cur = cur.strip()
        if cur == val:
            say(f"  {DIM}已是{RESET} {key} = {val}")
            continue
        changes.append((key, val, why, cur))
    if not changes:
        ok("全部配置已到位，无需改动。")
        return 0
    for key, val, why, cur in changes:
        tag = f"{key} = {val}"
        note = f"{BOLD}{tag}{RESET}" if not cur else f"{BOLD}{tag}{RESET} {DIM}(原: {cur}){RESET}"
        if not value_is_acceptable(key, val):
            say(f"  {RED}[跳过]{RESET} {note}")
            say(f"          {DIM}git 不接受这个值，写进去会让所有 git 命令报错{RESET}")
            continue
        say(("  [试算] " if dry else "  [写入] ") + note)
        say(f"          {DIM}{why}{RESET}")
        if not dry:
            backup[key] = cur or None
            git("config", "--global", key, val)
    if not dry and changes:
        old = {}
        if bk.exists():
            old = json.loads(bk.read_text(encoding="utf-8"))
        old.update(backup)
        bk.write_text(json.dumps(old, ensure_ascii=False, indent=2), encoding="utf-8")
        ok(f"{len(changes)} 项已写入。回退：git-cn fix --revert")
    elif dry:
        say(f"\n{DIM}试算模式，未做任何修改。去掉 --dry 即可生效。{RESET}")
    if not in_repo:
        say(f"\n{DIM}提示：你现在不在仓库里，以上都是全局配置，对以后所有仓库生效。{RESET}")
    return 0


def cmd_doctor():
    head("体检")
    if not have_git():
        bad("没找到 git 命令。")
        say("  安装：winget install Git.Git   然后重开终端")
        return 1
    v = git_out("--version")
    say(f"  git 版本         {v}")
    fork = "cn" in v or "git-cn" in v.lower()
    say(f"  汉化版 git-cn    {'是（中文提示可用）' if fork else '否（用的是上游 git，中文提示由 git-cn 包装层提供）'}")
    rc, _, err = git("--exec-path")
    checks = [
        ("core.quotepath", "false", "中文文件名显示"),
        ("i18n.commitEncoding", "utf-8", "中文提交信息"),
        ("pull.rebase", "true", "pull 线性历史"),
        ("push.autoSetupRemote", "true", "新分支直接 push"),
        ("rebase.autoStash", "true", "变基自动收改动"),
        ("help.autocorrect", "0", "拼错只提示不自动执行"),
        ("rerere.enabled", "true", "冲突记忆"),
        ("core.autocrlf", "input", "换行符策略"),
        ("core.longpaths", "true", "Windows 长路径"),
        ("init.defaultBranch", "main", "默认分支名"),
    ]
    miss = []
    for key, want, why in checks:
        cur = git_out("config", "--global", "--get", key)
        flag = cur == want
        say(f"  {'OK  ' if flag else '缺失'}  {key:<24} {DIM}{why}{RESET}"
            + ("" if flag or not cur else f"  (当前: {cur})"))
        if not flag:
            miss.append(key)
    head("环境")
    enc = sys.stdout.encoding
    say(f"  {'OK  ' if 'utf' in (enc or '').lower() else '缺失'}  终端编码 UTF-8        (当前 {enc})")
    code = os.environ.get("LC_ALL") or os.environ.get("LANG") or ""
    say(f"  {'OK  ' if 'utf' in code.lower() else '缺失'}  locale 指定 UTF-8      (当前 {code or '未设置'})")
    root = repo_root()
    say(f"  {'OK  ' if root else '--  '} 当前在 Git 仓库内      ({root or '不在仓库'})")
    if root:
        say(f"  {'OK  ' if current_branch() != 'HEAD' else '注意'} 分支: {current_branch()}")
    if miss:
        say(f"\n{YELLOW}有 {len(miss)} 项建议修复。{RESET} 执行：git-cn fix")
    else:
        ok("配置全部到位。")
    return 0


def cmd_net(apply=False):
    head("GitHub 连通性体检")
    rc, out, err = git("config", "--global", "--get", "http.version")
    say(f"  http.version      {out.strip() or '(默认 HTTP/2)'}")
    for url in ("https://github.com", "https://codeload.github.com"):
        p = subprocess.run(["git", "ls-remote", url, "HEAD"], capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=25)
        say(f"  {url:<32} {'通' if p.returncode == 0 else '不通'}")
    say(f"  SSH 22 端口常被干扰；443 端口 SSH 更稳：git remote set-url origin git@ssh.github.com:用户/仓库.git")
    if apply:
        for k, v, why in [("http.version", "HTTP/1.1", "HTTP/2 在部分网络下反复断流"),
                          ("http.postBuffer", "524288000", "大包一次发完，减少 RPC failed / early EOF"),
                          ("core.compression", "4", "低带宽下压缩换速度"),
                          ("fetch.fsckObjects", "false", "关掉对象校验，提速明显")]:
            git("config", "--global", k, v)
            say(f"  [写入] {k} = {v}  {DIM}{why}{RESET}")
        ok("网络参数已套用，重试刚才失败的命令。")
    else:
        say(f"\n{DIM}加 --apply 可写入上面这几项修复。{RESET}")
    return 0


def parse_status():
    rc, out, _ = git("-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "-b")
    toks = [t for t in out.split("\0") if t]
    branch, entries, i = "", [], 0
    if toks and toks[0].startswith("##"):
        branch, i = toks[0], 1
    while i < len(toks):
        rec = toks[i]
        i += 1
        if len(rec) < 4:
            continue
        x, y, path = rec[0], rec[1], rec[3:]
        if "R" in (x, y) or "C" in (x, y):
            if i < len(toks):
                i += 1  # rename/copy 记录后面还跟着原路径
        entries.append((x, y, path))
    return branch, entries


def cmd_status():
    root = in_repo_or_exit()
    branch, entries = parse_status()
    staged = [p for x, y, p in entries if x not in " ?"]
    dirty = [p for x, y, p in entries if y not in " ?"]
    untracked = [p for x, y, p in entries if x == "?" or y == "?"]
    conflicts = [p for x, y, p in entries if (x, y) in
                 (("U", "U"), ("A", "U"), ("U", "A"), ("D", "U"), ("U", "D"), ("A", "A"), ("D", "D"))]
    head(f"仓库状态  {root}")
    say(f"  分支行: {branch[2:] or current_branch()}")
    ahead = re.search(r"\[(.*)\]", branch)
    if ahead:
        say(f"  与远端关系: {ahead.group(1)}")
    def block(title, items, hint):
        if not items:
            return
        say(f"\n  {BOLD}{title}{RESET}  {len(items)} 个")
        for p in items[:15]:
            say(f"    {p}")
        if len(items) > 15:
            say(f"    ... 另有 {len(items) - 15} 个")
        if items:
            say(f"    {DIM}下一步: {hint}{RESET}")
    if conflicts:
        block("有冲突，必须先解决", conflicts, "git-cn conflict")
    block("已暂存（下次 commit 会带上）", staged, "git-cn commit")
    block("改了但没暂存（commit 不会带上！）", dirty, "git add <文件> 或 git-cn commit 里挑文件")
    block("未跟踪（git 完全不管这些）", untracked,
          "确实要版本化就 git add；本地产物请写进 .gitignore")
    if not (staged or dirty or untracked or conflicts):
        ok("工作区干净，没有待处理的东西。")
    return 0


def cmd_commit():
    in_repo_or_exit()
    _, entries = parse_status()
    cands = [(x, y, p) for x, y, p in entries if not (x == "?" and y == "?")]
    untracked = [p for x, y, p in entries if x == "?" and y == "?"]
    if not cands and not untracked:
        ok("没有待提交的改动。")
        return 0
    head("选择要提交的文件")
    rows = []
    for i, (x, y, p) in enumerate(entries, 1):
        state = "已暂存" if x not in " ?-" else ("改了没暂存" if y not in " ?" else "未跟踪")
        say(f"  {i:>2}  [{state}]  {p}")
        rows.append(p)
    say(f"  {DIM}输入编号（多选用空格分隔），直接回车 = 全部{RESET}")
    pick = ask("要提交哪些")
    if pick:
        try:
            chosen = [rows[int(n) - 1] for n in pick.split()]
        except (ValueError, IndexError):
            bad("编号不对，已取消。")
            return 2
    else:
        chosen = rows
    say("")
    say(f"{BOLD}提交说明{RESET}{DIM}（第一行写清做了什么，空行后写原因；直接回车可用自动建议）{RESET}")
    auto = "更新文件"
    msg = ask("标题", auto)
    if confirm("要补充正文说明为什么这样改吗？", default_no=True):
        lines2 = []
        say("  逐行输入，空行结束：")
        while True:
            try:
                ln = input("  > ")
            except EOFError:
                break
            if not ln.strip():
                break
            lines2.append(ln.strip())
        body = "\n".join(lines2)
    else:
        body = ""
    full = msg + (f"\n\n{body}" if body else "")
    rc, out, err = git("add", "-A", "--", *chosen)
    if rc != 0:
        translate_error(err + out)
        return rc
    say(f"  {DIM}git add {len(chosen)} 个文件{RESET}")
    rc, out, err = git("commit", "-m", full)
    if rc != 0:
        translate_error(err + out)
        return rc
    ok("已提交。")
    short = git_out("log", "--oneline", "-1")
    say(f"  {short}")
    if confirm("要顺手推到远端吗？"):
        return cmd_push([])
    say(f"  {DIM}以后推：git-cn push{RESET}")
    return 0


def cmd_push(argv, assume_yes=False):
    root = in_repo_or_exit()
    br = current_branch()
    if not danger_check(["push", *argv], assume_yes):
        return 130
    if br == "HEAD":
        bad("你处于游离 HEAD，先建分支：git switch -c 分支名")
        return 1
    up = upstream_of(br)
    if not up:
        heads = git_out("remote")
        remote = heads.split()[0] if heads else "origin"
        warn(f"分支 {br} 还没有对应远端。")
        if confirm(f"用 {remote}/{br} 建立对应关系并推送吗？"):
            rc, out, err = git("push", "-u", remote, br, capture=False)
            if rc:
                translate_error(out + err)
            return rc
        return 130
    ahead = git_out("rev-list", "--count", f"{up}..HEAD")
    say(f"  分支 {BOLD}{br}{RESET} → {up}，待推送 {ahead or '?'} 个提交")
    if git_out("log", "--oneline", f"{up}..HEAD"):
        for ln in git_out("log", "--oneline", f"{up}..HEAD").splitlines()[:20]:
            say(f"    {ln}")
    rc, out, err = git("push", *argv, capture=False)
    if rc:
        translate_error(out + err)
    else:
        ok("推送完成。")
    return rc


def cmd_pull():
    root = in_repo_or_exit()
    br = current_branch()
    up = upstream_of(br)
    if not up:
        warn(f"分支 {br} 没有上游，无法直接 pull。")
        say("  用 git-cn push 建立对应关系，或 git pull origin <分支>")
        return 1
    behind = git_out("rev-list", "--count", f"HEAD..{up}")
    say(f"  远端比你新 {behind or '?'} 个提交；用变基方式拉取。")
    rc, out, err = git("pull", "--rebase", "--autostash", capture=False)
    if rc:
        translate_error(out + err)
        if repo_root() and parse_status()[1]:
            if any((x, y) in (("U", "U"), ("A", "U"), ("U", "A")) for x, y, _ in parse_status()[1]):
                if confirm("看起来停在冲突上，进入中文冲突处理吗？"):
                    return cmd_conflict()
    else:
        ok("已更新到最新。")
    return rc


def cmd_log(n=30):
    in_repo_or_exit()
    head(f"最近 {n} 条提交")
    git("log", "--graph", "--date=short", "--pretty=format:%C(yellow)%h%Creset %ad %C(cyan)%an%Creset %s%C(auto)%d",
        f"-{n}", "--decorate", capture=False)
    say("")
    say(f"{DIM}看某个提交：git show <哈希>    看某文件历史：git log -- 文件路径{RESET}")
    return 0


def cmd_branch(argv):
    in_repo_or_exit()
    if argv:
        return cmd_run(["branch", *argv], False)
    cur = current_branch()
    head("分支")
    for ln in git_out("branch", "-vv").splitlines():
        mark = ln.strip().startswith("*")
        say(("  " + BOLD + GREEN + ln.strip() + RESET) if mark else ("    " + ln.strip()))
    say(f"\n  {DIM}操作：1 新建  2 切换  3 合并当前分支进来  4 删除  0 返回{RESET}")
    pick = ask("要做哪个")
    if pick == "1":
        name = ask("新分支名")
        base = ask("基于哪个分支/提交", "HEAD")
        rc, out, err = git("switch", "-c", name, base, capture=False)
        return rc or translate_error(out + err)
    if pick == "2":
        name = ask("切到哪个分支")
        rc, out, err = git("switch", name, capture=False)
        return rc or translate_error(out + err)
    if pick == "3":
        name = ask("把哪个分支合并进当前分支")
        rc, out, err = git("merge", "--no-ff", name, capture=False)
        if rc:
            translate_error(out + err)
            if confirm("进入冲突处理吗？"):
                return cmd_conflict()
        return rc
    if pick == "4":
        name = ask("要删除的分支")
        if not danger_check(["branch", "-D", name], False):
            return 130
        rc, out, err = git("branch", "-D", name, capture=False)
        return rc
    return 0


def cmd_undo():
    root = in_repo_or_exit()
    sha = git_out("rev-parse", "HEAD")
    if not sha:
        bad("这个仓库还没有任何提交。")
        return 1
    head("撤销上一次提交")
    say(f"  {BOLD}{git_out('log','--oneline','-1')}{RESET}")
    say(f"""
  三种「撤销」区别，选错会丢代码：
   1  只撤销提交这个动作，改动留在暂存区        {DIM}最常用，安全{RESET}
   2  撤销提交并撤销 git add，改动留在工作区    {DIM}安全{RESET}
   3  撤销提交并且丢弃改动                      {DIM}不可恢复，慎用{RESET}
   4  只做反向提交（历史保留，新增一次抵消提交）{DIM}已经 push 过时用这个{RESET}""")
    pick = ask("选哪个", "1")
    only_one = (git_out("rev-list", "--count", "HEAD") or "0") == "1"
    if pick in ("1", "2") and only_one:
        say(f"  {DIM}这是仓库的第一个提交，没有上一个提交可回退，改为撤销“已提交”这件事本身。{RESET}")
        rc, _, _ = git("update-ref", "-d", "HEAD")
        if pick == "2" and rc == 0:
            git("rm", "-r", "--cached", "-q", "--", ".")
    elif pick == "1":
        rc, _, _ = git("reset", "--soft", "HEAD^")
    elif pick == "2":
        rc, _, _ = git("reset", "--mixed", "HEAD^")
    elif pick == "3":
        if not danger_check(["reset", "--hard", "HEAD^"], False):
            return 130
        rc, _, _ = git("reset", "--hard", "HEAD^")
    elif pick == "4":
        rc, out, err = git("revert", "--no-edit", "HEAD", capture=False)
        if rc:
            translate_error(out + err)
        return rc
    else:
        bad("没认出来的选项。")
        return 2
    if rc == 0:
        ok("完成。")
        say(f"  万一改坏了，回到撤销前：{BOLD}git reset --hard {sha}{RESET}")
    return rc


def cmd_reset(argv=()):
    root = in_repo_or_exit()
    pre = next((a.lstrip("-") for a in argv if a.lstrip("-") in ("soft", "mixed", "hard")), "mixed")
    head("git reset 到底会动什么（这是 Git 最反人类的地方）")
    say("""
   模式     提交记录   暂存区(git add 状态)  工作区(文件内容)
   soft     回退       保留                 保留
   mixed    回退       清空                 保留        (默认，不带参数就是它)
   hard     回退       清空                 丢弃        (唯一会丢代码的)""")
    n = ask("往回退几个提交", "1")
    if not n.isdigit() or int(n) < 1:
        bad(f"“{n}”不是正整数，已取消，未执行任何操作。")
        return 2
    mode = ask("选 soft / mixed / hard", pre).lower()
    if mode not in ("soft", "mixed", "hard"):
        bad("模式只能三选一。")
        return 2
    if mode == "hard" and not danger_check(["reset", "--hard", f"HEAD~{n}"], False):
        return 130
    rc, out, err = git("reset", f"--{mode}", f"HEAD~{n}", capture=False)
    if rc:
        translate_error(out + err)
    else:
        ok(f"已 {mode} 回退 {n} 个提交。当前状态：")
        cmd_status()
    return rc


def cmd_checkout(argv):
    if not argv:
        say("  用法：git-cn checkout <文件或分支名>")
        say(f"  {DIM}Git 2.23 已把 checkout 拆成 switch(切分支) 和 restore(还原文件)，"
            f"包装层按你的意图路由。{RESET}")
        return 2
    target = argv[-1]
    root = in_repo_or_exit()
    if target in (".", "--", "-", "*"):
        return cmd_run(["restore", *argv], False)
    rc_branch, _, _ = git("rev-parse", "--verify", "--quiet", f"refs/heads/{target}")
    rc_file, _, _ = git("rev-parse", "--verify", "--quiet", f"HEAD:{target.replace(os.sep, '/')}")
    is_branch, is_file = rc_branch == 0, rc_file == 0
    if not is_branch and not is_file:
        say(f"  {BOLD}{target}{RESET} 既不是分支名，也不是已被跟踪的文件。")
        near = difflib.get_close_matches(target,
                                         git_out("branch", "--format=%(refname:short)").split(), n=3)
        if near:
            say(f"  你是不是想用分支：{' / '.join(near)}")
        say(f"  {DIM}从未被 git add 过的文件无法还原，Git 里没有它的副本。{RESET}")
        return 1
    if is_file and (not is_branch or confirm(f"{target} 既是分支名也是文件名，这次是要还原文件吗？", default_no=True)):
        warn("还原文件 = 丢弃该文件未提交的修改，交给安全闸确认。")
        return cmd_run(["restore", "--", target], False)
    say("  切分支不会丢弃改动；如果 Git 拒绝切换，是因为改动会和新分支内容冲突，那是保护。")
    return cmd_run(["switch", target], False)


def cmd_conflict():
    root = in_repo_or_exit()
    _, entries = parse_status()
    files = [p for x, y, p in entries if x == "U" or y == "U" or (x, y) in (("A", "A"), ("D", "D"))]
    if not files:
        ok("当前没有冲突文件。")
        return 0
    state = git_out("status")
    mode = "变基中" if "rebase" in state.lower() else ("合并中" if "merge" in state.lower() else "冲突中")
    head(f"{mode}，{len(files)} 个文件有冲突")
    if mode == "变基中":
        say(f"  {BOLD}注意方向是反的：{RESET}变基时 --ours 指“你在往上面重放的分支”（通常是远端），")
        say(f"        --theirs 才是“你自己那个提交”。这和 merge 恰好相反，Git 这里非常反人类。")
    for f in files:
        say(f"\n  {BOLD}{f}{RESET}")
        try:
            n_markers = Path(root, f).read_text(encoding="utf-8", errors="replace").count("<<<<<<<")
        except OSError:
            n_markers = 0
        say(f"    冲突块 {n_markers} 处")
        say(f"    {DIM}1 我自己开编辑器改   2 用对方版本   3 用我方版本   4 跳过{RESET}")
        pick = ask("    怎么处理", "1")
        if pick == "1":
            ed = os.environ.get("GIT_CN_EDITOR") or ("notepad" if os.name == "nt" else "vi")
            subprocess.run([ed, str(Path(root) / f)])
        elif pick in ("2", "3"):
            side = {"2": "--theirs", "3": "--ours"}[pick]
            if mode == "变基中":
                side = {"--theirs": "--ours", "--ours": "--theirs"}[side]
            warn(f"采用 {side} 一方，另一方的改动丢失。")
            git("checkout", side, "--", f)
        else:
            continue
        if confirm(f"    {f} 已确认改好，标记为已解决？"):
            git("add", "--", f)
    _, entries = parse_status()
    still = [p for x, y, p in entries if x == "U" or y == "U"]
    if still:
        warn(f"仍有 {len(still)} 个未解决：" + ", ".join(still[:5]))
        return 1
    say(f"\n  {DIM}下一步：变基 git rebase --continue / 合并 git commit / 中止 git rebase --abort{RESET}")
    if "rebase" in state.lower():
        if confirm("现在继续变基？"):
            rc, out, err = git("rebase", "--continue", capture=False)
            return rc
    elif confirm("现在提交这次合并？"):
        rc, out, err = git("commit", "--no-edit", capture=False)
        return rc
    return 0


def cmd_stash(argv):
    return cmd_run(["stash", "push", "-u", *argv] if not argv else ["stash", *argv], False)


def cmd_init():
    cur = Path.cwd()
    name = ask("仓库名（回车用当前目录名）", cur.name)
    tgt = cur if name == cur.name else cur / name
    tgt.mkdir(parents=True, exist_ok=True)
    rc, out, err = git("init", "--initial-branch=main", "--", str(tgt), capture=False)
    if rc:
        return rc
    ok(f"仓库已建在 {tgt}")
    gi = tgt / ".gitignore"
    if not gi.exists():
        gi.write_text(GITIGNORE_CN, encoding="utf-8")
        say("  写入中文注释版 .gitignore")
    ga = tgt / ".gitattributes"
    if not ga.exists():
        ga.write_text("# 统一按 LF 存仓库，避免 Windows/ Linux 换行符打架\n* text=auto eol=lf\n", encoding="utf-8")
        say("  写入 .gitattributes（换行符统一 LF）")
    rc, out, err = git("-C", str(tgt), "add", "-A")
    if rc != 0:
        translate_error(out + err)
        return rc
    say("  已把当前文件加入暂存区，接着 git-cn commit 就能提交")
    return 0


GITIGNORE_CN = """# 版本控制不该带上机器专属和个人私有的东西
# 依赖与构建产物
node_modules/
dist/
build/
__pycache__/
*.pyc
.venv/
venv/
# 编辑器与系统杂物
.vscode/
.idea/
*.swp
Thumbs.db
.DS_Store
# 本地私有配置和密钥：这类文件一旦提交到远端就是泄露
.env
.env.*
*.key
credentials.json
# 本地数据与日志
*.log
data/local/
cache/
# 大文件与产物，走单独分发渠道
*.zip
*.7z
*.exe
"""

MENU = [
    ("1", "提交改动（挑文件 + 写说明 + 提交）", cmd_commit),
    ("2", "推送到远端（自动处理新分支没上游的问题）", lambda: cmd_push([])),
    ("3", "拉取更新（变基 + 自动收起改动）", cmd_pull),
    ("4", "中文解读当前状态", cmd_status),
    ("5", "看历史", lambda: cmd_log()),
    ("6", "分支管理", lambda: cmd_branch([])),
    ("7", "撤销上一次提交（讲清三种撤销差别）", cmd_undo),
    ("8", "git reset（讲清会动到什么再执行）", cmd_reset),
    ("9", "处理冲突（含变基时 ours/theirs 反向陷阱）", cmd_conflict),
    ("a", "丢弃文件改动（安全闸确认）", lambda: cmd_checkout(["."])),
    ("b", "新建仓库（带中文 .gitignore）", cmd_init),
    ("c", "临时收起当前改动（stash -u）", lambda: cmd_stash([])),
    ("d", "体检：git 版本 / 配置 / 编码 / 汉化", cmd_doctor),
    ("e", "一键写入符合国内习惯的默认配置", lambda: cmd_fix()),
    ("f", "GitHub 网络体检与修复", lambda: cmd_net(False)),
    ("g", "执行原始 git 命令（带报错翻译）", None),
]


def cmd_menu():
    say(f"""
{BOLD}  git-cn  中文 Git 工具箱{RESET}
  {DIM}所有操作都不偷偷改历史；危险动作会先讲清后果{RESET}
""")
    for key, label, _ in MENU:
        say(f"   {BOLD}{key:>2}{RESET}  {label}")
    say(f"    0  退出")
    pick = ask("\n  选一个")
    if pick in ("0", "q", ""):
        return 0
    for key, label, fn in MENU:
        if pick == key:
            if fn is None:
                raw = ask("  输入完整 git 命令（例：git status -sb）")
                argv = raw.split()
                return cmd_run(argv, False)
            return fn()
    bad("没有这个选项。")
    return 2


USAGE = """git-cn  中文优先的 Git 工具箱

  git-cn                  打开中文向导菜单
  git-cn <git子命令> ...   等价 git，但带危险操作确认 + 报错中文翻译
  git-cn status|commit|push|pull|log|branch|undo|reset|conflict|checkout|init|stash
  git-cn doctor            体检 git / 配置 / 编码 / 汉化是否到位
  git-cn fix               写入符合国内习惯的默认配置（--dry 试算，--revert 回退）
  git-cn net               GitHub 连通性体检与修复（--apply 生效）
  git-cn help              看这个说明

  例子
    git-cn fix
    git-cn push
    git-cn undo
    git-cn status
"""


def main(argv):
    init_console()
    assume_yes = "--yes" in argv
    argv = [a for a in argv if a != "--yes"]
    if not have_git():
        bad("没找到 git。先装：winget install Git.Git  然后重开终端。")
        return 127
    if not argv:
        return cmd_menu()
    sub, rest = argv[0], argv[1:]
    table = {
        "help": lambda: say(USAGE) or 0, "--help": lambda: say(USAGE) or 0, "-h": lambda: say(USAGE) or 0,
        "doctor": cmd_doctor, "fix": lambda: cmd_fix("--dry" in rest, "--revert" in rest),
        "net": lambda: cmd_net("--apply" in rest),
        "status": cmd_status, "commit": cmd_commit, "push": lambda: cmd_push(rest, assume_yes),
        "pull": cmd_pull, "log": lambda: cmd_log(int(rest[0]) if rest and rest[0].isdigit() else 30),
        "branch": lambda: cmd_branch(rest), "undo": cmd_undo,
        "reset": lambda: cmd_reset(rest),
        "conflict": cmd_conflict, "checkout": lambda: cmd_checkout(rest),
        "co": lambda: cmd_checkout(rest), "init": cmd_init,
        "stash": lambda: cmd_stash(rest), "menu": cmd_menu,
        "_probe": lambda: cmd_probe_value(rest),
    }
    if sub in table:
        try:
            return table[sub]() or 0
        except SystemExit as e:
            return e.code if isinstance(e.code, int) else 1
        except BrokenPipeError:
            return 0
        except Exception as e:
            bad(f"git-cn 自己出错了：{type(e).__name__}: {e}")
            say(f"{DIM}（不影响直接用 git；这条反馈值得发给作者）{RESET}")
            return 3
    return cmd_run(argv, assume_yes)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
