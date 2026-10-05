using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using GunMix.App.ViewModels;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Fire;
using GunMix.Core.Model;
using GunMix.Core.Persistence;
using GunMix.Core.Timeline;

namespace GunMix.App.Views;

public partial class FireStudioWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly List<AssetInfo> _pending=[];
    public Recipe Draft { get; }
    public FireProfile Profile => Draft.FireProfile!;
    public Array Modes=>Enum.GetValues<FireMode>();
    public Array ReleasePolicies=>Enum.GetValues<ReleasePolicy>();
    public string SoundsRoot { get; set; }
    public bool Ads { get=>Profile.Context.Ads;set=>Profile.Context=Profile.Context with{Ads=value}; }
    public bool Suppressed { get=>Profile.Context.Suppressed;set=>Profile.Context=Profile.Context with{Suppressed=value}; }
    public string Perspective { get=>Profile.Context.Perspective;set=>Profile.Context=Profile.Context with{Perspective=value}; }
    public string EnvironmentName { get=>Profile.Context.Environment;set=>Profile.Context=Profile.Context with{Environment=value}; }
    public string DistanceName { get=>Profile.Context.Distance;set=>Profile.Context=Profile.Context with{Distance=value}; }
    public FireStudioWindow(MainViewModel vm)
    {
        _vm=vm;Draft=vm.CurrentRecipe!.Clone();Draft.FireProfile??=new();SoundsRoot=vm.Project.SourceRoot;
        var candidate=Directory.Exists(SoundsRoot)?new DirectoryInfo(SoundsRoot):null;
        while(candidate!=null){if(candidate.Name.Equals("sounds",StringComparison.OrdinalIgnoreCase)){SoundsRoot=candidate.FullName;break;}candidate=candidate.Parent;}
        InitializeComponent();DataContext=this;TriggerColumn.ItemsSource=Enum.GetValues<LayerFireTrigger>();
        InputJson.Text=Profile.Inputs.Count>0?JsonSerializer.Serialize(Profile.Inputs,ProjectStore.JsonOptions):"";
        RefreshLists();EvidenceText.Text=string.Join("\n",EvidenceLedger.Facts.Select(f=>$"{f.State} · {f.Finding}；缺口：{f.MissingLink}"));
        Feedback.Text="新建场景默认不执行 alias2；请选择事件映射与 bank，检查编译诊断后应用。";
    }
    private void RefreshLists()
    {
        Mappings.ItemsSource=null;Mappings.ItemsSource=Draft.Layers;Relations.ItemsSource=null;Relations.ItemsSource=Profile.Relations;
        BankPicker.ItemsSource=null;BankPicker.ItemsSource=Profile.Banks;if(Profile.Banks.Count>0)BankPicker.SelectedIndex=0;
    }
    private void ChooseRoot(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFolderDialog{Title="选择声音根目录（通常是 sounds）",InitialDirectory=Directory.Exists(SoundsRoot)?SoundsRoot:""};
        if(picker.ShowDialog(this)==true){SoundsRoot=picker.FolderName;DataContext=null;DataContext=this;}
    }
    private async void ImportBank(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog{Title="导入 soundbank JSON",Filter="Soundbank JSON|*.json",Multiselect=true};
        if(picker.ShowDialog(this)!=true)return;
        ImportButton.IsEnabled=false;Feedback.Text="正在校验原始定义、文件哈希与引用素材…";
        try
        {
            var root=SoundsRoot;var banks=await Task.Run(()=>picker.FileNames.Select(file=>SoundDefinitionBank.Load(file,root)).ToList());
            foreach(var bank in banks)
            {
                if(Profile.Banks.All(b=>b.BankKey!=bank.BankKey))Profile.Banks.Add(bank);
                foreach(var row in bank.Rows.Where(r=>r.ResolvedPath!=null).DistinctBy(r=>r.SourceHash))
                {
                    if(_vm.Project.Assets.Concat(_pending).Any(a=>string.Equals(a.Sha256,row.SourceHash,StringComparison.OrdinalIgnoreCase)))continue;
                    var (format,error)=WavReader.ReadFormat(row.ResolvedPath!);
                    if(format==null){bank.Diagnostics.Add(error??"素材格式无法读取");continue;}
                    var file=Path.GetFileName(row.ResolvedPath!);var parsed=NameParser.Parse(file);
                    _pending.Add(new(){WeaponId=_vm.Project.ActiveWeapon!.Id,FileName=file,SourceDirectory=Path.GetDirectoryName(row.ResolvedPath!)!,Sha256=row.SourceHash!,Format=format,Parsed=parsed,GroupKey=parsed.GroupKey});
                }
            }
            RefreshLists();Feedback.Text=$"已导入 {banks.Count} 个 bank、{banks.Sum(b=>b.Rows.Count)} 行，待应用素材 {_pending.Count}；诊断 {banks.Sum(b=>b.Diagnostics.Count)} 项。";
        }
        catch(Exception ex){Feedback.Text=$"导入失败：{ex.Message}";}
        finally{ImportButton.IsEnabled=true;}
    }
    private void BankChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Rows==null)return;Rows.ItemsSource=(BankPicker.SelectedItem as SoundDefinitionBank)?.Rows;
    }
    private void RowChanged(object sender,SelectionChangedEventArgs e)
    {
        if(RawRow!=null)RawRow.Text=Rows.SelectedItem is SoundDefinitionRow row?$"原始行：{row.RawJson}\n素材：{row.ResolvedPath??"缺失"}；原版增益、音高、权重、loop：未知":"";
    }
    private void AddAlias(object sender,RoutedEventArgs e)
    {
        if(BankPicker.SelectedItem is not SoundDefinitionBank bank||Rows.SelectedItem is not SoundDefinitionRow row){Feedback.Text="先选择 bank 和一条别名记录。";return;}
        bool uncertain = row.Alias.Contains("fcg",StringComparison.OrdinalIgnoreCase)||row.Alias.Contains("interrupt",StringComparison.OrdinalIgnoreCase)||row.Alias.Contains("prefire",StringComparison.OrdinalIgnoreCase);
        Draft.Layers.Add(new(){Enabled=!uncertain,Name=row.Alias,Role=row.Alias.Contains("shot",StringComparison.OrdinalIgnoreCase)?LayerRoles.Shot:LayerRoles.Custom,
            BankKey=bank.BankKey,AliasId=row.Alias,FireTrigger=row.Alias.Contains("last",StringComparison.OrdinalIgnoreCase)?LayerFireTrigger.LastRound:LayerFireTrigger.EveryShot});
        RefreshLists();Feedback.Text="已添加别名层；请在映射页设置其触发事件，并检查是否与旧层重复。";
    }
    private void RemoveLayer(object sender,RoutedEventArgs e){if(Mappings.SelectedItem is Layer layer){Draft.Layers.Remove(layer);RefreshLists();}}
    private void AddRelations(object sender,RoutedEventArgs e)
    {
        foreach(var bank in Profile.Banks)
            foreach(var edge in bank.Rows.Where(r=>!string.IsNullOrEmpty(r.Secondary)).DistinctBy(r=>(r.Alias,r.Secondary)))
                if(Profile.Relations.All(r=>r.BankKey!=bank.BankKey||r.FromAlias!=edge.Alias||r.ToAlias!=edge.Secondary))
                    Profile.Relations.Add(new(){BankKey=bank.BankKey,FromAlias=edge.Alias,ToAlias=edge.Secondary!});
        RefreshLists();Feedback.Text="候选关联已加入，默认全部关闭。勾选执行即采用该条项目叠加规则。";
    }
    private bool ValidateInput()
    {
        if(!Mappings.CommitEdit(DataGridEditingUnit.Cell,true)||!Mappings.CommitEdit(DataGridEditingUnit.Row,true)||
            !Relations.CommitEdit(DataGridEditingUnit.Cell,true)||!Relations.CommitEdit(DataGridEditingUnit.Row,true))
        {Feedback.Text="映射或关联有无效输入，请先修正。";return false;}
        var visited=new HashSet<DependencyObject>();
        bool Check(DependencyObject node)
        {
            if(!visited.Add(node))return true;
            if(node is TextBox text)text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if(Validation.GetHasError(node))return false;
            if(node is Visual or System.Windows.Media.Media3D.Visual3D)
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(!Check(VisualTreeHelper.GetChild(node,i)))return false;
            foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())if(!Check(child))return false;
            return true;
        }
        if(!Check(this)){Feedback.Text="有无效输入，请修正标红的数字字段。";return false;}
        Profile.Inputs=string.IsNullOrWhiteSpace(InputJson.Text)?[]:JsonSerializer.Deserialize<List<TriggerInput>>(InputJson.Text,ProjectStore.JsonOptions)??[];
        FireController.Compile(Profile.Scenario(_vm.Project.SampleRate,Draft.BurstRpm,Draft.BurstShotCount,false));
        return true;
    }
    private void PreviewSingle(object sender,RoutedEventArgs e)=>Preview(ManifestKind.Single);
    private void PreviewBurst(object sender,RoutedEventArgs e)=>Preview(ManifestKind.Burst);
    private void Preview(ManifestKind kind)
    {
        try
        {
            if(!ValidateInput())return;
            var assets=_vm.Project.Assets.Concat(_pending).ToList();var id=_vm.Project.ActiveWeapon!.Id;
            var m=TimelineCompiler.BuildManifest(Draft,assets,id,kind);var plan=TimelineCompiler.Compile(Draft,m,assets,id,_vm.Project.SampleRate,kind);
            DomainEvents.ItemsSource=plan.DomainEvents;Instances.ItemsSource=plan.Events;
            Summary.Text=$"{(kind==ManifestKind.Single?"单发":"连发")} · {plan.DomainEvents.Count(x=>x.Kind==FireEventKind.ShotCommitted)} 次射击 · {plan.EventCount} 个实例 · 剩余弹药 {plan.AmmoAfter}";
            Feedback.Text=plan.Issues.Count==0?"编译完成，未发现缺失引用或规则诊断。":string.Join("\n",plan.Issues.Select(x=>$"{x.LayerName}：{x.Message}").Distinct().Take(3))+$"\n共 {plan.Issues.Count} 项；全部诊断随导出报告保存。";
        }
        catch(Exception ex){Feedback.Text=$"无法编译：{ex.Message}";}
    }
    private void Apply(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!ValidateInput())return;
            // Validate the complete plan before mutating the live project.
            var assets=_vm.Project.Assets.Concat(_pending).ToList();
            foreach(var kind in Enum.GetValues<ManifestKind>())
                if(Profile.Enabled)FirePlanCompiler.Build(Draft,assets,_vm.Project.ActiveWeapon!.Id,_vm.Project.SampleRate,kind);
            _vm.ApplyFireConfiguration(Draft,_pending);DialogResult=true;
        }
        catch(Exception ex){Feedback.Text=$"无法应用：{ex.Message}";}
    }
}
