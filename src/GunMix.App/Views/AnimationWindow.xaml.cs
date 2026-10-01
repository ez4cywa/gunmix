using System.Windows;
using GunMix.App.ViewModels;

namespace GunMix.App.Views;

public partial class AnimationWindow : Window
{
    public AnimationWindow(MainViewModel owner)
    {
        InitializeComponent();
        DataContext = new AnimationWindowVm(owner);
    }

    private void OnBrowseAnim(object sender, RoutedEventArgs e)
        => ((AnimationWindowVm)DataContext).BrowseAnimationDir();

    private void OnBrowseSound(object sender, RoutedEventArgs e)
        => ((AnimationWindowVm)DataContext).BrowseSoundDir();

    private void OnBrowseBank(object sender, RoutedEventArgs e)
        => ((AnimationWindowVm)DataContext).BrowseBankDir();

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
        => ((AnimationWindowVm)DataContext).BrowseOutputDir();
}
