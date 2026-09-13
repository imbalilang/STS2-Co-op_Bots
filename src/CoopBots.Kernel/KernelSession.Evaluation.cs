using CoopBots.Kernel.Vendor;
using CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;
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
                foreach (var hit in Combat.CurrentMonsterMove(enemy).AttackHits)
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
    public bool SandpitDeath(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Any(p => p.Target == target && p.Amount <= 1 && Hp(p.Owner) > 0);
    public int? SandpitTurnsRemaining(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Where(p => p.Target == target && Hp(p.Owner) > 0).Select(p => (int?)p.Amount).Min();
    public double SandpitReserve(Creature target) => Combat.EffectivePowers().OfType<SandpitPower>()
        .Where(p => p.Target == target && Hp(p.Owner) > 0)
        .Sum(p => p.Amount switch { <= 1 => 30.0, 2 => 12, 3 => 3, _ => 0 });
    public double IntentHit(Creature enemy, Creature target, decimal raw)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        return Hp(enemy) <= 0 ? 0 : Math.Max(0, (int)HookMirrors.ModifyDamage(simulator, target, enemy, raw, ValueProp.Move, null, null));
    }

    /// <summary>
    /// Strength a later round would have back: temporary Strength powers are
    /// removed at the enemy side turn end, so a this-turn debuff must not be
    /// credited against rounds the enemy has not taken yet.
    /// </summary>
    public int RestorableStrength(Creature enemy)
    {
        var restore = 0;
        foreach (var power in Combat.EffectivePowers().OfType<TemporaryStrengthPower>())
            if (power.Owner == enemy && power.Amount > 0)
                restore += power.TypeForCurrentAmount == PowerType.Buff ? -power.Amount : power.Amount;
        return restore;
    }
    public double EndPlating(Creature target)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var amount = Power<PlatingPower>(target);
        return amount <= 0 ? 0 : (double)HookMirrors.ModifyBlock(simulator, target, amount, ValueProp.Unpowered, null, null, out _);
    }
    // This is the upstream sustained-power model, evaluated against each bot's
    // branch deck. Human hands are never strategically evaluated here.
    public double PersistentValue(Player player, int incoming, int hits)
    {
        using var isolation = SimulationNotificationIsolation.Enter();
        var powers = Combat.EffectivePowers().Where(p => p.Owner == player.Creature
            && StrategicEffectMirrors.Contributes(p, player.Creature)).ToArray();
        if (powers.Length == 0) return 0;
        var requirements = powers.Aggregate(StrategicEffectRequirements.None,
            (requirements, power) => requirements | StrategicEffectModel.Requirements(power));
        var pcs = simulator.State.GetPlayerCombatState(player);
        var cards = pcs.DiscardPile.Cards.Concat(pcs.DrawPile.Cards).Concat(pcs.Hand.Cards).ToArray();
        var context = StrategicEffectContext.Build(cards, Enemies.Sum(Hp), incoming, hits, requirements);
        return powers.Sum(p => (double)StrategicEffectModel.Evaluate(p, context).RetentionValue);
    }
}
