using System.Text.RegularExpressions;

namespace GitCn.Core;

public sealed record GuideItem(string Why, IReadOnlyList<string> Fixes);

public static class ErrorGuide
{
    private sealed record Rule(string Pattern, string Why, string[] Fixes);

    private static readonly Rule[] Rules =
    [
        new("not a git repository",
            "这里没有 Git 仓库：当前目录和它的父目录里都没有 .git 文件夹。",
            ["在项目根目录执行 git-cn init 新建仓库", "或先切到真正的仓库目录再运行 git 命令"]),
        new(@"has no upstream branch|no upstream branch|has no upstream configured",
            "这个分支是第一次推送，Git 不知道要推到远端的哪个分支。",
            ["git push -u origin <分支名>    # -u 记住对应关系，以后直接 git push",
             "git-cn fix    # 打开 push.autoSetupRemote，以后所有新分支都不用再带 -u"]),
        new(@"(failed to push some refs|non-fast-forward|Updates were rejected)|(fetch first|tip of your current branch is behind)",
            "远端分支上有你本地没有的提交，直接推会覆盖别人（或你在另一台机器上）的工作，所以被拒绝。",
            ["git pull --rebase    # 把你的提交挪到远端最新提交之后，历史保持一条直线",
             "git push             # 再推",
             "不要用 git push -f 硬推，那会真的删掉远端的提交"]),
        new(@"Your local changes to the following files would be overwritten|Please commit your changes or stash them|would be overwritten by (merge|checkout)",
            "你有没提交的改动会被这次操作覆盖，Git 选择停下来保护你。",
            ["先 git stash push -u 把改动收起来", "重跑刚才的命令", "最后 git stash pop 取回改动"]),
        new("untracked working tree files would be (overwritten|removed)",
            "本地有未跟踪文件挡路了，继续会直接删掉它们。",
            ["把提示里列出的文件改名或移走，再重跑命令",
             "确认不需要了：先 git clean -nd 看清单，再用 git-cn clean 处理"]),
        new(@"cannot rebase: (You have unstaged changes|your local working directory|with a dirty)",
            "工作区有未保存的改动时禁止变基，避免变基中途冲突时你的改动无处可回。",
            ["git stash push -u && git rebase origin/main && git stash pop",
             "git-cn fix    # 打开 rebase.autoStash，以后自动帮你收和放"]),
        new(@"divergent branches|Need to specify how to reconcile",
            "本地和远端各有对方没有的提交。Git 2.27 起强制你选 merge 还是 rebase，所以报错。",
            ["git pull --rebase    # 推荐：历史线性、好读",
             "git-cn fix    # 打开 pull.rebase，以后不用每次带参数"]),
        new("HEAD detached",
            "你现在处于游离 HEAD：不在任何分支上。此时的提交不属于任何分支，切走就可能找不到。",
            ["git switch -c save-my-work    # 把当前状态存成新分支（最稳）",
             "git switch <分支名>           # 或者放弃回到正轨"]),
        new(@"did not match any file\(s\)|pathspec '\S+' did not match",
            "这个路径 Git 不认识：可能拼错了、不在仓库根目录、或者该文件从来没被 git add 过。",
            ["git status --short    # 用真实路径",
             "git-cn fix    # 打开 core.quotepath，中文文件名不再显示成八进制乱码，便于核对"]),
        new(@"Author identity unknown|Please tell me who you are|unable to auto-detect",
            "Git 还不知道你是谁（缺 user.name / user.email），所以拒绝写提交。",
            ["git config --global user.name  \"你的名字\"",
             "git config --global user.email \"你的邮箱@example.com\""]),
        new(@"Filename too long|cannot create directory.*No such file|Invalid path|too long",
            "Windows 默认路径长度上限 260 字符，深层目录会被砍断。",
            ["git-cn fix    # 打开 core.longpaths，Windows 下几乎必开"]),
        new("detected dubious ownership",
            "Git 认为仓库目录的所有者不是当前用户（Windows 上管理员权限和普通权限混用很常见），拒绝操作。",
            ["git config --global --add safe.directory \"<仓库绝对路径>\""]),
        new(@"Permission denied \(publickey\)|SSH authentication|kex_exchange_identification|Connection closed by .*port 22",
            "SSH 连不上 GitHub。大陆网络干扰 22 端口是常见原因，不一定是你没配 key。",
            ["ssh -T git@ssh.github.com -p 443    # 先测 443 端口的 SSH 是否通",
             "改用 https 也能绕过：git remote set-url origin https://github.com/用户/仓库.git",
             "git-cn net    # 一键体检并套用 GitHub 常用网络修复"]),
        new(@"Could not resolve host|Failed to connect to|Connection reset by peer|Empty reply from server|curl \d+|RPC failed|early EOF|TLS connect error|gnutls|The requested URL returned error: (408|502|503|504)|port 443",
            "网络没走到 GitHub：DNS、连接被重置或超时。这类失败通常是瞬时的，重试往往就成功。",
            ["先重跑一次同样的命令",
             "git-cn net    # 自动切 HTTP/1.1、加大 postBuffer，并测连通",
             "克隆大仓库失败时：重新 clone 一次比反复重试更快"]),
        new(@"Authentication failed|Invalid username or password|returned error: 40[13]|could not read Username",
            "身份验证没过。GitHub 从 2021 年起不接受账号密码，只接受 token、SSH key 或凭据管理器。",
            ["用 gh 登录最省事：gh auth login",
             "Windows 凭据残留时：控制面板里删掉 github.com 的凭据后重试"]),
        new(@"index\.lock: File exists|cannot lock ref|Unable to create .*\.lock",
            "有 .lock 文件残留，说明上次 git 进程被强杀了。Git 不敢并发写，所以停住。",
            ["先确认没有 git 命令在跑，再删除提示里那个 lock 文件",
             "rm -f .git/index.lock    # 只删 lock，别删 .git 下别的东西"]),
        new(@"nothing added to commit|no changes added to commit",
            "改动没进暂存区，所以这次提交是空的。这是新手最常踩的一步：改了文件不等于提交了文件。",
            ["git add -A    # 全部加入暂存区",
             "git-cn commit    # 或者走向导，自动列文件让你挑"]),
        new("nothing to commit, working tree clean",
            "没有需要提交的改动。可能是改动已经提交过了，也可能你在错的目录。",
            ["git-cn status    # 中文解读当前状态",
             "git log --oneline -5    # 看最近提交，确认改动是不是已经在里面"]),
        new(@"CONFLICT \(content\)|Merge conflict in |both modified:",
            "出现合并冲突，同一个文件里有两方改动的版本，Git 不替你决定。",
            ["git-cn conflict    # 中文逐个文件引导处理",
             "记住坑点：变基时 --ours 是“你在往上面重放的目标分支”，--theirs 才是你自己的提交，和 merge 正好相反"]),
        new(@"Your branch is ahead of|have diverged",
            "本地有远端还没有的提交（或两边分叉了），需要 push 或先 pull --rebase。",
            ["git push", "git pull --rebase && git push"]),
        new(@"gpg failed to sign|secret key not available|error writing the gpg file",
            "提交签名失败：GPG 没配好，但仓库却开着自动签名。",
            ["git-cn fix    # 关掉 commit.gpgsign，先把提交跑通"]),
        new(@"problem with the editor|Waiting for your editor to close|error: There was a problem with",
            "Git 调不出编辑器（Windows 上常见于默认 vim 不存在，或编辑器不等待就返回）。",
            ["git config --global core.editor \"notepad -w\"    # -w 让记事本等你关闭",
             "装了 VSCode 可以用：git config --global core.editor \"code --wait\""]),
        new("bad config file line",
            "Git 配置文件语法坏了，所有 git 命令都会失败。",
            ["按提示的行号修 ~/.gitconfig（Windows 是 C:\\Users\\你\\.gitconfig）",
             "git-cn fix --revert    # 用备份把 git-cn 写过的项恢复回去"]),
        new(@"could not read Repository format|does not appear to be a git repository",
            "地址不对，或者这个仓库在你没权限的账号下（私有仓库匿名访问就是这个报错）。",
            ["核对地址：git remote -v", "私有仓库先登录：gh auth status"]),
        new(@"Repository is not large enough|pack is corrupt|unable to read sha1 file|invalid index file",
            "仓库对象或索引疑似损坏，本地继续写有风险。",
            ["最稳：另存一份工作改动后重新 clone",
             "只坏索引时：rm -f .git/index && git reset    # 重建索引，不动工作区"]),
        new("remote origin already exists",
            "远端 origin 已经存在，不能再 add，但可以直接改地址。",
            ["git remote set-url origin <新地址>"]),
        new(@"is not a git command",
            "这个命令不存在，八成是拼错了。",
            ["git help -a    # 查看全部命令"]),
        new(@"unable to access|SSL|certificate",
            "连接或证书被中间环节改了。大陆网络下常见于代理/加速程序注入了证书。",
            ["换个网络再试一次，多数情况会自己好",
             "不要在没搞清原因前关掉 git 的证书校验，那是给自己开后门"]),
    ];

    public static IReadOnlyList<GuideItem> Match(string text)
    {
        var hits = new List<GuideItem>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        foreach (var r in Rules)
        {
            if (Regex.IsMatch(text, r.Pattern, RegexOptions.IgnoreCase))
                hits.Add(new GuideItem(r.Why, r.Fixes));
        }
        var typo = Regex.Match(text, @"git: '([^']+)' is not a git command");
        if (typo.Success)
        {
            var name = typo.Groups[1].Value;
            var near = Closest(name);
            hits.RemoveAll(h => h.Why.StartsWith("这个命令不存在"));
            if (near.Count > 0)
                hits.Insert(0, new GuideItem($"Git 里没有 “{name}” 这个命令。",
                    [$"你是不是想用：{string.Join(" / ", near)}"]));
        }
        return hits;
    }

    private static readonly string[] AllCommands =
    [
        "add", "am", "apply", "bisect", "blame", "branch", "cat-file", "cherry-pick", "checkout",
        "clean", "clone", "commit", "config", "describe", "diff", "fetch", "gc", "grep", "help",
        "init", "log", "ls-files", "ls-remote", "merge", "mv", "pull", "push", "rebase", "reflog",
        "remote", "reset", "restore", "revert", "rm", "show", "stash", "status", "switch", "tag",
        "worktree",
    ];

    private static List<string> Closest(string typo)
    {
        static double Similarity(string a, string b)
        {
            var common = a.Intersect(b).Count();
            return (2.0 * common) / (a.Length + b.Length);
        }
        return AllCommands.Where(c => Similarity(typo, c) >= 0.5)
            .OrderByDescending(c => Similarity(typo, c))
            .Take(3).ToList();
    }

    public static string Summarize(string text)
    {
        var hits = Match(text);
        if (hits.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("\n--- git-cn 帮你翻译这份报错 ---\n");
        foreach (var h in hits)
        {
            sb.Append("为什么失败：").Append(h.Why).Append('\n');
            foreach (var f in h.Fixes) sb.Append("  # ").Append(f).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

public sealed record DangerVerdict(string Label, string Harm, string Safer, bool OfferBackup);

public static class DangerGate
{
    private sealed record Rule(string Pattern, string Label, string Harm, string Safer, bool Backup);

    private static readonly Rule[] Rules =
    [
        new(@"^push\b.*(\s--force(-with-lease)?|\s-f)(\s|$)", "强制推送",
            "会用你本地的历史覆盖远端历史，远端上你没有的提交会立即对其他人和另一台机器消失。",
            "先 git pull --rebase 再普通 push；只有确认远端那些提交是垃圾时才强推。", false),
        new(@"^reset\b.*--(hard|keep|merge)\b", "硬重置",
            "工作区里所有未提交的改动会被直接抹掉，git 不保留副本，reflog 也救不回未提交内容。",
            "先 git stash push -u 备份，再 reset。", true),
        new(@"^clean\b.*-[a-z]*f", "清理未跟踪文件",
            "未跟踪文件不在版本控制里，删了就永久没了——包括你没提交的新建文件、.env、本地笔记。",
            "先 git clean -nd 看清单；要删 .gitignore 里的构建产物时不要加 -x。", true),
        new(@"^restore\b(?!.*--staged)", "还原文件内容",
            "restore 只作用于文件：这些文件未提交的修改会被丢掉，Git 里没有它们的另一份副本。",
            "先保住改动：git stash push -u，或 git diff > 我的改动.patch。", true),
        new(@"^(checkout|switch)\b.*(\s-f\b|\s\.\s*$|\s\.$|\s--\s)", "丢弃本地修改",
            "会把文件内容回退到上一次提交的状态，未提交的修改不可恢复。",
            "先 git stash push -u，或者只回退指定文件而不是整个目录。", true),
        new(@"^branch\b.*\s-D\b", "删除未合并分支",
            "-D 会连未合并进主干的提交一起删掉，那些提交只能靠 reflog 短期找回。",
            "先确认分支内容已合并：git log main..<分支> --oneline", false),
        new(@"^stash\b.*\s(drop|clear)\b", "丢弃 stash",
            "stash 是 Git 里唯一的临时抽屉，drop/clear 后里面的内容很难找回。",
            "改用 git stash list + git stash apply <编号>，保留抽屉。", false),
        new(@"^push\b.*(--delete|\s:\S)", "删除远端分支",
            "远端分支和它独有的提交对所有人消失。", "确认合并请求已合并再删。", false),
        new(@"^(filter-branch|filter-repo)", "重写全部历史",
            "所有提交的哈希会变，别人和远端会彻底分叉，且不可逆。",
            "先在临时克隆里试，并且必须通知所有协作者。", false),
        new(@"^gc\b.*--prune", "立即清除悬空对象",
            "被 reflog 之外的悬空提交会被物理删除，未合并的救命提交可能就此消失。",
            "一般不用手动 gc，交给 git 自动维护。", false),
        new(@"^(tag\b.*\s-d\b|push\b.*--delete.*tags)", "删除标签",
            "发布版本标签删除后，别人按标签拉的构建会失败。", "确认这是你自己打错的标签。", false),
    ];

    public static DangerVerdict? Assess(IEnumerable<string> argv)
    {
        var joined = string.Join(' ', argv);
        if (Regex.IsMatch(joined, @"^(checkout|restore)\b.*--staged\b"))
            return null;    // 取消暂存是可逆操作，不动文件内容
        foreach (var r in Rules)
        {
            if (Regex.IsMatch(joined, r.Pattern, RegexOptions.IgnoreCase))
                return new DangerVerdict(r.Label, r.Harm, r.Safer, r.Backup);
        }
        return null;
    }
}
