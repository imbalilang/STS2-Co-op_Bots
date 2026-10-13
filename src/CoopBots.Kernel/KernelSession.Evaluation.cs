using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots.Kernel;

public sealed partial class KernelSession
{
    /// <summary>One base attack hit the enemy is forecast to make next round.</summary>
    public sealed record ForecastAttack(Creature Enemy, decimal Raw, int Repeats);

    /// <summary>
    /// Enemy attacks for the next <paramref name="rounds"/> rounds from the
    /// upstream move-state forecaster. This is the cross-turn information the
    /// single-turn search lacks: it lets the team value killing or weakening an
    /// enemy before its coming hits, without pretending to have simulated the
    /// enemy turn itself.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ForecastAttack>> ForecastRounds(CombatState live, int rounds)
    {
        if (rounds < 1) return [];
        try
        {
            var forecast = IntentForecaster.Build(live, rounds + 1);
            var result = new List<IReadOnlyList<ForecastAttack>>();
            for (var round = 1; round < forecast.Rounds.Count && result.Count < rounds; round++)
            {
                var attacks = new List<ForecastAttack>();
                foreach (var move in forecast.Rounds[round])
                    foreach (var hit in move.AttackHits)
                        attacks.Add(new ForecastAttack(move.Owner, hit.BaseDamage, 1));
                result.Add(attacks);
            }
            return result;
        }
        catch
        {
            // Forecasting is optional; a state machine we cannot roll simply means
            // no future-turn term rather than a broken plan.
            return [];
        }
    }

    /// <summary>
    /// The attacks this branch's enemies will actually make on the coming enemy
    /// phase. Unlike a forecast built from the live root, this reflects the
    /// branch's own monster AI (stuns, forced moves, previous rounds).
    /// </summary>
    public IReadOnlyList<(Creature Enemy, decimal Raw, int Repeats)> CurrentAttacks()
    {
        var result = new List<(Creature, decimal, int)>();
        foreach (var enemy in Enemies)
        {
            var read = false;
            try
            {
                var move = Combat.CurrentMonsterMove(enemy);
                read = true; // A modeled non-attack is authoritative, not a missing forecast.
                foreach (var hit in move.AttackHits)
                {
                    if (hit.BaseDamage <= 0) continue;
                    result.Add((enemy, hit.BaseDamage, 1));
                    read = true;
                }
            }
            catch
            {
                // Fall through to the live intent below.
            }
            if (read || enemy.Monster is null) continue;
            // Fallback for enemies whose predicted AI we cannot read (mocks, or a
            // state machine the simulator does not track): use the current intent.
            foreach (var intent in enemy.Monster.NextMove.Intents.OfType<AttackIntent>())
                result.Add((enemy, intent.DamageCalc?.Invoke() ?? 0m, intent.Repeats));
        }
        return result;
    }

    public int Power<T>(Creature target) where T : PowerModel => Combat.GetAmount<T>(target);

    internal bool HasEnemyCardReactions => Combat.EffectivePowers().Any(p => Enemies.Contains(p.Owner)
        && p.GetType().GetMethod(nameof(PowerModel.AfterCardPlayed))?.DeclaringType != typeof(PowerModel));

    internal KernelSession WithoutEnemyVulnerable()
        => WithEnemyVulnerableFrom(null);

    internal KernelSession WithEnemyVulnerableFrom(KernelSession? reference)
    {
        var copy = Fork();
        foreach (var enemy in copy.Enemies)
            copy.Combat.SetAmount<VulnerablePower>(enemy, reference?.Power<VulnerablePower>(enemy) ?? 0);
        return copy;
    }

    internal int Stars(Player player) => simulator.State.GetPlayerCombatState(player).Stars;

    internal (int Energy, int Stars) AttackCosts(CardModel source)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source)!;
        var owner = simulator.State.GetPlayerCombatState(card.Preview.Owner);
        return (card.GetEnergyCostWithModifiers(simulator, owner), card.GetStarCostWithModifiers(simulator, owner));
    }

    // A cheap ordering hint, not a substitute for resolving CardBranches. Read
    // branch-local costs, variables and damage hooks (Strength, Weak, Vulnerable,
    // Paper Phrog, etc.). Bully's stack conversion uses the branch-local registry
    // shared with simulation. Other calculated attacks retain their existing
    // ordering path; widening this hint changes unrelated investment decisions.
    internal int AttackDamageHint(CardModel source, Creature target)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source);
        if (card is null || card.Preview.Type != CardType.Attack) return 0;
        var damage = card.Preview.DynamicVars.Values.OfType<DamageVar>().Sum(v => v.BaseValue);
        if (card.Preview is MegaCrit.Sts2.Core.Models.Cards.Bully)
            foreach (var calculated in card.Preview.DynamicVars.Values.OfType<CalculatedDamageVar>())
                if (CalculatedVarSpecRegistry.TryCalculate(calculated, simulator, card, target, out var amount))
                    damage += amount;
        if (damage <= 0) return 0;
        return Math.Max(0, (int)HookMirrors.ModifyDamage(simulator, target, card.Preview.Owner.Creature,
            damage, ValueProp.Move, card, null));
    }

    // Both initial candidate pruning and rollout ordering use this path. Area
    // attacks need the same branch-local hooks as individually targeted attacks.
    internal (double Damage, bool Lethal) AttackHint(CardModel source, Creature? target)
    {
        var area = source.Type == CardType.Attack && source.TargetType == TargetType.AllEnemies;
        if (area)
        {
            var damage = Enemies.Sum(e => Math.Min(Hp(e) + Block(e), AttackDamageHint(source, e)));
            return (damage, Enemies.Any(e => AttackDamageHint(source, e) >= Hp(e) + Block(e)));
        }
        var hit = target is not null && source.Type == CardType.Attack ? AttackDamageHint(source, target)
            : source.DynamicVars.Values.OfType<DamageVar>().Sum(v => (double)v.BaseValue);
        return (hit, hit > 0 && target is not null && hit >= Hp(target) + Block(target));
    }

    // Ordering only: surplus block is valuable when it persists or feeds a
    // payable Body Slam. Otherwise score the damage this block can prevent.
    internal double UsefulBlockHint(CardModel source, Creature? target)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var card = simulator.State.FindCard(source);
        var recipient = target?.Player ?? CardOwner(source);
        if (card is null) return 0;
        var amount = card.Preview.DynamicVars.Values.OfType<BlockVar>().Sum(v => v.BaseValue);
        if (amount <= 0) return 0;
        var gain = Math.Max(0, (double)HookMirrors.ModifyBlock(simulator, recipient.Creature,
            amount, ValueProp.Move, card, null, out _));
        var persistent = Combat.EffectivePowers().Any(p => p.Owner == recipient.Creature
            && p.Amount > 0 && p.GetType().Name is "BarricadePower" or "BlurPower");
        var slam = Hand(recipient).Any(c => c.GetType().Name == "BodySlam" && CanPlay(c)
            && AttackCosts(c).Energy + (CardOwner(source) == recipient ? AttackCosts(source).Energy : 0) <= Energy(recipient));
        if (persistent || slam) return gain;
        var threat = Threat(recipient);
        return Math.Min(gain, Math.Max(0, threat.HpLoss - threat.DirectHpLoss));
    }
    /// <summary>The Knowledge Demon curse counter this branch has reached.</summary>
    public int KnowledgeDemonCurseCounter(Creature source) => Combat.GetKnowledgeDemonCurseCounter(source);
    /// <summary>
    /// Full visible-state text of this branch for one player: HP, block, energy,
    /// stars, gold, hands with upgrades, orbs and orb slots, potions, relics and
    /// powers. Finer than the planner's own view, which is why it is the check for
    /// "did this action change anything" — a hand upgrade or an extra orb slot is
    /// real in the branch yet invisible in the HP and pile counters.
    /// </summary>
    public string StateText(Player player)
    {
        if (player.PlayerCombatState is null) throw new InvalidOperationException("Player has no combat state.");
        IntentForecast forecast;
        try { forecast = IntentForecaster.Build(liveRoot ?? throw new InvalidOperationException(), 1); }
        catch
        {
            forecast = new IntentForecast
            {
                Rounds = [], HasUnsupportedIntent = true, IsExactForModeledDamage = false,
                UnsupportedDetails = [], ApproximationDetails = [], MonsterAiCountersByRound = [],
            };
        }
        // The branch's OWN turn number, never the live PlayerCombatState's. This render
        // happens right after the search finishes, while the live board is still on the
        // old turn, and deployment compares it after the boundary has been crossed — so
        // reading the live value here guaranteed `field=turn` drift on the first step past
        // every crossing. See KernelSession.TurnNumber.
        return ContinuationStamp.CapturePredicted(
            player, simulator, Combat.GetPlayerTurnNumber(player), forecast, Combat.GetRootPlayerTurnNumber(player)).StateText;
    }
    public bool SandpitDeath(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Any(p => p.Target == target && p.Amount <= 1 && Hp(p.Owner) > 0);
    public int? SandpitTurnsRemaining(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Where(p => p.Target == target && Hp(p.Owner) > 0).Select(p => (int?)p.Amount).Min();
    public double SandpitReserve(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Where(p => p.Target == target && Hp(p.Owner) > 0)
        .Sum(p => p.Amount switch { <= 1 => 30.0, 2 => 12, 3 => 3, _ => 0 });

    /// <summary>
    /// The live card's mutable-state key, the same identity <see cref="FindLiveCardByKey"/>
    /// resolves a planned step against. Exposed so deployment can re-assert that the
    /// object it found is still in the state the plan was verified against.
    /// </summary>
    public static string LiveCardStateKey(CardModel card) => CardChoiceSupport.ChoiceCardKey(card);

    /// <summary>Per-seat sandpit countdowns, "0" for a seat with no live pit.</summary>
    /// <remarks>
    /// P0-SIM-2 diagnostics: the adopted-VICTORY log has to show the boundary and the live
    /// board side by side, or the next false victory costs another full triage.
    /// </remarks>
    public static string SandpitCountdowns(CombatState state, IReadOnlyList<Player> party)
        => FormatSandpitCountdowns(state.Creatures.SelectMany(creature => creature.Powers), party);
    public string SandpitCountdowns(IReadOnlyList<Player> party)
        => FormatSandpitCountdowns(Combat.EffectivePowers(), party);
    private static string FormatSandpitCountdowns(IEnumerable<PowerModel> powers, IReadOnlyList<Player> party)
    {
        SandpitPower[] pits = powers.OfType<SandpitPower>().Where(pit => pit.Amount > 0).ToArray();
        return string.Join(",", party.Select(seat => pits
            .Where(pit => ReferenceEquals(pit.Target, seat.Creature))
            .Select(pit => (int?)pit.Amount).Min() ?? 0));
    }

    /// <summary>
    /// Targeted powers as an order-independent identity set, (owner,type,target)=amount.
    /// P0-SIM-2's adoption guard uses it to compare the validated boundary with the live
    /// board even when a caller supplied a rendered text from elsewhere.
    /// </summary>
    public static string TargetedPowerState(CombatState state)
        => FormatTargetedPowers(state.Creatures.SelectMany(creature => creature.Powers));
    public string TargetedPowerState()
        => FormatTargetedPowers(Combat.EffectivePowers());
    private static string FormatTargetedPowers(IEnumerable<PowerModel> powers)
        => string.Join(",", powers
            .Where(power => power.Amount != 0 && power.Target is not null)
            .OrderBy(power => power.Owner.CombatId)
            .ThenBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ThenBy(power => power.Target!.CombatId)
            .Select(power => $"{power.Owner.CombatId}:{power.Id.Entry}={power.Amount}@{power.Target!.CombatId}"));

    public double IntentHit(Creature enemy, Creature target, decimal raw)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        return Hp(enemy) <= 0 ? 0 : Math.Max(0, (int)HookMirrors.ModifyDamage(simulator, target, enemy, raw, ValueProp.Move, null, null));
    }

    /// <summary>Cards/gold still held by a thief in this branch. See TerminalRecord.</summary>
    public int OutstandingStolenResource => Combat.OutstandingStolenResource(simulator);

    /// <summary>
    /// Strength a later round would have back: temporary Strength powers are
    /// removed at the enemy side turn end, so a this-turn debuff must not be
    /// credited against rounds the enemy has not taken yet.
    /// </summary>
    public int RestorableStrength(Creature enemy)
        => AfterTemporaryStatExpiry([enemy]).Power<StrengthPower>(enemy) - Power<StrengthPower>(enemy);
    public double EndPlating(Creature target)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var amount = Power<PlatingPower>(target);
        return amount <= 0 ? 0 : (double)HookMirrors.ModifyBlock(simulator, target, amount, ValueProp.Unpowered, null, null, out _);
    }
    // This is the upstream sustained-power model, evaluated against each bot's
    // branch deck. Human hands are never strategically evaluated here.
    public double PersistentValue(Player player, int incoming, int hits)
        => PersistentValue(player, incoming, hits, this);

    // Hold the opportunity context fixed while comparing an action. Consuming
    // a Shiv must not reduce the value of the same unchanged Afterimage twice:
    // once as a spent card and again as a smaller hypothetical future engine.
    internal double PersistentValue(Player player, int incoming, int hits, KernelSession reference)
    {
        var residual = AfterTemporaryStatExpiry([player.Creature]);
        return residual.PersistentValueAfterTemporaryStatExpiry(player, incoming, hits, reference);
    }

    internal int CardsPlayedThisTurn(Player player) => Combat.GetCardsPlayedThisTurn(player.Creature);
    internal int TurnNumberFor(Player player) => Combat.GetPlayerTurnNumber(player);
}
