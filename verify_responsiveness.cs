#:project src/GunMix.App/GunMix.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
// 离屏真实 WPF 导入、取消、素材切换和导出验收。不会修改源素材。
// dotnet run --no-cache verify_responsiveness.cs -- <output-directory> <weapon-wav-directory>
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GunMix.App;
using GunMix.App.ViewModels;
using GunMix.App.Views;
using GunMix.Core.Persistence;

var argsList=Environment.GetCommandLineArgs().Skip(1).ToArray();
string output=Path.GetFullPath(argsList[0]),source=Path.GetFullPath(argsList[1]);
Directory.CreateDirectory(output);
using var watchdog=new System.Threading.Timer(_=>{Console.Error.WriteLine("UI responsiveness timeout");Environment.Exit(2);},null,90000,Timeout.Infinite);
var thread=new Thread(()=>
{
    var dispatcher=Dispatcher.CurrentDispatcher;
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
    var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
    ProjectStore.RecoveryDirectoryOverride=Path.Combine(output,"recovery-"+Guid.NewGuid().ToString("N"));
    var window=new MainWindow{Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,
        ShowActivated=false,ShowInTaskbar=false,Width=1280,Height=800};
    window.Show();var vm=(MainViewModel)window.DataContext;
    dispatcher.BeginInvoke(async ()=>
    {
        var intervals=new List<double>();
        var watch=System.Diagnostics.Stopwatch.StartNew();double previous=watch.Elapsed.TotalMilliseconds;
        var heartbeat=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(40)};
        heartbeat.Tick+=(_,_)=>{double now=watch.Elapsed.TotalMilliseconds;intervals.Add(now-previous);previous=now;};
        heartbeat.Start();
        try
        {
            await vm.ImportFolderAsync(source);
            Check(vm.ProjectLoaded&&!vm.IsImporting&&vm.ErrorText.Length==0,"导入成功并退出忙碌状态");
            Check(intervals.Count>=2,"导入期间界面心跳继续执行");
            int assetCount=vm.Project.Assets.Count;
            var layer=vm.SelectedLayer!;
            var pool=layer.Pool;
            Check(pool.Count>=2,"固定样本测试至少两个候选");
            for(int i=0;i<12;i++)
            {
                var asset=pool[i%pool.Count];vm.AssignFixedAsset(layer.Model,asset.Id);
                Check(layer.CurrentVariant==asset.FileName&&layer.PathDisplay.EndsWith(asset.FileName),"素材名称与路径立即刷新");
                Check(layer.FixedAssetId==asset.Id,"固定样本选择回显");
                await Task.Delay(20);
            }
            // 设置修改后直接点击导出，验证后台任务不改写界面绑定集合。
            vm.BurstRpm=777;
            var export=new ExportDialog(vm){Owner=window,Left=-20000,Top=-20000,
                WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,ShowInTaskbar=false};
            export.Show();((TextBox)export.FindName("TxtOutDir")).Text=output;
            ((ComboBox)export.FindName("CmbFormat")).SelectedIndex=2;
            ((Button)export.FindName("BtnExportChecked")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var result=(TextBox)export.FindName("ResultText");
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while(result.Text.Length==0&&DateTime.UtcNow<deadline)await Task.Delay(40);
            Check(result.Text.Contains("[成功] 单发")&&result.Text.Contains("[成功] 连发"),"导出窗口分别输出单发与连发："+result.Text);
            export.Close();
            var cancelling=vm.ImportFolderAsync(source);
            await Task.Delay(20);
            var import=app.Windows.OfType<ImportDialog>().FirstOrDefault();
            Check(import!=null,"取消时导入进度窗口仍存在");
            var cancelButton=FindButtons(import!).First(b=>Equals(b.Content,"取消"));
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await cancelling;
            Check(!vm.IsImporting&&vm.Project.Assets.Count==assetCount&&vm.StatusText.Contains("取消"),"取消不应用半成品且恢复操作");
            heartbeat.Stop();
            double maxGap=intervals.Max();
            Check(maxGap<1500,$"界面心跳最大间隔过长：{maxGap:0} ms");
            File.WriteAllText(Path.Combine(output,"responsiveness.json"),JsonSerializer.Serialize(new{
                Passed=true,Assets=assetCount,UiHeartbeats=intervals.Count,MaxHeartbeatGapMs=maxGap,
                FixedSelections=12,SingleAndBurstExport=true,CancelImport=true,ExportResult=result.Text,
                Scope="真实 WPF 窗口离屏运行，界面线程心跳；播放锁与淡出线程另由可控设备回归测试验证"
            },new JsonSerializerOptions{WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
            vm.Shutdown();Console.WriteLine($"Passed: {assetCount} assets, {intervals.Count} heartbeats, max gap {maxGap:0} ms");Environment.Exit(0);
        }
        catch(Exception ex){Console.Error.WriteLine(ex);Environment.Exit(1);}
    });
    Dispatcher.Run();
});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();

void Check(bool ok,string description){if(!ok)throw new InvalidOperationException(description);}
List<Button> FindButtons(System.Windows.DependencyObject root)
{
    var result=new List<Button>();if(root is Button b)result.Add(b);
    for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)result.AddRange(FindButtons(System.Windows.Media.VisualTreeHelper.GetChild(root,i)));
    return result;
}
