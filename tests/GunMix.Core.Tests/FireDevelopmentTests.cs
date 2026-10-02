using System.IO;
using System.Text.Json;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Fire;
using GunMix.Core.Mixing;
using GunMix.Core.Model;
using GunMix.Core.Persistence;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

public class FireDevelopmentTests
{
    private static (Recipe recipe,List<AssetInfo> assets,Guid weapon) Fixture()
    {
        var weapon=Guid.NewGuid();var a=new AssetInfo{WeaponId=weapon,Sha256="A",FileName="A.wav",GroupKey="shot",Format=new(48000,16,1,false,48000)};
        var b=new AssetInfo{WeaponId=weapon,Sha256="B",FileName="B.wav",GroupKey="last",Format=a.Format};
        var recipe=new Recipe{FireProfile=new(){Enabled=true},Layers=[new(){Name="SHOT",Role=LayerRoles.Shot,PoolAssetIds=[a.Id]}]};
        return(recipe,[a,b],weapon);
    }
    private static CompiledTimeline Plan(Recipe recipe,List<AssetInfo> assets,Guid weapon,ManifestKind kind=ManifestKind.Burst)
    {
        var manifest=TimelineCompiler.BuildManifest(recipe,assets,weapon,kind);
        return TimelineCompiler.Compile(recipe,manifest,assets,weapon,48000,kind);
    }
    [Fact]
    public void AllTwelveDocumentVectorsRunAgainstActualController()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null&&!File.Exists(Path.Combine(root.FullName,"output/contracts/fire-controller-v3.test-vectors.json")))root=root.Parent;
        Assert.NotNull(root);
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName,"output/contracts/fire-controller-v3.test-vectors.json")));
        int count=0;
        foreach(var vector in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var input=vector.GetProperty("input");int Int(string key)=>input.GetProperty(key).GetInt32();
            var scenario=new FireScenario{SampleRate=Int("sampleRate"),Rpm=Int("rpm"),Ammo=Int("ammo"),AttemptLimit=Int("attemptLimit"),
                Mode=Enum.Parse<FireMode>(input.GetProperty("mode").GetString()!,true),ReadyFrame=Int("readyFrame"),
                BurstSize=input.TryGetProperty("burstSize",out var burst)?burst.GetInt32():3,
                Inputs=[new(0,FireEventKind.TriggerDown),new(Int("releaseFrame"),FireEventKind.TriggerUp)]};
            var result=FireController.Compile(scenario);var expected=vector.GetProperty("expected");
            long[] Frames(string key)=>expected.GetProperty(key).EnumerateArray().Select(x=>x.GetInt64()).ToArray();
            Assert.Equal(Frames("shotFrames"),result.Events.Where(e=>e.Kind==FireEventKind.ShotCommitted).Select(e=>e.Frame));
            Assert.Equal(Frames("dryFrames"),result.Events.Where(e=>e.Kind==FireEventKind.DryFire).Select(e=>e.Frame));
            Assert.Equal(Frames("lastRoundFrames"),result.Events.Where(e=>e.LastRound).Select(e=>e.Frame));
            Assert.Equal(expected.GetProperty("ammoAfter").GetInt32(),result.AmmoAfter);count++;
        }
        Assert.Equal(12,count);
    }
    [Theory]
    [InlineData(0,30,0)][InlineData(600,-1,0)][InlineData(600,30,-1)]
    public void InvalidScenariosAreRejected(int rpm,int ammo,long release)=>Assert.Throws<ArgumentException>(()=>FireController.Compile(new(){Rpm=rpm,Ammo=ammo,Inputs=[new(0,FireEventKind.TriggerDown),new(release,FireEventKind.TriggerUp)]}));
    [Fact]
    public void RepeatedPressAndContextChangeUseStableFrames()
    {
        var result=FireController.Compile(new(){Mode=FireMode.Single,Inputs=[new(0,FireEventKind.TriggerDown),new(4800,FireEventKind.TriggerUp),new(4800,FireEventKind.ContextChanged,new(Ads:true)),new(4800,FireEventKind.TriggerDown),new(9600,FireEventKind.TriggerUp)]});
        var shots=result.Events.Where(e=>e.Kind==FireEventKind.ShotCommitted).ToList();Assert.Equal(new long[]{0,4800},shots.Select(e=>e.Frame));Assert.True(shots[1].Context.Ads);
    }
    [Fact]
    public void CompleteBurstOptionPreservesScheduledShotsAfterRelease()
    {
        var s=new FireScenario{Mode=FireMode.Burst,CompleteBurstOnRelease=true,Inputs=[new(0,FireEventKind.TriggerDown),new(4800,FireEventKind.TriggerUp)]};
        Assert.Equal(new long[]{0,4800,9600},FireController.Compile(s).Events.Where(e=>e.Kind==FireEventKind.ShotCommitted).Select(e=>e.Frame));
    }
    [Fact]
    public void ActionGateDoesNotSpendAmmoOnBlockedAttemptsAndCycleSurvivesRelease()
    {
        var result=FireController.Compile(new(){CycleFrames=6000,Inputs=[new(0,FireEventKind.TriggerDown),new(14400,FireEventKind.TriggerUp)]});
        Assert.Equal(new long[]{0,9600},result.Events.Where(e=>e.Kind==FireEventKind.ShotCommitted).Select(e=>e.Frame));
        Assert.Equal(new long[]{6000,15600},result.Events.Where(e=>e.Kind==FireEventKind.CycleComplete).Select(e=>e.Frame));Assert.Equal(28,result.AmmoAfter);
    }
    [Fact]
    public void LastRoundReplacesOrdinaryShotAndEmptyPressHasNoShotSound()
    {
        var (r,a,w)=Fixture();r.FireProfile!.Ammo=1;r.Layers.Add(new(){Name="LAST",FireTrigger=LayerFireTrigger.LastRound,PoolAssetIds=[a[1].Id]});
        var plan=Plan(r,a,w);Assert.Single(plan.Events);Assert.Equal(a[1].Id,plan.Events[0].AssetId);Assert.Equal(0,plan.AmmoAfter);
        r.FireProfile.Ammo=0;Assert.Empty(Plan(r,a,w).Events);
    }
    private static SoundDefinitionBank Bind(Recipe r,List<AssetInfo> assets,string key="bank")
    {
        var bank=new SoundDefinitionBank{BankKey=key,Name=key,Rows=[new(){Alias="root",Secondary="child",SourceHash=assets[0].Sha256},new(){Alias="child",Secondary="root",SourceHash=assets[1].Sha256}]};
        r.FireProfile!.Banks.Add(bank);r.Layers[0].BankKey=key;r.Layers[0].AliasId="root";return bank;
    }
    [Fact]
    public void RelationsDefaultToReferenceOnlyAndCycleDiagnosticRetainsIndependentShots()
    {
        var (r,a,w)=Fixture();Bind(r,a);Assert.Equal(6,Plan(r,a,w).EventCount);
        r.FireProfile!.Relations=[new(){BankKey="bank",FromAlias="root",ToAlias="child",Enabled=true},new(){BankKey="bank",FromAlias="child",ToAlias="root",Enabled=true}];
        var plan=Plan(r,a,w);Assert.Equal(12,plan.EventCount);Assert.Equal(12,plan.Events.Select(e=>e.InstanceId).Distinct().Count());Assert.Contains(plan.Issues,i=>i.Message.Contains("循环"));
        Assert.Equal(6,plan.Events.Count(e=>e.ParentInstanceId!=null));
    }
    [Fact]
    public void InstanceBudgetAndDepthStopWithDiagnostic()
    {
        var (r,a,w)=Fixture();Bind(r,a);r.FireProfile!.MaxInstances=1;r.FireProfile.Relations=[new(){BankKey="bank",FromAlias="root",ToAlias="child",Enabled=true}];
        var plan=Plan(r,a,w);Assert.Equal(6,plan.EventCount);Assert.Contains(plan.Issues,i=>i.Message.Contains("预算"));
        r.FireProfile.MaxDepth=1;r.FireProfile.MaxInstances=5;Assert.Contains(Plan(r,a,w).Issues,i=>i.Message.Contains("深度"));
    }
    [Fact]
    public void BankScopeAndAdsCandidatesPreventGlobalMerging()
    {
        var (r,a,w)=Fixture();var bank=Bind(r,a,"one");bank.Rows[0].Secondary=null;
        bank.Rows.Add(new(){Alias="root",SourceHash="B",AdsCandidate=true});r.FireProfile!.Banks.Add(new(){BankKey="two",Rows=[new(){Alias="root",SourceHash="B"}]});
        Assert.All(Plan(r,a,w).Events,e=>Assert.Equal(a[0].Id,e.AssetId));r.FireProfile.Context=new(Ads:true);
        Assert.All(Plan(r,a,w).Events,e=>Assert.Equal(a[1].Id,e.AssetId));
        r.Layers[0].BankKey="two";Assert.Empty(Plan(r,a,w).Events);r.FireProfile.AllowFilenameContextFallback=true;Assert.Equal(6,Plan(r,a,w).EventCount);
    }
    [Fact]
    public void MissingReferenceIsNotSubstituted()
    {
        var (r,a,w)=Fixture();var b=Bind(r,a);b.Rows[0].SourceHash="absent";var plan=Plan(r,a,w);
        Assert.Empty(plan.Events);Assert.Contains(plan.Issues,i=>i.Message.Contains("缺失"));
    }
    [Fact]
    public void FingerprintChangesWithTriggerSeedSourceAndContext()
    {
        var (r,a,w)=Fixture();var manifest=TimelineCompiler.BuildManifest(r,a,w,ManifestKind.Burst);Assert.True(manifest.Matches(r,ManifestKind.Burst,a,w));
        r.RandomSeed++;Assert.False(manifest.Matches(r,ManifestKind.Burst));r.RandomSeed--;
        r.Layers[0].TriggerShotNumber=3;Assert.False(manifest.Matches(r,ManifestKind.Burst));r.Layers[0].TriggerShotNumber=1;
        a[0].Sha256="changed";Assert.False(manifest.Matches(r,ManifestKind.Burst,a,w));a[0].Sha256="A";
        r.FireProfile!.Context=new(Ads:true);Assert.False(manifest.Matches(r,ManifestKind.Burst));
    }
    [Fact]
    public void ReleaseLayerOrderDoesNotChangeLegacyReleaseFrame()
    {
        var (r,a,w)=Fixture();r.FireProfile=null;var release=new Layer{Name="release",IsExperimental=true,Trigger=ExperimentalTrigger.ReleaseMoment,PoolAssetIds=[a[0].Id]};
        r.Layers.Insert(0,release);Assert.Equal(28800,Plan(r,a,w).Events.Single(e=>e.LayerId==release.Id).StartSample);
        r.Layers.Reverse();Assert.Equal(28800,Plan(r,a,w).Events.Single(e=>e.LayerId==release.Id).StartSample);
    }
    [Fact]
    public void ReleasePoliciesAffectInstancesAndEmitExplicitStopCommands()
    {
        var (r,a,w)=Fixture();r.FireProfile!.ReleaseFrame=4800;var natural=Plan(r,a,w);Assert.Equal(48000,natural.TotalSamples);
        r.FireProfile.ReleasePolicy=ReleasePolicy.StopOnRelease;var stopped=Plan(r,a,w);Assert.Equal(4800,stopped.TotalSamples);Assert.Contains(stopped.Commands,c=>c.Kind=="StopInstance"&&c.Frame==4800);
        r.FireProfile.ReleasePolicy=ReleasePolicy.FadeOnRelease;Assert.Equal(6240,Plan(r,a,w).TotalSamples);
    }
    [Fact]
    public void MixPeakIsMeasuredAfterCancellationAndPitchChangesLength()
    {
        var (r,a,w)=Fixture();var plan=Plan(r,a,w,ManifestKind.Single);var opposite=plan.Events[0] with{AssetId=a[1].Id};
        plan=plan with{Events=[plan.Events[0],opposite]};
        var mix=MixKernel.Render(plan,e=>new AssetBuffer{Data=Enumerable.Repeat(e.AssetId==a[0].Id?0.5f:-0.5f,48000).ToArray(),Channels=1,SampleRate=48000,FrameCount=48000});
        Assert.Equal(0,mix.Peak);Assert.All(mix.Data,x=>Assert.Equal(0,x));
        r.Layers[0].PitchRatio=2;Assert.Equal(24000,Plan(r,a,w,ManifestKind.Single).TotalSamples);
    }
    [Fact]
    public void RawDefinitionPreservesUnpaddedHashUnknownFieldsAndBlocksPathEscape()
    {
        string dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"bank.json");File.WriteAllText(path,"[{\"alias\":\"853d29e139f3520\",\"alias2\":\"8a04ea8d047e978a\",\"snd\":\"../outside\",\"speaker\":3}]");
            var bank=SoundDefinitionBank.Load(path,dir);var row=Assert.Single(bank.Rows);Assert.Equal("853d29e139f3520",row.Alias);Assert.Equal("0853d29e139f3520",SoundDefinitionRow.NormalizeHash(row.Alias));
            Assert.Null(row.GainDb);Assert.Null(row.Weight);Assert.Null(row.Loop);Assert.Contains("speaker",row.RawJson);Assert.Contains(bank.Diagnostics,x=>x.Contains("越出"));Assert.NotEmpty(bank.SourceSha256);
        }
        finally{foreach(var file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);}
    }
    [Fact]
    public void SchemaMigrationPreservesOriginalAndCloneDoesNotShareRules()
    {
        string dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());Directory.CreateDirectory(dir);
        try
        {
            var old=Path.Combine(dir,"old.gunmix.json");File.WriteAllText(old,"{\"schemaVersion\":1,\"projectName\":\"original\"}");var bytes=File.ReadAllText(old);
            var p=ProjectStore.Load(old);Assert.Equal(3,p.SchemaVersion);Assert.NotNull(p.MigrationReport);Assert.Throws<InvalidOperationException>(()=>ProjectStore.Save(p,old));Assert.Equal(bytes,File.ReadAllText(old));
            var fresh=Path.Combine(dir,"new.gunmix.json");ProjectStore.Save(p,fresh);Assert.Equal(3,ProjectStore.Load(fresh).SchemaVersion);
            var (r,_,_)=Fixture();var clone=r.Clone();clone.FireProfile!.Ammo=1;Assert.Equal(30,r.FireProfile!.Ammo);
        }
        finally{foreach(var file in Directory.GetFiles(dir))File.Delete(file);Directory.Delete(dir);}
    }
    [Fact]
    public void RuntimeVoiceBudgetProducesExplicitFadeCommands()
    {
        var (recipe,assets,weapon)=Fixture();recipe.BurstShotCount=6;recipe.BurstRpm=600;recipe.Layers[0].BurstVoiceLimit=1;
        var plan=Plan(recipe,assets,weapon);
        Assert.Equal(6,plan.Events.Count);
        Assert.Equal(5,plan.Commands.Count(c=>c.Kind=="FadeInstance"));
        Assert.All(plan.Events.Take(5),e=>Assert.NotNull(e.FadeOutStartSample));
    }
    [Fact]
    public void ExplicitReleaseMappingWorksInSingleExport()
    {
        var (recipe,assets,weapon)=Fixture();recipe.Layers.Add(new(){Name="Release",PoolAssetIds=[assets[0].Id],FireTrigger=LayerFireTrigger.Release});
        var plan=Plan(recipe,assets,weapon,ManifestKind.Single);
        Assert.Equal(2,plan.Events.Count);Assert.Equal(4800,plan.Events[1].StartSample);
    }
}
