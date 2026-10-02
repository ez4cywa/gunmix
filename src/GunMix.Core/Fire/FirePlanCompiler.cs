using GunMix.Core.Assets;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

namespace GunMix.Core.Fire;

public sealed record FireCommand(string Id,string Kind,string TriggerEventId,string InstanceId,long Frame,string? BankKey,string? AliasId);

public static class FirePlanCompiler
{
    public static CompiledTimeline Build(Recipe recipe,IReadOnlyList<AssetInfo> assets,Guid weaponId,int rate,ManifestKind kind)
    {
        var profile=recipe.FireProfile!;var issues=new List<TimelineValidationIssue>();var instances=new List<ShotEvent>();
        var byId=assets.ToDictionary(a=>a.Id);var result=FireController.Compile(profile.Scenario(rate,recipe.BurstRpm,recipe.BurstShotCount,kind==ManifestKind.Single));
        if(profile.SchemaVersion!=3 || profile.MaxDepth is < 1 or > 64 || profile.MaxInstances is < 1 or > 4096 ||
            !double.IsFinite(profile.FadeMs) || profile.FadeMs is < 0 or > 1000)
            throw new ArgumentException("播放规则版本或实例限制无效。");
        foreach(var layer in recipe.Layers.Where(l=>l.Enabled))
        {
            if(!double.IsFinite(layer.GainDb)||layer.GainDb is < -60 or > 6 || !double.IsFinite(layer.DelayMs)||layer.DelayMs is < 0 or > 1000 ||
                !double.IsFinite(layer.PitchRatio)||layer.PitchRatio is < 0.25 or > 4)
            {issues.Add(new(layer.Name,"增益、延时或音高超出支持范围"));continue;}
            int layerStart=instances.Count;
            var trigger=layer.IsReleaseTail?LayerFireTrigger.Release:layer.FireTrigger;
            // An explicit last-round mapping replaces every ordinary SHOT layer; other layers remain independent.
            bool lastMapping=recipe.Layers.Any(l=>l.Enabled&&l.FireTrigger==LayerFireTrigger.LastRound);
            var selected=result.Events.Where(e=>Matches(trigger,e)&&!(kind==ManifestKind.Single&&layer.IsReleaseTail)&&
                !(e.LastRound&&lastMapping&&layer.Role==LayerRoles.Shot&&trigger==LayerFireTrigger.EveryShot)&&
                !(layer.IsExperimental&&layer.Trigger==ExperimentalTrigger.ShotN&&e.ShotIndex+1!=layer.TriggerShotNumber)).ToList();
            var pool=AssetService.ResolvePool(layer,assets,weaponId);
            var picks=VariantSelector.SelectForLayer(layer,recipe,pool,selected.Count,ManifestKind.Burst);
            for(int n=0;n<selected.Count;n++)
            {
                var e=selected[n];int budget=profile.MaxInstances;uint random=VariantSelector.Hash(layer.Seed??recipe.RandomSeed,layer.Id)^(uint)(n+1)*0x9e3779b9;
                var command=$"{e.Id}:{layer.Id}";
                long start=checked(e.Frame+ToFrames(layer.DelayMs,rate));
                if(layer.BankKey is {Length:>0} bankKey&&layer.AliasId is {Length:>0} alias)
                {
                    var bank=profile.Banks.FirstOrDefault(b=>b.BankKey==bankKey);
                    if(bank==null){issues.Add(new(layer.Name,"声音定义 bank 缺失"));continue;}
                    Visit(bank,alias,start,layer.GainDb,null,new HashSet<string>(StringComparer.Ordinal),0);
                }
                else if(n<picks.Count&&byId.TryGetValue(picks[n],out var asset))
                    Add(asset,start,layer.GainDb,null,null,null,"项目素材池变体",null);
                else issues.Add(new(layer.Name,$"{e.Id}：素材池为空或素材缺失"));

                void Add(AssetInfo asset,long frame,double gain,string? parent,string? bank,string? aliasId,string reason,int? row)
                {
                    if(budget--<=0){issues.Add(new(layer.Name,$"{command}：实例预算 {profile.MaxInstances} 已耗尽"));return;}
                    var id=$"{command}:instance-{profile.MaxInstances-budget}";
                    instances.Add(new(){LayerId=layer.Id,AssetId=asset.Id,ShotIndex=e.ShotIndex,StartSample=frame,GainDb=gain,PitchRatio=layer.PitchRatio,
                        InstanceId=id,ParentInstanceId=parent,TriggerEventId=e.Id,CommandId=command,BankKey=bank,AliasId=aliasId,RowIndex=row,
                        ContextSnapshot=e.Context,SourceHash=asset.Sha256,RuleOrigin="projectAuthored",SelectionReason=reason});
                }
                void Visit(SoundDefinitionBank bank,string aliasId,long frame,double gain,string? parent,HashSet<string> path,int depth)
                {
                    if(depth>=profile.MaxDepth){issues.Add(new(layer.Name,$"{aliasId}：关联深度限制 {profile.MaxDepth}"));return;}
                    if(budget<=0){issues.Add(new(layer.Name,$"{aliasId}：实例预算耗尽"));return;}
                    if(!path.Add(aliasId)){issues.Add(new(layer.Name,$"{aliasId}：关联循环，停止此路径"));return;}
                    try
                    {
                        var members=bank.Rows.Where(r=>r.Alias==aliasId).ToList();
                        if(members.Count==0){issues.Add(new(layer.Name,$"{aliasId}：同 bank 中没有此别名"));return;}
                        bool mixed=members.Any(r=>r.AdsCandidate)&&members.Any(r=>!r.AdsCandidate);
                        var candidates=mixed?members.Where(r=>r.AdsCandidate==e.Context.Ads).ToList():members;
                        if(!mixed&&e.Context.Ads&&!members.Any(r=>r.AdsCandidate)&&!profile.AllowFilenameContextFallback)
                        {issues.Add(new(layer.Name,$"{aliasId}：无 ADS 候选；请明确允许普通素材回退"));return;}
                        random^=random<<13;random^=random>>17;random^=random<<5;
                        var chosen=candidates[layer.VariantMode==VariantMode.Random?(int)(random%(uint)candidates.Count):layer.VariantMode==VariantMode.Rotation?n%candidates.Count:0];
                        var asset=assets.FirstOrDefault(a=>a.WeaponId==weaponId&&chosen.SourceHash!=null&&a.Sha256==chosen.SourceHash);
                        string? currentParent=parent;
                        if(asset==null)issues.Add(new(layer.Name,$"{aliasId} 行 {chosen.RowIndex}：素材未导入或缺失 {chosen.Snd}"));
                        else
                        {
                            Add(asset,frame,gain,parent,bank.BankKey,aliasId,mixed?"项目文件名上下文候选 + 变体":"项目变体 / 已允许的上下文回退",chosen.RowIndex);
                            currentParent=instances[^1].InstanceId;
                        }
                        if(chosen.Secondary is not {Length:>0} secondary)return;
                        var relation=profile.Relations.FirstOrDefault(r=>r.BankKey==bank.BankKey&&r.FromAlias==aliasId&&r.ToAlias==secondary&&r.Enabled);
                        if(relation==null){issues.Add(new(layer.Name,$"{aliasId} → {secondary}：关联仅作参考，未启用项目执行规则",false));return;}
                        if(!double.IsFinite(relation.DelayMs)||relation.DelayMs is < 0 or > 1000 || !double.IsFinite(relation.GainDb)||relation.GainDb is < -60 or > 6)
                        {issues.Add(new(layer.Name,"关联增益或延时无效"));return;}
                        Visit(bank,secondary,frame+ToFrames(relation.DelayMs,rate),relation.GainDb,currentParent,path,depth+1);
                    }
                    finally{path.Remove(aliasId);}
                }
            }
            if(kind==ManifestKind.Burst&&layer.BurstVoiceLimit>0)
                TimelineCompiler.ApplyVoiceLimit(instances,layerStart,layer.BurstVoiceLimit,byId,rate);
        }
        if(profile.ReleasePolicy!=ReleasePolicy.NaturalTail)
            for(int i=0;i<instances.Count;i++)
            {
                var e=instances[i];var up=result.Events.FirstOrDefault(x=>x.Kind==FireEventKind.TriggerUp&&x.Frame>e.StartSample);
                if(up!=null&&(e.FadeOutStartSample==null||up.Frame<e.FadeOutStartSample))instances[i]=e with{FadeOutStartSample=up.Frame,FadeOutSamples=profile.ReleasePolicy==ReleasePolicy.StopOnRelease?0:ToFrames(profile.FadeMs,rate)};
            }
        instances.Sort((a,b)=>a.StartSample.CompareTo(b.StartSample));
        var commands=instances.Select(e=>new FireCommand(e.CommandId+":"+e.InstanceId,e.AliasId==null?"PlayAsset":"PlayAlias",e.TriggerEventId,e.InstanceId,e.StartSample,e.BankKey,e.AliasId)).ToList();
        foreach(var instance in instances.Where(e=>e.FadeOutStartSample!=null))
            commands.Add(new($"stop:{instance.InstanceId}",instance.FadeOutSamples==0?"StopInstance":"FadeInstance",result.Events.FirstOrDefault(e=>e.Kind==FireEventKind.TriggerUp&&e.Frame==instance.FadeOutStartSample)?.Id??"voiceLimit",instance.InstanceId,instance.FadeOutStartSample!.Value,instance.BankKey,instance.AliasId));
        return new(){Events=instances,AssetById=byId,TotalSamples=instances.Count==0?0:instances.Max(e=>TimelineCompiler.EventEnd(e,byId,rate)),Commands=commands,
            Issues=issues,DomainEvents=result.Events.ToList(),AmmoAfter=result.AmmoAfter,ReleaseFrame=result.ReleaseFrame,InputFingerprint=CompileFingerprint.Create(recipe,kind,assets,weaponId)};
    }
    private static long ToFrames(double ms,int rate)=>(long)Math.Round(ms*rate/1000,MidpointRounding.AwayFromZero);
    private static bool Matches(LayerFireTrigger trigger,DomainEvent e)=>trigger switch
    {
        LayerFireTrigger.EveryShot=>e.Kind==FireEventKind.ShotCommitted,
        LayerFireTrigger.OrdinaryShot=>e.Kind==FireEventKind.ShotCommitted&&!e.LastRound,
        LayerFireTrigger.LastRound=>e.Kind==FireEventKind.ShotCommitted&&e.LastRound,
        LayerFireTrigger.Release=>e.Kind==FireEventKind.TriggerUp,
        LayerFireTrigger.DryFire=>e.Kind==FireEventKind.DryFire,
        LayerFireTrigger.Prefire=>e.Kind==FireEventKind.Prefire,
        _=>e.Kind==FireEventKind.CycleComplete,
    };
}
