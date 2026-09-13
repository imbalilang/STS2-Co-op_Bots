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
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopBots;

internal static class BotPotionPlanner
{
    internal sealed record Choice(PotionModel Potion, Creature? Target, double Score, string Reason, int SavedLives = 0);
    internal static Choice? Choose(IReadOnlyList<Player> bots, IReadOnlyList<Player> party)
    {
        var cm = CombatManager.Instance;
        if (!cm.IsInProgress || cm.IsEnding || cm.IsPaused
            || RunManager.Instance.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase) return null;
        return Evaluate(bots, party);
    }

    internal static Choice? Evaluate(IReadOnlyList<Player> bots, IReadOnlyList<Player> party)
    {
        var endangered = party.Where(p => p.Creature.IsAlive && CombatAssessment.InDanger(p.Creature)).ToList();
        if (endangered.Count == 0) return null;
        var choices = new List<Choice>();
        foreach (var owner in bots.Where(p => p.CanUseOrRemovePotions))
        foreach (var potion in owner.Potions.Where(p => !p.IsQueued && !p.HasBeenRemovedFromState
            && p.PassesCustomUsabilityCheck && p.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime))
        {
            var name = potion.GetType().Name;
            double Var(string key) => potion.DynamicVars.Values.Where(v => v.Name == key).Sum(v => (double)v.BaseValue);
            if (name is "BlockPotion" or "BloodPotion")
            {
                foreach (var ally in endangered.Where(p => potion.IsValidTarget(p.Creature)))
                {
                    // A defensive potion alone cannot stop forced death. Do not
                    // claim a rescue merely because the ordinary attack is small.
                    if (MonsterHazards.Imminent(ally.Creature)) continue;
                    var benefit = name == "BlockPotion"
                        ? (double)Hook.ModifyBlock(ally.Creature.CombatState!, ally.Creature, (decimal)Var("Block"),
                            ValueProp.Unpowered, null, null, out _)
                        : Math.Min(ally.Creature.MaxHp - ally.Creature.CurrentHp,
                            Math.Floor(ally.Creature.MaxHp * Var("HealPercent") / 100));
                    if (benefit <= 0) continue;
                    var saves = CombatAssessment.Uncovered(ally.Creature) - benefit < ally.Creature.CurrentHp;
                    if (!saves && !TeamCombatPlanner.CanRescueWithProtection(bots, party, ally, benefit, name == "BloodPotion")) continue;
                    choices.Add(new(potion, ally.Creature,
                        (saves ? 1600 : 1450) * CombatAssessment.HumanWeight(ally.Creature) + benefit,
                        saves ? "prevent-death" : "potion-plus-team-rescue", BotRegistry.IsBot(ally.NetId) ? 1 : 3));
                }
            }
            else if (name is "FirePotion" or "ExplosiveAmpoule")
            {
                var enemies = owner.Creature.CombatState!.HittableEnemies.Where(e => e.IsAlive).ToList();
                var targets = name == "FirePotion" ? enemies.Cast<Creature?>() : new Creature?[] { null };
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
                    if (saved.Count > 0)
                        choices.Add(new(potion, target, 1600 * saved.Sum(p => CombatAssessment.HumanWeight(p.Creature)), "lethal-attacker-removal",
                            saved.Sum(p => BotRegistry.IsBot(p.NetId) ? 1 : 3)));
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
        }
        return choices.OrderByDescending(c => c.Score).ThenBy(c => c.Potion.Owner.NetId).FirstOrDefault();
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


