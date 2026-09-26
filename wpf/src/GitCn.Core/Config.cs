using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitCn.Core;

public sealed record ConfigItem(string Key, string Value, string Why);
public sealed record ConfigChange(ConfigItem Item, string Previous, bool Written, string? Reason = null);

public static class ConfigFixer
{
    public static readonly ConfigItem[] Desired =
    [
        new("core.quotepath", "false", "中文文件名不再显示成 \\346\\226\\207 八进制乱码"),
        new("i18n.commitEncoding", "utf-8", "中文提交信息按 UTF-8 存，别人不会看到乱码"),
        new("i18n.logOutputEncoding", "utf-8", "读日志也按 UTF-8 输出"),
        new("locale.default", "zh_CN", "启用中文界面（上游 git 会忽略这一项，无害）"),
        new("init.defaultBranch", "main", "新仓库默认分支叫 main，不用 master"),
        new("pull.rebase", "true", "pull 用变基：历史一条直线，没有 Merge branch 噪音"),
        new("rebase.autoStash", "true", "变基前自动收起未提交改动，结束后自动放回"),
        new("push.autoSetupRemote", "true", "新分支第一次 git push 不再报 no upstream branch"),
        new("push.default", "current", "只推当前分支，不会误推别的分支"),
        new("push.followTags", "true", "推送时自动带上对应标签，发版少一步"),
        new("help.autocorrect", "0", "拼错只提示相近命令、绝不自动执行（实测 10 会真的把猜出来的命令跑掉）"),
        new("rerere.enabled", "true", "同样的冲突解决一次就记住，变基重来时不用重做"),
        new("merge.conflictstyle", "zdiff3", "冲突块多显示基准内容，看得懂谁改了什么"),
        new("diff.algorithm", "histogram", "改动块显示更接近人眼看到的最小集合"),
        new("diff.indentHeuristic", "true", "缩进变化不被误判成代码改动"),
        new("color.ui", "auto", "彩色输出（重定向时自动关）"),
        new("status.relativePaths", "true", "status 显示相对路径，短而好读"),
        new("core.autocrlf", "input", "提交时统一存 LF，检出时不改成 CRLF，跨平台不打架"),
        new("core.longpaths", "true", "Windows 突破 260 字符路径限制"),
        new("commit.gpgsign", "false", "先关掉提交签名，避免 GPG 未配置导致提交不了"),
    ];

    private static readonly ConfigItem WindowsEditor =
        new("core.editor", "notepad -w", "调编辑器时用记事本，-w 保证等你保存关闭");

    public static IEnumerable<ConfigItem> DesiredForThisOs()
    {
        foreach (var d in Desired) yield return d;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) yield return WindowsEditor;
    }

    public static string BackupPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".git-cn-backup.json");

    public static async Task<List<ConfigChange>> ApplyAsync(bool dryRun)
    {
        var changes = new List<ConfigChange>();
        var backup = LoadBackup();
        foreach (var item in DesiredForThisOs())
        {
            var current = GitRunner.Out(["config", "--global", "--get", item.Key]);
            if (current == item.Value) continue;
            if (!await Repo.ValueIsAcceptableAsync(item.Key, item.Value).ConfigureAwait(false))
            {
                changes.Add(new ConfigChange(item, current, false, "git 不接受这个值，写进去会让所有 git 命令报错"));
                continue;
            }
            if (!dryRun)
            {
                await GitRunner.RunAsync(["config", "--global", item.Key, item.Value]).ConfigureAwait(false);
                backup[item.Key] = current.Length == 0 ? null : current;
            }
            changes.Add(new ConfigChange(item, current, !dryRun));
        }
        if (!dryRun && changes.Any(c => c.Written)) SaveBackup(backup);
        return changes;
    }

    public static async Task<int> RevertAsync()
    {
        if (!File.Exists(BackupPath)) return 0;
        var backup = LoadBackup();
        var n = 0;
        foreach (var (key, value) in backup)
        {
            if (value is null)
                await GitRunner.RunAsync(["config", "--global", "--unset", key]).ConfigureAwait(false);
            else
                await GitRunner.RunAsync(["config", "--global", key, value]).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    private static Dictionary<string, string?> LoadBackup()
    {
        if (!File.Exists(BackupPath)) return new();
        try
        {
            var json = File.ReadAllText(BackupPath);
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static void SaveBackup(Dictionary<string, string?> backup) =>
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup,
            new JsonSerializerOptions { WriteIndented = true }));
}

public sealed record Check(string Label, bool Ok, string Detail);

public static class Doctor
{
    public static async Task<List<Check>> RunAsync(string dir)
    {
        var list = new List<Check>();
        var version = await GitRunner.RunAsync(["--version"]).ConfigureAwait(false);
        if (!version.Ok)
        {
            list.Add(new Check("git 可用", false, "没找到 git，先 winget install Git.Git"));
            return list;
        }
        var v = version.Trimmed;
        list.Add(new Check("git 版本", true, v));
        list.Add(new Check("汉化版 git", v.Contains(".cn"),
            v.Contains(".cn") ? "中文提示由 git 本体提供" : "用的是上游 git，中文提示由 git-cn 包装层提供"));

        foreach (var key in new[] { "core.quotepath", "i18n.commitEncoding", "pull.rebase",
                     "push.autoSetupRemote", "rebase.autoStash", "help.autocorrect", "rerere.enabled",
                     "core.autocrlf", "core.longpaths", "init.defaultBranch" })
        {
            var want = ConfigFixer.Desired.First(d => d.Key == key).Value;
            var cur = GitRunner.Out(["config", "--global", "--get", key]);
            list.Add(new Check(key, cur == want, cur.Length == 0 ? "未设置（建议 " + want + "）" : cur));
        }

        if (!Console.IsOutputRedirected)
        {
            var enc = Console.OutputEncoding?.WebName ?? "";
            list.Add(new Check("终端编码", enc.Contains("utf", StringComparison.OrdinalIgnoreCase), enc));
        }
        var locale = Environment.GetEnvironmentVariable("LC_ALL")
                     ?? Environment.GetEnvironmentVariable("LANG") ?? "";
        list.Add(new Check("locale 指定 UTF-8",
            Regex.IsMatch(locale, "utf-?8", RegexOptions.IgnoreCase), locale.Length == 0 ? "未设置" : locale));

        var info = await Repo.ReadAsync(dir).ConfigureAwait(false);
        list.Add(new Check("当前在 Git 仓库内", info.IsRepo, info.IsRepo ? info.Root : "不在仓库"));
        if (info.IsRepo)
            list.Add(new Check("分支", !info.Detached, info.UpstreamLine));
        return list;
    }

    public static int MissingCount(IEnumerable<Check> checks) =>
        checks.Count(c => !c.Ok && c.Label != "汉化版 git");
}
