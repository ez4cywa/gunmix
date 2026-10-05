using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GunMix.App;
using Xunit;

namespace GunMix.Core.Tests;

public class KeyboardAccessibilityTests
{
    [Fact]
    public void PlaybackShortcutPreservesNativeSpaceActionsIncludingNestedContent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.False(MainWindow.ReservesSpaceKey(null));
                Assert.False(MainWindow.ReservesSpaceKey(new TextBlock()));
                foreach (var control in new System.Windows.DependencyObject[]
                         { new TextBox(), new ComboBox(), new Slider(), new Button(), new CheckBox(),
                           new ToggleButton(), new ListBox(), new ListBoxItem(), new TreeView(), new TreeViewItem(), new DataGrid() })
                    Assert.True(MainWindow.ReservesSpaceKey(control));
                var child = new TextBlock { Text = "按钮内部文本" };
                var button = new Button { Content = child };
                Assert.True(MainWindow.ReservesSpaceKey(child));
                GC.KeepAlive(button);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
