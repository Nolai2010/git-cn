using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitCn.Core;
using Microsoft.Win32;

namespace GitCn.App;

public sealed record FileItem(string Path, string State)
{
    public string Display => $"[{State}]  {Path}";
}

public partial class MainWindow : Window
{
    private string _dir;
    private RepoInfo _info = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(string dir)
    {
        _dir = dir;
        InitializeComponent();
        foreach (var l in new[] { ConflictList, StagedList, UnstagedList, UntrackedList })
            l.DisplayMemberPath = nameof(FileItem.Display);
        Loaded += async (_, _) => await RefreshAsync();
    }

    public Task ReadyAsync() => _ready.Task;

    public string LogBoxSize => $"{LogBox.ActualWidth:0}x{LogBox.ActualHeight:0}";
    public bool LogBoxVisible => LogBox.IsVisible;

    private void Log(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LogBox.AppendText(text.TrimEnd('\n') + "\n");
        LogBox.ScrollToEnd();
    }

    private void SetBusy(string text) => BusyText.Text = text;

    private void ShowOp(OpResult r, bool alsoRawOnFail = true)
    {
        Log(r.Output);
        if (r.Ok)
        {
            GuidePanel.Visibility = Visibility.Collapsed;
            return;
        }
        var guide = r.Guide;
        if (guide.Length == 0 && alsoRawOnFail)
            guide = "这条失败不在翻译表里，原始输出如下：\n" + r.Output.Trim();
        if (guide.Length > 0)
        {
            GuideText.Text = guide.Trim();
            GuidePanel.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshAsync()
    {
        SetBusy("正在读取仓库状态…");
        try
        {
            _info = await Repo.ReadAsync(_dir);
            RepoTextBlock.Text = _info.IsRepo ? _info.Root : _dir;
            BranchText.Text = _info.IsRepo ? _info.UpstreamLine : "当前目录不是 Git 仓库";
            ConflictList.ItemsSource = _info.Conflicts.Select(f => new FileItem(f.Path, f.StateLabel)).ToList();
            StagedList.ItemsSource = _info.Staged.Select(f => new FileItem(f.Path, f.StateLabel)).ToList();
            UnstagedList.ItemsSource = _info.Unstaged.Select(f => new FileItem(f.Path, f.StateLabel)).ToList();
            UntrackedList.ItemsSource = _info.Untracked.Select(f => new FileItem(f.Path, f.StateLabel)).ToList();
            ConflictHeader.Visibility = _info.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ConflictList.Visibility = _info.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Log($"刷新：{(_info.IsRepo ? _info.UpstreamLine : "不在仓库")}，" +
                $"待处理 {(_info.Entries.Count)} 项");
        }
        catch (Exception ex)
        {
            Log("读取失败：" + ex.Message);
        }
        finally
        {
            SetBusy("");
            _ready.TrySetResult();
        }
    }

    private List<FileItem> Selected()
    {
        var picked = new List<FileItem>();
        foreach (var l in new[] { ConflictList, StagedList, UnstagedList, UntrackedList })
            picked.AddRange(l.SelectedItems.Cast<FileItem>());
        return picked.DistinctBy(f => f.Path).ToList();
    }

    private async Task<bool> ConfirmDangerAsync(string dir, DangerVerdict v)
    {
        var dlg = new ConfirmDialog(v) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            Log("已取消，未执行任何操作。");
            return false;
        }
        if (dlg.WantBackup)
        {
            var b = await Ops.BackupAsync(dir, v.Label);
            Log(b.Output);
        }
        return true;
    }

    private async void OpenRepoBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择仓库目录" };
        if (dlg.ShowDialog() != true) return;
        _dir = dlg.FolderName;
        await RefreshAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void StageBtn_Click(object sender, RoutedEventArgs e)
    {
        var paths = Selected().Select(f => f.Path).ToList();
        if (paths.Count == 0)
        {
            Log("没选中文件：先在左边挑要暂存的文件。");
            return;
        }
        ShowOp(await Ops.StageAsync(_dir, paths));
        await RefreshAsync();
    }

    private async void CommitBtn_Click(object sender, RoutedEventArgs e)
    {
        var paths = Selected().Select(f => f.Path).ToList();
        if (paths.Count > 0)
        {
            var st = await Ops.StageAsync(_dir, paths);
            if (!st.Ok) { ShowOp(st); return; }
        }
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            GuideText.Text = "提交说明不能为空。第一行写清这次改了什么，例如“修复导出按钮点了没反应”。";
            GuidePanel.Visibility = Visibility.Visible;
            return;
        }
        var done = await Ops.CommitAsync(_dir, title, BodyBox.Text);
        ShowOp(done);
        if (done.Ok)
        {
            // 失败时保留用户刚写的说明，让他改一个字就能重试
            TitleBox.Clear();
            BodyBox.Clear();
        }
        await RefreshAsync();
    }

    private async void PushBtn_Click(object sender, RoutedEventArgs e)
    {
        var v = DangerGate.Assess(["push"]);
        SetBusy("正在推送…");
        try { ShowOp(await Ops.PushAsync(_dir, assumeYes: v is null)); }
        finally { SetBusy(""); }
        await RefreshAsync();
    }

    private async void PullBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy("正在拉取…");
        try { ShowOp(await Ops.PullAsync(_dir)); }
        finally { SetBusy(""); }
        await RefreshAsync();
    }

    private async void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        var choice = await ChoiceDialog.ShowAsync(this, "撤销上次提交",
            "三种“撤销”丢的东西不一样，选错会丢代码：",
            ["只撤销“已提交”这件事，改动留在暂存区（最常用）",
             "撤销提交并取消暂存，改动留在工作区",
             "撤销提交并丢弃改动（不可恢复）",
             "历史保留，新增一次抵消提交（已经推送过时用这个）",
             "取消"]);
        if (choice is null || choice == 4) { Log("已取消。"); return; }
        Ops.UndoMode mode = choice switch
        {
            0 => Ops.UndoMode.KeepStaged,
            1 => Ops.UndoMode.KeepUnstaged,
            2 => Ops.UndoMode.Discard,
            _ => Ops.UndoMode.ReverseCommit,
        };
        if (mode == Ops.UndoMode.Discard)
        {
            var v = DangerGate.Assess(["reset", "--hard", "HEAD^"])!;
            if (!await ConfirmDangerAsync(_dir, v)) return;
        }
        ShowOp(await Ops.UndoLastCommitAsync(_dir, mode));
        await RefreshAsync();
    }

    private async void DiscardBtn_Click(object sender, RoutedEventArgs e)
    {
        var files = Selected().Select(f => f.Path).ToList();
        if (files.Count == 0)
        {
            Log("没选中文件：这个按钮只会作用在你选中的文件上，不会整个目录一起丢。");
            return;
        }
        var v = DangerGate.Assess(["restore", .. files]) ??
                DangerGate.Assess(["restore", "."])!;
        if (!await ConfirmDangerAsync(_dir, v)) return;
        foreach (var f in files)
            ShowOp(await Ops.RunGatedAsync(_dir, ["restore", "--", f], assumeYes: true));
        await RefreshAsync();
    }

    private async void ConflictBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = await Repo.ReadAsync(_dir);
        var files = Ops.ConflictFiles(info);
        if (files.Count == 0)
        {
            GuidePanel.Visibility = Visibility.Collapsed;
            Log("当前没有冲突文件。");
            return;
        }
        var rebase = await Ops.IsRebasingAsync(_dir);
        Log(rebase
            ? "注意方向是反的：变基时 --ours 指“你在往上面重放的目标分支”，你自己的提交是 --theirs。"
            : "合并中：--ours 是你当前分支，--theirs 是被合进来的那边。");
        foreach (var f in files)
        {
            var choice = await ChoiceDialog.ShowAsync(this, "解决冲突：" + f,
                rebase ? "变基中，ours/theirs 的含义和合并时相反，已按下表处理。" : "合并冲突，选保留哪一方。",
                ["保留我的改动（" + (rebase ? "theirs" : "ours") + "）",
                 "保留对方的改动（" + (rebase ? "ours" : "theirs") + "）",
                 "我自己打开编辑器改",
                 "跳过这个文件"]);
            if (choice is null || choice >= 3) continue;
            var side = choice == 0 ? Ops.Side.Mine : Ops.Side.Theirs;
            ShowOp(await Ops.ResolveConflictAsync(_dir, f, side, rebase));
        }
        var again = await Repo.ReadAsync(_dir);
        if (again.Conflicts.Count == 0)
        {
            var go = await ChoiceDialog.ShowAsync(this, "冲突都解决了",
                "下一步？", ["继续（变基则 continue，合并则提交）", "先不动"]);
            if (go == 0) ShowOp(await Ops.ContinueAfterConflictAsync(_dir));
        }
        await RefreshAsync();
    }

    private async void DoctorBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy("体检中…");
        try
        {
            var checks = await Doctor.RunAsync(_dir);
            Log("== 体检 ==");
            foreach (var c in checks)
                Log($"  {(c.Ok ? "OK   " : "缺失")} {c.Label,-22} {c.Detail}");
            var miss = Doctor.MissingCount(checks);
            Log(miss == 0 ? "全部到位。" : $"有 {miss} 项建议修复，点“一键写入合理默认值”。");
        }
        finally { SetBusy(""); }
    }

    private async void FixDryBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy("计算中…");
        try
        {
            var changes = await ConfigFixer.ApplyAsync(dryRun: true);
            Log(changes.Count == 0 ? "全部配置已到位。" : "== 准备写入 ==");
            foreach (var c in changes)
                Log(c.Reason is null
                    ? $"  {c.Item.Key} = {c.Item.Value}   {c.Item.Why}"
                    : $"  [跳过] {c.Item.Key}={c.Item.Value}  {c.Reason}");
        }
        finally { SetBusy(""); }
    }

    private async void FixBtn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy("写入中…");
        try
        {
            var changes = await ConfigFixer.ApplyAsync(dryRun: false);
            foreach (var c in changes)
                Log(c.Reason is null
                    ? $"  [写入] {c.Item.Key} = {c.Item.Value}   {c.Item.Why}"
                    : $"  [跳过] {c.Item.Key}={c.Item.Value}  {c.Reason}");
            Log("完成。要恢复原值点“撤销这些改动”。");
        }
        finally { SetBusy(""); }
    }

    private async void RevertBtn_Click(object sender, RoutedEventArgs e)
    {
        var n = await ConfigFixer.RevertAsync();
        Log(n > 0 ? $"已恢复 {n} 项原值。" : "没有备份文件，无法回退。");
    }

    private async void InitBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要作为仓库根目录的文件夹" };
        if (dlg.ShowDialog() != true) return;
        var r = await Ops.InitAsync(dlg.FolderName);
        if (!r.Ok) { Log(r.Output); return; }
        var gi = Path.Combine(dlg.FolderName, ".gitignore");
        if (!File.Exists(gi)) await File.WriteAllTextAsync(gi, Ops.GitignoreCn);
        var ga = Path.Combine(dlg.FolderName, ".gitattributes");
        if (!File.Exists(ga)) await File.WriteAllTextAsync(ga, Ops.GitattributesCn);
        Log($"已在 {dlg.FolderName} 建好仓库（默认分支 main，含中文注释的 .gitignore / .gitattributes）");
        _dir = dlg.FolderName;
        await RefreshAsync();
    }

    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) TitleBox.Clear();
    }
}
