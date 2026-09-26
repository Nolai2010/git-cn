using System.Windows;
using GitCn.Core;

namespace GitCn.App;

public partial class ConfirmDialog : Window
{
    public bool WantBackup { get; private set; }

    public ConfirmDialog(DangerVerdict verdict)
    {
        InitializeComponent();
        TitleText.Text = "危险操作：" + verdict.Label;
        HarmText.Text = verdict.Harm;
        SaferText.Text = verdict.Safer;
        BackupCheck.Visibility = verdict.OfferBackup ? Visibility.Visible : Visibility.Collapsed;
        OkBtn.Focus();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        WantBackup = BackupCheck.IsChecked == true;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
