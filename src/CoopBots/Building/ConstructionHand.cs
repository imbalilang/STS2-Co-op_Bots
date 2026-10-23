using System.Numerics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Native rules draw before Prepared's discard, exhaust before Burning Pact's
// draw, and auto-play Sly after discard hooks. The construction model uses a
// bounded, deterministic hand search for those transitions (owner 2026-10-01).
// It does not reproduce all combat hooks or inspect the live draw order.
internal static class ConstructionHand
{
    internal readonly record struct Slot(ConstructionCard? Model, CardProfile.Facts? Facts,
        bool Status = false, double EndDamage = 0, double PlayBlock = 0, double PlayBlockEvents = 0);
    internal readonly record struct Context(double Energy, double Incoming, double Health,
        int Enemies = 1, bool Chains = false, double WitherDamage = 0, int Counter = 0,
        double ExhaustDraw = 0, double ExhaustBlock = 0, bool Corruption = false, int InitialWithers = 0, double DeckSize = 0, int ExhaustBefore = 0,
        IReadOnlyList<double>? EnemyHealth = null)
    {
        // A single bounded continuation ranks an exhaust target. Its value is
        // not paid here: the opening/cycle caller already plays the retained deck.
        internal bool DisableExhaustRetention { get; init; }
        internal int StartDiscard { get; init; }
        internal double Juggling { get; init; }
        internal double PhantomBlades { get; init; }
        internal double SkillEnrage { get; init; }
        internal double BlockTriggerDamage { get; init; }
        internal double OwnerHp { get; init; }
        internal double OwnerMaxHp { get; init; }
    }
    internal sealed record Result(int Played, int Removed, int Exhausted, int Drawn,
        double EnergySpent, double EnergyLeft, double Damage, double Block, double SelfLoss,
        double StatusDamage, int Generated, int Counter, int StatusExhausted, int WitherExhausted,
        int NextDraw, int NextEnergy, double Utility, int[] Targets)
    {
        internal int ExhaustTriggers { get; init; }
        internal double TriggeredPoison { get; init; }
        internal double TriggeredBlock { get; init; }
        internal double BlockEvents { get; init; }
        internal double ActiveBlockTriggerDamage { get; init; }
        internal double BlockTriggeredDamage { get; init; }
        internal double EnemyAttackDamage { get; init; }
        internal double ProjectedHpLoss { get; init; }
        internal double FirstPoisonTickDamage { get; init; }
        internal double TemporaryDamage { get; init; }
        internal double TemporaryBlock { get; init; }
        internal int AutomaticPlayed { get; init; }
        internal ulong Executions { get; init; }
        internal double CopiedAttackDamage { get; init; }
        internal double FirstShivDamage { get; init; }
        internal int StartDiscardCount { get; init; }
    }

    private struct State
    {
        internal int Played, AutomaticPlayed, Removed, Exhausted, Drawn, BoundPlayed, Generated, Counter, HandWithers;
        internal double Energy, Spent, Damage, Block, Loss, Vulnerable, Weak, StrengthDown;
        internal double BlockEvents;
        internal double Strength, Dexterity, Hits, Blocks, Payoff, Future;
        internal double TemporaryStrength, TemporaryDexterity, TemporaryDamage, TemporaryBlock;
        internal int NextDraw, NextEnergy, StatusExhausted, WitherExhausted;
        internal int ExhaustTriggers;
        internal double PoisonPerDraw, BlockPerAttack, TriggeredPoison, TriggeredBlock;
        internal double Poison, PoisonOthers, AoeDamage, AoeHits, AoePayoff;
        internal bool NoDraw;
        internal ulong Targets;
        internal double ExhaustRetentionLoss;
        internal int Burst, AttacksPlayed, SkillsPlayed, StartDiscarded;
        internal ulong Executions;
        internal double CopiedAttackDamage, FirstShivDamage;
        internal double BlockTrigger, BlockTriggeredDamage;
        internal bool ShivPlayed;
    }
    // One scratch buffer per executing thread; nested callers take a separate
    // buffer. Contents never survive as semantic state between searches.
    private readonly record struct PlayInfo(double Cost,double Energy,double Loss,double Damage,double Block,
        double Vulnerable,double Weak,double StrengthDown,double Strength,double Dexterity,double Hits,
        double Blocks,double Payoff,double Future,int NextDraw,int NextEnergy,int Draw,int ExtraPlays,
        bool Exhaust,bool Cleanup);
    private readonly record struct DiscardContinuation(int Mask,int Next,int ExtraPlays,
        int AutoCount = -1, bool ForceExhaust = false, int ExhaustIndex = -1, int SourceIndex = -1);
    private sealed class Workspace
    {
        internal readonly Dictionary<(uint,int,int,bool,int,int,bool),int> Indices = new();
        internal readonly List<State> States = new();
        internal readonly List<double> Values = new();
        internal readonly List<(double Value,int Index)> Ranks = new();
        internal readonly State[] Frontier = new State[48];
        internal readonly PlayInfo[] Plays = new PlayInfo[Window];
        internal readonly DiscardContinuation[] Continuations = new DiscardContinuation[Window+1];
    }
    [ThreadStatic] private static Stack<Workspace>? spares;
    internal const int Window = 9;
    private static int Beam => ConstructionBudget.Current.Beam;
    private static int Count(int mask) => BitOperations.PopCount((uint)mask);
    private static int Hand(State s) => ((1 << s.Drawn) - 1) & ~(s.Played | s.Removed);
    private static int Amount(ConstructionCard card, string key, int fallback = 1)
        => card.IntAmount(key, fallback);

    internal static Result Solve(Slot[] input, int opening, Context context)
    {
        var pool = spares ??= new();
        var work = pool.Count > 0 ? pool.Pop() : new Workspace();
        try { return Search(input, opening, context, work); }
        finally { pool.Push(work); }
    }

    private static Result Search(Slot[] input, int opening, Context context, Workspace work)
    {
        var cards = input.Length <= Window ? input : input.Take(Window).ToArray();
        var retention = ExhaustRetention(cards, opening, context);
        var bound=0;var boundCount=0;
        var attacks=0;var sly=0;var statuses=0;var harmful=0;
        for(var i=0;i<cards.Length;i++)
        {
            var c=cards[i].Model;var f=cards[i].Facts;
            if(c?.Type==CardType.Attack)attacks|=1<<i;
            if(c?.Keywords.Contains(CardKeyword.Sly)==true)sly|=1<<i;
            if(cards[i].Status)statuses|=1<<i;
            if(cards[i].EndDamage!=0)harmful|=1<<i;
            if(c is null || f is null)continue;
            var free=context.Corruption && c.Type==CardType.Skill;
            work.Plays[i]=new(free?0:Math.Max(0,f.Cost),f.Energy,f.HpLoss,
                (c.Kind is nameof(FiendFire)?0:f.Damage*(f.IsAoe?context.Enemies:1))+f.Doom*2
                    + f.Effects.GeneratedShivs * 4,
                (c.Kind is nameof(SecondWind)?0:f.Block)+cards[i].PlayBlock,f.Vulnerable,f.Weak,f.StrengthDown,
                c.Kind is nameof(Inflame) ? c.Amount("StrengthPower") * (1 + Math.Max(0,c.GetEnchantedReplayCount())) : 0,
                c.Kind is nameof(Footwork) ? c.Amount("DexterityPower") * (1 + Math.Max(0,c.GetEnchantedReplayCount())) : 0,
                c.Type==CardType.Attack?Math.Max(1,f.Hits):f.Effects.GeneratedShivs,
                f.Roles.Contains("block")?1:0,SustainedDeckValue.DamagePerVulnerable(c),
                // Allied output is support credit, not an owner's immediate
                // lethal that can erase this seat's incoming damage.
                f.Scaling+f.NextTurnDraw+f.NextTurnEnergy+f.Effects.AlliedShivDamage+f.AlliedEnergyValue+f.AlliedDrawValue
                    + (c.Kind is nameof(Pyre) ? 4*c.Amount("Energy") : c.Kind is nameof(MachineLearning) ? 3*c.Amount("Cards",1) : 0),
                (int)f.NextTurnDraw+(c.Kind is nameof(MachineLearning)?c.IntAmount("Cards"):0),
                (int)f.NextTurnEnergy+(c.Kind is nameof(Pyre)?c.IntAmount("Energy"):0),
                c.Kind is nameof(BurningPact) or nameof(CalculatedGamble)?0:(int)Math.Clamp(f.Draw,0,Window),
                (int)f.Effects.GeneratedShivs,free || c.Keywords.Contains(CardKeyword.Exhaust),SustainedDeckValue.Cleanup(c));
        }
        if(context.Chains)for(var i=0;i<cards.Length && boundCount<3;i++)
            if(cards[i].Model?.HasAffliction != true){bound|=1<<i;boundCount++;}
        var initial = new State { Drawn = Math.Min(cards.Length, Math.Max(0, opening)), Energy = context.Energy, Counter = context.Counter,
            HandWithers=context.InitialWithers,Generated=context.InitialWithers,ExhaustTriggers=context.ExhaustBefore,
            BlockTrigger=context.BlockTriggerDamage };
        var best = initial; var bestValue = PlanningValue(initial);
        var frontier = work.Frontier;
        frontier[0] = initial;
        var frontierCount = 1;
        if (context.StartDiscard > 0)
        {
            bestValue = double.NegativeInfinity;
            work.Indices.Clear(); work.States.Clear(); work.Values.Clear(); work.Ranks.Clear();
            var held = Hand(initial); var needed = Math.Min(Count(held), context.StartDiscard);
            for (var subset = held;; subset = (subset - 1) & held)
            {
                if (Count(subset) == needed)
                {
                    var filtered = initial; filtered.Removed |= subset;filtered.StartDiscarded=subset;
                    ResolveDiscard(filtered, subset, 0, 0, -1, 0);
                }
                if (subset == 0) break;
            }
            frontierCount = Math.Min(Beam, work.States.Count);
            for (var i = 0; i < frontierCount; i++) frontier[i] = work.States[i];
        }
        // Every explicit or Sly play consumes one unique sampled card. Cards
        // discarded this turn never become independently playable from discard.
        for (var depth = 0; depth < cards.Length && frontierCount > 0; depth++)
        {
            work.Indices.Clear(); work.States.Clear(); work.Values.Clear(); work.Ranks.Clear();
            for (var stateIndex = 0; stateIndex < frontierCount; stateIndex++)
            {
                var s = frontier[stateIndex];
                for (var i = 0; i < s.Drawn; i++)
                {
                    if ((Hand(s) & (1 << i)) == 0 || cards[i].Facts is not { } f || f.Unplayable || f.AutoOnly) continue;
                    var cost = Cost(s,i,false);
                    if (cost > s.Energy || (bound & (1 << i)) != 0 && s.BoundPlayed != 0) continue;
                    if (!work.Plays[i].Cleanup && cards[i].Model?.Kind is not (nameof(Cascade) or nameof(Havoc)))
                    {
                        if (StartPlay(s, i, false, out var played))
                            Keep(Finish(played, i, (int)f.Effects.GeneratedShivs));
                    }
                    else Play(s, i, false, 0);
                }
            }
            for (var i = 0; i < work.States.Count; i++)
                work.Ranks.Add((work.Values[i] + .01 * work.States[i].Energy, i));
            // LINQ's old OrderBy was stable. The insertion index preserves its
            // tie order, including entries replaced by a better path.
            work.Ranks.Sort(static (a,b) => {
                var value = b.Value.CompareTo(a.Value);
                return value != 0 ? value : a.Index.CompareTo(b.Index);
            });
            frontierCount = Math.Min(Beam, work.Ranks.Count);
            for (var i = 0; i < frontierCount; i++) frontier[i] = work.States[work.Ranks[i].Index];
        }
        void Keep(State played)
        {
            // Window=9: three 9-bit masks, 4-bit drawn count, 1 bound bit.
            var masks=(uint)(played.Played | played.Removed<<9 | played.Exhausted<<18
                | played.Drawn<<27 | played.BoundPlayed<<31);
            var key = (masks,played.HandWithers,played.Counter,played.NoDraw,played.Burst,played.AttacksPlayed,played.ShivPlayed);
            var value = PlanningValue(played);
            if (!work.Indices.TryGetValue(key, out var index))
            {
                work.Indices.Add(key, work.States.Count);
                work.States.Add(played); work.Values.Add(value);
            }
            else if (value > work.Values[index] + 1e-9
                || Math.Abs(value-work.Values[index]) < 1e-9 && played.Energy > work.States[index].Energy)
            { work.States[index] = played; work.Values[index] = value; }
            if (value > bestValue + 1e-9) { bestValue = value; best = played; }
        }
        var targets = Enumerable.Range(0,cards.Length).Select(i => (int)((best.Targets >> (i*4)) & 15)).ToArray();
        return new(best.Played, best.Removed, best.Exhausted, best.Drawn, best.Spent, best.Energy,
            Damage(best), Block(best), best.Loss, StatusDamage(best), best.Generated, best.Counter,
            best.StatusExhausted, best.WitherExhausted, best.NextDraw, best.NextEnergy, Value(best), targets)
            { ExhaustTriggers=best.ExhaustTriggers, TriggeredPoison=best.TriggeredPoison, TriggeredBlock=best.TriggeredBlock,
                BlockEvents=best.BlockEvents, ActiveBlockTriggerDamage=best.BlockTrigger, BlockTriggeredDamage=best.BlockTriggeredDamage,
                EnemyAttackDamage=EnemyAttackDamage(best), ProjectedHpLoss=HpLoss(best), FirstPoisonTickDamage=PoisonTickDamage(best),
                TemporaryDamage=best.TemporaryDamage, TemporaryBlock=best.TemporaryBlock,
                AutomaticPlayed=best.AutomaticPlayed, Executions=best.Executions,
                CopiedAttackDamage=best.CopiedAttackDamage, FirstShivDamage=best.FirstShivDamage,
                StartDiscardCount=Count(best.StartDiscarded) };

        double PlanningValue(State s) => Value(s) - (Damage(s) >= context.Health ? 0 : s.ExhaustRetentionLoss);

        double Damage(State s) => (s.Damage + .5*s.Vulnerable*s.Payoff) * (s.Vulnerable > 0 ? 1.25 : 1)
            + s.BlockTriggeredDamage;
        double Block(State s) => s.Block;
        double TargetHealth(State s, int target)
        {
            var count=Math.Max(1,context.Enemies);
            var damage=Damage(s);
            var area=Math.Clamp((s.AoeDamage+.5*s.Vulnerable*s.AoePayoff)
                *(s.Vulnerable>0?1.25:1),0,Math.Max(0,damage));
            // The macro model has finite target capacities, not the live board.
            // Single-target poison shares its primary target with ordinary hits;
            // AoE hits consume capacity on every target before the poison tick.
            var capacity=context.EnemyHealth is { } hp && hp.Count==count ? hp[target] : context.Health/count;
            return Math.Max(0,capacity-area/count-(target==0?damage-area:0));
        }
        double PoisonTickDamage(State s)
        {
            if(s.Poison<=0 && s.PoisonOthers<=0 || Damage(s)>=context.Health)return 0;
            double total=0;
            for(var i=0;i<Math.Max(1,context.Enemies);i++)
                total+=Math.Min(TargetHealth(s,i),i==0?s.Poison:s.PoisonOthers);
            return total;
        }
        double PoisonCredit(State s)
        {
            if(s.Poison<=0 && s.PoisonOthers<=0)return 0;
            double total=0;
            for(var i=0;i<Math.Max(1,context.Enemies);i++)
                total+=Math.Min(TargetHealth(s,i),2*(i==0?s.Poison:s.PoisonOthers));
            return total;
        }
        double EnemyAttackDamage(State s)
        {
            if(Damage(s)>=context.Health)return 0;
            var pressure=Math.Max(0,(context.Incoming+context.SkillEnrage*s.SkillsPlayed)*(s.Weak>0?.75:1)-s.StrengthDown);
            if(s.Poison<=0 && s.PoisonOthers<=0)return pressure;
            var count=Math.Max(1,context.Enemies);
            var alive=0;
            for(var i=0;i<count;i++)if(TargetHealth(s,i)> (i==0?s.Poison:s.PoisonOthers))alive++;
            // Native first-tick poison precedes enemy attacks. Partial kills
            // remove only those targets' equal macro share of incoming pressure.
            return pressure*alive/count;
        }
        double StatusDamage(State s)
        {
            double damage = s.HandWithers * context.WitherDamage;
            var held=Hand(s)&harmful;
            while(held!=0)
            {
                var i=BitOperations.TrailingZeroCount((uint)held);
                damage+=cards[i].EndDamage; held&=held-1;
            }
            return damage;
        }
        double HpLoss(State s)
        {
            // A kill ends combat before hand-end statuses or enemy attacks.
            var incoming = Damage(s) >= context.Health ? 0 : EnemyAttackDamage(s) + StatusDamage(s);
            return s.Loss + Math.Max(0,incoming-Block(s));
        }
        double Value(State s)
        {
            var damage = Damage(s);
            return Math.Min(context.Health,damage)-SurvivalValue.LossCost(context.OwnerHp,context.OwnerMaxHp,HpLoss(s))
                +(damage>=context.Health?0:s.Future+PoisonCredit(s));
        }
        void GainBlockEvents(ref State s, double count)
        {
            s.BlockEvents += count;
            s.BlockTriggeredDamage += ConstructionCombatMath.BlockTriggerDamage(s.BlockTrigger,count,context.Health);
        }
        State Draw(State s, int count)
        {
            if (!s.NoDraw)
            {
                var before = s.Drawn;
                s.Drawn = Math.Min(cards.Length, s.Drawn + Math.Max(0, count));
                var poison = (s.Drawn - before) * s.PoisonPerDraw;
                s.TriggeredPoison += poison;
                s.Poison += poison; s.PoisonOthers += poison;
                // Poison is future enemy-turn damage, never a direct lethal
                // that erases hand-end Wither damage in this hand search.
                // The existing two-tick credit is capped per target in Value;
                // the first tick can prevent enemy attacks, never held statuses.
            }
            return s;
        }
        State Finish(State s, int sourceIndex, int extraPlays = 0)
        {
            if (sourceIndex < 0) return s;
            var executions = Math.Max(1,(int)((s.Executions >> (sourceIndex * 4)) & 15));
            var replay = 1 + Math.Max(0,cards[sourceIndex].Model!.GetEnchantedReplayCount());
            extraPlays = extraPlays * executions / replay;
            s.ExhaustTriggers += extraPlays;
            var rageBlock = s.BlockPerAttack * extraPlays;
            s.Block += rageBlock; s.TriggeredBlock += rageBlock;
            if (s.BlockPerAttack > 0) GainBlockEvents(ref s,extraPlays);
            if (context.WitherDamage <= 0) return s;
            var ownPlays=executions;
            var total=s.Counter+ownPlays+extraPlays;
            var generated=total/6;
            s.Counter=total%6; s.Generated+=generated; s.HandWithers+=generated;
            return s;
        }
        double Cost(State s,int i,bool automatic) => automatic ? 0
            : cards[i].Model?.Kind is nameof(Cascade) ? Math.Max(0,s.Energy)
            : cards[i].Model?.Kind is nameof(Midnight) ? Math.Max(0,work.Plays[i].Cost-s.ExhaustTriggers)
            : work.Plays[i].Cost;
        bool StartPlay(State original, int i, bool automatic, out State s)
        {
            s = original;
            ref readonly var p=ref work.Plays[i];
            // Chains guards automatic Sly plays as well.
            if ((bound&(1<<i))!=0 && s.BoundPlayed!=0) return false;
            var cost=Cost(s,i,automatic);
            if (cost>s.Energy) return false;
            s.Played|=1<<i; s.Energy-=cost; s.Spent+=cost;
            if (automatic) s.AutomaticPlayed |= 1 << i;
            if ((bound&(1<<i))!=0) s.BoundPlayed=1;
            if(p.Exhaust){s.Exhausted|=1<<i;s.ExhaustTriggers++;}
            s.Energy+=p.Energy; s.Loss+=p.Loss;
            var facts=cards[i].Facts!;
            var effects=facts.Effects;
            var replay=1+Math.Max(0,cards[i].Model!.GetEnchantedReplayCount());
            var skillReplay = cards[i].Model!.Type == CardType.Skill && s.Burst > 0;
            if (skillReplay) s.Burst--;
            // Native Burst adds one execution to the already enchanted count.
            // CardProfile contains the base enchanted body, so an extra body
            // contributes only 1/replay of those aggregate facts.
            var executions = replay + (skillReplay ? 1 : 0);
            var effectFactor = executions / (double)replay;
            if(cards[i].Model!.Type==CardType.Skill)s.SkillsPlayed+=executions;
            s.Executions += (ulong)executions << (i * 4);
            // Stats are stocks at this action, not a multiplier on all prior
            // hits/blocks. Setup Strike deals its body before its grant; only
            // later executions and later payable attacks receive that grant.
            var bodyHits=cards[i].Model!.Type==CardType.Attack && cards[i].Model!.Kind is not nameof(FiendFire)
                ? Math.Max(1,facts.Hits) : 0;
            var hits=bodyHits+effects.GeneratedShivs;
            var repeatedGrant=PayableHands.TemporaryStatShare*effects.TemporaryStrength
                *bodyHits*(replay-1)/2;
            var temporaryDamage=s.TemporaryStrength*hits+repeatedGrant;
            s.Damage+=cards[i].Facts!.DrawPileDamage
                ? Math.Max(0, (context.DeckSize > 0 ? context.DeckSize : cards.Length) - s.Drawn) * (1 + cards[i].Model!.GetEnchantedReplayCount())
                : p.Damage;
            s.Damage+=s.Strength*hits+temporaryDamage;
            s.TemporaryDamage+=temporaryDamage;
            var blockEvents=facts.Block>0 && cards[i].Model!.Kind is not nameof(SecondWind)?replay:0;
            var temporaryBlock=s.TemporaryDexterity*blockEvents;
            s.Block+=p.Block+s.Dexterity*blockEvents+temporaryBlock;
            GainBlockEvents(ref s,blockEvents);
            if (cards[i].Model!.Kind is nameof(Juggernaut))
                s.BlockTrigger += cards[i].Model!.Amount("JuggernautPower") * executions;
            GainBlockEvents(ref s,cards[i].PlayBlockEvents);
            s.TemporaryBlock+=temporaryBlock;
            s.Vulnerable+=p.Vulnerable; s.Weak+=p.Weak; s.StrengthDown+=p.StrengthDown;
            s.Strength+=p.Strength; s.Dexterity+=p.Dexterity; s.Hits+=p.Hits; s.Blocks+=p.Blocks;
            s.TemporaryStrength+=PayableHands.TemporaryStatShare*effects.TemporaryStrength*replay;
            s.TemporaryDexterity+=PayableHands.TemporaryStatShare*effects.TemporaryDexterity*replay;
            s.Payoff+=p.Payoff; s.Future+=p.Future;
            s.Poison+=facts.Poison;
            if(facts.IsAoe)
            {
                s.PoisonOthers+=facts.Poison;
                s.AoeDamage+=p.Damage+original.Strength*bodyHits+original.TemporaryStrength*bodyHits+repeatedGrant;
                s.AoeHits+=p.Hits; s.AoePayoff+=p.Payoff;
            }
            s.NextDraw+=p.NextDraw; s.NextEnergy+=p.NextEnergy;
            if (cards[i].Model!.Type == CardType.Attack)
            {
                var rageBlock = s.BlockPerAttack * replay;
                s.Block += rageBlock; s.TriggeredBlock += rageBlock;
                if (s.BlockPerAttack > 0) GainBlockEvents(ref s,replay);
            }
            s.PoisonPerDraw += effects.PoisonPerDraw * replay;
            s.BlockPerAttack += effects.BlockPerAttack * replay;
            s=Draw(s,p.Draw);
            if (skillReplay)
            {
                // The copied execution pays no energy, but its printed effect
                // and draws execute again. Cleanup below pays both selections.
                s.Damage += (p.Damage + s.Strength * effects.GeneratedShivs) / replay;
                GainBlockEvents(ref s,(blockEvents > 0 ? 1 : 0) + cards[i].PlayBlockEvents / replay);
                s.Block += p.Block / replay + s.Dexterity * (blockEvents > 0 ? 1 : 0) + s.TemporaryDexterity * (blockEvents > 0 ? 1 : 0);
                s.TemporaryBlock += s.TemporaryDexterity * (blockEvents > 0 ? 1 : 0);
                s.Energy += p.Energy / replay; s.Loss += p.Loss / replay;
                s.Vulnerable += p.Vulnerable / replay; s.Weak += p.Weak / replay; s.StrengthDown += p.StrengthDown / replay;
                s.Poison += facts.Poison / replay;
                if (facts.IsAoe) s.PoisonOthers += facts.Poison / replay;
                s.NextDraw += p.NextDraw / replay; s.NextEnergy += p.NextEnergy / replay;
                s.PoisonPerDraw += effects.PoisonPerDraw;
                s.BlockPerAttack += effects.BlockPerAttack;
                s.TemporaryStrength += PayableHands.TemporaryStatShare * effects.TemporaryStrength;
                s.TemporaryDexterity += PayableHands.TemporaryStatShare * effects.TemporaryDexterity;
                s.Future += p.Future / replay;
                s=Draw(s,p.Draw / replay);
            }
            s.Weak += effects.GeneratedShivWeak * effectFactor;
            var shivCount = effects.GeneratedShivs * effectFactor + (cards[i].Model!.Kind is nameof(Shiv) ? 1 : 0);
            if (shivCount > 0 && !s.ShivPlayed)
            {
                s.ShivPlayed = true; s.Damage += context.PhantomBlades; s.FirstShivDamage += context.PhantomBlades;
            }
            var attacksThisPlay = cards[i].Model!.Type == CardType.Attack ? replay : (int)(effects.GeneratedShivs * effectFactor);
            var previousAttacks = s.AttacksPlayed; s.AttacksPlayed += attacksThisPlay;
            if (context.Juggling > 0 && previousAttacks < 3 && s.AttacksPlayed >= 3)
            {
                // Native clones the third attack, with its normal energy cost.
                // Generated Shivs are zero-cost; a paid attack's copy is useful
                // only when the remaining hand budget can really fund it.
                var copyCost = cards[i].Model!.Type == CardType.Attack ? cost : 0;
                var copies = copyCost <= 0 ? context.Juggling : Math.Min(context.Juggling, Math.Floor(s.Energy / copyCost));
                var copyDamage = cards[i].Model!.Type == CardType.Attack ? p.Damage + s.Strength * bodyHits
                    : shivCount > 0 ? p.Damage / shivCount + s.Strength : 0;
                var extra = Math.Max(0, copies * copyDamage);
                s.Energy -= copies * copyCost; s.Spent += copies * copyCost;
                s.Damage += extra; s.CopiedAttackDamage += extra;
                s.ExhaustTriggers += shivCount > 0 ? (int)copies : 0;
            }
            s.Burst += (int)(effects.ReplaySkills * executions);
            if(cards[i].Model?.Kind is nameof(BattleTrance))s.NoDraw=true;
            return true;
        }
        // Resolve discard branches directly into Keep. The continuation stack
        // preserves Sly's ordered Cartesian branches without allocating lists
        // and iterator state machines for each candidate play.
        void Resume(State state,int depth)
        {
            if(depth==0) { Keep(state); return; }
            var next=work.Continuations[depth-1];
            if(next.ExhaustIndex>=0 && (state.Exhausted&(1<<next.ExhaustIndex))==0)
            { state.Exhausted|=1<<next.ExhaustIndex; state.ExhaustTriggers++; }
            if(next.AutoCount>=0) ResolveAuto(state,next.AutoCount,next.ForceExhaust,next.SourceIndex,depth-1);
            else ResolveDiscard(state,next.Mask,next.Next,next.ExtraPlays,next.SourceIndex,depth-1);
            work.Continuations[depth-1]=next;
        }
        void ResolveAuto(State state,int count,bool forceExhaust,int sourceIndex,int depth)
        {
            if(count<=0 || state.Drawn>=cards.Length) { Resume(Finish(state,sourceIndex),depth); return; }
            var i=state.Drawn++;
            work.Continuations[depth]=new(0,0,0,count-1,forceExhaust,forceExhaust?i:-1,sourceIndex);
            // Native autoplay bypasses energy, never a card's unplayability or
            // Chains. Revealed cards leave the draw pile even when unplayable.
            if(cards[i].Facts is not { Unplayable:false } || !Play(state,i,true,depth+1))
            { state.Removed|=1<<i; Resume(state,depth+1); }
        }
        void ResolveDiscard(State state,int mask,int start,int extra,int sourceIndex,int depth)
        {
            for(var j=start;j<cards.Length;j++)
                if((mask&sly&(1<<j))!=0)
                {
                    work.Continuations[depth]=new(mask,j+1,extra,SourceIndex:sourceIndex);
                    if(!Play(state,j,true,depth+1)) Resume(state,depth+1);
                    return;
                }
            Resume(Finish(state,sourceIndex,extra),depth);
        }
        bool Play(State original, int i, bool automatic,int depth)
        {
            if (!StartPlay(original, i, automatic, out var s)) return false;
            var card=cards[i].Model!; var f=cards[i].Facts!;
            if(card.Kind is nameof(Cascade) or nameof(Havoc))
            {
                var count=card.Kind is nameof(Havoc)?1:(automatic?0:(int)original.Energy)+(card.IsUpgraded?1:0);
                ResolveAuto(s,count,card.Kind is nameof(Havoc),i,depth); return true;
            }
            var burns=card.Kind is nameof(BurningPact) or nameof(TrueGrit) or nameof(Cinder) or nameof(SecondWind) or nameof(FiendFire) or nameof(Purity);
            var cleanup=SustainedDeckValue.Cleanup(card);
            var beforeDraw=card.Kind is nameof(BurningPact) or nameof(CalculatedGamble);
            if (!cleanup)
            {
                Resume(Finish(s,i,(int)f.Effects.GeneratedShivs),depth); return true;
            }
            var hand=Hand(s);
            if (card.Kind is nameof(SecondWind)) hand&=~attacks;
            var all=card.Kind is nameof(SecondWind) or nameof(FiendFire) or nameof(CalculatedGamble);
            var executions = (int)((s.Executions >> (i * 4)) & 15);
            var targetCount=all?Count(hand)+s.HandWithers:(card.Kind is nameof(Prepared) or nameof(Purity)?Amount(card,"Cards"):1)*Math.Max(1,executions);
            var optional=card.Kind is nameof(Purity);
            if(all) Choice(hand,s.HandWithers);
            else if(card.Kind is nameof(Cinder) || card.Kind is nameof(TrueGrit) && !card.IsUpgraded)
            {
                // Same paired random target and ascending slot order.
                var count=Count(hand); var n=count+s.HandWithers;
                if(n==0) Choice(0,0);
                else
                {
                    var pick=(i*31+s.Played*17+s.Drawn)%n;
                    if(pick>=count)Choice(0,1);
                    else for(var j=0;j<cards.Length;j++)if((hand&(1<<j))!=0 && pick--==0)
                    { Choice(1<<j,0); break; }
                }
            }
            else
            {
                var needed=Math.Min(targetCount,Count(hand)+s.HandWithers);
                for(var subset=hand;;subset=(subset-1)&hand)
                {
                    var n=Count(subset); var w=Math.Min(s.HandWithers,Math.Max(0,needed-n));
                    if(n+w==needed || optional && n+w<=needed) Choice(subset,w);
                    if(subset==0)break;
                }
            }
            return true;
            void Choice(int mask,int withers)
            {
                var t=s; t.Removed|=mask; t.HandWithers-=withers;
                var count=Count(mask)+withers;
                t.Targets|=(ulong)Math.Min(15,count)<<(i*4);
                if(burns)
                {
                    for (var j = 0; j < cards.Length; j++)
                        if ((mask & (1 << j)) != 0) t.ExhaustRetentionLoss += .7 * retention[j];
                    t.Exhausted|=mask;
                    t.ExhaustTriggers+=count;
                    var status=Count(mask&statuses);var harmfulCount=0;
                    for(var j=0;j<cards.Length;j++)if((mask&harmful&(1<<j))!=0 && cards[j].EndDamage>0)harmfulCount++;
                    t.StatusExhausted+=status+withers; t.WitherExhausted+=harmfulCount+withers;
                    t.Block+=context.ExhaustBlock*count;
                    if (context.ExhaustBlock > 0) GainBlockEvents(ref t,count);
                    t=Draw(t,(int)(context.ExhaustDraw*count));
                    if(card.Kind is nameof(FiendFire))
                    {
                        t.Damage+=(f.Damage+t.Strength+t.TemporaryStrength)*count;
                        t.TemporaryDamage+=t.TemporaryStrength*count;
                    }
                    if(card.Kind is nameof(SecondWind))
                    {
                        t.Block+=(f.Block+t.Dexterity+t.TemporaryDexterity)*count;
                        if (f.Block+t.Dexterity+t.TemporaryDexterity > 0) GainBlockEvents(ref t,count);
                        t.TemporaryBlock+=t.TemporaryDexterity*count;
                    }
                }
                if(beforeDraw) t=Draw(t,card.Kind is nameof(CalculatedGamble)?count:(int)Math.Clamp(f.Draw*executions/(1+Math.Max(0,card.GetEnchantedReplayCount())),0,Window));
                // Its own AfterCardPlayed runs after all discard hooks.
                if(burns) Resume(Finish(t,i,(int)f.Effects.GeneratedShivs),depth);
                else ResolveDiscard(t,mask,0,(int)f.Effects.GeneratedShivs,i,depth);
            }
        }
    }

    private static double[] ExhaustRetention(Slot[] cards, int opening, Context context)
    {
        var values = new double[cards.Length];
        if (context.DisableExhaustRetention || !cards.Any(c => c.Model is { IsUpgraded: true, Kind: nameof(TrueGrit) }))
            return values;
        // Native TrueGrit+ can keep an unaffordable engine for a later hand.
        // Compare actual payable continuations with/without each held card,
        // instead of valuing all exhaust targets as equally harmless. At most
        // 2*(Window+1) one-hand solves; recursive target valuation is disabled.
        var next = context with { DisableExhaustRetention = true, InitialWithers = 0, Counter = 0 };
        var order = Enumerable.Range(0, cards.Length).Skip(Math.Min(opening, cards.Length))
            .Concat(Enumerable.Range(0, Math.Min(opening, cards.Length))).ToArray();
        var future = order.Select(i => cards[i]).ToArray();
        // A later attack opening and a later pressured hand expose different
        // payable roles. Keep the greater demonstrated loss, rather than
        // assuming today's over-blocked hand makes an attack engine worthless.
        foreach (var incoming in new[] { 0d, context.Incoming }.Distinct())
        {
            var scenario = next with { Incoming = incoming };
            var baseline = Solve(future, Math.Min(5, future.Length), scenario).Utility;
            for (var i = 0; i < cards.Length; i++)
            {
                if (cards[i].Facts is not { Unplayable: false }) continue;
                var continuation = order.Where(j => j != i).Select(j => cards[j]).ToArray();
                values[i] = Math.Max(values[i], Math.Max(0, baseline
                    - Solve(continuation, Math.Min(5, continuation.Length), scenario).Utility));
            }
        }
        return values;
    }
}
