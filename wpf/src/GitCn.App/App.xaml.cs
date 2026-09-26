using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GitCn.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 锁屏 / 无会话时硬件合成不出图，RenderTargetBitmap 会得到纯白画面
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        var shot = ArgValue(e.Args, "--shot");
        var dir = ArgValue(e.Args, "--dir") ?? Environment.CurrentDirectory;
        var w = new MainWindow(dir);
        MainWindow = w;

        if (shot is null)
        {
            w.Show();
            return;
        }

        // --shot 是界面验证通道：真渲染窗口再存 PNG，而不是画一张假的示意图
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = 80;
        w.Top = 60;
        w.Show();
        _ = CaptureAsync(w, shot);
    }

    private async Task CaptureAsync(MainWindow w, string path)
    {
        try
        {
            await w.ReadyAsync().ConfigureAwait(true);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Dispatcher.Yield(DispatcherPriority.Render);

            var root = w.Content as FrameworkElement
                ?? throw new InvalidOperationException("窗口没有可视化内容");
            // 根面板没有 Background 时，RenderTargetBitmap 会得到全透明位图
            if (root is System.Windows.Controls.Panel rp && rp.Background is null)
                rp.Background = System.Windows.Media.Brushes.White;
            var size = new Size(w.ActualWidth, w.ActualHeight);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(w);
            var pxW = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX));
            var pxH = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY));
            var bmp = new RenderTargetBitmap(pxW, pxH, dpi.PixelsPerDip, dpi.PixelsPerDip,
                PixelFormats.Pbgra32);
            bmp.Render(root);

            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(full))
            {
                enc.Save(fs);
            }

            var buf = new byte[pxW * pxH * 4];
            bmp.CopyPixels(buf, pxW * 4, 0);
            var painted = 0;
            var colors = new HashSet<uint>();
            for (var i = 0; i < buf.Length; i += 4)
            {
                if (buf[i + 3] > 8) painted++;
                colors.Add((uint)((buf[i] << 16) | (buf[i + 1] << 8) | buf[i + 2]));
            }
            await File.WriteAllTextAsync(Path.ChangeExtension(full, ".txt"),
                $"root={root.GetType().Name} rootSize={root.ActualWidth}x{root.ActualHeight} " +
                $"windowSize={w.ActualWidth}x{w.ActualHeight} renderMode={RenderOptions.ProcessRenderMode}\n" +
                $"bitmap={pxW}x{pxH} 已着色像素={painted} ({100.0 * painted / (pxW * pxH):F1}%) " +
                $"不同颜色数={colors.Count} 子元素数={System.Windows.Media.VisualTreeHelper.GetChildrenCount(root)}\n" +
                $"LogBox size={w.LogBoxSize} visible={w.LogBoxVisible}\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("截图失败：" + ex.Message);
        }
        finally
        {
            Shutdown();
        }
    }

    public static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
