namespace GunMix.Core.Fire;

public enum FireMode { Single, Auto, Burst, Bolt }
public enum FireEventKind { Ready, ContextChanged, TriggerUp, TriggerDown, Prefire, ShotCommitted, DryFire, CycleComplete }
public enum LayerFireTrigger { EveryShot, OrdinaryShot, LastRound, Release, DryFire, Prefire, CycleComplete }
public enum ReleasePolicy { NaturalTail, FadeOnRelease, StopOnRelease }
public sealed record FireContext(bool Ads = false, bool Suppressed = false, string Perspective = "player", string Environment = "outdoor", string Distance = "near");
public sealed record TriggerInput(long Frame, FireEventKind Kind, FireContext? Context = null);
public sealed record DomainEvent(string Id, FireEventKind Kind, long Frame, int ShotIndex, int AmmoBefore, bool LastRound, FireContext Context);
public sealed record FireResult(IReadOnlyList<DomainEvent> Events, int AmmoAfter, long ReleaseFrame);

public sealed class FireScenario
{
    public int SampleRate { get; set; } = 48000;
    public int Rpm { get; set; } = 600;
    public int Ammo { get; set; } = 30;
    public int AttemptLimit { get; set; } = 6;
    public int BurstSize { get; set; } = 3;
    public FireMode Mode { get; set; } = FireMode.Auto;
    public long ReadyFrame { get; set; }
    public long CycleFrames { get; set; }
    public bool CompleteBurstOnRelease { get; set; }
    public FireContext Context { get; set; } = new();
    public List<TriggerInput> Inputs { get; set; } = [];
}

/// <summary>Project-authored deterministic fire rules; never claims recovered COD predicates.</summary>
public static class FireController
{
    public const string Version = "project-fire-v3";
    public static long FrameAt(int n, int rate, int rpm) => checked((long)decimal.Round((decimal)n * rate * 60 / rpm, 0, MidpointRounding.AwayFromZero));
    private sealed record Work(long Frame, int Priority, int Sequence, TriggerInput? Input, int Generation, int Attempt, long Origin);
    public static FireResult Compile(FireScenario s)
    {
        if (s.SampleRate is < 8000 or > 192000 || s.Rpm is < 6 or > 1800 || s.Ammo is < 0 or > 10000 ||
            s.AttemptLimit is < 1 or > 100 || s.BurstSize is < 1 or > 100 || s.ReadyFrame < 0 || s.CycleFrames < 0 ||
            s.ReadyFrame > 60L*s.SampleRate || s.CycleFrames > 60L*s.SampleRate || s.Inputs.Count > 1024 ||
            s.Inputs.Any(i => i.Frame < 0 || i.Frame > 60L*s.SampleRate || i.Kind is not (FireEventKind.TriggerDown or FireEventKind.TriggerUp or FireEventKind.ContextChanged or FireEventKind.Ready)))
            throw new ArgumentException("场景参数无效：检查射速、弹药、动作时间及输入事件（最长 60 秒）。");
        var queue = new PriorityQueue<Work, (long, int, int)>(); int sequence=0;
        void Enqueue(Work w) => queue.Enqueue(w, (w.Frame,w.Priority,w.Sequence));
        foreach(var input in s.Inputs)
            Enqueue(new(input.Frame, input.Kind switch { FireEventKind.Ready=>0, FireEventKind.ContextChanged=>1, FireEventKind.TriggerUp=>2, _=>3 }, sequence++, input, 0, 0, 0));
        var events = new List<DomainEvent>(); int ammo=s.Ammo, generation=0, shot=0; bool held=false, dry=false;
        long ready=s.ReadyFrame,release=0;var context=s.Context;
        void Emit(FireEventKind kind,long frame,bool last=false) => events.Add(new($"event-{events.Count}",kind,frame,shot,ammo,last,context));
        while(queue.TryDequeue(out var work,out _))
        {
            if(work.Input is { } input)
            {
                if(input.Kind==FireEventKind.ContextChanged){context=input.Context??context;Emit(input.Kind,work.Frame);continue;}
                if(input.Kind==FireEventKind.Ready){ready=work.Frame;Emit(input.Kind,work.Frame);continue;}
                if(input.Kind==FireEventKind.TriggerUp){held=false;release=work.Frame;Emit(input.Kind,work.Frame);if(!(s.Mode==FireMode.Burst&&s.CompleteBurstOnRelease))generation++;continue;}
                if(held)continue;
                // A press followed by a release at the same frame has an empty envelope.
                if(s.Inputs.Skip(work.Sequence+1).Any(i=>i.Kind==FireEventKind.TriggerUp&&i.Frame==work.Frame))continue;
                held=true;dry=false;generation++;Emit(FireEventKind.TriggerDown,work.Frame);Emit(FireEventKind.Prefire,work.Frame);
                Enqueue(new(work.Frame,4,sequence++,null,generation,0,work.Frame));continue;
            }
            if(work.Generation!=generation || (!held && !(s.Mode==FireMode.Burst&&s.CompleteBurstOnRelease)))continue;
            if(work.Priority==0){Emit(FireEventKind.CycleComplete,work.Frame);continue;}
            if(work.Frame>=ready)
            {
                if(ammo==0){if(!dry){Emit(FireEventKind.DryFire,work.Frame);dry=true;}continue;}
                Emit(FireEventKind.ShotCommitted,work.Frame,ammo==1);ammo--;shot++;
                ready=checked(work.Frame+s.CycleFrames);
                if(s.CycleFrames>0)Enqueue(new(ready,0,sequence++,null,generation,0,0));
            }
            int limit=s.Mode switch { FireMode.Single or FireMode.Bolt=>1,FireMode.Burst=>Math.Min(s.AttemptLimit,s.BurstSize),_=>s.AttemptLimit };
            if(work.Attempt+1<limit)Enqueue(new(work.Origin+FrameAt(work.Attempt+1,s.SampleRate,s.Rpm),4,sequence++,null,generation,work.Attempt+1,work.Origin));
        }
        // Cycle completion belongs to the shot even after release. Add independent action events.
        if(s.CycleFrames>0)
        {
            events.RemoveAll(e=>e.Kind==FireEventKind.CycleComplete);
            foreach(var e in events.Where(e=>e.Kind==FireEventKind.ShotCommitted).ToList())
                events.Add(new($"{e.Id}-cycle",FireEventKind.CycleComplete,e.Frame+s.CycleFrames,e.ShotIndex,e.AmmoBefore-1,false,e.Context));
        }
        return new(events.OrderBy(e=>e.Frame).ThenBy(e=>e.Kind switch { FireEventKind.Ready or FireEventKind.CycleComplete=>0,FireEventKind.ContextChanged=>1,FireEventKind.TriggerUp=>2,_=>3 }).ToArray(),ammo,release);
    }
}
