using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

internal static class BotPotionPlanner
{
    internal sealed record Choice(PotionModel Potion, Creature? Target, double Score, string Reason,
        int SavedLives = 0, bool Preemptive = false);
    // Block a restoration potion must actually soak before the final encounter
    // may spend it without a death on the line. Below this the bottle is worth
    // more as the possibility of a rescue than as a few points of chip block.
    private const double FinalEncounterSoak = 8;
    // Which half of the "the dose would land in full" rule is live. A rescue is
    // always evaluated; these are the extra verdicts the runtime enables on top.
    // Kept as a flag set rather than a bool so the two halves can be tested (and
    // switched) independently: a defensive bottle is only judged once the team
    // has no card left to play, a lethal one any time it is the only kill.
    [Flags]
    internal enum FullValue { None = 0, Late = 1, Early = 2, All = Late | Early }

    internal static Choice? Choose(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, bool latePhase)
    {
        var cm = CombatManager.Instance;
        if (!cm.IsInProgress || cm.IsEnding || cm.IsPaused
            || RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase) return null;
        return EvaluateCore(bots, party, party.Any(NoFutureForPotion), FullValue.All, latePhase);
    }

    internal static Choice? Evaluate(IReadOnlyList<Player> bots, IReadOnlyList<Player> party)
        => EvaluateWithFuture(bots, party, party.Any(NoFutureForPotion));

    // The willingness rule with the "nothing left to save it for" verdict passed
    // in, so the final-encounter behaviour can be pinned on a board without
    // having to fabricate a map. Rescue-only, as it has always been.
    internal static Choice? EvaluateWithFuture(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, bool noFuture)
        => EvaluateCore(bots, party, noFuture, FullValue.None, latePhase: false);

    // The full-value rule on a board that can be pinned down: `blockValue` /
    // `lethalValue` select which verdicts are live and `latePhase` stands in for
    // "the team has no card left to play", so a test does not have to fabricate an
    // exhausted turn.
    internal static Choice? EvaluateFullValue(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, bool noFuture,
        bool latePhase, bool blockValue, bool lethalValue)
        => EvaluateCore(bots, party, noFuture,
            (blockValue ? FullValue.Late : FullValue.None) | (lethalValue ? FullValue.Early : FullValue.None),
            latePhase);

    private static Choice? EvaluateCore(IReadOnlyList<Player> bots, IReadOnlyList<Player> party, bool noFuture,
        FullValue fullValue, bool latePhase)
    {
        var endangered = party.Where(p => p.Creature.IsAlive && CombatAssessment.InDanger(p.Creature)).ToList();
        // A bottle still in the slot when the run ends was worth nothing. Past the
        // last shop there is no later fight to save it for, so the final encounter
        // may spend one for a measured gain instead of only to stop a death — the
        // reviewed party met the act-3 boss holding six of them. Everything else
        // keeps the rescue-only rule, plus the full-value verdicts when enabled.
        if (endangered.Count == 0 && !noFuture && fullValue == FullValue.None) return null;
        var choices = new List<Choice>();
        foreach (var owner in bots.Where(p => p.CanUseOrRemovePotions))
        foreach (var potion in owner.Potions.Where(p => !p.IsQueued && !p.HasBeenRemovedFromState
            && p.PassesCustomUsabilityCheck && p.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime))
        {
            var name = potion.GetType().Name;
            double Var(string key) => potion.DynamicVars.Values.Where(v => v.Name == key).Sum(v => (double)v.BaseValue);
            if (name is "BlockPotion" or "BloodPotion" or "ShipInABottle" or "Fortifier" or "Ambergris")
            {
                var heals = name is "BloodPotion" or "Ambergris";
                // Every living ally, not only the ones about to die: "the whole
                // dose lands" is worth acting on precisely when nobody is yet on
                // the edge, which is the fight the old rescue-only rule watched
                // the team lose with a full belt.
                foreach (var ally in party.Where(p => p.Creature.IsAlive && potion.IsValidTarget(p.Creature)))
                {
                    // A defensive potion alone cannot stop forced death. Do not
                    // claim a rescue merely because the ordinary attack is small.
                    if (MonsterHazards.Imminent(ally.Creature)) continue;
                    // The dose on the label, before the board clamps it. Reading it
                    // unclamped is what makes "the whole dose lands" measurable: a
                    // clamped value equals what lands by construction, so it could
                    // never tell a full heal from an overheal.
                    var nominal = heals
                        ? Math.Floor(ally.Creature.MaxHp * Var("HealPercent") / 100)
                        : (double)Hook.ModifyBlock(ally.Creature.CombatState!, ally.Creature,
                            // Fortifier doubles whatever block is already up, so its
                            // dose is a function of the board rather than a variable.
                            name == "Fortifier" ? (decimal)(ally.Creature.Block * 2) : (decimal)Var("Block"),
                            ValueProp.Unpowered, null, null, out _);
                    // Only the block that actually soaks incoming damage counts, and
                    // a heal stops counting past full health; a 12-block potion on a
                    // 5-damage hit is mostly wasted, and the score must not pretend
                    // otherwise.
                    var missing = ally.Creature.MaxHp - ally.Creature.CurrentHp;
                    var uncovered = CombatAssessment.Uncovered(ally.Creature);
                    var useful = heals ? Math.Min(missing, nominal) : Math.Min(nominal, uncovered);
                    if (useful <= 0) continue;
                    var saves = uncovered - useful < ally.Creature.CurrentHp;
                    if (endangered.Contains(ally))
                    {
                        // A human who has not ended may still cover the hit themselves,
                        // so only spend the potion on them when it is the difference
                        // between life and death; once they have ended, protecting a
                        // rescue is enough because nobody else can react.
                        if (!BotRegistry.IsBot(ally.NetId) && CombatAssessment.CanStillAct(ally) && !saves) continue;
                        // With the run ending there is nothing to hold the bottle for,
                        // so a bot may soak damage it would otherwise walk into. The
                        // human guard above still stands: a human who can still act may
                        // cover the hit themselves and must not have it spent for them.
                        var lastChance = noFuture && BotRegistry.IsBot(ally.NetId) && useful >= FinalEncounterSoak;
                        if (!saves && !lastChance
                            && !TeamCombatPlanner.CanRescueWithProtection(bots, party, ally, useful, heals)) continue;
                        choices.Add(new(potion, ally.Creature,
                            (saves ? 1600 : lastChance ? 1200 : 1450) * CombatAssessment.HumanWeight(ally.Creature) + useful,
                            saves ? "prevent-death" : lastChance ? "final-encounter-no-future" : "potion-plus-team-rescue",
                            BotRegistry.IsBot(ally.NetId) ? 1 : 3));
                        continue;
                    }
                    // Full value, late: the whole dose lands — no block spent on
                    // damage that was never coming, no overheal — and the recipient
                    // is still standing afterwards. Held any longer the same bottle
                    // cannot do more than it does right now, so there is nothing
                    // left to save it for. Only judged once the team has run out of
                    // cards, because until then a card may still cover this hit and
                    // turn the "full" dose into a partly wasted one.
                    if (!fullValue.HasFlag(FullValue.Late) || !latePhase) continue;
                    if (useful < nominal) continue;
                    if (!saves) continue;
                    choices.Add(new(potion, ally.Creature,
                        1250 * CombatAssessment.HumanWeight(ally.Creature) + nominal,
                        heals ? "full-value-heal" : "full-value-block",
                        BotRegistry.IsBot(ally.NetId) ? 1 : 3));
                }
            }
            else if (name is "FirePotion" or "PotionShapedRock" or "ExplosiveAmpoule" or "FoulPotion")
            {
                var enemies = owner.Creature.CombatState!.HittableEnemies.Where(e => e.IsAlive).ToList();
                var targets = name is "FirePotion" or "PotionShapedRock" ? enemies.Cast<Creature?>() : new Creature?[] { null };
                foreach (var target in targets.Where(potion.IsValidTarget))
                {
                    var hit = target is null ? enemies : new List<Creature> { target };
                    var killed = hit.Where(enemy =>
                    {
                        var damage = Hook.ModifyDamage(owner.RunState, enemy.CombatState, enemy, owner.Creature,
                            (decimal)Var("Damage"), ValueProp.Unpowered, null, null,
                            ModifyDamageHookType.All, CardPreviewMode.None, out _);
                        return (double)damage >= enemy.CurrentHp + enemy.Block;
                    }).ToList();
                    var saved = endangered.Where(p => !MonsterHazards.Imminent(p.Creature, killed)
                        && CombatAssessment.Uncovered(p.Creature)
                        - killed.Sum(e => CombatAssessment.FromEnemy(e, p.Creature)) < p.Creature.CurrentHp).ToList();
                    // If the team's own cards already kill everything this potion
                    // would, the potion buys nothing: keep it and let the plan take
                    // those kills. The measure is the same per-enemy potential the
                    // evaluator already trusts as "the bots can finish this enemy
                    // alone", so it cannot be stricter than the plan's own claim.
                    // A wasted potion looks exactly like the reported fight: the
                    // enemy sat at 9 HP, any Strike would finish it, and the throw
                    // still happened at the start of the next turn.
                    var potential = TeamCombatPlanner.AttackPotential(bots, killed);
                    var cardsAlreadyKill = killed
                        .Select((enemy, index) => potential[index] >= enemy.CurrentHp + enemy.Block)
                        .All(covered => covered);
                    if (cardsAlreadyKill) continue;
                    if (saved.Count > 0)
                        choices.Add(new(potion, target, 1600 * saved.Sum(p => CombatAssessment.HumanWeight(p.Creature)), "lethal-attacker-removal",
                            saved.Sum(p => BotRegistry.IsBot(p.NetId) ? 1 : 3)));
                    // Full value, early: the throw converts into a removal nothing
                    // else on the board can make. Front-loaded on purpose — the
                    // team's later plays are worth more once this enemy is gone, and
                    // `Preemptive` keeps a card plan from burying the throw. The run
                    // this exists for met the act-1 boss holding a FirePotion while
                    // the cards could not finish the add.
                    else if (fullValue.HasFlag(FullValue.Early))
                        choices.Add(new(potion, target, 1300 + killed.Sum(enemy => (double)enemy.CurrentHp),
                            "full-value-lethal", 0, Preemptive: true));
                    // Nobody is dying, but the run is: removing an enemy the cards
                    // cannot kill is the last thing this bottle will ever buy.
                    else if (noFuture)
                        choices.Add(new(potion, target, 900 + killed.Sum(enemy => (double)enemy.CurrentHp),
                            "final-encounter-no-future", 0));
                }
            }
            else if (name == "WeakPotion")
            {
                foreach (var enemy in owner.Creature.CombatState!.HittableEnemies.Where(e => potion.IsValidTarget(e)
                    && !e.Powers.Any(p => p.Amount > 0 && p.GetType().Name is "WeakPower" or "ArtifactPower")))
                {
                    var saved = endangered.Where(p => !MonsterHazards.Imminent(p.Creature)
                        && CombatAssessment.Uncovered(p.Creature)
                        - CombatAssessment.FromEnemy(enemy, p.Creature) * 0.25 < p.Creature.CurrentHp).ToList();
                    if (saved.Count > 0) choices.Add(new(potion, enemy,
                        1450 * saved.Sum(p => CombatAssessment.HumanWeight(p.Creature)), "weak-prevents-death-estimate",
                        saved.Sum(p => BotRegistry.IsBot(p.NetId) ? 1 : 3)));
                }
            }
            // A card-generation bottle cannot save a life: it buys tempo, and only
            // for the turn it is drunk on, because the offered card is free *this
            // turn*. It therefore has no rescue value to hoard for, which is exactly
            // why leaving it corked is pure loss — two runs in a row ended at a boss
            // with three of these still in the belt. Front-loaded into the fights
            // worth spending on, and the offered card is picked by the deterministic
            // fallback every peer already shares (BotBrain.SelectCards through the
            // CardSelectCmd patch), so no extra handshake is needed. A potion whose
            // cards are added without a choice is left to the kernel, which can
            // simulate it; only the choice-opening four need this path.
            else if (name is "AttackPotion" or "SkillPotion" or "PowerPotion" or "ColorlessPotion")
            {
                if (fullValue.HasFlag(FullValue.Early) && (noFuture || ImportantFight(owner))
                    && potion.IsValidTarget(owner.Creature))
                    choices.Add(new(potion, owner.Creature, 1000, "card-generation-tempo", 0));
            }
        }
        return choices.OrderByDescending(c => c.Score).ThenBy(c => c.Potion.Owner.NetId).FirstOrDefault();
    }

    // True in the encounter the run ends on: the last act's boss room. A potion
    // held past it is never drunk, so its only remaining value is what it does
    // right now. Read defensively — a map we cannot read must not make the bots
    // spend bottles for no reason.
    internal static bool NoFutureForPotion(Player player)
    {
        try
        {
            var state = player.RunState;
            return state.CurrentActIndex >= BotShopPlanner.FinalAct
                && state.CurrentMapPoint?.PointType == MapPointType.Boss;
        }
        catch
        {
            return false;
        }
    }

    // A fight worth spending a tempo bottle on. Both reviewed defeats happened in
    // one of these — an act-1 boss and an act-3 boss — and both ended with the
    // belt still full, because the only gate on a card-generation potion used to
    // be the last act's boss room. Read defensively for the same reason as above.
    private static bool ImportantFight(Player player)
    {
        try
        {
            return player.RunState.CurrentMapPoint?.PointType is MapPointType.Elite or MapPointType.Boss;
        }
        catch
        {
            return false;
        }
    }
}

// EnqueueManualUse preserves BeforeUse/IsQueued, but RequestEnqueue normally attributes
// actions to the host. Virtual players require their own identity in the network payload.
[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
internal static class BotPotionEnqueuePatch
{
    private static readonly MethodInfo EnqueueAs = AccessTools.Method(typeof(ActionQueueSynchronizer),
        "EnqueueAction", new[] { typeof(GameAction), typeof(ulong) });
    private static bool Prefix(ActionQueueSynchronizer __instance, GameAction action)
    {
        if (action is not UsePotionAction || !BotRegistry.IsBot(action.OwnerId)
            || RunManager.Instance.NetService.Type != NetGameType.Host) return true;
        EnqueueAs.Invoke(__instance, new object[] { action, action.OwnerId });
        return false;
    }
}


