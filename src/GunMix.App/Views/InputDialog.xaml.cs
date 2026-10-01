using System.Windows;

namespace GunMix.App.Views;

public partial class InputDialog : Window
{
    private InputDialog(string label, string initial)
    {
        InitializeComponent();
        LabelText.Text = label;
        ValueBox.Text = initial;
        ValueBox.SelectAll();
        Loaded += (_, _) => ValueBox.Focus();
    }

    public string Value => ValueBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>模态输入框；取消返回 null。</summary>
    public static string? Show(Window? owner, string title, string label, string initial)
    {
        var dlg = new InputDialog(label, initial) { Title = title, Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
