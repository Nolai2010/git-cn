using System.Text;
using GitCn.Core;

if (!await GitRunner.IsAvailableAsync())
{
    Console.Error.WriteLine("[git-cn] 没找到 git。先装：winget install Git.Git，然后重开本程序");
    return 127;
}

var argv = args.ToList();
var assumeYes = argv.RemoveAll(a => a == "--yes") > 0;
var dir = Environment.CurrentDirectory;
{
    var i = argv.IndexOf("--dir");
    if (i >= 0 && i + 1 < argv.Count)
    {
        dir = argv[i + 1];
        argv.RemoveAt(i + 1);
        argv.RemoveAt(i);
    }
    else if (i >= 0)
    {
        argv.RemoveAt(i);
    }
}
var sub = argv.Count > 0 ? argv[0] : "menu";
var rest = argv.Skip(1).ToList();

return sub switch
{
    "status" => await StatusAsync(),
    "doctor" => await DoctorAsync(),
    "fix" => await FixAsync(rest),
    "_probe" => await ProbeAsync(rest),
    "commit" => await CommitCmdAsync(rest),
    "push" => Line(await Ops.PushAsync(dir, assumeYes)),
    "pull" => Line(await Ops.PullAsync(dir)),
    "log" => await LogAsync(rest),
    "undo" => await UndoAsync(rest),
    "reset" => await ResetAsync(rest),
    "conflicts" => await ConflictsAsync(),
    "resolve" => await ResolveAsync(rest),
    "classify" => await ClassifyAsync(rest),
    "danger" => await DangerAsync(rest),
    "guide" => await GuideAsync(rest),
    "init" => await InitAsync(rest),
    "run" => await RunAsync(rest),
    "help" or "--help" or "-h" => Help(),
    "menu" => Help(),
    _ => await RunAsync([sub, .. rest]),
};

async Task<int> StatusAsync()
{
    var info = await Repo.ReadAsync(dir);
    if (!info.IsRepo)
    {
        Console.WriteLine("当前目录不在 Git 仓库里。git-cn init <路径> 可以新建一个。");
        return 128;
    }
    Console.WriteLine($"仓库  {info.Root}");
    Console.WriteLine($"分支  {info.UpstreamLine}");
    Section("有冲突，必须先解决", info.Conflicts);
    Section("已暂存（这次提交会带上）", info.Staged);
    Section("改了但没 git add（这次提交不会带上！）", info.Unstaged);
    Section("未跟踪（git 完全不管）", info.Untracked);
    if (info.Entries.Count == 0) Console.WriteLine("\n工作区干净，没有待处理的东西。");
    return 0;
}

void Section(string title, IReadOnlyList<StatusEntry> items)
{
    if (items.Count == 0) return;
    Console.WriteLine($"\n{title}  {items.Count} 个");
    foreach (var e in items.Take(30)) Console.WriteLine($"  {e.Path}");
    if (items.Count > 30) Console.WriteLine($"  ... 另有 {items.Count - 30} 个");
}

async Task<int> DoctorAsync()
{
    var checks = await Doctor.RunAsync(dir);
    Console.WriteLine("== 体检 ==");
    foreach (var c in checks)
        Console.WriteLine($"  {(c.Ok ? "OK  " : "缺失")} {c.Label,-22} {c.Detail}");
    var miss = Doctor.MissingCount(checks);
    Console.WriteLine(miss == 0 ? "\n配置全部到位。" : $"\n有 {miss} 项建议修复：git-cn fix");
    return miss == 0 ? 0 : 1;
}

async Task<int> FixAsync(List<string> rest)
{
    if (rest.Contains("--revert"))
    {
        var n = await ConfigFixer.RevertAsync();
        Console.WriteLine(n > 0 ? $"已恢复 {n} 项原值。" : "没有备份文件，无法回退。");
        return 0;
    }
    var dry = rest.Contains("--dry");
    var changes = await ConfigFixer.ApplyAsync(dry);
    if (changes.Count == 0)
    {
        Console.WriteLine("全部配置已到位，无需改动。");
        return 0;
    }
    foreach (var c in changes)
    {
        if (!c.Written && c.Reason is not null)
        {
            Console.WriteLine($"  [跳过] {c.Item.Key}={c.Item.Value}  原因：{c.Reason}");
            continue;
        }
        var prev = c.Previous.Length > 0 ? $"（原: {c.Previous}）" : "";
        Console.WriteLine($"  [{(dry ? "试算" : "写入")}] {c.Item.Key} = {c.Item.Value} {prev}");
        Console.WriteLine($"          {c.Item.Why}");
    }
    Console.WriteLine(dry ? "\n试算模式，未做任何修改。" : $"\n已写入。回退：git-cn fix --revert");
    return 0;
}

async Task<int> ProbeAsync(List<string> rest)
{
    var bad = false;
    foreach (var item in rest)
    {
        var i = item.IndexOf('=');
        if (i <= 0)
        {
            Console.WriteLine("用法：git-cn _probe key=value ...");
            return 2;
        }
        var ok = await Repo.ValueIsAcceptableAsync(item[..i], item[(i + 1)..]);
        bad |= !ok;
        Console.WriteLine($"{item}  ->  {(ok ? "可接受" : "被 git 拒绝")}");
    }
    return bad ? 1 : 0;
}

async Task<int> CommitCmdAsync(List<string> rest)
{
    var title = OptionValue(rest, "--title") ?? "";
    var body = OptionValue(rest, "--body") ?? "";
    var files = rest.SkipWhile(a => a != "--").Skip(1).ToList();
    var info = await Repo.ReadAsync(dir);
    if (!info.IsRepo) { Console.WriteLine("不在仓库里。"); return 128; }
    if (files.Count == 0)
    {
        var all = info.Staged.Select(e => e.Path)
            .Concat(info.Unstaged.Select(e => e.Path))
            .Concat(info.Untracked.Select(e => e.Path))
            .Distinct().ToList();
        if (all.Count == 0) { Console.WriteLine("没有待提交的改动。"); return 0; }
        var st = await Ops.StageAsync(dir, all);
        Console.Write(st.Output);
    }
    else
    {
        var st = await Ops.StageAsync(dir, files);
        if (!st.Ok) return Line(st);
        Console.Write(st.Output);
    }
    return Line(await Ops.CommitAsync(dir, title, body));
}

async Task<int> LogAsync(List<string> rest)
{
    var n = rest.Count > 0 && int.TryParse(rest[0], out var v) ? v : 40;
    Console.Write(await Ops.LogAsync(dir, n));
    Console.WriteLine();
    return 0;
}

async Task<int> UndoAsync(List<string> rest)
{
    var mode = OptionValue(rest, "--mode") ?? "keep-staged";
    Ops.UndoMode m = mode switch
    {
        "keep-staged" => Ops.UndoMode.KeepStaged,
        "keep-unstaged" => Ops.UndoMode.KeepUnstaged,
        "discard" => Ops.UndoMode.Discard,
        "reverse" => Ops.UndoMode.ReverseCommit,
        _ => Ops.UndoMode.KeepStaged,
    };
    if (m == Ops.UndoMode.Discard && !assumeYes)
    {
        var v = DangerGate.Assess(["reset", "--hard", "HEAD^"]);
        Console.WriteLine(Ops.VerdictText(v!));
        Console.WriteLine("（加 --yes 才真的执行）");
        return 130;
    }
    return Line(await Ops.UndoLastCommitAsync(dir, m));
}

async Task<int> ResetAsync(List<string> rest)
{
    var mode = OptionValue(rest, "--mode") ?? "mixed";
    var count = int.TryParse(OptionValue(rest, "--count"), out var c) ? c : 1;
    if (mode == "hard" && !assumeYes)
    {
        Console.WriteLine(Ops.VerdictText(DangerGate.Assess(["reset", "--hard"])!));
        Console.WriteLine("（加 --yes 才真的执行）");
        return 130;
    }
    Console.WriteLine(Ops.ModeTable);
    return Line(await Ops.ResetAsync(dir, mode, count));
}

async Task<int> ConflictsAsync()
{
    var info = await Repo.ReadAsync(dir);
    var files = Ops.ConflictFiles(info);
    if (files.Count == 0) { Console.WriteLine("当前没有冲突文件。"); return 0; }
    var rebase = await Ops.IsRebasingAsync(dir);
    Console.WriteLine($"{(rebase ? "变基中" : "合并中")}，{files.Count} 个文件有冲突");
    if (rebase)
    {
        Console.WriteLine("注意方向是反的：变基时 --ours 指“你在往上面重放的目标分支”，");
        Console.WriteLine("      你自己的提交是 --theirs。这和 merge 正好相反。");
    }
    foreach (var f in files) Console.WriteLine("  " + f);
    return 1;
}

async Task<int> ResolveAsync(List<string> rest)
{
    if (rest.Count < 2)
    {
        Console.WriteLine("用法：git-cn resolve <文件> --side mine|theirs");
        return 2;
    }
    var side = OptionValue(rest, "--side") == "theirs" ? Ops.Side.Theirs : Ops.Side.Mine;
    return Line(await Ops.ResolveConflictAsync(dir, rest[0], side, await Ops.IsRebasingAsync(dir)));
}

async Task<int> ClassifyAsync(List<string> rest)
{
    if (rest.Count == 0) return 2;
    Console.WriteLine(await Ops.ClassifyAsync(dir, rest[0]));
    return 0;
}

async Task<int> DangerAsync(List<string> rest)
{
    var v = DangerGate.Assess(rest);
    Console.WriteLine(v is null ? "不拦：这个命令不会丢数据" : Ops.VerdictText(v));
    return v is null ? 0 : 1;
}

async Task<int> GuideAsync(List<string> rest)
{
    var text = rest.Count > 0 ? string.Join(' ', rest) : await Console.In.ReadToEndAsync();
    var g = ErrorGuide.Summarize(text);
    Console.WriteLine(g.Length > 0 ? g : "（这条报错不在翻译表里）");
    return 0;
}

async Task<int> InitAsync(List<string> rest)
{
    var path = rest.Count > 0 ? rest[0] : Environment.CurrentDirectory;
    Directory.CreateDirectory(path);
    var r = await Ops.InitAsync(path);
    if (!r.Ok)
    {
        Console.Write(r.Output);
        return r.Code;
    }
    var gi = Path.Combine(path, ".gitignore");
    if (!File.Exists(gi)) await File.WriteAllTextAsync(gi, Ops.GitignoreCn);
    var ga = Path.Combine(path, ".gitattributes");
    if (!File.Exists(ga)) await File.WriteAllTextAsync(ga, Ops.GitattributesCn);
    Console.WriteLine($"仓库已建在 {Path.GetFullPath(path)}（默认分支 main，已写好中文注释版 .gitignore）");
    return 0;
}

async Task<int> RunAsync(List<string> rest)
{
    if (rest.Count > 0 && rest[0] == "git") rest.RemoveAt(0);
    var v = DangerGate.Assess(rest);
    if (v is not null && !assumeYes)
    {
        Console.WriteLine(Ops.VerdictText(v));
        Console.WriteLine("（确认要执行请加 --yes，或在交互界面里选“仍要执行”）");
        return 130;
    }
    return Line(await Ops.RunGatedAsync(dir, rest, assumeYes: true));
}

int Line(OpResult r)
{
    var sb = new StringBuilder();
    if (r.Output.Length > 0) sb.Append(r.Output);
    if (r.Guide.Length > 0) sb.Append(r.Guide);
    Console.Write(sb.ToString());
    if (sb.Length > 0 && sb[^1] != '\n') Console.WriteLine();
    return r.Ok ? 0 : 1;
}

int Help()
{
    Console.WriteLine("""
        git-cn 命令行版（图形版是 git-cn-gui.exe）
          git-cn status          中文解读当前状态
          git-cn commit          暂存并提交（--title 说明 [--body 正文]）
          git-cn push / pull     带报错翻译的推送与拉取
          git-cn undo            撤销上次提交（--mode keep-staged|keep-unstaged|discard|reverse）
          git-cn reset           先讲清三层差别再执行（--mode soft|mixed|hard --count N）
          git-cn conflicts       列出冲突文件，含变基时 ours/theirs 反向提醒
          git-cn doctor          体检
          git-cn fix             写入符合国内习惯的默认值（--dry 试算 / --revert 回退）
          git-cn run <git 命令>  带危险闸与报错翻译地执行原始 git 命令
        """);
    return 0;
}

static string? OptionValue(List<string> argv, string name)
{
    var i = argv.IndexOf(name);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}
