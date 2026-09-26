namespace GitCn.Core;

public sealed record OpResult(bool Ok, string Output, string Guide = "")
{
    public static OpResult From(GitResult r, bool explain = true) =>
        new(r.Ok, r.Output, !r.Ok && explain ? ErrorGuide.Summarize(r.Output) : "");

    public string FailureSummary => Ok ? "" :
        Guide.Length > 0 ? Guide : Output.Trim();
}

public enum RouteKind { Branch, File, Ambiguous, Unknown }

public static class Ops
{
    public static async Task<OpResult> RunGatedAsync(string dir, IEnumerable<string> argv,
        bool assumeYes = false, IProgress<string>? onLine = null)
    {
        var args = argv.ToList();
        var verdict = DangerGate.Assess(args);
        if (verdict is not null && !assumeYes)
            return new OpResult(false, "", VerdictText(verdict));
        var r = await GitRunner.RunAsync(["-C", dir, .. args], dir, onLine).ConfigureAwait(false);
        return OpResult.From(r);
    }

    public static string VerdictText(DangerVerdict v) =>
        $"危险操作：{v.Label}\n会造成什么后果：{v.Harm}\n更安全的做法：{v.Safer}";

    public static DangerVerdict? AssessDanger(string dir, IEnumerable<string> argv) =>
        DangerGate.Assess(argv);

    public static async Task<OpResult> BackupAsync(string dir, string reason)
    {
        var r = await GitRunner.RunAsync(
            ["-C", dir, "stash", "push", "-u", "-m", $"git-cn 自动备份：{reason}"], dir).ConfigureAwait(false);
        if (r.Ok && !r.Output.Contains("No local changes", StringComparison.OrdinalIgnoreCase))
            return new OpResult(true, "已备份到 stash，恢复用 git stash pop\n" + r.Output);
        return new OpResult(true, "没有需要备份的改动。\n" + r.Output);
    }

    public static async Task<OpResult> StageAsync(string dir, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return new OpResult(true, "没选任何文件。");
        var r = await GitRunner.RunAsync(["-C", dir, "add", "-A", "--", .. list], dir).ConfigureAwait(false);
        return OpResult.From(r);
    }

    public static async Task<OpResult> CommitAsync(string dir, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new OpResult(false, "", "提交说明不能为空：写清这次改了什么。\n");
        var msg = body.Trim().Length > 0 ? title.Trim() + "\n\n" + body.Trim() : title.Trim();
        var r = await GitRunner.RunAsync(["-C", dir, "commit", "-m", msg], dir).ConfigureAwait(false);
        if (r.Ok)
        {
            var shortLog = GitRunner.Out(["-C", dir, "log", "--oneline", "-1"], dir);
            return new OpResult(true, $"已提交：{shortLog}\n" + r.Output);
        }
        return OpResult.From(r);
    }

    public static async Task<OpResult> PushAsync(string dir, bool assumeYes = false)
    {
        var info = await Repo.ReadAsync(dir).ConfigureAwait(false);
        if (!info.IsRepo) return new OpResult(false, "当前目录不是仓库。");
        if (info.Detached)
            return new OpResult(false, "", "你处于游离 HEAD，先建分支：git switch -c 分支名\n");
        var danger = DangerGate.Assess(["push"]);
        var upstream = Repo.UpstreamOf(dir, info.Branch);
        if (upstream is null)
        {
            var remote = Repo.DefaultRemote(dir) ?? "origin";
            var r = await GitRunner.RunAsync(["-C", dir, "push", "-u", remote, info.Branch], dir)
                .ConfigureAwait(false);
            return OpResult.From(r);
        }
        var res = await GitRunner.RunAsync(["-C", dir, "push"], dir).ConfigureAwait(false);
        _ = danger;
        return OpResult.From(res);
    }

    public static async Task<OpResult> PullAsync(string dir)
    {
        var info = await Repo.ReadAsync(dir).ConfigureAwait(false);
        if (!info.IsRepo) return new OpResult(false, "当前目录不是仓库。");
        if (Repo.UpstreamOf(dir, info.Branch) is null)
            return new OpResult(false, "",
                $"分支 {info.Branch} 没有上游，无法直接 pull。先 git-cn push 建立对应关系。\n");
        var r = await GitRunner.RunAsync(
            ["-C", dir, "pull", "--rebase", "--autostash"], dir).ConfigureAwait(false);
        return OpResult.From(r);
    }

    public enum UndoMode { KeepStaged, KeepUnstaged, Discard, ReverseCommit }

    public static async Task<OpResult> UndoLastCommitAsync(string dir, UndoMode mode)
    {
        var head = GitRunner.Out(["-C", dir, "rev-parse", "HEAD"], dir);
        if (head.Length == 0) return new OpResult(false, "", "这个仓库还没有任何提交。");
        var total = GitRunner.Out(["-C", dir, "rev-list", "--count", "HEAD"], dir);
        var onlyOne = total == "1";

        List<string> args = mode switch
        {
            UndoMode.KeepStaged => onlyOne ? ["update-ref", "-d", "HEAD"] : ["reset", "--soft", "HEAD^"],
            UndoMode.KeepUnstaged => onlyOne ? ["update-ref", "-d", "HEAD"] : ["reset", "--mixed", "HEAD^"],
            UndoMode.Discard => ["reset", "--hard", onlyOne ? "HEAD" : "HEAD^"],
            _ => ["revert", "--no-edit", "HEAD"],
        };
        var r = await GitRunner.RunAsync(["-C", dir, .. args], dir).ConfigureAwait(false);
        if (!r.Ok) return OpResult.From(r);
        if (mode == UndoMode.KeepUnstaged && onlyOne)
            await GitRunner.RunAsync(["-C", dir, "rm", "-r", "--cached", "-q", "--", "."], dir)
                .ConfigureAwait(false);
        var extra = mode == UndoMode.Discard
            ? "注意：改动也一起丢了。\n"
            : "";
        return new OpResult(true, $"已撤销上次提交。{extra}万一要回去：git reset --hard {head}\n" + r.Output);
    }

    public static async Task<OpResult> ResetAsync(string dir, string mode, int count)
    {
        if (count < 1) return new OpResult(false, "", "回退个数必须是正整数。");
        if (mode is not ("soft" or "mixed" or "hard"))
            return new OpResult(false, "", "模式只能 soft / mixed / hard 三选一。");
        var r = await GitRunner.RunAsync(["-C", dir, "reset", "--" + mode, $"HEAD~{count}"], dir)
            .ConfigureAwait(false);
        var op = OpResult.From(r);
        return op with { Guide = op.Ok ? ModeTable : op.Guide };
    }

    public const string ModeTable = """
        soft   提交记录回退、暂存区保留、文件内容保留
        mixed  提交记录回退、暂存区清空、文件内容保留   （不带参数时就是它）
        hard   提交记录回退、暂存区清空、文件内容丢弃   （唯一会丢代码的一种）
        """;

    public static async Task<RouteKind> ClassifyAsync(string dir, string target) => target switch
    {
        "." or "--" or "*" => RouteKind.File,
        _ => (Repo.BranchExists(dir, target), await IsTrackedAsync(dir, target)) switch
        {
            (true, true) => RouteKind.Ambiguous,
            (true, false) => RouteKind.Branch,
            (false, true) => RouteKind.File,
            _ => RouteKind.Unknown,
        },
    };

    private static Task<bool> IsTrackedAsync(string dir, string target) =>
        Task.Run(() => Repo.IsTrackedPath(dir, target));

    public static IReadOnlyList<string> ConflictFiles(RepoInfo info) =>
        info.Conflicts.Select(c => c.Path).ToList();

    public enum Side { Mine, Theirs }

    /// <summary>变基过程中 ours/theirs 的方向和合并时正好相反：变基时 ours 是“你在往上面重放的
    /// 目标分支”，你自己的提交才是 theirs。这是 Git 最反人类的一处。</summary>
    public static async Task<OpResult> ResolveConflictAsync(string dir, string file, Side side,
        bool inRebase)
    {
        var flag = side switch
        {
            Side.Mine => "--ours",
            _ => "--theirs",
        };
        if (inRebase) flag = flag == "--ours" ? "--theirs" : "--ours";
        var pick = await GitRunner.RunAsync(["-C", dir, "checkout", flag, "--", file], dir)
            .ConfigureAwait(false);
        if (!pick.Ok) return OpResult.From(pick);
        var add = await GitRunner.RunAsync(["-C", dir, "add", "--", file], dir).ConfigureAwait(false);
        return OpResult.From(add);
    }

    public static async Task<bool> IsRebasingAsync(string dir)
    {
        var r = await GitRunner.RunAsync(["-C", dir, "rev-parse", "--git-path", "rebase-merge"], dir)
            .ConfigureAwait(false);
        var p = r.Trimmed;
        return p.Length > 0 && Directory.Exists(Path.IsPathRooted(p) ? p : Path.Combine(dir, p));
    }

    public static async Task<OpResult> ContinueAfterConflictAsync(string dir)
    {
        if (await IsRebasingAsync(dir).ConfigureAwait(false))
            return OpResult.From(await GitRunner.RunAsync(["-C", dir, "rebase", "--continue"], dir)
                .ConfigureAwait(false));
        return OpResult.From(await GitRunner.RunAsync(["-C", dir, "commit", "--no-edit"], dir)
            .ConfigureAwait(false));
    }

    public static async Task<string> LogAsync(string dir, int count = 40)
    {
        var r = await GitRunner.RunAsync(["-C", dir, "log", "--graph", "--date=short",
            "--pretty=format:%h %ad %an %s%d", "--decorate", $"-{count}"], dir).ConfigureAwait(false);
        return r.Output;
    }

    public static Task<GitResult> InitAsync(string path) =>
        GitRunner.RunAsync(["init", "--initial-branch=main", "--", path]);

    public const string GitignoreCn = """
        # 版本控制不该带上机器专属和个人私有的东西
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

        """;

    public const string GitattributesCn =
        "# 统一按 LF 存仓库，避免 Windows / Linux 换行符打架\n* text=auto eol=lf\n";
}
