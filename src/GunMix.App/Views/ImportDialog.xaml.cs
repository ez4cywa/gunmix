using System.Windows;

namespace GunMix.App.Views;

public partial class ImportDialog : Window
{
    public event Action? Cancelled;

    public ImportDialog(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
    }

    public void Update(int done, int total)
    {
        ProgressText.Text = $"已检查 {done} / {total}";
        Bar.Maximum = Math.Max(1, total);
        Bar.Value = done;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}
