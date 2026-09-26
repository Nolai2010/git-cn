using GitCn.Core;
using Xunit;

namespace GitCn.Core.Tests;

public class ErrorGuideTests
{
    [Fact]
    public void 新分支没有上游时给出长期解法()
    {
        var hits = ErrorGuide.Match(
            "error: The current branch feat has no upstream branch.\n" +
            "To push the current branch and set the remote as upstream, use\n");
        var all = string.Join("\n", hits.Select(h => h.Why + string.Join("\n", h.Fixes)));
        Assert.Contains("第一次推送", all);
        Assert.Contains("push.autoSetupRemote", all);
    }

    [Fact]
    public void 被拒绝的推送不会教用户硬推()
    {
        var all = string.Join("\n", ErrorGuide.Match(
            "! [rejected] main -> main (fetch first)\nerror: failed to push some refs")
            .SelectMany(h => new[] { h.Why }.Concat(h.Fixes)));
        Assert.Contains("pull --rebase", all);
        Assert.Contains("不要用 git push -f", all);
    }

    [Fact]
    public void 拼错命令给出相近命令()
    {
        var hits = ErrorGuide.Match("git: 'rest' is not a git command. See 'git --help'.");
        Assert.NotEmpty(hits);
        Assert.Contains(hits[0].Fixes, f => f.Contains("reset") || f.Contains("restore"));
    }

    [Fact]
    public void Windows长路径限制被认出来()
    {
        Assert.Contains(ErrorGuide.Match("error: unable to create file: Filename too long"),
            h => h.Fixes.Any(f => f.Contains("longpaths")));
    }

    [Fact]
    public void 不认识的报错不编造解释()
    {
        Assert.Empty(ErrorGuide.Match("something completely unrelated happened"));
    }
}

public class DangerGateTests
{
    [Theory]
    [InlineData("push --force")]
    [InlineData("push -f")]
    [InlineData("reset --hard HEAD~1")]
    [InlineData("clean -fd")]
    [InlineData("restore 说明.md")]
    [InlineData("checkout .")]
    [InlineData("branch -D wip")]
    [InlineData("stash drop")]
    public void 会丢数据的命令必须被拦(string line)
    {
        Assert.NotNull(DangerGate.Assess(line.Split(' ')));
    }

    [Theory]
    [InlineData("restore --staged a.txt")]
    [InlineData("checkout -b feat")]
    [InlineData("switch main")]
    [InlineData("clean -nd")]
    [InlineData("status")]
    [InlineData("push")]
    public void 可逆或无害的命令不该被拦(string line)
    {
        Assert.Null(DangerGate.Assess(line.Split(' ')));
    }

    [Fact]
    public void 丢数据的命令要提供自动备份()
    {
        var v = DangerGate.Assess(["reset", "--hard", "HEAD^"]);
        Assert.NotNull(v);
        Assert.True(v!.OfferBackup);
        Assert.False(string.IsNullOrWhiteSpace(v.Harm));
        Assert.False(string.IsNullOrWhiteSpace(v.Safer));
    }
}

public class ConfigTableTests
{
    [Fact]
    public void 默认值表里的键都是合法配置项名()
    {
        foreach (var item in ConfigFixer.Desired)
        {
            Assert.Matches(@"^[a-zA-Z][\w-]*\.[\w-]+$", item.Key);
            Assert.False(string.IsNullOrWhiteSpace(item.Value));
            Assert.False(string.IsNullOrWhiteSpace(item.Why));
        }
    }

    [Fact]
    public void 拼错自动纠正必须钉在只提示不执行()
    {
        // help.autocorrect=10 会真的执行猜出来的命令，实测过；这里防回归
        var item = Assert.Single(ConfigFixer.Desired, d => d.Key == "help.autocorrect");
        Assert.Equal("0", item.Value);
    }

    [Fact]
    public void diff算法必须是git认得的值()
    {
        var item = Assert.Single(ConfigFixer.Desired, d => d.Key == "diff.algorithm");
        Assert.Contains(item.Value, new[] { "myers", "minimal", "patience", "histogram", "zc", "none" });
    }

    [Fact]
    public void 中文文件名和上游跟踪这两项必须在表里()
    {
        Assert.Contains(ConfigFixer.Desired, d => d.Key == "core.quotepath" && d.Value == "false");
        Assert.Contains(ConfigFixer.Desired, d => d.Key == "push.autoSetupRemote" && d.Value == "true");
    }
}

public class TextTests
{
    [Fact]
    public void reset三种模式的说明确实区分了三层()
    {
        Assert.Contains("文件内容丢弃", Ops.ModeTable);
        Assert.Contains("暂存区清空", Ops.ModeTable);
        Assert.Contains("唯一会丢代码", Ops.ModeTable);
    }

    [Fact]
    public void 生成的gitignore覆盖了密钥和本地依赖()
    {
        Assert.Contains(".env", Ops.GitignoreCn);
        Assert.Contains("node_modules/", Ops.GitignoreCn);
        Assert.Contains("__pycache__/", Ops.GitignoreCn);
    }
}
