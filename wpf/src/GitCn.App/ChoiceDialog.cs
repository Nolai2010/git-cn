using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GitCn.App;

/// <summary>选项框。用它而不是 MessageBox，是因为要给出的选项经常带一整句解释，
/// MessageBox 的按钮放不下中文长标签。</summary>
public static class ChoiceDialog
{
    public static Task<int?> ShowAsync(Window owner, string title, string message, string[] options)
    {
        var tcs = new TaskCompletionSource<int?>();

        var win = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 460,
        };

        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            Margin = new Thickness(0, 0, 0, 12),
        });

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var btn = new Button
            {
                Content = options[i],
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 13,
            };
            btn.Click += (_, _) =>
            {
                var isCancel = options[index].Contains("取消") || options[index].Contains("不动");
                tcs.TrySetResult(isCancel ? (index == options.Length - 1 ? null : index) : index);
                win.Close();
            };
            panel.Children.Add(btn);
        }

        win.Content = panel;
        win.Closed += (_, _) => tcs.TrySetResult(null);
        win.Show();
        return tcs.Task;
    }
}
