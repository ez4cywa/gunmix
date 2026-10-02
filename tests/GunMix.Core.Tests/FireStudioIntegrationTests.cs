using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GunMix.App.ViewModels;
using GunMix.App.Views;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Fire;
using GunMix.Core.Model;
using GunMix.Core.Mixing;
using System.Reflection;
using GunMix.Core.Persistence;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

[CollectionDefinition("FireStudioUI",DisableParallelization=true)]
public class FireStudioCollection { }

[Collection("FireStudioUI")]
public class FireStudioIntegrationTests
{
    [Fact]
    public void RealBankToScenarioWindowToSingleAndSequenceExport()
    {
        if(!TestPaths.HasAssets)return;
        Exception? failure=null;var thread=new Thread(()=>
        {
            MainViewModel? vm=null;FireStudioWindow? window=null;
            try
            {
                var app=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/GunMix.App;component/Themes/Dark.xaml",UriKind.Absolute)});
                vm=new MainViewModel();string folder=Path.Combine(TestPaths.WpnRoot!,"ar_mike4");
                vm.ApplyImport(new AssetService().ImportDirectory(folder,includeSubdirectories:false),folder);
                var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"output/contracts")))root=root.Parent;
                Assert.NotNull(root);string output=Path.Combine(root!.FullName,"output/development_validation_20261002");Directory.CreateDirectory(output);
                var bank=SoundDefinitionBank.Load(Path.Combine(TestPaths.BankDir!,"weapon_rex_ar_mike4_plr.all.json"),TestPaths.SoundsRoot!);
                Assert.Equal(318,bank.Rows.Count);
                var draft=vm.CurrentRecipe!.Clone();draft.BurstRpm=600;draft.BurstShotCount=6;draft.FireProfile=new(){Enabled=true,Banks=[bank],Ammo=30};
                draft.Layers=[new(){Name="SHOT bank root",Role=LayerRoles.Shot,GainDb=-12,BankKey=bank.BankKey,AliasId="weap_rex_mike4_fire_plr_shot"}];
                vm.ApplyFireConfiguration(draft,[]);
                window=new FireStudioWindow(vm){Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false};window.Show();window.UpdateLayout();
                var tabs=Find<TabControl>(window);Assert.Single(tabs);tabs[0].SelectedIndex=3;window.UpdateLayout();
                var button=Find<Button>(window).First(b=>Equals(b.Content,"编译连发"));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
                var summary=(TextBlock)window.FindName("Summary");Assert.Contains("6 次射击",summary.Text);
                Capture(window,Path.Combine(output,"fire-events.png"));
                tabs[0].SelectedIndex=0;window.UpdateLayout();Capture(window,Path.Combine(output,"fire-scenario.png"));
                tabs[0].SelectedIndex=1;window.UpdateLayout();Capture(window,Path.Combine(output,"fire-definitions.png"));
                tabs[0].SelectedIndex=2;window.UpdateLayout();Capture(window,Path.Combine(output,"fire-mappings.png"));
                var opts=new MainViewModel.ExportOptions{OutputDirectory=output,BitDepth=32,Dither=false,TrimTail=false,AttenuateToDbfs=-1};
                foreach(var kind in Enum.GetValues<ManifestKind>())
                {
                    var exported=vm.ExportKind(kind,opts);Assert.NotNull(exported);Assert.True(exported!.Success,exported.Error);
                    var recipe=vm.CurrentRecipe!;
                    var manifest=kind==ManifestKind.Single?recipe.SingleManifest!:recipe.BurstManifest!;
                    var plan=TimelineCompiler.Compile(recipe,manifest,vm.Project.Assets,vm.Project.ActiveWeapon!.Id,vm.Project.SampleRate,kind);
                    var resolve=(Func<ShotEvent,AssetBuffer?>)typeof(MainViewModel).GetMethod("ResolveBuffer",BindingFlags.Instance|BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<ShotEvent,AssetBuffer?>),vm);
                    var previewEngine=MixKernel.Render(plan,resolve,2);
                    var audio=WavReader.Read(exported.FinalPath!);
                    double gain=Math.Pow(10,(exported.AppliedGainDb??0)/20);
                    Assert.Equal(previewEngine.Data.Length,audio.Data.Length);
                    for(int sample=0;sample<audio.Data.Length;sample++)Assert.True(Math.Abs(audio.Data[sample]-previewEngine.Data[sample]*gain)<0.000001,"试听引擎数据与 WAV 不一致");
                    Assert.True(File.Exists(Path.ChangeExtension(exported.FinalPath!,".evidence-report.json")));Assert.Contains(audio.Data,x=>Math.Abs(x)>0.01);
                    using var json=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(exported.FinalPath!,".recipe.json")));
                    Assert.Equal(kind==ManifestKind.Single?1:6,json.RootElement.GetProperty("domain_events").EnumerateArray().Count(e=>e.GetProperty("Kind").GetInt32()==(int)FireEventKind.ShotCommitted));
                    Assert.Contains(json.RootElement.GetProperty("diagnostics").EnumerateArray(),d=>!d.GetProperty("IsError").GetBoolean());
                    Assert.Equal(kind==ManifestKind.Single?1:6,json.RootElement.GetProperty("events").GetArrayLength());
                }
                ProjectStore.Save(vm.Project,Path.Combine(output,"mike4_event_scenario.gunmix.json"));
                File.WriteAllText(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{ScenarioUi=true,MappingUi=true,EventUi=true,BankRows=318,SingleShots=1,SequenceShots=6,NonSilentWavs=true,Rules="projectAuthored"}));
            }
            catch(Exception ex){failure=ex;}
            finally{window?.Close();vm?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"界面集成测试超时");
        if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static List<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        var result=new List<T>();if(root is T t)result.Add(t);
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)result.AddRange(Find<T>(VisualTreeHelper.GetChild(root,i)));
        return result;
    }
    private static void Capture(Window window,string path)
    {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
