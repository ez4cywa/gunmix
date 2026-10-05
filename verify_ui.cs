#:project src/GunMix.App/GunMix.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
// 离屏检查真实 WPF 控件、自动化名称和布局；不代表已完成 Narrator 或系统 DPI 实测。
// dotnet run --no-cache verify_ui.cs -- <output-directory> <project.gunmix.json>
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GunMix.App;
using GunMix.App.ViewModels;
using GunMix.App.Views;
using GunMix.Core.Audio;
using GunMix.Core.Persistence;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

var argsList=Environment.GetCommandLineArgs().Skip(1).ToArray();
var output=Path.GetFullPath(argsList[0]);
Directory.CreateDirectory(output);
Exception? failure=null;
var thread=new Thread(()=>
{
    MainViewModel? vm=null;
    try
    {
        ProjectStore.RecoveryDirectoryOverride=Path.Combine(output,"recovery-"+Guid.NewGuid().ToString("N"));
        var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var window=new MainWindow{WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,
            ShowActivated=false,ShowInTaskbar=false,Width=1280,Height=800};
        window.Show();vm=(MainViewModel)window.DataContext;Pump(window);
        Check(!vm.ProjectLoaded,"空工程状态");
        Check(Find<Button>(window).Any(b=>Equals(b.Content,"导入音频文件夹")&&b.IsVisible),"空工程入口可见");
        Capture(window,"empty.png");
        vm.OpenProjectPath(argsList[1]);
        vm.SelectedWeapon=vm.Weapons.First(x=>x.Name=="mike4");vm.RegenerateManifests();Pump(window);
        Check(vm.SelectedLayer?.FormatDisplay.StartsWith("48 kHz") == true,"采样率单位正确");
        var list=(ListBox)window.FindName("LayerList");
        Check(list.Items.Count>0,"层列表非空");
        Check(((Border)window.FindName("LayerInspector")).Visibility==Visibility.Collapsed,"默认双栏");
        Check(PeerName(list)=="混音分层列表","层列表自动化名称");
        Check(Find<CheckBox>(list).All(x=>PeerName(x).Length>0),"层开关自动化名称");
        var settings=Find<Button>(list).First(x=>Equals(x.Content,"设置"));
        Check(PeerName(settings).StartsWith("编辑层设置："),"设置按钮带层名");
        Capture(window,"main.png");
        var originalRecipe=vm.SelectedRecipe;
        vm.SelectedRecipe=vm.Recipes.First();Pump(window);
        window.Width=1080;
        ((ToggleButton)window.FindName("InspectorToggle")).IsChecked=true;Pump(window);
        AssertInside(window,Find<Button>(window).First(b=>Equals(b.Content,"复制配方")));
        Capture(window,"teaching-recipe.png");
        vm.SelectedRecipe=originalRecipe;window.Width=1280;Pump(window);
        settings=Find<Button>(list).First(x=>Equals(x.Content,"设置"));
        settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
        Check(((Border)window.FindName("LayerInspector")).IsVisible,"设置按钮展开属性区");
        Check(ReferenceEquals(vm.SelectedLayer,settings.DataContext),"设置按钮选择正确的层");
        list.SelectedIndex=Math.Min(1,list.Items.Count-1);Pump(window);
        Check(ReferenceEquals(vm.SelectedLayer,list.SelectedItem),"键盘选择绑定");
        var error=(TextBox)window.FindName("WorkspaceError");
        Check(!error.IsVisible,"无错误时不占用空间");
        vm.ErrorText="素材路径未找到："+string.Join("；",Enumerable.Range(0,12).Select(i=>$"第 {i+1} 个文件 D:\\素材库\\很长的武器动画文件名_{i:000}.wav"));Pump(window);
        Check(error.IsVisible&&error.IsReadOnly&&error.Text.Contains("第 12 个文件"),"长错误完整且可复制");
        Capture(window,"inspector-error.png");
        var viewports=new List<object>();
        foreach(var size in new[]{(1080d,680d),(1280d,720d),(1440d,900d)})
        {
            window.Width=size.Item1;window.Height=size.Item2;Pump(window);
            AssertInside(window,Find<Button>(window).First(b=>Equals(b.Content,"导出单发 / 连发")));
            Check(list.ActualHeight>0,"窄窗口仍能访问层列表");
            foreach(var box in Find<TextBox>(list).Where(b=>b.IsVisible))
            {
                var bounds=box.TransformToAncestor(list).TransformBounds(new Rect(box.RenderSize));
                Check(bounds.Left>=0&&bounds.Right<=list.ActualWidth+1,"层输入框没有横向溢出");
            }
            foreach(var button in Find<Button>(list).Where(b=>b.IsVisible))
            {
                var bounds=button.TransformToAncestor(list).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Left>=0&&bounds.Right<=list.ActualWidth-12,"层按钮完整可见，不被滚动条遮挡");
            }
            AssertInside(window,error);
            Capture(window,$"layout-{size.Item1:0}x{size.Item2:0}.png");
            viewports.Add(new{Width=size.Item1,Height=size.Item2,Inspector=true,LongError=true});
        }
        vm.ErrorText="";
        var export=new ExportDialog(vm){Owner=window,WindowStartupLocation=WindowStartupLocation.Manual,
            Left=-20000,Top=-20000,ShowActivated=false,ShowInTaskbar=false,Width=680,Height=560};
        export.Show();Pump(export);
        Check(PeerName((TextBox)export.FindName("TxtOutDir"))=="输出文件夹","导出目录自动化名称");
        AssertInside(export,(Button)export.FindName("BtnExportChecked"));
        Capture(export,"export-compact.png");
        var advanced=Find<Expander>(export).First();advanced.IsExpanded=true;Pump(export);
        var scroll=Find<ScrollViewer>(export).First();Check(scroll.ScrollableHeight>0,"高级选项可滚动");
        scroll.ScrollToEnd();Pump(export);AssertInside(export,(Button)export.FindName("BtnExportChecked"));
        Capture(export,"export-advanced.png");export.Close();
        var options=new MainViewModel.ExportOptions{OutputDirectory=output,BitDepth=32,Dither=false,TrimTail=false,AttenuateToDbfs=-1};
        var exports=new List<object>();
        foreach(var kind in Enum.GetValues<ManifestKind>())
        {
            var result=vm.ExportKind(kind,options);
            Check(result is{Success:true},$"{kind} 导出成功：{result?.Error}");
            var audio=WavReader.Read(result!.FinalPath!);
            Check(audio.Data.Any(v=>Math.Abs(v)>0.01)&&audio.Data.All(float.IsFinite),$"{kind} 非静音且采样有限");
            Check(File.Exists(Path.ChangeExtension(result.FinalPath,".recipe.json")),"导出配方已发布");
            exports.Add(new{Kind=kind.ToString(),Path=result.FinalPath,Frames=audio.Data.Length/audio.Channels});
        }
        File.WriteAllText(Path.Combine(output,"ui-verification.json"),JsonSerializer.Serialize(new{
            Passed=true,Viewports=viewports,Exports=exports,Accessibility="WPF AutomationPeer 名称、键盘选择绑定、错误可复制、控件边界及滚动",
            Limitations=new[]{"未运行 Narrator 人工朗读","未在系统显示缩放 150% / 200% 下实测","未进行耳听音质评价"}
        },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("UI verification passed.");
        // No edits are saved to the supplied project.
        vm.Shutdown();app.Shutdown();
    }
    catch(Exception ex){failure=ex;vm?.Shutdown();}
});
thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
if(failure!=null)throw new Exception("UI verification failed",failure);

void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);}
string PeerName(UIElement element)=>UIElementAutomationPeer.CreatePeerForElement(element)?.GetName()??"";
void Pump(Window window){System.Windows.Input.CommandManager.InvalidateRequerySuggested();window.UpdateLayout();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);window.UpdateLayout();}
void AssertInside(FrameworkElement parent,FrameworkElement child)
{
    var rect=child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
    Check(rect.Left>=-1&&rect.Right<=parent.ActualWidth+1&&rect.Top>=-1&&rect.Bottom<=parent.ActualHeight+1,$"控件越界：{child.GetType().Name} {rect} / {parent.RenderSize}");
}
List<T> Find<T>(DependencyObject root) where T:DependencyObject
{
    var found=new List<T>();if(root is T item)found.Add(item);
    for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)found.AddRange(Find<T>(VisualTreeHelper.GetChild(root,i)));
    return found;
}
void Capture(Window window,string name)
{
    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);
}

