// AttackGainValue array R2: all three physical mutations used force-Rebuild.
// Every named runtime RED exited 1 after build 0; exact original bytes restored,
// restore build 0 and the same native21 corpus GREEN 0. Actual raw logs/exit sidecars:
// work/szg-repair-20261008T124624/perf-audit/attack-gain-arrays/r2-v4/results.json
// Compiled fixture before these comment-only annotations: 91D07414D79BAD0E0C22E567506A9BC71371DEBDC70D876924680A748CD60785
// array-value-boosted-consumption: System.InvalidOperationException: Attack gain arrays: equal gain-per-cost must keep the first native hand card before an unaffordable second option; case=first-best-tie VALUE:0000000000000000
// array-native-boost-plain-order: System.InvalidOperationException: Attack gain arrays: native sampling and external fallback call order must match; case=absent-owner
// array-duplicate-selector-before-throw: System.InvalidOperationException: Attack gain arrays: duplicate enemy must sample its repeated HP selector before throwing and never begin plain HP; case=duplicate-enemy
// Native21 baseline was generated from the original implementation, never from the candidate.
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CoopBots.Kernel;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Hooks.Damage;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters.Mocks;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

internal static class AttackGainArrayScenarios
{
    internal static void RunIsolated()
    {
        // R2: real Apply(Boosted)->Apply(Plain) mutation, Rebuild=0, child=1,
        // parent=1: "Isolated native attack-gain corpus failed: exit=1".
        // Restored exact source bytes, Rebuild=0 and native21 parent/child=0.
        // Raw logs, exit sidecars and physical DLL epochs:
        // work/szg-repair-20261008T124624/perf-audit/attack-gain-arrays/r2-isolation/proof.json
        NativeCorpusIsolation.Run("--attack-gain-array-only", "Isolated native attack-gain corpus");
    }

    static bool watch, externalCounterEffect;
    static int fallbackCalls, getterCalls;
    static readonly List<string> trace = new();
    static readonly Dictionary<object,string> labels = new(ReferenceEqualityComparer.Instance);
    static KernelSession? boosted, plain;
    static Player? injectedOwner;
    static IReadOnlyList<CardModel>? injectedHand;
    static readonly MethodInfo gain = AccessTools.Method(typeof(KernelTeamCoordination), "AttackGainValue");
    static string Label(object? value) => value is null ? "null" : labels.GetValueOrDefault(value, value.GetType().Name);
    static string Branch(KernelSession session) => ReferenceEquals(session,boosted) ? "boosted" : ReferenceEquals(session,plain) ? "plain" : "other";
    static void Sample(KernelSession __instance, MethodBase __originalMethod, object[] __args)
    { if(watch) trace.Add(Branch(__instance)+":"+__originalMethod.Name+":"+string.Join(',',__args.Select(Label))); }
    static void NativeGetter(object[] __args)
    {
        if(!watch)return;
        getterCalls++;
        trace.Add("native-getter:"+getterCalls+":"+string.Join(',',__args.Select(a=>a is decimal d?d.ToString(CultureInfo.InvariantCulture):a is Enum?Convert.ToInt64(a).ToString():Label(a))));
    }
    static void NativeGetterResult(ref decimal __result)
    {
        // External evolving state makes eliminating/reordering getter calls
        // observable. Original native method still executes, then this test-only
        // postfix changes its result; production never receives this hook.
        if(watch && externalCounterEffect)__result+=getterCalls%3;
    }
    static void NativeFallback(AbstractModel listener, ModifyDamageMirrorContext context)
    {
        if(watch && listener is StrengthPower power)
        {
            fallbackCalls++;
            trace.Add("fallback:"+fallbackCalls+":"+Label(power.Owner)+":"+context.Amount.ToString(CultureInfo.InvariantCulture));
        }
    }
    static void HandOverride(Player player, ref IReadOnlyList<CardModel> __result)
    { if(watch && player==injectedOwner && injectedHand is not null)__result=injectedHand; }
    static string Snapshot(KernelSession s,Player[] party)=>s.PartyStateText()+"/"+s.RoundsAdvanced+":"+s.EnemyPhaseCompleted+":"+string.Join(';',party.Select(p=>p.NetId+":"+s.IsReady(p)+":"+s.CanAct(p)));
    static string Bits(double value)=>BitConverter.DoubleToInt64Bits(value).ToString("X16");
    static string OutcomeForComparison(string outcome,string key)
    {
        // AbstractModel.ToString appends RuntimeHelpers.GetHashCode(this).
        // The original corpus is kept verbatim. Only this malformed-card case
        // compares the stable exception family/text/Card.Id rather than a CLR
        // allocation identity; all values, traces and physical states stay exact.
        // Actual full-run evidence: array-full-diagnostic.run.log (11854148 vs 12855016).
        if(key!="duplicate-card" || !outcome.StartsWith("THROW:ArgumentException:")
            || !outcome.Contains("Key: CARD."))return outcome;
        var start=outcome.LastIndexOf(" (");
        return start>=0 && outcome.EndsWith(')')
            && int.TryParse(outcome.AsSpan(start+2,outcome.Length-start-3),out _)
                ? outcome[..start] : outcome;
    }
    static bool Format(ref string __result){__result="";return false;}
    static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException("Attack gain arrays: "+why);}

    internal static void Run(bool record=false)
    {
        TestEnvironment.Ensure();var h=new Harmony("coopbots.private-attack-gain-arrays");
        h.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString),"GetFormattedText"),prefix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(Format)));
        h.Patch(AccessTools.Method(typeof(ModifyDamageMirrors),"InvokeOriginalAdditive"),prefix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(NativeFallback)));
        h.Patch(AccessTools.Method(typeof(StrengthPower),"ModifyDamageAdditive"),prefix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(NativeGetter)),postfix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(NativeGetterResult)));
        foreach(var name in new[]{"Energy","Stars","Hp","Block","AttackCosts","Targets","CardTargetType","AttackDamageHint"})
            h.Patch(AccessTools.Method(typeof(KernelSession),name),prefix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(Sample)));
        h.Patch(AccessTools.Method(typeof(KernelSession),"Hand"),postfix:new HarmonyMethod(typeof(AttackGainArrayScenarios),nameof(HandOverride)));
        try
        {
            var rows=new SortedDictionary<string,string>();
            foreach(var two in new[]{false,true})foreach(var reverse in new[]{false,true})foreach(var lowHp in new[]{false,true})foreach(var volatileGetter in new[]{false,true})
                Case(rows,$"two{two}-reverse{reverse}-low{lowHp}-volatile{volatileGetter}",two,reverse,lowHp,volatileGetter,"");
            foreach(var failure in new[]{"duplicate-player","duplicate-enemy","duplicate-card","absent-owner"})
                Case(rows,failure,true,false,false,true,failure);
            Case(rows,"first-best-tie",false,false,false,false,"",true);
            var path=Path.Combine(AppContext.BaseDirectory,"attack-gain-arrays.baseline.json");
            if(record){File.WriteAllText(path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("RECORDED native fallback/sample/state rows="+rows.Count);return;}
            var expected=JsonSerializer.Deserialize<SortedDictionary<string,string>>(File.ReadAllText(path))!;
            foreach(var row in rows)
            {
                Check(expected.TryGetValue(row.Key,out var old),"recorded original case must exist; case="+row.Key);
                var actual=row.Value;var wanted=old!;
                string Part(string text,string start,string end)=>text.Split(start,2)[1].Split(end,2)[0];
                // Assert the external/native trace before scalar comparison: a
                // reordering getter may also change damage through its evolving
                // external state, but that must not hide the call-order fault.
                Check(Part(actual,"/trace=","/state=")==Part(wanted,"/trace=","/state="),
                    "native sampling and external fallback call order must match; case="+row.Key);
                Check(OutcomeForComparison(actual.Split("/fallback=",2)[0],row.Key)
                    ==OutcomeForComparison(wanted.Split("/fallback=",2)[0],row.Key),
                    "scalar IEEE bits and native exception identity must match; case="+row.Key
                    +" actual="+actual.Split("/fallback=",2)[0]+" wanted="+wanted.Split("/fallback=",2)[0]);
                Check(Part(actual,"/fallback=","/trace=")==Part(wanted,"/fallback=","/trace="),
                    "external fallback and native getter call counts must match; case="+row.Key);
                Check(actual.Split("/state=",2)[1]==wanted.Split("/state=",2)[1],
                    "both original physical branches must remain exact; case="+row.Key);
            }
            Check(expected.Count==rows.Count,"the complete baseline corpus must be consumed");
            Console.WriteLine("PASS: original native fallback/sample/state rows "+rows.Count);
        }
        finally{watch=false;boosted=plain=null;injectedOwner=null;injectedHand=null;h.UnpatchAll(h.Id);}
    }

    static void Case(SortedDictionary<string,string> rows,string key,bool two,bool reverse,bool lowHp,bool volatileGetter,string failure,bool tie=false)
    {
        var(party,c,e)=(ValueTuple<Player[],CombatState,Creature>)AccessTools.Method(typeof(ExpiryValueScenarios),"Board").Invoke(null,null)!;
        var enemies=new List<Creature>{e};
        if(two){var e2=c.CreateCreature(ModelDb.Monster<MockAttackMonster>().ToMutable(),CombatSide.Enemy,"1");c.AddCreature(e2);e2.Monster!.SetUpForCombat();e2.Monster.SetMoveImmediate((MoveState)e2.Monster.MoveStateMachine!.States["ATTACK"],true);e2.SetMaxHpInternal(100);e2.SetCurrentHpInternal(100);enemies.Add(e2);}
        labels.Clear();
        for(var i=0;i<party.Length;i++)
        {
            var p=party[i];labels[p]="owner"+i;labels[p.Creature]="player"+i;
            ModelDb.Power<StrengthPower>().ToMutable().ApplyInternal(p.Creature,i+2,true);
            CardModel[] templates=tie?(i==0?[ModelDb.Card<StrikeIronclad>(),ModelDb.Card<Bash>()]:[])
                :i==0?[ModelDb.Card<StrikeIronclad>(),ModelDb.Card<Anger>(),ModelDb.Card<Thunderclap>(),ModelDb.Card<Whirlwind>(),ModelDb.Card<Bully>()]
                :[ModelDb.Card<StrikeIronclad>(),ModelDb.Card<TwinStrike>(),ModelDb.Card<DaggerSpray>()];
            for(var card=0;card<templates.Length;card++){var created=c.CreateCard(templates[card],p);if(tie)created.DynamicVars.Damage.BaseValue=card==0?4:10;p.PlayerCombatState!.Hand.AddInternal(created);labels[created]="card"+i+"-"+card;}
            if(tie&&i==0)p.PlayerCombatState!.LoseEnergy(1);
        }
        for(var i=0;i<enemies.Count;i++){labels[enemies[i]]="enemy"+i;ModelDb.Power<VulnerablePower>().ToMutable().ApplyInternal(enemies[i],i+2,true);if(!tie)enemies[i].GainBlockInternal(i+3);if(lowHp)enemies[i].SetCurrentHpInternal(9+i);}
        boosted=KernelSession.Capture(c);plain=boosted.WithoutEnemyVulnerable();
        IReadOnlyList<Player> actors=reverse?party.Reverse().ToArray():party;
        var targets=enemies.ToArray();injectedOwner=null;injectedHand=null;
        if(failure=="duplicate-player")actors=[party[0],party[0]];
        if(failure=="duplicate-enemy")targets=[e,e];
        if(failure=="duplicate-card"){injectedOwner=party[0];var card=party[0].PlayerCombatState!.Hand.Cards[0];injectedHand=[card,card];}
        if(failure=="absent-owner"){actors=[party[0]];injectedOwner=party[0];injectedHand=[party[1].PlayerCombatState!.Hand.Cards[0]];}
        var before=Snapshot(boosted,party)+Snapshot(plain,party);
        trace.Clear();fallbackCalls=getterCalls=0;externalCounterEffect=volatileGetter;watch=true;
        string outcome;
        try{outcome="VALUE:"+Bits((double)gain.Invoke(null,[boosted,plain,actors,targets])!);}
        catch(TargetInvocationException ex){outcome="THROW:"+ex.InnerException!.GetType().Name+":"+ex.InnerException.Message;}
        finally{watch=false;injectedOwner=null;injectedHand=null;}
        if(failure=="")Check(outcome.StartsWith("VALUE:")&&fallbackCalls>0&&fallbackCalls==getterCalls,"unregistered native additive getter must actually execute once per fallback publication; case="+key);
        else Check(outcome.StartsWith("THROW:"+(failure=="absent-owner"?"KeyNotFoundException":"ArgumentException")),"malformed inputs must preserve the original dictionary exception family; case="+key);
        if(failure=="duplicate-player")Check(trace.Count(x=>x=="boosted:Energy:owner0")==2 && !trace.Any(x=>x.Contains(":Stars:")),
            "duplicate owner must sample its repeated energy selector before throwing and never begin stars; case="+key);
        if(failure=="duplicate-enemy")Check(trace.Count(x=>x=="boosted:Hp:enemy0")==2 && !trace.Any(x=>x.StartsWith("plain:Hp:")),
            "duplicate enemy must sample its repeated HP selector before throwing and never begin plain HP; case="+key);
        if(failure=="duplicate-card")Check(trace.Count(x=>x=="boosted:AttackCosts:card0-0")==2 && !trace.Any(x=>x.Contains(":Targets:")),
            "duplicate card must sample its repeated cost selector before throwing and never begin targets; case="+key);
        Check(Snapshot(boosted,party)+Snapshot(plain,party)==before,"local projected budgets must not change either physical native branch; case="+key);
        if(tie)Check(outcome=="VALUE:"+Bits(3),"equal gain-per-cost must keep the first native hand card before an unaffordable second option; case="+key+" "+outcome);
        rows[key]=outcome+"/fallback="+fallbackCalls+"/native="+getterCalls+"/trace="+JsonSerializer.Serialize(trace)+"/state="+before;
    }
}
