using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitCn.Core;

public sealed record GitResult(int Code, string Output)
{
    public bool Ok => Code == 0;

    public string Trimmed => Output.Trim();

    public IEnumerable<string> Lines
    {
        get
        {
            if (Output.Length == 0) yield break;
            foreach (var l in Output.Split('\n'))
            {
                var t = l.TrimEnd('\r');
                if (t.Length > 0) yield return t;
            }
        }
    }
}

/// <summary>唯一的 git 调用出口：把 stdout 与 stderr 合成一条时间线，
/// 这样报错翻译拿到的是用户实际看到的完整输出，而不是被拆开的两半。</summary>
public static class GitRunner
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var r = await RunAsync(["--version"]);
            return r.Ok;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Task<GitResult> RunAsync(IEnumerable<string> args, string? workDir = null,
        IProgress<string>? onLine = null, CancellationToken ct = default)
        => RunAsync("git", args, workDir, onLine, ct);

    public static async Task<GitResult> RunAsync(string exe, IEnumerable<string> args,
        string? workDir = null, IProgress<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sb = new StringBuilder();
        void Append(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (sb)
            {
                sb.Append(e.Data).Append('\n');
            }
            onLine?.Report(e.Data);
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += Append;
        p.ErrorDataReceived += Append;

        try
        {
            if (!p.Start())
                return new GitResult(-1, $"无法启动 {exe}");
        }
        catch (Exception ex)
        {
            return new GitResult(-1, $"找不到 {exe}：{ex.Message}\n" +
                "先装 Git：winget install Git.Git，装完重开本程序");
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (ct.CanBeCanceled)
        {
            using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } });
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        p.WaitForExit();  // 让异步读取器把残余输出排空，别只信 WaitForExitAsync

        lock (sb)
        {
            return new GitResult(p.ExitCode, sb.ToString());
        }
    }

    public static GitResult Run(IEnumerable<string> args, string? workDir = null)
        => RunAsync(args, workDir).GetAwaiter().GetResult();

    public static GitResult Run(params string[] args) => Run((IEnumerable<string>)args);

    public static string Out(IEnumerable<string> args, string? workDir = null)
    {
        var r = Run(args, workDir);
        return r.Ok ? r.Trimmed : "";
    }

    public static string Out(params string[] args) => Out((IEnumerable<string>)args, null);

    /// <summary>写入前先试一遍，避免把 git 不认的值写进配置里砖掉用户环境。</summary>
    public static async Task<bool> ValueIsAcceptableAsync(string key, string value)
    {
        var r = await RunAsync(["-c", $"{key}={value}", "rev-parse", "--git-dir"]);
        if (r.Ok) return true;
        var t = r.Output;
        return !(t.Contains("config", StringComparison.OrdinalIgnoreCase)
                 || t.Contains("unknown value", StringComparison.OrdinalIgnoreCase)
                 || t.Contains("bad ", StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(GitResult r) =>
        r.Ok ? $"成功（退出码 0）" : $"失败（退出码 {r.Code.ToString(CultureInfo.InvariantCulture)})";
}
