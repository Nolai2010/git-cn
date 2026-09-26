using System.Text.RegularExpressions;

namespace GitCn.Core;

public sealed record StatusEntry(char X, char Y, string Path)
{
    public bool Staged => X is not (' ' or '?');
    public bool Unstaged => Y is not (' ' or '?');
    public bool Untracked => X == '?' || Y == '?';

    public bool Conflict => X == 'U' || Y == 'U'
        || (X, Y) is ('A', 'A') or ('D', 'D') or ('D', 'A') or ('A', 'D');

    public string StateLabel => Conflict ? "有冲突"
        : Untracked ? "未跟踪"
        : Staged && Unstaged ? "已暂存，之后又改了"
        : Staged ? "已暂存"
        : "改了但没 git add";
}

public sealed class RepoInfo
{
    public string Root { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool Detached { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public IReadOnlyList<StatusEntry> Entries { get; init; } = Array.Empty<StatusEntry>();

    public bool IsRepo => Root.Length > 0;
    public IReadOnlyList<StatusEntry> Conflicts => Entries.Where(e => e.Conflict).ToList();
    public IReadOnlyList<StatusEntry> Staged => Entries.Where(e => e.Staged && !e.Conflict).ToList();
    public IReadOnlyList<StatusEntry> Unstaged => Entries.Where(e => e.Unstaged && !e.Conflict).ToList();
    public IReadOnlyList<StatusEntry> Untracked => Entries.Where(e => e.Untracked).ToList();

    public string UpstreamLine =>
        Upstream is null ? $"{Branch}（还没有上游分支）"
        : Detached ? "游离 HEAD"
        : $"{Branch} → {Upstream}" +
          (Ahead > 0 || Behind > 0 ? $"  [领先 {Ahead}、落后 {Behind}]" : "  [已同步]");
}

public static class Repo
{
    /// <summary>用 -z 读：中文路径在默认 porcelain 输出里是八进制转义串，
    /// 那些转义串再回传给 git add 必然匹配不到文件。</summary>
    public static async Task<RepoInfo> ReadAsync(string dir, CancellationToken ct = default)
    {
        var root = GitRunner.Out(["rev-parse", "--show-toplevel"], dir);
        if (root.Length == 0)
            return new RepoInfo { Root = "", Branch = "" };

        var (_, raw) = await GitRunner.RunAsync(
            ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "-b"], root, ct: ct)
            .ConfigureAwait(false);

        var tokens = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var header = tokens.Length > 0 && tokens[0].StartsWith("##", StringComparison.Ordinal)
            ? tokens[0] : "";
        var entries = new List<StatusEntry>();
        for (var i = header.Length > 0 ? 1 : 0; i < tokens.Length; i++)
        {
            var rec = tokens[i];
            if (rec.Length < 4) continue;
            var x = rec[0];
            var y = rec[1];
            var path = rec[3..];
            if (x is 'R' or 'C' || y is 'R' or 'C')
                i++;    // rename/copy 记录后面还跟着原路径
            entries.Add(new StatusEntry(x, y, path));
        }

        var branch = header.Length > 2 ? header[2..] : "";
        var detached = branch.Contains("(no branch", StringComparison.OrdinalIgnoreCase)
                       || branch == "HEAD";
        var ahead = 0;
        var behind = 0;
        var mm = Regex.Match(branch, @"\[ahead (\d+)(?:, behind (\d+))?\]");
        if (mm.Success)
        {
            ahead = int.Parse(mm.Groups[1].Value);
            behind = mm.Groups[2].Success ? int.Parse(mm.Groups[2].Value) : 0;
        }
        var dotdot = branch.IndexOf("...", StringComparison.Ordinal);
        var name = dotdot >= 0 ? branch[..dotdot] : branch;
        var upstream = dotdot >= 0 ? branch[(dotdot + 3)..].Split(' ')[0] : null;
        if (upstream is not null)
        {
            var probe = GitRunner.Out(["rev-parse", "--abbrev-ref", "--quiet", "HEAD@{upstream}"], root);
            if (probe.Length == 0) upstream = null;
        }

        return new RepoInfo
        {
            Root = root.Trim(),
            Branch = detached ? "游离 HEAD" : name.Trim(),
            Detached = detached,
            Upstream = upstream?.Trim(),
            Ahead = ahead,
            Behind = behind,
            Entries = entries,
        };
    }

    public static string CurrentBranch(string dir) =>
        GitRunner.Out(["rev-parse", "--abbrev-ref", "--quiet", "HEAD"], dir);

    public static string? UpstreamOf(string dir, string branch)
    {
        var r = GitRunner.Run(["rev-parse", "--abbrev-ref", "--quiet", $"{branch}@{{upstream}}"], dir);
        return r.Ok && r.Trimmed.Length > 0 ? r.Trimmed : null;
    }

    public static bool IsTrackedPath(string dir, string path) =>
        GitRunner.Run(["rev-parse", "--verify", "--quiet", "HEAD:" + path.Replace('\\', '/')], dir).Ok;

    public static bool BranchExists(string dir, string name) =>
        GitRunner.Run(["rev-parse", "--verify", "--quiet", "refs/heads/" + name], dir).Ok;

    public static string? DefaultRemote(string dir)
    {
        var remotes = GitRunner.Out(["remote"], dir);
        var first = remotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (first.Length == 0) return null;
        return Array.Find(first, r => r == "origin") ?? first[0];
    }

    /// <summary>试值台。git 只在会真正解析配置值的命令里报“unknown value for config”，
    /// rev-parse / config --get / --version 都不会解析值，拿它们当护栏等于没护栏。</summary>
    private static string? _probeDir;
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    public static async Task<string?> ProbeRepoAsync()
    {
        if (_probeDir is not null) return _probeDir;
        await ProbeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_probeDir is not null) return _probeDir;
            var d = Path.Combine(Path.GetTempPath(), "git-cn-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            var init = await GitRunner.RunAsync(["init", "-q", "--", d]).ConfigureAwait(false);
            if (!init.Ok) return null;
            await GitRunner.RunAsync(
                ["-c", "user.email=probe@local", "-c", "user.name=probe",
                 "commit", "-q", "--allow-empty", "-m", "probe"], d).ConfigureAwait(false);
            _probeDir = d;
            return d;
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    public static async Task<bool> ValueIsAcceptableAsync(string key, string value)
    {
        var d = await ProbeRepoAsync().ConfigureAwait(false);
        if (d is null) return true;
        var r = await GitRunner.RunAsync(["-c", $"{key}={value}", "diff", "--quiet"], d).ConfigureAwait(false);
        return !Regex.IsMatch(r.Output,
            "unknown value for config|bad config|invalid .*config", RegexOptions.IgnoreCase);
    }
}
