using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CoopBots.Building;

// Paired six-turn expectation, deliberately NOT a native combat simulation or
// win probability. Adds engine startup, finite energy and status pressure to
// construction. Both alternatives see the same ordinary/long-fight scenarios;
// no shuffled live order or live RNG is inspected. Upstream has no draft-cycle
// evaluator. Our mechanism-local estimate and structural residual are requested
// in the owner's 2026-09-28 optimization plan; no combat legality is changed.
internal static class SustainedDeckValue
{
    internal const int EnduranceHorizon=9;
    internal const double EnduranceHealth=240;
    // Absolute deck value: scenario choice must not depend on which card is edited.
    internal static double Strength(IReadOnlyList<CardModel> deck, int energy, bool includePollution)
    {
        var entries = deck.Select(c => new Entry(c, CardProfile.Of(c, energy))).ToArray();
        var values = new List<double>();
        foreach (var threat in new[] { 12d, 26d })
        foreach (var pollution in includePollution ? new[] { false, true } : new[] { false })
        foreach (var enemies in new[] { 1, 3 })
        for (var sample = 0; sample < ConstructionBudget.Current.Samples; sample++)
            values.Add(Score(entries, energy, threat, pollution, enemies,
                allyAttacks: deck.Any(c => c is Sneaky) ? AlliedAttacks(deck[0].Owner, threat, pollution, enemies) : null,
                allyVulnerable: deck.Any(c => c is Bully) ? AlliedVulnerable(deck[0].Owner, threat, pollution, enemies) : null,
                vulnerablePayoffs: true, outcomes: true, trajectory: sample));
        // Apply terminal outcomes inside each draw trajectory. Capping the
        // mean damage first creates a false deterministic kill at the mean HP.
        return .7 * values.Average() + .3 * values.Order().Take(Math.Max(1, values.Count / 4)).Average();
    }

    internal static double ScenarioStrength(IReadOnlyList<CardModel> deck,int energy,DeckRobustness.Interference interference,bool endurance=false)
        => ScenarioStrengthForOwner(deck, energy, interference, endurance, 0, 0);

    internal static double ScenarioStrengthForOwner(IReadOnlyList<CardModel> deck,int energy,
        DeckRobustness.Interference interference,bool endurance,double ownerHp,double ownerMaxHp)
        => ConstructionJobs.Value(() => PrepareScenario(deck, energy, interference, endurance, ownerHp, ownerMaxHp));

    private static Func<double> PrepareScenario(IReadOnlyList<CardModel> deck,int energy,DeckRobustness.Interference interference,
        bool endurance,double ownerHp,double ownerMaxHp)
    {
        var entries=deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray();
        var plan=new ScenarioPlan(entries,energy,ownerHp,ownerMaxHp);
        var scenarios=new List<Func<int,double>>();
        foreach(var threat in endurance?new[]{18d,26d}:new[]{12d,26d})
        foreach(var enemies in endurance?new[]{1}:new[]{1,3})
        {
            var pollution=interference==DeckRobustness.Interference.Wither;
            var horizon=endurance?EnduranceHorizon:6;
            var adaptable=interference==DeckRobustness.Interference.Adaptable ? AdaptableBossStress.For(deck.FirstOrDefault()?.Owner) : null;
            var shared=AlliedStatus(deck.FirstOrDefault()?.Owner,threat,pollution,enemies,horizon);
            var attacks=deck.Any(c=>c is Sneaky)?AlliedAttacks(deck[0].Owner,threat,pollution,enemies,horizon):null;
            var vulnerable=deck.Any(c=>c is Bully)?AlliedVulnerable(deck[0].Owner,threat,pollution,enemies,horizon):null;
            scenarios.Add(sample=>Score(entries,energy,threat,pollution,enemies,
                allyAttacks:attacks, allyVulnerable:vulnerable,
                vulnerablePayoffs:true,outcomes:true,trajectory:sample,chains:interference==DeckRobustness.Interference.Chains,
                horizon:horizon,targetHealth:endurance?EnduranceHealth:null,shared:shared,plan:plan,adaptable:adaptable));
        }
        return () => {
            var values=new List<double>();
            foreach(var scenario in scenarios)
                for(var sample=0;sample<ConstructionBudget.Current.Samples;sample++) values.Add(scenario(sample));
            return .7*values.Average()+.3*values.Order().Take(values.Count/4).Average();
        };
    }

    private sealed record Entry(ConstructionCard Card, CardProfile.Facts Facts, CardModel Original, bool PoisonSource, int Index = -1)
    {
        internal Entry(CardModel card, CardProfile.Facts facts) : this(ConstructionCard.Capture(card), facts, card,
            facts.Roles.Contains("poison-source")) { }
    }
    // Deck facts and conditional availability are identical across all paired
    // trajectories. Compute them once; only turn state belongs to each sample.
    private sealed class ScenarioPlan
    {
        internal readonly Entry[] Ordinary, LegacyOrdinary, Powers;
        internal readonly double Finale;
        internal readonly int InitialStatus, EtherealStatuses, PoisonSources, NonAttacks;
        internal readonly bool ShuffleSoot;
        internal readonly double OwnerHp, OwnerMaxHp;
        internal ScenarioPlan(Entry[] deck,int energy,double ownerHp=0,double ownerMaxHp=0)
        {
            var creature=deck.FirstOrDefault()?.Original.Owner?.Creature;
            OwnerHp=ownerMaxHp>0?ownerHp:creature?.CurrentHp??0;
            OwnerMaxHp=ownerMaxHp>0?ownerMaxHp:creature?.MaxHp??0;
            Finale=GrandFinaleAvailability.Estimate(deck.Select(e=>e.Original).ToArray(),energy);
            var corruption=deck.Any(e=>e.Card.Kind is nameof(Corruption));
            var ordinary=deck.Where(e=>!e.Facts.Unplayable && !e.Facts.IsPower && !Unsupported(e.Card)).ToArray();
            Ordinary=ordinary.Select((e,i)=>e with {Index=i}).ToArray();
            LegacyOrdinary=ordinary.Where(e=>e.Facts.Cost<=energy || e.Card.Type==CardType.Skill && corruption)
                .Select((e,i)=>e with {Index=i}).ToArray();
            Powers=deck.Where(e=>e.Facts.IsPower && KnownPower(e.Card)
                && (e.Facts.Cost<=energy || deck.Any(d=>d.Card.Kind is nameof(Cascade) or nameof(Havoc))))
                .Select((e,i)=>e with {Index=i}).ToArray();
            InitialStatus=deck.Count(e=>e.Facts.Unplayable);
            EtherealStatuses=deck.Count(e=>e.Facts.Unplayable && e.Card.Keywords.Contains(CardKeyword.Ethereal));
            PoisonSources=deck.Count(e=>e.PoisonSource);
            NonAttacks=deck.Count(e=>e.Card.Type!=CardType.Attack);
            ShuffleSoot=ShuffleStatusContext.Active(deck.FirstOrDefault()?.Original.Owner);
        }
    }
    internal sealed record TurnEstimate(int Turn, double Damage, double Strength, double Shivs,
        double StatusExhausted, IReadOnlyDictionary<CardModel,double> Plays, double EnergySpent, double EnergyAvailable)
    {
        internal double AttackPlays { get; init; }
        internal double VulnerableApplied { get; init; }
        internal double EnemyHealthAfter { get; init; } = double.NaN;
        internal double StatusDamage { get; init; }
        internal double AttackDamage { get; init; }
        internal double VulnerablePayoff { get; init; }
        internal double ExhaustCount { get; init; }
        internal double Block { get; init; }
        internal double Incoming { get; init; }
        internal int ShuffleStatusesGenerated { get; init; }
        internal double TriggeredPoison { get; init; }
        internal double TriggeredBlock { get; init; }
        internal double BlockTriggerDamage { get; init; }
        internal double OwnerHpAfter { get; init; }
        internal double SurvivalPenalty { get; init; }
        internal double PoisonDamage { get; init; }
        internal int BossPhase { get; init; }
        internal double ReactiveStrength { get; init; }
        internal double HandDrawn { get; init; }
        internal double StartDiscardCount { get; init; }
    }
    internal static IReadOnlyList<TurnEstimate> Estimate(IReadOnlyList<CardModel> deck, int energy,
        bool pollution = false, int enemies = 1)
    {
        var turns=new List<TurnEstimate>();
        Score(deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray(),energy,26,pollution,enemies,turns.Add);
        return turns;
    }
    internal static IReadOnlyList<TurnEstimate> OutcomeEstimate(IReadOnlyList<CardModel> deck, int energy)
    {
        var turns = new List<TurnEstimate>();
        Score(deck.Select(c => new Entry(c, CardProfile.Of(c, energy))).ToArray(), energy, 12, false, 1,
            turns.Add, vulnerablePayoffs: true, outcomes: true, trajectory: 0);
        return turns;
    }
    internal static IReadOnlyList<TurnEstimate> EnduranceTrace(IReadOnlyList<CardModel> deck,int energy)
    {
        var turns=new List<TurnEstimate>();
        Score(deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray(),energy,26,true,1,turns.Add,
            vulnerablePayoffs:true,outcomes:true,horizon:EnduranceHorizon,targetHealth:EnduranceHealth);
        return turns;
    }
    internal static IReadOnlyList<TurnEstimate> AdaptableTrace(IReadOnlyList<CardModel> deck,int energy)
    {
        var turns=new List<TurnEstimate>();
        Score(deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray(),energy,26,false,1,turns.Add,
            vulnerablePayoffs:true,outcomes:true,horizon:EnduranceHorizon,adaptable:AdaptableBossStress.For(deck.FirstOrDefault()?.Owner));
        return turns;
    }
    internal static bool KnownPower(CardModel c) => c is Pyre or Afterimage or Footwork or InfiniteBlades
        or MachineLearning or Inflame or DemonForm or FeelNoPain or DarkEmbrace
        or Accelerant or Accuracy or Cruelty or Unmovable or FanOfKnives or SerpentForm or Corruption or Sneaky or Tank
        or ToolsOfTheTrade or Juggling or PhantomBlades or Juggernaut;
    internal static bool Cleanup(CardModel c) => c is SecondWind or TrueGrit or BurningPact
        or Cinder or FiendFire or Prepared or CalculatedGamble or Survivor or Purity or Acrobatics or DaggerThrow;
    internal static bool KnownPower(ConstructionCard c) => c.Kind is nameof(Pyre) or nameof(Afterimage) or nameof(Footwork) or nameof(InfiniteBlades)
        or nameof(MachineLearning) or nameof(Inflame) or nameof(DemonForm) or nameof(FeelNoPain) or nameof(DarkEmbrace)
        or nameof(Accelerant) or nameof(Accuracy) or nameof(Cruelty) or nameof(Unmovable) or nameof(FanOfKnives) or nameof(SerpentForm) or nameof(Corruption) or nameof(Sneaky) or nameof(Tank)
        or nameof(ToolsOfTheTrade) or nameof(Juggling) or nameof(PhantomBlades) or nameof(Juggernaut);
    internal static bool Cleanup(ConstructionCard c) => c.Kind is nameof(SecondWind) or nameof(TrueGrit) or nameof(BurningPact)
        or nameof(Cinder) or nameof(FiendFire) or nameof(Prepared) or nameof(CalculatedGamble) or nameof(Survivor) or nameof(Purity) or nameof(Acrobatics) or nameof(DaggerThrow);
    internal static bool Unsupported(ConstructionCard c) => c.Type==CardType.Power && !KnownPower(c)
        // Random generation, alternate resources and unusual damage depend on
        // state absent from this expectation. Preserve their structural policy.
        || c.Kind is "Metamorphosis" or "Chrysalis" or "Discovery" or "MementoMori";
    private static double Amount(ConstructionCard c, string key, double fallback=0)
        => c.Amount(key, fallback);

    internal static bool Unsupported(CardModel c) => c.Type == CardType.Power && !KnownPower(c)
        || c.GetType().Name is "Metamorphosis" or "Chrysalis" or "Discovery" or "MementoMori";
    internal static string Coverage(IReadOnlyList<CardModel> deck, CardModel? added = null)
    {
        var unknown = deck.Concat(added is null ? [] : [added])
            .Where(Unsupported).Select(c => c.Id.Entry).Distinct().ToArray();
        return unknown.Length == 0 ? "cycle:modeled" : "cycle:partial,unknown:" + string.Join('/', unknown);
    }

    // Native Unmovable doubles all block events from the first eligible card
    // play per stack, excluding later card plays and non-move power block.
    // Expected availability is bounded; a Second Wind is ONE card play even
    // though its exhausts can produce multiple native block events.
    internal static double FirstBlockBonus(IEnumerable<double> cards,double probability,double slots)
    {
        double bonus=0;
        foreach(var block in cards.OrderByDescending(x=>x))
        {
            var used=Math.Min(Math.Clamp(probability,0,1),Math.Max(0,slots));
            bonus+=used*block;slots-=used;
        }
        return bonus;
    }

    internal static CounterfactualDraft.Result? Compare(IReadOnlyList<CardModel> deck,
        CardModel? added, CardModel? removed, int energy, bool includePollution = true)
    {
        if(deck.Count<8 || !deck.Append(added).Any(c=>c is not null && (KnownPower(c)||Cleanup(c)||c is GrandFinale))) return null;
        // Never decide the addition/removal of an unknown engine by treating it
        // as blank. Unknown HELD engines no longer erase independent comparisons.
        if(new[]{added,removed}.Any(c=>c is not null && Unsupported(c)))return null;
        // Removing a known card can remove the input to an unknown held engine.
        // Additions/upgrades retain partial evidence, but deletion retains the
        // complete structural policy until that dependency is represented.
        if(removed is not null && added is null && deck.Any(Unsupported))return null;
        var before=deck.Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray();
        var after=deck.Where(c=>!ReferenceEquals(c,removed)).Concat(added is null ? [] : [added])
            .Select(c=>new Entry(c,CardProfile.Of(c,energy))).ToArray();
        var old=new List<double>(); var next=new List<double>();
        foreach(var threat in new[]{12d,26d})
        foreach(var status in includePollution ? new[]{false,true} : new[]{false})
        foreach(var enemies in new[]{1,3})
        {
            var allies = before.Concat(after).Any(e => e.Card.Kind is nameof(Sneaky))
                ? AlliedAttacks(deck.FirstOrDefault()?.Owner, threat, status, enemies) : null;
            old.Add(Score(before,energy,threat,status,enemies,allyAttacks:allies));
            next.Add(Score(after,energy,threat,status,enemies,allyAttacks:allies));
        }
        var meanOld=old.Average();var meanNew=next.Average();
        var unknown=before.Count(e=>Unsupported(e.Card));
        // Powers have interactions beyond their single draw slot. Reserve a
        // structural share for each unknown engine instead of reporting certainty.
        var share=unknown==0 ? 1 : deck.Count/(deck.Count+3d*unknown);
        return new(.7*(meanNew-meanOld)+.3*(next.Min()-old.Min()),meanOld,meanNew,old.Min(),next.Min(),0,0)
            {ModelShare=share,Coverage=Coverage(deck,added)};
    }

    private sealed class AllyMemo
    {
        internal readonly Dictionary<(MegaCrit.Sts2.Core.Entities.Players.Player,double,bool,int,int,int),double[]> Supply = new();
        internal readonly Dictionary<(MegaCrit.Sts2.Core.Entities.Players.Player,double,bool,int,int),SharedStatus?> Status = new();
    }
    [ThreadStatic] private static AllyMemo? allyMemo;
    internal static IDisposable BeginEvaluation()
    {
        var previous=allyMemo; allyMemo ??=new(); return new AllyScope(previous);
    }
    private sealed class AllyScope(AllyMemo? previous):IDisposable
    { public void Dispose()=>allyMemo=previous; }

    // Native Sneaky gives its OWNER block for every OTHER player's attack card.
    // Use public allied decks and the same finite six-turn attack supply. Never
    // count the owner's attacks or assume three allies when playing solo.
    private static double[] AlliedAttacks(MegaCrit.Sts2.Core.Entities.Players.Player? owner,
        double threat, bool pollution, int enemies,int horizon=6)
    {
        var key=(owner!,threat,pollution,enemies,horizon,0);
        if(owner is not null && allyMemo is not null && allyMemo.Supply.TryGetValue(key,out var held))return held;
        var result = new double[horizon];
        if (owner?.RunState is not { } run) return result;
        foreach (var ally in run.Players.Where(p => p != owner))
        {
            var energy = Math.Max(0, ally.MaxEnergy);
            Score(ally.Deck.Cards.Select(c => new Entry(c, CardProfile.Of(c, energy))).ToArray(),
                energy, threat, pollution, enemies, t => result[t.Turn - 1] += t.AttackPlays,horizon:horizon);
        }
        if(owner is not null && allyMemo is not null)allyMemo.Supply[key]=result;
        return result;
    }

    // Native Bully: CalculationBase 4 + ExtraDamage (2, upgraded 3) per
    // target Vulnerable stack. A null-target preview contains only the 4.
    // Share native amounts with the hand model, never a card-name preference.
    internal static double DamagePerVulnerable(CardModel card)
        => card is Bully && card.DynamicVars.TryGetValue("ExtraDamage", out var value)
            ? (double)value.BaseValue * (1 + Math.Max(0, card.GetEnchantedReplayCount())) : 0;

    internal static double DamagePerVulnerable(ConstructionCard card)
        => card.Kind is nameof(Bully) ? Amount(card, "ExtraDamage", 0) * (1 + Math.Max(0, card.GetEnchantedReplayCount())) : 0;

    private static double[] AlliedVulnerable(MegaCrit.Sts2.Core.Entities.Players.Player? owner,
        double threat, bool pollution, int enemies,int horizon=6)
    {
        var key=(owner!,threat,pollution,enemies,horizon,1);
        if(owner is not null && allyMemo is not null && allyMemo.Supply.TryGetValue(key,out var held))return held;
        var result = new double[horizon];
        if (owner?.RunState is not { } run) return result;
        foreach (var ally in run.Players.Where(p => p != owner && p.Creature.IsAlive))
        {
            var energy = BuildValue.StableEnergy(ally);
            Score(ally.Deck.Cards.Select(c => new Entry(c, CardProfile.Of(c, energy))).ToArray(),
                energy, threat, pollution, enemies, t => result[t.Turn - 1] += t.VulnerableApplied,horizon:horizon);
        }
        if(owner is not null && allyMemo is not null)allyMemo.Supply[key]=result;
        return result;
    }

    private sealed record SharedStatus(double[] AttackDamage,double[] Vulnerable,double[] Payoff,
        double[] Exhausts,double[] GuardedSavings);
    private static SharedStatus? AlliedStatus(MegaCrit.Sts2.Core.Entities.Players.Player? owner,
        double threat,bool pollution,int enemies,int horizon)
    {
        var key=(owner!,threat,pollution,enemies,horizon);
        if(owner is not null && allyMemo is not null && allyMemo.Status.TryGetValue(key,out var held))return held;
        var allies=owner?.RunState.Players.Where(p=>p!=owner && p.Creature.IsAlive).ToArray();
        if(allies is null || allies.Length==0)return null;
        var result=new SharedStatus(new double[horizon],new double[horizon],new double[horizon],new double[horizon],new double[horizon]);
        foreach(var ally in allies)
        {
            // Each living ally contributes its own energy-limited attack supply.
            // Empty or non-attacking decks add no fictitious damage. This bounded
            // expected trace, also used by allied Bully/Sneaky support, is not an
            // exact joint combat replay and never recurses into team evaluation.
            double stock=0;
            Score(ally.Deck.Cards.Select(c=>new Entry(c,CardProfile.Of(c,BuildValue.StableEnergy(ally)))).ToArray(),
                BuildValue.StableEnergy(ally),threat,pollution,enemies,t=>
                {
                    stock=Math.Max(0,stock-1);
                    result.AttackDamage[t.Turn-1]+=t.AttackDamage;
                    result.Vulnerable[t.Turn-1]+=stock+.5*t.VulnerableApplied;
                    result.Payoff[t.Turn-1]+=t.VulnerablePayoff;
                    result.Exhausts[t.Turn-1]+=t.ExhaustCount;
                    result.GuardedSavings[t.Turn-1]+=Math.Max(0,t.Incoming-t.Block)-Math.Max(0,.5*t.Incoming-t.Block);
                    stock+=t.VulnerableApplied;
                },horizon:horizon);
        }
        if(owner is not null && allyMemo is not null)allyMemo.Status[key]=result;
        return result;
    }

    private static double Score(Entry[] deck,int energy,double pressure,bool pollution,int enemies,
        Action<TurnEstimate>? trace = null, IReadOnlyList<double>? allyAttacks = null,
        IReadOnlyList<double>? allyVulnerable = null, bool vulnerablePayoffs = false, bool outcomes = false, int trajectory = 0,
        bool chains = false,int horizon=6,double? targetHealth=null,SharedStatus? shared=null,ScenarioPlan? plan=null,
        AdaptableBossStress? adaptable=null)
    {
        plan ??=new ScenarioPlan(deck,energy);
        var finaleAvailability=plan.Finale;
        var ordinary=outcomes?plan.Ordinary:plan.LegacyOrdinary;
        var powers=plan.Powers;
        var active=new double[powers.Length];
        var atTurnStart=new double[powers.Length];
        var remaining=Enumerable.Repeat(1d,ordinary.Length).ToArray();
        var weights=new double[ordinary.Length];
        var initialStatus=plan.InitialStatus;
        double seen=0,status=initialStatus,poison=0,poisonOthers=0,growthStrength=0,total=0,nextEnergy=0,nextDraw=0,exhaustStock=0,shivStock=0,upgradedShivStock=0,vulnerableStock=0;
        // Finite ordinary and endurance targets. Health is per encounter, not
        // a licence to score the same lethal damage on all six turns.
        var health = adaptable?.Health[0] ?? targetHealth ?? (pressure < 20 ? 42d * enemies : 100d * enemies);
        var bossPhase=0;var phaseTurns=0;double reactiveStrength=0;
        var futureVulnerable = new double[horizon];
        var noVulnerable = Enumerable.Repeat(1d, horizon).ToArray();
        var drawOffset = 0;
        // Native Biiig Hug adds one unplayable Soot after EVERY shuffle. Count
        // draws against the post-removal deck, including later dilution and
        // exhaust/power thinning. This macro trajectory samples the new slots
        // on the next hand; Soot itself deals no hand-end damage.
        double untilShuffle = deck.Length;
        double witherStock=0;
        var witherCounter=0;
        double ownVulnerable=0,sharedCredit=0;
        double cumulativeHpLoss=0;
        var hasAutoPlay=ordinary.Any(e=>e.Card.Kind is nameof(Cascade) or nameof(Havoc));
        // HP LOSS IS A RESOURCE THIS DECK CAN SPEND, and nothing read it. Rupture turns
        // each self-hit into Strength; Spite hits 2 (3 upgraded) instead of 1 once the
        // owner has lost HP this turn; Tear Asunder hits once per HP loss suffered this
        // combat. Measured live 2026-10-02 (A10, seat 90000000000000001): the deck held
        // RUPTURE and SPITE x2 and refused BLOODLETTING four times and INFERNO once —
        // every self-damage card was charged for its life with none of these three
        // payoffs modelled, so the whole engine read as pure cost.
        var ruptureStacks = 0.0;
        var hpLossEvents = 0.0;
        for(var turn=1;turn<=horizon;turn++)
        {
            active.CopyTo(atTurnStart,0);
            var size=Math.Max(5,deck.Length-initialStatus+status-active.Sum()-remaining.Sum(e=>1-e));
            // Draw throughput and hand advantage are different. A cantrip
            // still reaches the next card/engine; discard then pays a hand card.
            var draw=5+nextDraw+(outcomes ? 0 : ordinary.Sum(e=>remaining[e.Index]*e.Facts.Draw)*5/size);
            draw=Math.Min(10,draw);seen+=draw;
            // Pyre increases future turns only; a newly played copy cannot pay
            // its own activation. Use the same budget for setup and ordinary plays.
            var turnEnergy=energy+powers.Where(p=>p.Card.Kind==nameof(Pyre))
                .Sum(p=>atTurnStart[p.Index]*Amount(p.Card,"Energy"));
            double setupEnergy=0,setupPlays=0;
            foreach(var p in powers)
            {
                // Innate brings the engine online immediately, but still pays
                // its real cost. More copies/crowding cannot create free energy.
                var chance=p.Card.Keywords.Contains(CardKeyword.Innate) ? 1 : Math.Min(1,seen/size);
                if (outcomes) chance = PayableHands.Reached(p.Card, chance, trajectory) ? 1 : 0;
                var prior=active[p.Index];
                var gain=Math.Max(0,chance-prior);
                if(p.Facts.Cost>0)gain=outcomes ? (p.Facts.Cost <= turnEnergy-setupEnergy ? gain : 0)
                    : Math.Min(gain,Math.Max(0,turnEnergy-setupEnergy)/p.Facts.Cost);
                active[p.Index]=prior+gain;
                setupEnergy+=gain*p.Facts.Cost;
                setupPlays+=gain;
            }
            double Power<T>(string key,double fallback=0) where T:CardModel
            {
                double value=0;
                foreach(var e in powers)if(e.Card.Is<T>())value+=active[e.Index]*Amount(e.Card,key,fallback);
                return value;
            }
            double StartPower<T>(string key,double fallback=0) where T:CardModel
            {
                double value=0;
                foreach(var e in powers)if(e.Card.Is<T>())value+=atTurnStart[e.Index]*Amount(e.Card,key,fallback);
                return value;
            }
            var tools = StartPower<ToolsOfTheTrade>("Power", 1);
            if (outcomes) draw = Math.Min(10, draw + StartPower<MachineLearning>("Cards", 1) + tools);
            else draw = Math.Min(10, draw + tools);
            var available=Math.Max(0,turnEnergy+nextEnergy-setupEnergy);
            var corruption=Math.Min(1,Power<Corruption>("Power",1));
            var drawn=remaining.Sum()*draw/size;
            var handPayment=ordinary.Sum(e=>remaining[e.Index]*(e.Card.Kind is nameof(Prepared) ? Amount(e.Card,"Cards",1)
                : e.Card.Kind is nameof(Acrobatics) or nameof(DaggerThrow) or nameof(Survivor) or nameof(BurningPact) ? 1 : 0))*draw/size;
            var retained=drawn<=0 ? 0 : Math.Clamp(1-Math.Max(0,handPayment-status*draw/size)/drawn,0,1);
            var exposure=Math.Min(1,draw/size)*retained;
            double FreeChance(Entry e)=>e.Facts.Cost==0?1:e.Card.Type==CardType.Skill?corruption:0;
            double PaidExposure(Entry e)=>e.Facts.Cost>energy ? 0 : remaining[e.Index]*exposure*(1-FreeChance(e));
            var demand=ordinary.Sum(e=>PaidExposure(e)*e.Facts.Cost);
            var paidShare=demand<=0 ? 0 : Math.Min(1,available/demand);
            // Finite one-use stock: exhaust cards and Corruption skills cannot
            // return every cycle. Allocate free and paid cards separately;
            // averaging them would grant free attacks on a zero-energy setup turn.
            foreach(var e in ordinary)weights[e.Index]=remaining[e.Index]*exposure*FreeChance(e)+PaidExposure(e)*paidShare;
            foreach(var e in ordinary.Where(e=>e.Card.Kind is nameof(GrandFinale))) weights[e.Index]*=finaleAvailability;
            // Every self-hit this turn is an HP-loss event: it feeds Rupture for the rest
            // of the combat and lights up Spite for this turn. Counted AFTER the weights
            // so an unaffordable Bloodletting does not pay a dividend it never bought.
            hpLossEvents += ordinary.Where(e => e.Facts.HpLoss > 0).Sum(e => weights[e.Index]);
            var energySpent=demand*paidShare;
            double CleanupCount(Entry e, double badHand)
            {
                var count=e.Card.Kind is nameof(SecondWind) or nameof(FiendFire) or nameof(CalculatedGamble) ? badHand
                    : e.Card.Kind is nameof(Purity) or nameof(Prepared) ? Math.Min(Amount(e.Card,"Cards",1),badHand) : Math.Min(1,badHand);
                return e.Card.Kind is nameof(Cinder) || e.Card.Kind is nameof(TrueGrit) && !e.Card.IsUpgraded
                    ? badHand/Math.Max(1,draw-1) : count;
            }
            double ExhaustTargets(Entry e, double badHand)
                => e.Card.Kind is nameof(FiendFire) ? Math.Max(0,draw-1)
                    : e.Card.Kind is nameof(SecondWind) ? Math.Max(CleanupCount(e,badHand),(draw-1)*plan.NonAttacks/size)
                    : e.Card.Kind is nameof(Purity) ? Math.Min(Amount(e.Card,"Cards",3),badHand) : Math.Max(CleanupCount(e,badHand),1);
            bool ExhaustsTargets(Entry e) => Cleanup(e.Card) && e.Card.Kind is not (nameof(Prepared) or nameof(CalculatedGamble) or nameof(Survivor) or nameof(Acrobatics) or nameof(DaggerThrow));
            PayableHands.Outcome? handOutcome=null;
            if (outcomes)
            {
                // The subset chooser must see the same active triggers as the
                // outcome below, or it never plays a Shiv generator / cheap
                // trigger for Afterimage merely because its printed damage is 0.
                // Rupture converts every HP loss already suffered into Strength for the
                // rest of the combat. Counted the same way the other persistent stat
                // engines are (Accuracy, Footwork): the stacks this deck holds times the
                // number of self-hits the scenario has played, weighted by how often the
                // hand actually pays for them.
                if (ruptureStacks <= 0) ruptureStacks = Power<Rupture>("Strength", 1);
                var hpLossThisTurn = ordinary.Any(e => e.Facts.HpLoss > 0 && weights[e.Index] > .001);
                var currentStrength = ruptureStacks * hpLossEvents
                    + Power<Inflame>("StrengthPower") + growthStrength + StartPower<DemonForm>("StrengthPower");
                // A conditional hit count is the whole card. Spite is 1 hit until the
                // owner has lost HP this turn and Repeat (2; 3 upgraded) after; Tear
                // Asunder is one hit per HP loss of the combat. Both were coverage-0
                // before this, priced at a flat "unmodelled, assume ordinary play".
                // The numbers are the card's own, spelled out because CardProfile
                // deliberately does not read this Repeat: it is conditional, which is
                // why the card was coverage-0. Spite.cs: RepeatVar(2), OnUpgrade adds 1,
                // and hitCount = LostHpThisTurn ? Repeat : 1. TearAsunder.cs:
                // RepeatVar(1) plus one hit per DamageReceivedEntry on the owner.
                double Hits(Entry e) => e.Card.Kind is nameof(Spite)
                    ? (hpLossThisTurn ? (e.Card.IsUpgraded ? 3 : 2) : 1)
                    : e.Card.Kind is nameof(TearAsunder) ? 1 + hpLossEvents
                    : Math.Max(1, e.Facts.Hits);
                double exhausts(Entry e) => e.Facts.Effects.GeneratedShivs
                    + (e.Card.Keywords.Contains(CardKeyword.Exhaust) || e.Card.Type == CardType.Skill && corruption > 0 ? 1 : 0);
                var accuracy=Power<Accuracy>("AccuracyPower");
                var footwork=Power<Footwork>("DexterityPower");
                var embrace=Power<DarkEmbrace>("Cards",1);
                var afterimage=Power<Afterimage>("AfterimagePower",1);
                var feelNoPain=Power<FeelNoPain>("Power");
                var playable = ordinary.Select(e =>
                {
                    var f=e.Facts;
                    if(e.Card.Kind is nameof(Midnight))
                        f=f with {Cost=Math.Max(0,f.Cost-(int)(exhaustStock+(shared?.Exhausts.Take(turn-1).Sum()??0)))};
                    // The same adjustments as before, evaluated once per turn.
                    var unplayable=f.Unplayable || e.Card.Kind is nameof(GrandFinale) && !PayableHands.Reached(e.Card,finaleAvailability,trajectory);
                    var damage=(f.DrawPileDamage ? Math.Max(0,size-draw) : f.Damage)+(e.Card.Type==CardType.Attack?currentStrength*Hits(e):0)
                        +f.Effects.GeneratedShivs*(currentStrength+accuracy)
                        +DamagePerVulnerable(e.Card)*futureVulnerable[turn-1];
                    var block=f.Block+(f.Block>0?footwork:0);
                    var extraDraw=f.Draw+embrace*exhausts(e);
                    if(unplayable!=f.Unplayable || damage!=f.Damage || block!=f.Block || extraDraw!=f.Draw)
                        f=f with {Unplayable=unplayable,Damage=damage,Block=block,Draw=extraDraw};
                    return new PayableHands.Card(e.Card,f,remaining[e.Index],
                        afterimage*(1+f.Effects.GeneratedShivs)+feelNoPain*exhausts(e),
                        (afterimage>0?1+f.Effects.GeneratedShivs:0)+(feelNoPain>0?exhausts(e):0));
                }).Concat(hasAutoPlay ? powers.Select(p=>new PayableHands.Card(p.Card,p.Facts with {AutoOnly=true},1-active[p.Index])) : []).ToArray();
                // Setup cards trigger Wither too; their generated cards are
                // available to the ordinary hand's cleanup this turn.
                var setupWithers=pollution?(witherCounter+(int)setupPlays)/6:0;
                if(pollution)witherCounter=(witherCounter+(int)setupPlays)%6;
                // ConstructionHand pays the producer's cost/HP and grants energy
                // at its actual play. Pre-granting an expected amount here credits
                // undrawn/unplayed bottles and grants the same resource twice.
                var handPressure = adaptable is null ? pressure : bossPhase==0 ? (phaseTurns%2==0?adaptable.Bite:adaptable.Bash)+reactiveStrength
                    : bossPhase==1 ? adaptable.Claw*(3+phaseTurns) : phaseTurns%3==0?adaptable.Claw*3:phaseTurns%3==1?45:0;
                handOutcome = PayableHands.Evaluate(playable, size, Math.Max(0, draw - setupPlays), available,
                    handPressure, health, enemies, turn, corruption, trajectory, drawOffset,status,witherStock,
                    pollution?3+3*((turn-1)/3):0,witherCounter,chains && turn>=2,
                    Power<DarkEmbrace>("Cards",1),Power<FeelNoPain>("Power"),setupWithers,
                    startDiscard:(int)tools,juggling:Power<Juggling>("Power",1),phantomBlades:Power<PhantomBlades>("PhantomBladesPower"),
                    skillEnrage:adaptable is not null && bossPhase==0?adaptable.Enrage:0,
                    blockTriggerDamage:Power<Juggernaut>("JuggernautPower"),
                    ownerHp:Math.Max(0,plan.OwnerHp-cumulativeHpLoss),ownerMaxHp:plan.OwnerMaxHp);
                var plays=handOutcome.Plays;
                for (var i = 0; i < ordinary.Length; i++) weights[i] = plays[i];
                if(hasAutoPlay)for(var i=0;i<powers.Length;i++)
                    active[i]=Math.Min(1,active[i]+plays[ordinary.Length+i]);
                energySpent = handOutcome.Spent;
                available += handOutcome.Refunded;
                drawOffset += (int)handOutcome.Drawn;
                witherCounter=handOutcome.Counter;
            }
            var fraction=remaining.Sum()==0 ? 0 : weights.Sum()/remaining.Sum();
            double W(Entry e)=>weights[e.Index];
            nextEnergy=ordinary.Sum(e=>e.Facts.NextTurnEnergy*W(e));
            nextDraw=ordinary.Sum(e=>e.Facts.NextTurnDraw*W(e));
            growthStrength+=StartPower<DemonForm>("StrengthPower");
            // Same Rupture term as the per-hand path: the payoff accounting has to see
            // the same Strength the attacks were priced with, or the card that supplies
            // the HP loss reads as pure cost twice.
            if (ruptureStacks <= 0) ruptureStacks = Power<Rupture>("Strength", 1);
            var strength=Power<Inflame>("StrengthPower")+growthStrength+ruptureStacks*hpLossEvents;
            var dex=Power<Footwork>("DexterityPower");
            // Temporary attributes expire each turn; expected same-hand overlap
            // is bounded, never added to the persistent strength/dexterity stock.
            var temporaryStrength=ordinary.Sum(e=>e.Facts.Effects.TemporaryStrength
                *(1+Math.Max(0,e.Card.GetEnchantedReplayCount()))*W(e))*PayableHands.TemporaryStatShare;
            var temporaryDexterity=ordinary.Sum(e=>e.Facts.Effects.TemporaryDexterity
                *(1+Math.Max(0,e.Card.GetEnchantedReplayCount()))*W(e))*PayableHands.TemporaryStatShare;
            var shivs=StartPower<InfiniteBlades>("Cards",1)+ordinary.Sum(e=>e.Facts.Effects.GeneratedShivs*W(e))
                +powers.Where(e=>e.Card.Kind is nameof(FanOfKnives)).Sum(e=>(active[e.Index]-atTurnStart[e.Index])*Amount(e.Card,"Shivs"));
            // Knife Trap replays the existing exhausted Shivs; those cards
            // exhaust again and remain available to subsequent Knife Traps.
            var knifePlays=ordinary.Where(e=>e.Card.Kind is nameof(KnifeTrap)).Sum(W);
            var knifeStock=shivStock+shivs*.5;
            var knifeShivs=knifePlays*knifeStock;
            var upgradedKnife=ordinary.Where(e=>e.Card.Kind is nameof(KnifeTrap) && e.Card.IsUpgraded).Sum(W);
            var upgradedReplay=upgradedShivStock*Math.Max(0,knifePlays-upgradedKnife)+knifeStock*upgradedKnife;
            upgradedShivStock+=Math.Max(0,knifeStock-upgradedShivStock)*Math.Min(1,upgradedKnife);
            shivStock+=shivs+ordinary.Where(e=>e.Card.Kind is nameof(Shiv)).Sum(W);
            shivs+=knifeShivs;
            var ordinaryHits=ordinary.Where(e=>e.Card.Type==CardType.Attack).Sum(e=>Math.Max(1,e.Facts.Hits)*W(e));
            var ordinaryShivs=ordinary.Sum(e=>e.Facts.Effects.GeneratedShivs*W(e));
            // The sampled hand has an actual action order. Reuse its realized
            // temporary payoff once instead of multiplying end-of-hand stock
            // into the source's own first hit or attacks played before it.
            // The analytic path keeps the established .7 overlap prior, but
            // a grant cannot support its own first execution there either.
            var temporaryDamage=handOutcome is not null ? handOutcome.TemporaryDamage
                +Math.Max(0,shivs-ordinaryShivs)*temporaryStrength
                : ordinary.Sum(e=>
                {
                    var plays=1+Math.Max(0,e.Card.GetEnchantedReplayCount());
                    var ownHits=e.Card.Type==CardType.Attack?Math.Max(1,e.Facts.Hits):0;
                    var otherHits=Math.Max(0,ordinaryHits-ownHits*W(e))+shivs;
                    return e.Facts.Effects.TemporaryStrength*PayableHands.TemporaryStatShare*W(e)
                        *(plays*otherHits+ownHits*(plays-1)/2);
                });
            var damage=ordinary.Sum(e=>((e.Facts.DrawPileDamage ? Math.Max(0,size-draw) : e.Facts.Damage)*(e.Facts.IsAoe?enemies:1)
                +Math.Max(1,e.Facts.Hits)*strength*(e.Card.Type==CardType.Attack?1:0))*W(e));
            // The analytic fallback has no known third-attack identity. Estimate
            // from the paid attack/Shiv mix and remaining energy, not a fixed
            // per-card bonus or a cost-free replay of an arbitrary best attack.
            var nativeAttacks=ordinary.Where(e=>e.Card.Type==CardType.Attack)
                .Sum(e=>(1+Math.Max(0,e.Card.GetEnchantedReplayCount()))*W(e))+shivs;
            var copyBody=nativeAttacks>0 ? (damage+shivs*(4+strength))/nativeAttacks : 0;
            var copyCost=nativeAttacks>0 ? ordinary.Where(e=>e.Card.Type==CardType.Attack)
                .Sum(e=>Math.Max(0,e.Facts.Cost)*W(e))/nativeAttacks : 0;
            var copyCount=Math.Min(Power<Juggling>("Power",1),copyCost<=0?double.PositiveInfinity:Math.Max(0,available-energySpent)/copyCost);
            damage += handOutcome?.CopiedAttackDamage ?? Math.Clamp(nativeAttacks-2,0,1)*copyCount*copyBody;
            damage += handOutcome?.FirstShivDamage ?? Math.Min(1,shivs)*Power<PhantomBlades>("PhantomBladesPower");
            damage+=temporaryDamage;
            var appliedVulnerable = ordinary.Sum(e => e.Facts.Vulnerable * W(e) * (e.Facts.IsAoe ? 1 : 1d/enemies));
            var vulnerablePresence = 0d;
            if (vulnerablePayoffs)
            {
                // Expected stacks on one target, with one enemy-turn decay.
                // Only half of this turn's supply precedes a payoff on average;
                // allied supply is bounded by the allies' own playable cards.
                var previous = outcomes ? futureVulnerable[turn - 1] : Math.Max(0, vulnerableStock - (turn > 1 ? 1 : 0));
                var supply = appliedVulnerable + (allyVulnerable is not null && turn<=allyVulnerable.Count?allyVulnerable[turn-1]:0);
                damage += ordinary.Sum(e => DamagePerVulnerable(e.Card) * W(e)) * (previous + .5 * supply);
                vulnerableStock = previous + supply;
                if (outcomes)
                {
                    var absent = noVulnerable[turn - 1];
                    foreach (var e in ordinary.Where(e => e.Facts.Vulnerable > 0))
                    {
                        var chance = Math.Clamp(W(e) * (e.Facts.IsAoe ? 1 : 1d / enemies), 0, 1);
                        absent *= 1 - .5 * chance;
                        for (var age = 1; turn - 1 + age < horizon && age < e.Facts.Vulnerable; age++)
                        {
                            futureVulnerable[turn - 1 + age] += chance * (e.Facts.Vulnerable - age);
                            noVulnerable[turn - 1 + age] *= 1 - chance;
                        }
                    }
                    // Allied duration is not available in this trace. Carry a
                    // conservative one-turn remainder of its bounded supply.
                    if (turn < horizon) futureVulnerable[turn] += Math.Max(0, (allyVulnerable is not null && turn<=allyVulnerable.Count?allyVulnerable[turn-1]:0) - 1);
                    vulnerablePresence = 1 - absent;
                }
            }
            var shivTargets=1+(enemies-1)*Math.Min(1,Power<FanOfKnives>("Power",1));
            damage+=shivs*(4+strength+Power<Accuracy>("AccuracyPower"))*shivTargets;
            var alliedShivBonus=ordinary.Sum(e=>(e.Facts.Effects.AlliedShivDamage+e.Facts.AlliedEnergyValue+e.Facts.AlliedDrawValue)*W(e));
            damage+=2*upgradedReplay*shivTargets;
            var attackPlays=ordinary.Where(e=>e.Card.Type==CardType.Attack).Sum(W)+shivs;
            var repeats=Math.Min(attackPlays,ordinary.Sum(e=>e.Facts.Effects.ReplayAttacks*W(e)));
            damage+=attackPlays>0 ? repeats*damage/attackPlays : 0;
            var block=ordinary.Where(e=>e.Card.Kind is not nameof(SecondWind)).Sum(e=>(e.Facts.Block+(e.Facts.Block>0?dex:0))*W(e));
            block+=handOutcome?.TemporaryBlock ?? temporaryDexterity*ordinary
                .Where(e=>e.Facts.Block>0 && e.Card.Kind is not nameof(SecondWind))
                .Sum(e=>(1+Math.Max(0,e.Card.GetEnchantedReplayCount()))*W(e));
            var triggeredBlock=handOutcome?.TriggeredBlock ?? .5*attackPlays*ordinary.Sum(e=>e.Facts.Effects.BlockPerAttack*W(e));
            block+=triggeredBlock;
            var cardBlocks=ordinary.Where(e=>e.Facts.Block>0 && e.Card.Kind is not nameof(SecondWind))
                .Select(e=>e.Facts.Block+dex).ToList();
            var badHand=status*draw/size;
            double cleaned=0,permanent=0,exhausted=shivs;
            foreach(var e in ordinary)
            {
                var spent=handOutcome is not null ? handOutcome.Exhausted[e.Index]
                    : e.Card.Keywords.Contains(CardKeyword.Exhaust)?W(e)
                    : e.Card.Type==CardType.Skill ? (outcomes ? W(e) : remaining[e.Index]*exposure)*corruption : 0;
                remaining[e.Index]=Math.Max(0,remaining[e.Index]-spent);
                exhausted+=spent;
            }
            if(handOutcome is not null)
            {
                permanent=handOutcome.StatusExhausted;
                exhausted+=permanent;
                for(var i=0;i<ordinary.Length;i++)
                {
                    var e=ordinary[i];var targets=handOutcome.Targets[i];
                    if(e.Card.Kind is nameof(FiendFire))damage+=(e.Facts.Damage+strength)*(targets-W(e));
                    if(e.Card.Kind is nameof(SecondWind))
                    {
                        var fromCard=(e.Facts.Block+dex)*targets;
                        block+=fromCard;cardBlocks.Add(fromCard);
                    }
                }
            }
            foreach(var e in ordinary.Where(e=>handOutcome is null && Cleanup(e.Card)))
            {
                var probability=W(e);
                var count=CleanupCount(e,badHand);
                // Cinder and unupgraded True Grit choose randomly, unlike a
                // targeted exhaust. Do not grant them a guaranteed status hit.
                // A status cleaned by one card cannot be cleaned again by the
                // next card. Group exhaust removes all affected future copies,
                // not the old one-status-per-cleaner cap.
                var removed=Math.Min(Math.Max(0,badHand-cleaned),count*probability);
                cleaned+=removed;
                var consumes=ExhaustsTargets(e);
                if(consumes)
                {
                    permanent+=removed;
                    var targets=ExhaustTargets(e,badHand);
                    var totalBurned=targets*probability;
                    exhausted+=totalBurned;
                    // Consume the expected useful targets too. Otherwise a
                    // mass-exhaust engine keeps burning the same skills forever.
                    var eligible=ordinary.Where(x=>x.Card!=e.Card
                        && (e.Card.Kind is not nameof(SecondWind) || x.Card.Type!=CardType.Attack)).ToArray();
                    var stock=eligible.Sum(x=>remaining[x.Index]);
                    var lost=Math.Min(stock,Math.Max(0,totalBurned-removed));
                    if(stock>0)foreach(var target in eligible)
                        remaining[target.Index]*=1-lost/stock;
                    if(e.Card.Kind is nameof(FiendFire))damage+=(e.Facts.Damage+temporaryStrength)*Math.Max(0,targets-1)*probability;
                }
                if(e.Card.Kind is nameof(SecondWind))
                {
                    var fromCard=(e.Facts.Block+dex+temporaryDexterity)*ExhaustTargets(e,badHand);
                    block+=fromCard*probability;cardBlocks.Add(fromCard);
                }
            }
            cleaned=Math.Min(badHand,cleaned);
            block+=FirstBlockBonus(cardBlocks,Math.Min(1,fraction),Power<Unmovable>("Power",1));
            var triggerPlays=ordinary.Sum(e=>(1+e.Card.GetEnchantedReplayCount())*W(e))+shivs+setupPlays+repeats;
            // Serpent deals unpowered damage after other plays. Its own
            // application does not trigger the newly acquired stack.
            var serpent=Power<SerpentForm>("SerpentFormPower")*(triggerPlays-setupPlays)
                +StartPower<SerpentForm>("SerpentFormPower")*setupPlays;
            damage+=ordinary.Sum(e=>e.Facts.Effects.ConditionalDamage*W(e)
                *Math.Clamp((exhaustStock+exhausted*.5)/Math.Max(1,e.Facts.Effects.ExhaustRequired),0,1));
            exhaustStock+=exhausted;
            block+=Power<Afterimage>("AfterimagePower",1)*triggerPlays
                +Power<FeelNoPain>("Power")*exhausted
                +Power<Sneaky>("SneakyPower")*(allyAttacks is not null && turn<=allyAttacks.Count?allyAttacks[turn-1]:0);
            // Exhaust draw is useful future throughput, bounded by the energy
            // this model can pay. It cannot generate an unlimited free loop.
            var extraDraw=Math.Min(3,Power<DarkEmbrace>("Cards",1)*exhausted+StartPower<MachineLearning>("Cards",1));
            if (!outcomes) damage+=Math.Min(extraDraw,available)*2;
            var weakened=Math.Min(1,ordinary.Sum(e=>(e.Facts.Weak+e.Facts.Effects.GeneratedShivWeak)*W(e)));
            var baseIncoming=pressure;
            if(adaptable is not null)
            {
                if(bossPhase==0)
                {
                    // Enrage applies after each actually played Skill. This
                    // owner-only contribution is a lower bound: no invented
                    // exact allied hand or teammate future action is inspected.
                    reactiveStrength+=adaptable.Enrage*ordinary.Where(e=>e.Card.Type==CardType.Skill).Sum(e=>(1+Math.Max(0,e.Card.GetEnchantedReplayCount()))*W(e));
                    baseIncoming=(phaseTurns%2==0?adaptable.Bite:adaptable.Bash)+reactiveStrength;
                }
                else if(bossPhase==1)baseIncoming=adaptable.Claw*(3+phaseTurns);
                else baseIncoming=phaseTurns%3==0?adaptable.Claw*3:phaseTurns%3==1?45:0;
                phaseTurns++;
            }
            var incoming=baseIncoming*(weakened>0?1-.25*weakened:1);
            incoming=Math.Max(0,incoming-ordinary.Sum(e=>e.Facts.StrengthDown*W(e)));
            // Native Tank costs the owner +50% powered attack damage while
            // each living ally's Guarded halves theirs. Preserve block waste:
            // an already fully protected ally supplies no invented prevention.
            var tank=Math.Min(1,Power<Tank>("TankPower",1));
            incoming*=1+.5*tank;
            poison+=ordinary.Sum(e=>e.Facts.Poison*W(e));
            // One primary target also receives targeted poison. Every other
            // target has its own all-target stock, so combining poison sources
            // never decrements the primary twice or the whole encounter once.
            poisonOthers+=ordinary.Where(e=>e.Facts.IsAoe).Sum(e=>e.Facts.Poison*W(e));
            var triggeredPoison=handOutcome?.TriggeredPoison
                ?? .5*ordinary.Sum(e=>e.Facts.Effects.PoisonPerDraw*W(e))*ordinary.Sum(e=>e.Facts.Draw*W(e));
            poison+=triggeredPoison;
            poisonOthers+=triggeredPoison;
            var sourceChance=1-Math.Pow(1-plan.PoisonSources/(double)Math.Max(1,deck.Length),
                Math.Min(deck.Length,seen));
            poison+=ordinary.Sum(e=>e.Facts.Effects.ConditionalPoison*W(e))*sourceChance;
            poisonOthers+=ordinary.Where(e=>e.Facts.IsAoe).Sum(e=>e.Facts.Effects.ConditionalPoison*W(e))*sourceChance;
            var ticks=1+Power<Accelerant>("Accelerant");
            // Native poison cannot trigger after its stock reaches zero. A
            // fractional expected stock interpolates adjacent integer stocks;
            // ceil keeps its final probability-weighted tick instead of dropping it.
            var primaryTicks=Math.Min(ticks,Math.Ceiling(poison));
            var secondaryTicks=Math.Min(ticks,Math.Ceiling(poisonOthers));
            var poisonDamage=Math.Max(0,poison*primaryTicks-primaryTicks*(primaryTicks-1)/2)
                +(enemies-1)*Math.Max(0,poisonOthers*secondaryTicks-secondaryTicks*(secondaryTicks-1)/2);
            poison=Math.Max(0,poison-ticks);
            poisonOthers=Math.Max(0,poisonOthers-ticks);
            var vulnerable=outcomes ? vulnerablePresence : Math.Min(1,ordinary.Sum(e=>e.Facts.Vulnerable*W(e)));
            var attackDamage=damage;
            var vulnerablePayoff=ordinary.Sum(e=>DamagePerVulnerable(e.Card)*W(e));
            damage*=1+vulnerable*(.5+Power<Cruelty>("CrueltyPower")/100);
            var blockEvents=handOutcome?.BlockEvents ?? ordinary.Where(e=>e.Facts.Block>0)
                .Sum(e=>W(e)*(e.Card.Kind is nameof(SecondWind)?ExhaustTargets(e,badHand):1+Math.Max(0,e.Card.GetEnchantedReplayCount())))
                +(Power<Afterimage>("AfterimagePower",1)>0?triggerPlays:0)
                +(Power<FeelNoPain>("Power")>0?exhausted:0)
                +(triggeredBlock>0?attackPlays:0);
            var alliedBlockEvents=Power<Sneaky>("SneakyPower")>0 && allyAttacks is not null && turn<=allyAttacks.Count
                ? allyAttacks[turn-1] : 0;
            var blockTriggerDamage=Math.Min(health,(handOutcome?.BlockTriggerDamage
                ?? ConstructionCombatMath.BlockTriggerDamage(Power<Juggernaut>("JuggernautPower"),blockEvents,health))
                +ConstructionCombatMath.BlockTriggerDamage(Power<Juggernaut>("JuggernautPower"),alliedBlockEvents,health));
            damage+=serpent+blockTriggerDamage;
            var statusDamage=handOutcome?.StatusDamage ?? (pollution ? Math.Max(0,badHand-cleaned)*(3+3*((turn-1)/3)) : 0);
            var harm=ordinary.Sum(e=>e.Facts.HpLoss*W(e))+Math.Max(0,incoming+statusDamage-block);
            if (outcomes)
            {
                var killedBeforeHandEnd=damage>=health;
                var actual = Math.Min(health, Math.Max(0, damage + poisonDamage));
                health -= actual;
                // Poison ticks at the enemy's turn start, after our hand-end
                // Withers. Only direct lethal prevents those status effects.
                if (health <= 0) harm = ordinary.Sum(e => e.Facts.HpLoss * W(e))
                    +(killedBeforeHandEnd?0:Math.Max(0,statusDamage-block));
                total += actual - harm * 1.3;
                if(health>0)total+=1.3*tank*(shared?.GuardedSavings[turn-1]??0);
                // Match other shared support below: an ally's generated attack
                // cannot count as an owner's strike, borrow their multipliers,
                // or suppress the owner's hand-end/enemy damage in this trace.
                var shivCredit=Math.Min(Math.Max(0,health-sharedCredit),alliedShivBonus);
                sharedCredit+=shivCredit;total+=shivCredit;
                if(shared is not null)
                {
                    ownVulnerable=Math.Max(0,ownVulnerable-1);
                    var stacks=ownVulnerable+.5*appliedVulnerable;
                    var others=shared.Vulnerable[turn-1];
                    // Diminishing coverage from expected duration: another
                    // source matters less when allies already maintain Vulnerable.
                    // Credit only the extra TEAM damage enabled by this deck;
                    // the owner's own 50% bonus is already in damage above.
                    var coverage=Math.Exp(-others)*(1-Math.Exp(-stacks));
                    var bonus=.5*shared.AttackDamage[turn-1]*coverage
                        +shared.Payoff[turn-1]*stacks*(1+.5*(1-Math.Exp(-others-stacks)));
                    // Shared support is utility credit, not an invented local
                    // lethal that suppresses our hand-end damage. Cap total
                    // credit by health still unaccounted for in this encounter.
                    bonus=Math.Min(Math.Max(0,health-sharedCredit),bonus);
                    sharedCredit+=bonus;total+=bonus;
                    ownVulnerable+=appliedVulnerable;
                }
            }
            else total+=damage+poisonDamage+Math.Min(incoming+statusDamage,block)-harm*1.3;
            var survivalPenalty=SurvivalValue.ExtraLossCost(plan.OwnerHp,plan.OwnerMaxHp,cumulativeHpLoss+harm)
                -SurvivalValue.ExtraLossCost(plan.OwnerHp,plan.OwnerMaxHp,cumulativeHpLoss);
            total-=survivalPenalty;
            cumulativeHpLoss+=harm;
            // Discard prevents this hand's damage; only exhaust removes future
            // pollution. The ordinary scenario never fabricates boss statuses.
            status=Math.Max(0,status+(handOutcome?.Generated??0)-(handOutcome is null?Math.Min(cleaned,permanent):permanent));
            if(turn==1)status=Math.Max(0,status-plan.EtherealStatuses*Math.Min(1,draw/size));
            var shuffleStatuses=0;
            var circulating=Math.Max(0,deck.Length-initialStatus+status-active.Sum()-remaining.Sum(e=>1-e));
            if(plan.ShuffleSoot && circulating>0)
            {
                // The hand model pads tiny decks to five unavailable slots.
                // Those placeholders are not native draws and cannot shuffle
                // an empty pile or recreate a deck exhausted to nothing.
                untilShuffle-=Math.Min(circulating,handOutcome is { } sampled ? sampled.Drawn+setupPlays : draw);
                // Merely emptying the pile does not shuffle. The next attempted
                // draw causes it, then its Soot participates in later cycles.
                while(untilShuffle<0)
                {
                    shuffleStatuses++; status++;
                    untilShuffle+=Math.Max(1,deck.Length-initialStatus+status-active.Sum()-remaining.Sum(e=>1-e));
                }
            }
            if(pollution)
            {
                var generated=handOutcome?.Generated ?? triggerPlays/6;
                var arrival=health<=0 && outcomes?0:(turn%3==0?2:0);
                status+=(handOutcome is null?generated:0)+arrival;
                witherStock=Math.Max(0,witherStock+generated-(handOutcome?.WitherExhausted??0))+arrival;
            }
            trace?.Invoke(new(turn,damage+poisonDamage,strength+temporaryStrength,shivs,permanent,ordinary.ToDictionary(e=>e.Original,e=>weights[e.Index]),energySpent,available)
                { AttackPlays = attackPlays + repeats, VulnerableApplied = appliedVulnerable, EnemyHealthAfter = outcomes ? health : double.NaN, StatusDamage=statusDamage,
                    AttackDamage=attackDamage,VulnerablePayoff=vulnerablePayoff,ExhaustCount=exhausted,Block=block,Incoming=incoming,
                    BossPhase=bossPhase,ReactiveStrength=reactiveStrength,HandDrawn=handOutcome?.Drawn??draw,StartDiscardCount=handOutcome?.StartDiscardCount??0,
                    ShuffleStatusesGenerated=shuffleStatuses,TriggeredPoison=triggeredPoison,TriggeredBlock=triggeredBlock,PoisonDamage=poisonDamage,
                    BlockTriggerDamage=blockTriggerDamage,OwnerHpAfter=Math.Max(0,plan.OwnerHp-cumulativeHpLoss),SurvivalPenalty=survivalPenalty });
            if (outcomes && health <= 0)
            {
                if(adaptable is null || ++bossPhase>=adaptable.Health.Length)break;
                health=adaptable.Health[bossPhase];phaseTurns=0;reactiveStrength=0;
                poison=0;poisonOthers=0;vulnerableStock=0;
                Array.Clear(futureVulnerable);Array.Fill(noVulnerable,1);
            }
        }
        return total/horizon;
    }
}
