using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CoopBots;

// The kernel owns effects. Open player turns use a threat forecast; completed
// enemy phases use settled HP and must not pay the same incoming attack twice.
internal sealed class KernelCombatEvaluation
{
    internal sealed record Metrics(double Score, int WeightedDeaths, double HpLoss, double SoftFinish);

    // Human output is a soft, confidence-scaled nudge so the team does not block
    // away a kill the human could still finish. It never creates a confirmed
    // victory and never reduces the value of a bot's own attack.
    private const double HumanFinishConfidence = 0.5;
    // Flat credit for an enemy the humans can still finish, capped so crossing
    // into range cannot outweigh a large amount of real value.
    private const double SoftFinishCap = 120;
    private const double DeathGuardedSoftFinish = 0.25;
    // Future incoming is real but the party gets fresh turns, draw and block
    // before it lands, so it is a discounted threat signal, not a loss; each
    // further round is discounted again.
    private const double NextTurnThreatWeight = 0.25;
    private const double NextTurnThreatDecay = 0.5;
    // An enemy that gains Strength every turn keeps compounding; its remaining
    // HP is worth attacking early instead of farming the low-HP target. Scaling
    // with remaining HP makes damage (and especially the kill) pay for itself.
    // Chosen so one point of damage into a scaling enemy is worth at least as
    // much as one point of blocked damage: blocking only defers the problem,
    // while damage shortens how long the enemy keeps compounding.
    private const double ScalingThreatWeight = 12;

    private readonly Player[] party;
    private readonly HashSet<Player> bots;
    private readonly Creature[] enemies;
    private readonly Creature? focus;
    private readonly int[] hp;
    private readonly int[] maxHp;
    private readonly (Creature Enemy, decimal Raw, int Repeats)[] rootAttacks;
    private readonly IReadOnlyList<IReadOnlyList<KernelSession.ForecastAttack>> forecastRounds;
    private readonly Dictionary<Creature, double> humanCover = new();
    private readonly HashSet<Creature> botKillable = new();
    // Enemies already Vulnerable when the hand estimate was taken. The estimate
    // is built from card previews, which run the real damage hooks, so Vulnerable
    // is already priced in for these; the plan's own Vulnerable is not.
    private readonly HashSet<Creature> coverHasVulnerable = new();

    // Soft human contribution per enemy, and the enemies the bots can finish
    // alone; the planner uses them to ask a human for the finishing damage.
    internal IReadOnlyDictionary<Creature, double> HumanCover => humanCover;
    internal IReadOnlyCollection<Creature> BotKillable => botKillable;

    internal KernelCombatEvaluation(CombatState combat, IReadOnlyList<Player> actors, uint? manualFocus,
        IReadOnlyList<IReadOnlyList<KernelSession.ForecastAttack>>? forecast = null, int futureRounds = 1)
    {
        party = combat.Players.Where(p => p.Creature.IsAlive).ToArray();
        bots = actors.ToHashSet();
        enemies = combat.HittableEnemies.Where(e => e.IsAlive).ToArray();
        focus = TeamFocus.Resolve(combat, enemies, manualFocus);
        hp = party.Select(p => p.Creature.CurrentHp).ToArray();
        maxHp = party.Select(p => p.Creature.MaxHp).ToArray();
        rootAttacks = enemies.SelectMany(e => e.Monster!.NextMove.Intents.OfType<AttackIntent>()
            .Select(intent => (e, intent.DamageCalc!(), intent.Repeats))).ToArray();
        forecastRounds = forecast ?? KernelSession.ForecastRounds(combat, futureRounds);
        try
        {
            var humans = party.Where(p => !BotRegistry.IsBot(p.NetId)).ToArray();
            var botPlayers = party.Where(p => BotRegistry.IsBot(p.NetId)).ToArray();
            var humanAttack = TeamCombatPlanner.AttackPotential(humans, enemies);
            var botAttack = TeamCombatPlanner.AttackPotential(botPlayers, enemies);
            for (var i = 0; i < enemies.Length; i++)
            {
                if (i < humanAttack.Length && humanAttack[i] > 0) humanCover[enemies[i]] = humanAttack[i];
                if (HasVulnerable(enemies[i])) coverHasVulnerable.Add(enemies[i]);
                // If the bots can already finish this enemy alone, the real kill
                // and victory terms must decide it; no soft human credit.
                if (i < botAttack.Length && botAttack[i] >= enemies[i].CurrentHp) botKillable.Add(enemies[i]);
            }
        }
        catch
        {
            // A hand estimate is optional; never let it break team scoring.
            humanCover.Clear();
            botKillable.Clear();
        }
    }

    internal Metrics Evaluate(KernelSession state)
    {
        var victory = state.HasWon;
        // Round 1 keeps the live intent snapshot the rest of the scoring was
        // tuned against; once the search has advanced a round, the branch's own
        // monster AI is the only accurate source.
        var attacks = state.RoundsAdvanced == 0 ? rootAttacks : state.CurrentAttacks();
        var deaths = 0; var weightedDeaths = 0;
        double cost = 0, loss = 0, future = 0;
        var endBlock = new double[party.Length];
        var beacon = party.Select(p => state.Power<BeaconOfHopePower>(p.Creature) > 0).ToArray();
        if (!victory && !state.EnemyPhaseCompleted)
            for (var i = 0; i < party.Length; i++)
                if (state.Hp(party[i].Creature) > 0)
                    ProjectedBlockSharing.Add(endBlock, beacon, party, i, state.EndPlating(party[i].Creature));
        for (var i = 0; i < party.Length; i++)
        {
            var p = party[i]; var creature = p.Creature;
            var health = state.Hp(creature);
            var incoming = victory || state.EnemyPhaseCompleted ? 0 : attacks.Sum(a => state.IntentHit(a.Enemy, creature, a.Raw) * a.Repeats);
            // Cross-turn awareness: what the enemy will do over the coming rounds,
            // evaluated against this branch's powers, so setup (kill/Weak) that
            // prevents a big future hit is preferred over greed now. Later rounds
            // are discounted further because the party gets fresh turns first.
            var nextIncoming = 0.0;
            // The multi-round forecast is built from the live root, so it is only
            // valid for the opening round. After an enemy phase (or once the search
            // has advanced a round) the settled/branch state decides, and the next
            // round's threat belongs to that round's own search.
            if (!victory && !state.EnemyPhaseCompleted && state.RoundsAdvanced == 0)
            {
                var weight = NextTurnThreatWeight;
                var turn = 0;
                foreach (var round in forecastRounds)
                {
                    turn++;
                    // A growing enemy's attack next round already carries the
                    // Strength it gains each turn, so extrapolate it here. A
                    // temporary Strength loss is restored before any of these
                    // rounds, so add it back rather than crediting a one-turn
                    // debuff against attacks the enemy has not taken yet.
                    nextIncoming += weight * round.Sum(a =>
                        state.IntentHit(a.Enemy, creature,
                            a.Raw + Growth(state, a.Enemy) * turn + state.RestorableStrength(a.Enemy)) * a.Repeats);
                    weight *= NextTurnThreatDecay;
                }
            }
            // Cards that punish being held at end of turn (Beckon: 6 unblockable)
            // are only avoided by playing them, so the search has to see what
            // keeping them costs. Charged while the turn is still open, and
            // skipped once the phase has settled because the simulation has then
            // already applied the real effect.
            if (!victory && !state.EnemyPhaseCompleted) cost += HeldPenalty(state, p);
            var block = state.Block(creature) + endBlock[i];
            var forced = !victory && !state.EnemyPhaseCompleted && state.SandpitDeath(creature);
            var dead = health <= 0 || forced || incoming - block >= health;
            var spent = hp[i] - health;
            var projectedLoss = spent + (forced ? Math.Max(0, health) : Math.Min(Math.Max(0, health), Math.Max(0, incoming - block)));
            loss += projectedLoss;
            var deficit = 1 - Math.Clamp((double)health / Math.Max(1, maxHp[i]), 0, 1);
            cost += projectedLoss * (1 + 2 * deficit * deficit) + (victory ? 0 : state.SandpitReserve(creature));
            // nextIncoming already carries the per-round discount; applying the
            // weight again here made the first future round 0.0625 instead of
            // 0.25, i.e. four times weaker than intended.
            if (!victory) cost += nextIncoming;
            if (dead)
            {
                // Every member's death is priced the same: the team outcome is
                // what matters, so a human is no longer protected beyond the HP
                // and future value they actually contribute.
                deaths++;
                weightedDeaths++;
                cost += 40 + maxHp[i] * .25;
            }
            else if (!victory)
            {
                // Future growth of doomed actors has no value. Allied persistent
                // effects are owned by that actor, not credited once per teammate.
                if (bots.Contains(p)) future += Math.Min(120, state.PersistentValue(p,
                    (int)Math.Min(int.MaxValue, incoming), attacks.Where(a => state.Hp(a.Enemy) > 0).Sum(a => a.Repeats))) * .35;
                if (state.Power<BarricadePower>(creature) > 0)
                    future += Math.Min(30, Math.Max(0, block - incoming)) * .25;
            }
        }
        var softFinish = SoftFinish(state, deaths);
        var enemyHp = state.Enemies.Sum(e => (double)state.Hp(e));
        var focusCost = !victory && focus is not null && state.Hp(focus) > 0 ? state.Hp(focus) * .15 : 0;
        // Compounding enemies: leaving them alive costs the whole party every
        // future turn. Counting it once per enemy keeps this from scaling with
        // the party loop, while still shrinking as the enemy is damaged.
        var scalingThreat = 0.0;
        if (!victory)
            foreach (var enemy in state.Enemies)
            {
                var growth = Growth(state, enemy);
                if (growth <= 0) continue;
                var max = Math.Max(1, state.MaxHp(enemy));
                scalingThreat += growth * ScalingThreatWeight * party.Length * (double)state.Hp(enemy) / max;
            }
        // Bounded tactical term preserves the lexicographic human / wipe / victory
        // priorities even with unusual modded damage values.
        var tactical = Math.Clamp(cost + enemyHp * .35 + focusCost + scalingThreat - future - softFinish,
            -1_000_000, 1_000_000);
        // Team-wipe and victory remain the hard tiers; individual deaths are paid
        // for through cost rather than an unconditional human-death penalty.
        var score = -(deaths == party.Length ? 100_000_000 : 0) + (victory ? 10_000_000 : 0) - tactical;
        return new(score, weightedDeaths, loss, softFinish);
    }

    // Unblockable damage this player will take at end of turn for the cards they
    // are still holding. Zero for a hand with nothing that punishes holding.
    private static double HeldPenalty(KernelSession state, Player player)
    {
        var total = 0.0;
        foreach (var card in state.Hand(player))
        {
            try
            {
                if (!card.HasTurnEndInHandEffect) continue;
                total += card.DynamicVars.Values
                    .Where(variable => variable.GetType().Name == "HpLossVar")
                    .Sum(variable => (double)variable.BaseValue);
            }
            catch { /* an unreadable card simply charges nothing */ }
        }
        return total;
    }

    // Per-turn Strength this enemy gains (Ritual-like scaling). Zero for the
    // vast majority of enemies, so this stays cheap.
    private static int Growth(KernelSession state, Creature enemy) => state.Power<RitualPower>(enemy);

    // The enemy a human can still finish this turn, with the numbers the
    // callout needs. Only enemies the bots cannot kill alone qualify, so a bot
    // never defers its own confirmed kill to the human.
    internal sealed record HumanFinish(Creature Enemy, int Remaining, double Cover);

    // Shared by the score credit and the callout so the two can never disagree.
    // The cover figure already prices in Vulnerable that was live when the hand
    // was measured, so it is only amplified for Vulnerable the plan itself
    // applies. Multiplying unconditionally double-counted it and made the score
    // credit claim kills the callout (correctly) would not ask for, which is why
    // the request never reached the player.
    /// <param name="requireBotsUnable">
    /// The score credit must not hand a kill to the human that the bots could
    /// have taken themselves, or the planner would rather leave an enemy alive
    /// than finish it. The callout is the opposite case: it is exactly when the
    /// plan leaves an enemy standing that the human's damage is worth asking
    /// for, so it passes false. Using the score's stricter test there is why no
    /// callout was ever raised in real play.
    /// </param>
    internal HumanFinish? HumanFinishTarget(KernelSession state, bool requireBotsUnable = true)
    {
        foreach (var enemy in state.Enemies)
        {
            var remaining = state.Hp(enemy);
            if (remaining <= 0) continue;
            if (requireBotsUnable && botKillable.Contains(enemy)) continue;
            if (!humanCover.TryGetValue(enemy, out var cover)) continue;
            if (state.Power<VulnerablePower>(enemy) > 0 && !coverHasVulnerable.Contains(enemy)) cover *= 1.5;
            if (remaining <= cover) return new(enemy, remaining, cover);
        }
        return null;
    }

    /// <summary>
    /// The enemy the plan came closest to leaving within the humans' reach, for
    /// diagnosis. A missing estimate means the hand probe never ran at all, which
    /// is a different problem from simply not being close enough.
    /// </summary>
    internal (Creature Enemy, int Remaining, double Cover)? ClosestFinish(KernelSession state)
    {
        (Creature Enemy, int Remaining, double Cover)? closest = null;
        foreach (var enemy in state.Enemies)
        {
            var remaining = state.Hp(enemy);
            if (remaining <= 0) continue;
            if (!humanCover.TryGetValue(enemy, out var cover)) continue;
            if (state.Power<VulnerablePower>(enemy) > 0 && !coverHasVulnerable.Contains(enemy)) cover *= 1.5;
            if (closest is null || remaining - cover < closest.Value.Remaining - closest.Value.Cover)
                closest = (enemy, remaining, cover);
        }
        return closest;
    }

    // Vulnerable on the live target at capture time, so the hand estimate that
    // already reflects it is not amplified a second time.
    private static bool HasVulnerable(Creature enemy)
    {
        try { return enemy.Powers.Any(power => power is VulnerablePower && power.Amount > 0); }
        catch { return false; }
    }

    // Credit at most one enemy the human could realistically finish this turn.
    // Bots that are about to die keep their survival priority; the credit is
    // heavily discounted in that case.
    private double SoftFinish(KernelSession state, int deaths)
    {
        // The amount is flat rather than proportional to the remaining HP:
        // otherwise leaving an enemy big enough for the human's whole hand
        // scored better than actually damaging it, so the planner would decline
        // to attack at all.
        if (HumanFinishTarget(state) is null) return 0;
        var bonus = SoftFinishCap * HumanFinishConfidence;
        return deaths > 0 ? bonus * DeathGuardedSoftFinish : bonus;
    }
}
