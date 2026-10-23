using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    internal bool SamePowerAmounts(KernelSession other) => Combat.EffectivePowers()
        .Select(p => (p.Owner, Type: p.GetType(), p.Amount)).ToHashSet()
        .SetEquals(other.Combat.EffectivePowers().Select(p => (p.Owner, Type: p.GetType(), p.Amount)));

    // Duration is a resource too: additional Vulnerable/Weak after the first
    // stack can amplify later turns. Price only future turns here; current
    // hand damage and current incoming attacks are already resolved elsewhere.
    // After enemy settlement, elapsedTurns=0: native duration has already ticked.
    // This is the ordinary duration estimate, not a cap on stack conversion;
    // native damage/Strength branches use the complete Vulnerable amount.
    internal double FutureDebuffValue(IReadOnlyList<Player> actors, int elapsedTurns = 1, KernelSession? reference = null)
    {
        if (!Enemies.Any(e => Power<VulnerablePower>(e) > elapsedTurns || Power<WeakPower>(e) > elapsedTurns)) return 0;
        // Compare branches against one frozen opportunity window. Recomputing
        // ceil(HP/output) after each hit made six more damage lose an entire
        // turn's Vulnerable dividend (owner's 2026-10-04 replay).
        reference ??= this;
        var futureStats = AfterTemporaryStatExpiry();
        var referenceStats = ReferenceEquals(reference, this) ? futureStats : reference.AfterTemporaryStatExpiry();
        double output = 0, referenceOutput = 0;
        foreach (var player in actors.Where(p => reference.Hp(p.Creature) > 0 && Hp(p.Creature) > 0))
        {
            var pcs = reference.simulator.State.GetPlayerCombatState(player);
            var cards = pcs.Hand.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.DiscardPile.Cards).ToArray();
            var f = reference.ReadPowerOpportunities(player, cards);
            var plays = Math.Min(f.Attacks, player.MaxEnergy / Math.Max(1d, f.AttackCost));
            referenceOutput += plays * Math.Max(0, f.AttackDamage + referenceStats.Power<StrengthPower>(player.Creature));
            // Freeze opportunities, not newly earned Strength. Otherwise the
            // same window prices stronger future Vulnerable hits as unchanged
            // and can cash out Dominate before the native duration multiplier.
            output += plays * Math.Max(0, f.AttackDamage + futureStats.Power<StrengthPower>(player.Creature));
        }
        var totalHp = Math.Max(1, reference.Enemies.Sum(reference.Hp));
        var future = Math.Clamp(totalHp / Math.Max(1, referenceOutput) - 1, 0, 8);
        var attacks = reference.CurrentAttacks();
        double debuffValue = 0;
        foreach (var enemy in Enemies)
        {
            // 巨斧机器人 (an Axe-Bot summons) is a root creature whose SimCreatureState was
            // never materialized, so reading its powers threw
            //   InvalidOperationException: Root creature state was not materialized for 巨斧机器人
            // three times in the live 2026-10-05 AXEBOTS fight; each throw was caught by the
            // round settlement and dropped that decision to the LEGACY planner
            // (log: `CoopBots tournament: falling back for this tick`, lines 50312/50355/50396).
            //
            // This is a bounded PRICING estimate, not a rules path: an enemy whose state
            // cannot be resolved contributes no future-debuff value instead of taking the
            // whole round down. The rules simulation still sees the real creature.
            try
            {
                var vulnerable = Math.Min(Math.Max(0, Power<VulnerablePower>(enemy) - elapsedTurns), future);
                var weak = Math.Min(Math.Max(0, Power<WeakPower>(enemy) - elapsedTurns), future);
                var debilitate = Math.Min(Math.Max(0, Power<DebilitatePower>(enemy) - elapsedTurns), future);
                debuffValue += .8 * (vulnerable + Math.Min(vulnerable, debilitate))
                    * output * reference.Hp(enemy) / totalHp * (TeamSynergyNative.VulnerableFactor - 1);
                foreach (var player in ChoiceParty.Where(p => Hp(p.Creature) > 0 && reference.Hp(p.Creature) > 0))
                {
                    var raw = attacks.Where(a => a.Enemy == enemy).Sum(a => Math.Max(0,
                        (double)a.Raw + futureStats.Power<StrengthPower>(enemy)) * a.Repeats);
                    var pcs = reference.simulator.State.GetPlayerCombatState(player);
                    var opportunities = reference.ReadPowerOpportunities(player,
                        pcs.Hand.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.DiscardPile.Cards).ToArray());
                    var block = opportunities.Blocks * opportunities.Block;
                    var reduction = 1 - TeamSynergyNative.WeakFactorFor(player, enemy);
                    var ordinary = Math.Max(0, raw - block) - Math.Max(0, raw * (1 - reduction) - block);
                    var amplified = Math.Max(0, raw - block)
                        - Math.Max(0, raw * Math.Max(0, 1 - 2 * reduction) - block);
                    debuffValue += weak * ordinary + Math.Min(weak, debilitate) * (amplified - ordinary);
                }
            }
            catch (InvalidOperationException)
            {
                // Unmaterialized summons: price them at zero rather than truncating the round.
            }
        }
        return debuffValue;
    }

    internal double CopyResidualValue(KernelSession reference, Player owner)
    {
        if (!CanFork || HasTerminal || Hp(owner.Creature) <= 0 || KernelSearchBudgetScope.IsStopped) return 0;
        var original = CombatCards(reference, owner).Select(c => c.Original).ToHashSet();
        var source = CombatCards(this, owner);
        var copies = source.Where(c => !original.Contains(c.Original)).ToList();
        var nightmares = Combat.EffectivePowers().OfType<NightmarePower>()
            .Where(p => p.Owner == owner.Creature && p.Amount > 0).ToArray();
        foreach (var nightmare in nightmares)
        {
            var selected = Combat.GetNightmareSelection(nightmare);
            for (var i = 0; i < Math.Min(10, nightmare.Amount); i++) copies.Add(selected);
        }
        if (!copies.Any(c => TeamSynergyNative.HasComponent(TeamSynergyNative.Component(c.Preview,
            c.GetEnergyCostWithModifiers(simulator, simulator.State.GetPlayerCombatState(owner)))))) return 0;
        var before = new List<TeamSynergyMath.Seat>();
        var after = new List<TeamSynergyMath.Seat>();
        var unchanged = source.Where(c => original.Contains(c.Original)).ToArray();
        foreach (var player in ChoiceParty)
        {
            var cards = player == owner ? unchanged : CombatCards(this, player);
            before.Add(CopySeat(player, cards));
            after.Add(player == owner ? CopySeat(player, unchanged.Concat(copies).ToArray()) : before[^1]);
        }
        var hp = Enemies.Sum(Hp);
        var pressure = new TeamSynergyMath.Pressure(hp, 0, 0,
            Enemies.Count > 0 && Enemies.All(e => Power<ArtifactPower>(e) > 0) ? 1 : 0);
        var ordinary = TeamSynergyMath.Evaluate(before, pressure);
        var copied = TeamSynergyMath.Evaluate(after, pressure);
        var ordinaryOutput = before.Sum(s => s.Attack + s.OtherDamage);
        var marginal = copied.Damage - ordinary.Damage
            - (copied.UnamplifiedDamage - ordinary.UnamplifiedDamage);
        var pcs = simulator.State.GetPlayerCombatState(owner);
        var inHand = copies.Any(c => pcs.Hand.Cards.Contains(c));
        var arrival = nightmares.Length > 0 ? 1 : inHand ? 0 : Math.Max(1, pcs.DrawPile.Cards.Count / 5d);
        var danger = ChoiceParty.Where(p => Hp(p.Creature) > 0)
            .Max(p => Threat(p).HpLoss / Math.Max(1d, Hp(p.Creature)));
        var survival = Math.Clamp(1 - danger, 0, 1);
        return .8 * TeamSynergyMath.DelayedMarginal(marginal, hp, ordinaryOutput, arrival, 1, survival);
    }

    private static PredictedCard[] CombatCards(KernelSession state, Player player)
    {
        var pcs = state.simulator.State.GetPlayerCombatState(player);
        return pcs.Hand.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.DiscardPile.Cards).ToArray();
    }

    private TeamSynergyMath.Seat CopySeat(Player player, IReadOnlyList<PredictedCard> cards)
    {
        var opportunities = ReadPowerOpportunities(player, cards);
        var energy = Math.Max(0, PersistentPowerSupport.GetModifiedMaxEnergy(Combat, player));
        var pcs = simulator.State.GetPlayerCombatState(player);
        var components = cards.Where(c => !c.Preview.Keywords.Contains(CardKeyword.Ethereal)
                && c.GetStarCostWithModifiers(simulator, pcs) <= pcs.Stars)
            .OrderBy(CardChoiceSupport.ChoiceCardKey, StringComparer.Ordinal)
            .Select(c => TeamSynergyNative.Component(c.Preview, c.GetEnergyCostWithModifiers(simulator, pcs)))
            .Where(TeamSynergyNative.HasComponent).ToArray();
        var attack = Math.Min(opportunities.Attacks, energy / Math.Max(1d, opportunities.AttackCost))
            * Math.Max(0, opportunities.AttackDamage + Power<StrengthPower>(player.Creature));
        return new(cards.Count, Math.Min(cards.Count, 5 + Math.Min(3, opportunities.Draws)), energy,
            attack, 0, opportunities.Blocks * opportunities.Block, Hp(player.Creature), MaxHp(player.Creature),
            components, TeamSynergyNative.VulnerableFactorFor(player, Enemies.FirstOrDefault()),
            TeamSynergyNative.WeakFactorFor(player));
    }

    // Upstream's generic fallback is one point per stack for these powers.
    // Our multiplayer tactical frontier needs their recurring output before a
    // complete rollout is available (owner reports, 2026-09-28). These are only
    // bounded retention estimates; native simulation still judges full endings.
    private static StrategicEffectRequirements EngineRequirements(PowerModel power) => power switch
    {
        InfiniteBladesPower => StrategicEffectRequirements.RemainingTurns,
        FanOfKnivesPower => StrategicEffectRequirements.ShivPlays,
        SneakyPower => StrategicEffectRequirements.RemainingTurns,
        SerpentFormPower => StrategicEffectRequirements.UsefulCardPlays,
        _ when ExtendedPowerValueSupported(power) => StrategicEffectRequirements.RemainingTurns,
        _ => StrategicEffectRequirements.None,
    };

    private double? EngineValue(PowerModel power, Player owner, KernelSession reference,
        StrategicEffectContext context, PowerOpportunities opportunities)
    {
        var futureTurns = Math.Clamp(context.RemainingTurns - 1, 0, 3);
        var shivDamage = Math.Max(0, 4 + Power<StrengthPower>(owner.Creature)
            + Power<AccuracyPower>(owner.Creature));
        switch (power)
        {
            case InfiniteBladesPower:
                // Starts on the NEXT turn. Duplicates add one Shiv each; Fan's
                // all-target effect does not stack its target count.
                var targets = Power<FanOfKnivesPower>(owner.Creature) > 0 ? Enemies.Count : 1;
                return Math.Min(context.EnemyHp, power.Amount * futureTurns * shivDamage * targets);
            case FanOfKnivesPower:
                var futureShivs = context.ShivPlays + Power<InfiniteBladesPower>(owner.Creature) * futureTurns;
                return Math.Min(context.EnemyHp, futureShivs * shivDamage * Math.Max(0, Enemies.Count - 1));
            case SerpentFormPower:
                return Math.Min(context.EnemyHp, power.Amount * context.UsefulCardPlays);
            case SneakyPower:
                // Public allied decks, never an undelegated human's hidden hand.
                // Count attack CARD plays, not hits, and exclude the owner.
                var attacks = reference.Party.Where(p => p != owner && reference.Hp(p.Creature) > 0)
                    .Sum(p =>
                    {
                        var deck = p.Deck.Cards;
                        var count = deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
                        return Math.Min(Math.Max(0, p.MaxEnergy), count * 5d / Math.Max(1, deck.Count));
                    });
                return StrategicEffectModel.Prevention((int)Math.Round(power.Amount * attacks * context.RemainingTurns), context).RetentionValue;
            default: return ExtendedPowerValue(power, owner, reference, context, opportunities);
        }
    }

    // Upstream's StrategicEffectModel falls back to stack count for these effects.
    // CoopBots values their bounded future output in the tactical frontier, using
    // branch cards and native triggers. Requested by the owner, 2026-10-01 (all
    // Ironclad/Silent powers). These estimates never change terminal ranking.
    private static bool ExtendedPowerValueSupported(PowerModel p) => p is
        AggressionPower or BarricadePower or CrimsonMantlePower or CrueltyPower
        or HellraiserPower or InfernoPower or JuggernautPower or JugglingPower
        or PyrePower or RupturePower or StampedePower or PlatingPower or TankPower
        or GuardedPower or UnmovablePower or ViciousPower or AccelerantPower
        or NoxiousFumesPower or PhantomBladesPower or SpeedsterPower
        or ToolsOfTheTradePower or TrackingPower or WellLaidPlansPower
        or IntangiblePower or WraithFormPower;

    private readonly record struct PowerOpportunities(double Attacks, double AttackDamage, double AttackCost,
        double Blocks, double Block, double Draws, double Vulnerable, double Weak,
        double SelfHits, double Shivs, double Strikes, double EnergyDemand,
        double Useful, double Retainable, bool HasX);

    private PowerOpportunities ReadPowerOpportunities(Player owner, IReadOnlyList<PredictedCard> cards)
    {
        double attacks=0, damage=0, blocks=0, block=0, draws=0, vulnerable=0, weak=0,
            self=0, shivs=0, strikes=0, demand=0, attackCost=0, useful=0, retainable=0;
        bool hasX=false;
        var pcs=simulator.State.GetPlayerCombatState(owner);
        foreach(var predicted in cards)
        {
            var c=predicted.Preview;
            if(c.Keywords.Contains(CardKeyword.Unplayable)) continue;
            useful++;
            if(!c.Keywords.Contains(CardKeyword.Retain) && !c.Keywords.Contains(CardKeyword.Ethereal)) retainable++;
            if(c.Type==CardType.Attack){attacks++; damage+=c.DynamicVars.Values.OfType<DamageVar>().Sum(v=>(double)v.BaseValue);}
            var b=c.DynamicVars.Values.OfType<BlockVar>().Sum(v=>(double)v.BaseValue);
            if(b>0){blocks++;block+=b;}
            double Var(string key)=>c.DynamicVars.TryGetValue(key,out var v)?Math.Max(0,(double)v.BaseValue):0;
            draws+=SilentCardFlowFacts.DrawCount(c.Id.Entry,(int)Var("Cards"),5);
            if(c.GetType().Name is "BattleTrance" or "BurningPact" or "PommelStrike" or "ShrugItOff" or "Offering")
                draws+=Var("Cards");
            vulnerable+=Var("VulnerablePower")>0?1:0;
            weak+=Var("WeakPower")>0?1:0;
            self+=Var("HpLoss")>0?1:0;
            if(c.Tags.Contains(CardTag.Shiv)) shivs++;
            if(c.GetType().Name is "BladeDance" or "CloakAndDagger" or "HiddenDaggers") shivs+=Var("Cards");
            if(c.Tags.Contains(CardTag.Strike)) strikes++;
            hasX|=c.EnergyCost.CostsX;
            var cost=Math.Max(0,predicted.GetEnergyCostWithModifiers(simulator,pcs));
            demand+=cost;
            if(c.Type==CardType.Attack) attackCost+=cost;
        }
        var drawsPerTurn=5d+Math.Min(3,draws*5/Math.Max(5,cards.Count));
        var fraction=drawsPerTurn/Math.Max(5,cards.Count);
        return new(attacks*fraction, attacks>0?damage/attacks:0, attacks>0?attackCost/attacks:0, blocks*fraction,
            blocks>0?block/blocks:0, draws*fraction, vulnerable*fraction, weak*fraction,
            self*fraction, shivs*fraction, strikes*fraction, demand*fraction,
            useful*fraction, retainable*fraction, hasX);
    }

    // Owner rule 2026-10-05: a Vulnerable fight that outlasts this horizon (no win-in-three
    // script) must deploy Cruelty immediately. `SetupValueHorizonTurns` is 16, so
    // `context.RemainingTurns` can exceed this; the estimate below simply stops growing at
    // three, which is why a long fight never invested in it.
    private const int CrueltyLongFightTurns = 3;
    // Deliberate dominance, NOT a calibrated effect size. See the CrueltyPower case.
    private const double CrueltyLongFightPriority = 8;

    private double? ExtendedPowerValue(PowerModel power, Player owner, KernelSession reference,
        StrategicEffectContext context, PowerOpportunities f)
    {
        var turns=Math.Clamp(context.RemainingTurns-1,0,3);
        var amount=Math.Max(0,power.Amount);
        var energy=Math.Max(0,owner.MaxEnergy);
        var attacks=Math.Min(f.Attacks,Math.Max(1,energy));
        var blocks=Math.Min(f.Blocks,Math.Max(1,energy));
        double Damage(double n)=>Math.Min(context.EnemyHp,Math.Max(0,n))*.8;
        double BlockValue(double n)=>Math.Max(0,Math.Min(n,context.IncomingDamage>0?context.IncomingDamage*Math.Max(1,turns):n));
        double SelfCost(PowerModel p)=>p.DynamicVars.TryGetValue("SelfDamage",out var v)?Math.Max(0,(double)v.BaseValue):0;
        var recurringSelf=Combat.EffectivePowers().Count(p=>p.Owner==owner.Creature
            && p is CrimsonMantlePower or InfernoPower && p.Amount>0 && SelfCost(p)>0);
        switch(power)
        {
            case PyrePower:
                // A zero-cost deck cannot spend extra energy; X cards can always use it.
                // Otherwise the surplus is worth what the deck actually pays per turn.
                // Pricing only the part that exceeds the CURRENT average demand valued
                // Pyre at zero in an energy-rich deck, and a permanent engine was then
                // never deployed (live 2026-10-04: PYRE+ sat in hand at 2 energy while the
                // script spent it on a Strike). A cost-2 investment must still compete with
                // real plays, so the estimate stays capped by that demand.
                var spendable=f.HasX ? amount
                    : f.EnergyDemand<=0 ? 0
                    : Math.Min(amount,Math.Max(1,f.EnergyDemand));
                var energyOutput=Math.Max(3,.8*f.AttackDamage/Math.Max(1,f.AttackCost));
                return Damage(spendable*turns*energyOutput/.8);
            case AggressionPower:
                // Retrieves/upgrades attacks; they still cost energy to play.
                return Math.Min(amount,f.Attacks)*turns*(.7+Math.Min(3,f.AttackDamage*.25));
            case BarricadePower:
                return BlockValue(Math.Max(0,Block(owner.Creature)+blocks*f.Block-context.IncomingDamage)*turns*.6);
            case CrimsonMantlePower:
                return BlockValue(amount*turns)-SelfCost(power)*turns;
            case CrueltyPower:
            {
                // ALREADY-VULNERABLE is what Cruelty multiplies. A hand that merely HOLDS a
                // Vulnerable applier still has to land it first, which is the existing fallback.
                var vulnerableNow=reference.Enemies.Any(e=>reference.Power<VulnerablePower>(e)>0);
                if (vulnerableNow && context.RemainingTurns > CrueltyLongFightTurns)
                {
                    // OWNER RULE 2026-10-05 (deviation — see AGENT.md §6). Once the fight is known
                    // to outlast the three-turn setup horizon — i.e. the search found no
                    // win-in-three script — a +25% multiplier on EVERY remaining attack must not be
                    // priced as a four-turn engine. Value it across the whole remaining fight and
                    // apply CrueltyLongFightPriority so it outranks the other plays: every later
                    // turn without it forfeits a quarter of the party's output, and no single card
                    // in hand can match that. The multiplier is a POLICY (deliberate dominance), not
                    // a measured effect size; an un-scaled estimate would only make Cruelty tie with
                    // a Strike instead of being played first.
                    var wholeFight=Math.Max(1,context.RemainingTurns);
                    return Damage(attacks*f.AttackDamage*wholeFight*amount/100d)*CrueltyLongFightPriority;
                }
                return Damage(attacks*f.AttackDamage*(turns+1)*amount/100d
                    * (vulnerableNow?1:Math.Min(1,f.Vulnerable)));
            }
            case HellraiserPower:
                return Damage(f.Strikes*turns*f.AttackDamage);
            case InfernoPower:
                return Damage(amount*(f.SelfHits+recurringSelf)*turns*Enemies.Count)-SelfCost(power)*turns;
            case JuggernautPower:
                var gains=blocks+(Power<AfterimagePower>(owner.Creature)>0?Math.Min(f.Useful,energy):0)
                    +(Power<CrimsonMantlePower>(owner.Creature)>0?1:0)+(Power<PlatingPower>(owner.Creature)>0?1:0);
                return Damage(amount*gains*(turns+1));
            case JugglingPower:
                // Only the THIRD attack generates a copy, once per turn; not a free autoplay.
                var copies=f.AttackCost<=0?amount:Math.Max(0,energy-3*f.AttackCost)/f.AttackCost;
                return f.Attacks>=3 ? Damage(Math.Min(amount,copies)*turns*f.AttackDamage) : 0;
            case RupturePower:
                return Damage(amount*(f.SelfHits+recurringSelf)*attacks*turns*(turns+1)/2);
            case StampedePower:
                return Damage(Math.Min(amount,Math.Max(0,f.Attacks-energy+1))*f.AttackDamage*(turns+1));
            case PlatingPower:
                // This turn's block is already counted by Threat/EndPlating.
                return BlockValue(Enumerable.Range(1,turns).Sum(t=>Math.Max(0,amount-t)));
            case TankPower:
            case GuardedPower:
                // Current incoming damage already includes both exact multipliers.
                return 0;
            case UnmovablePower:
                return BlockValue(Math.Min(amount,blocks)*f.Block*(turns+1));
            case ViciousPower:
                return amount*f.Vulnerable*(turns+1)*3;
            case AccelerantPower:
                // Poison loses a stack on EVERY trigger, including extra triggers.
                var other=Party.Where(p=>p!=owner && Hp(p.Creature)>0).Sum(p=>Power<AccelerantPower>(p.Creature));
                return Damage(Enemies.Sum(e=>Math.Min(Hp(e),Math.Max(0,
                    PoisonTicks(Power<PoisonPower>(e),1+other+amount,turns+1)
                    -PoisonTicks(Power<PoisonPower>(e),1+other,turns+1)))));
            case NoxiousFumesPower:
                return Damage(Enemies.Sum(e=>Math.Min(Hp(e),Math.Max(0,
                    FumesDamage(Power<PoisonPower>(e),amount,turns)
                    -FumesDamage(Power<PoisonPower>(e),0,turns)))));
            case PhantomBladesPower:
                // The bonus applies to only the first Shiv each turn.
                return Damage(amount*Math.Min(1,f.Shivs+Power<InfiniteBladesPower>(owner.Creature))*(turns+1));
            case SpeedsterPower:
                // Normal start-of-turn hand draw does not trigger Speedster.
                var extraDraws=f.Draws+Power<ViciousPower>(owner.Creature)*f.Vulnerable;
                return Damage(amount*extraDraws*(turns+1)*Enemies.Count);
            case ToolsOfTheTradePower:
                return Math.Min(amount,f.Useful)*turns*2; // extra draw plus mandatory discard
            case TrackingPower:
                return Damage(attacks*f.AttackDamage*(turns+1)*amount/100d
                    *(reference.Enemies.Any(e=>reference.Power<WeakPower>(e)>0)?1:Math.Min(1,f.Weak)));
            case WellLaidPlansPower:
                // All hand cards retain; price unspent useful cards, not harmful statuses.
                var unspent=f.EnergyDemand<=0?0:Math.Clamp(1-energy/f.EnergyDemand,0,1);
                return Math.Min(5,f.Retainable*unspent)*turns*1.4;
            case IntangiblePower:
                // This turn is already in Threat. Additional protected turns only.
                var raw=reference.CurrentAttacks().Sum(a=>(double)a.Raw*a.Repeats);
                return Math.Max(0,Math.Max(raw,context.IncomingDamage)-context.IncomingHitCount)*Math.Min(turns,Math.Max(0,amount-1));
            case WraithFormPower:
                // Dexterity falls at future player starts, including intangible turns.
                return -amount*blocks*turns*(turns+1)/2;
            default:return null;
        }
    }

    private static double PoisonTicks(int poison,int repeats,int turns)
    {
        var ticks=Math.Min(poison,Math.Max(0,repeats)*turns);
        return ticks*(2d*poison-ticks+1)/2;
    }
    private double FumesDamage(int poison,int amount,int turns)
    {
        double total=0;var repeats=1+Party.Where(p=>Hp(p.Creature)>0).Sum(p=>Power<AccelerantPower>(p.Creature));
        for(var t=0;t<turns;t++){poison+=amount;total+=PoisonTicks(poison,repeats,1);poison=Math.Max(0,poison-repeats);}
        return total;
    }
}
