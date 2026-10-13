using System.Diagnostics;
using CoopBots.Kernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CoopBots;

internal sealed partial class KernelCombatPlanner
{
    private bool emergencyPotionPending;
    private CombatState? emergencyPotionCombat;
    private string? emergencyPotionStamp;
    private string? emergencyPotionPlanKey;
    private IEnumerator<bool>? emergencyPotionTrials;
    private IReadOnlyList<Player>? emergencyPotionActors;
    private EmergencyLine? emergencyPotionRescue;
    private EmergencyLine? emergencyPotionRepair;
    private bool emergencyPotionRepaired;
    private int emergencyPotionCaptures;
    private int emergencyPotionActions;
    private double emergencyPotionComputeMs;
    private KernelDecisionRequest? emergencyPotionRequest;

    private sealed record EmergencyLine(KernelSession State,
        IReadOnlyList<KernelTeamSearch.Action> Actions,
        IReadOnlyList<ulong[]> Alive, IReadOnlyList<Creature[]> Enemies);

    // A cheap *upper bound*, not a survival score. Ignore mitigation here so
    // Weak/Frail/Intangible cannot accidentally hide a required native probe.
    private static bool MayNeedEmergencyPotion(CombatState combat, IReadOnlyList<Player> actors,
        TerminalRecord? forecast)
    {
        if (forecast is { Verified: true, Victory: true }) return false;
        if (!actors.Any(p => p.CanUseOrRemovePotions && p.Potions.Any(q => !q.IsQueued
            && !q.HasBeenRemovedFromState && q.PassesCustomUsabilityCheck
            && q.Usage is PotionUsage.AnyTime or PotionUsage.CombatOnly
            && KernelSession.CanModelPotion(q)))) return false;
        foreach (var player in actors.Where(p => p.Creature.IsAlive))
        {
            if (player.Creature.CurrentHp <= player.Creature.MaxHp * .3m
                || forecast is { Verified: true, RoundsSurvived: <= 1 }
                    && KernelPotionOpportunity.PredictedDeath(player, combat.Players.ToArray(), forecast)) return true;
            var tainted = player.Creature.Powers.OfType<TaintedPower>().Sum(p => Math.Max(0, p.Amount));
            decimal incoming = 0;
            foreach (var enemy in combat.Enemies.Where(e => e.IsAlive && e.Monster is not null))
            {
                var strength = enemy.Powers.OfType<StrengthPower>().Sum(p => Math.Max(0, p.Amount));
                foreach (var attack in enemy.Monster!.NextMove.Intents.OfType<AttackIntent>())
                    incoming += Math.Max(0, (attack.DamageCalc?.Invoke() ?? 0) + strength + tainted) * attack.Repeats;
            }
            if (player.Creature.Powers.OfType<VulnerablePower>().Any(p => p.Amount > 0)) incoming *= 1.5m;
            if (incoming >= player.Creature.CurrentHp) return true;
        }
        return false;
    }

    private string EmergencyPotionStamp(CombatState combat, IReadOnlyList<Player> actors)
        // ContinuationStamp includes piles, stateful powers + their targets,
        // potion slots and all combat RNG streams. Cursor/revision/readiness
        // cover a moved cached action even when its visible effects are equal.
        => combat.RoundNumber + ":" + tournamentScriptIndex + ":" + KernelSession.LiveRevision
            + ":" + string.Join(',', actors.Select(p => p.NetId)) + ":"
            + string.Join(',', combat.Players.Select(p => p.PlayerCombatState?.Phase)) + ":"
            + KernelSession.CaptureLivePartyText(combat) + ":inventory:"
            + string.Join(';', combat.Players.SelectMany(p => Enumerable.Range(0, p.PotionSlots.Count).Select(slot =>
            {
                var q = p.GetPotionAtSlotIndex(slot);
                return $"{p.NetId}/{slot}/{p.CanUseOrRemovePotions}/{q?.Id.Entry}/{q?.IsQueued}/{q?.HasBeenRemovedFromState}/"
                    + (q is null ? "" : string.Join(',', q.DynamicVars.Values.OrderBy(v => v.Name)
                        .Select(v => v.Name + "=" + v.BaseValue)));
            })));

    private static string EmergencyPlanKey(IReadOnlyList<KernelTeamSearch.Action>? plan)
        => string.Join(';', plan?.Take(65).Select(a => $"{a.Player.NetId}/{a.Card?.Id.Entry}/{a.Potion?.Id.Entry}/"
            + $"{a.Target?.CombatId}/{a.EndTurn}/{a.CardStateKey}/{a.CardStateOccurrence}/"
            + string.Join(',', a.ChoiceOrdinals ?? []) + "/"
            + string.Join(',', a.Choices?.SelectMany(c => c.Cards).Select(c => c.StateKey + "#" + c.Occurrence) ?? [])) ?? []);

    private bool TryEmergencyPotion(CombatState combat, IReadOnlyList<Player> actors,
        TerminalRecord? forecast, IReadOnlyList<KernelTeamSearch.Action>? plan)
    {
        emergencyPotionRepaired = false;
        if (!MayNeedEmergencyPotion(combat, actors, forecast))
        {
            if (emergencyPotionPending) emergencyPotionTrials?.Dispose();
            emergencyPotionPending = false; emergencyPotionTrials = null;
            return false;
        }
        var planKey = EmergencyPlanKey(plan);
        var stamp = EmergencyPotionStamp(combat, actors) + planKey;
        if (!ReferenceEquals(emergencyPotionCombat, combat) || stamp != emergencyPotionStamp)
        {
            emergencyPotionTrials?.Dispose();
            emergencyPotionCombat = combat; emergencyPotionStamp = stamp;
            emergencyPotionPlanKey = planKey;
            emergencyPotionActors = actors.ToArray();
            emergencyPotionRequest = tournamentRequest;
            emergencyPotionRescue = null; emergencyPotionRepair = null; emergencyPotionComputeMs = 0;
            var root = KernelCombatCapture.Capture(combat); emergencyPotionCaptures++;
            if (!root.CanFork) { emergencyPotionPending = false; emergencyPotionTrials = null; return false; }
            emergencyPotionTrials = EmergencyPotionComparisons(root, actors, plan).GetEnumerator();
            emergencyPotionPending = true;
        }
        return ResumeEmergencyPotion(combat);
    }

    private bool ResumeEmergencyPotion(CombatState combat)
    {
        if (!emergencyPotionPending || emergencyPotionTrials is null) return false;
        if (emergencyPotionRequest?.IsCancelled == true || !ReferenceEquals(emergencyPotionRequest, tournamentRequest)
            || !ReferenceEquals(emergencyPotionCombat, combat) || emergencyPotionActors is null
            || emergencyPotionStamp != EmergencyPotionStamp(combat, emergencyPotionActors) + emergencyPotionPlanKey)
        {
            emergencyPotionTrials.Dispose(); emergencyPotionTrials = null;
            emergencyPotionPending = false; emergencyPotionStamp = null;
            return false;
        }
        var watch = Stopwatch.StartNew();
        do
        {
            if (emergencyPotionRequest?.IsStopped == true)
            { emergencyPotionPending = false; break; }
            if (!emergencyPotionTrials.MoveNext()) { emergencyPotionPending = false; break; }
        } while (watch.ElapsedMilliseconds < TournamentSliceMs);
        emergencyPotionComputeMs += watch.Elapsed.TotalMilliseconds;
        if (emergencyPotionPending) return false;
        emergencyPotionTrials.Dispose(); emergencyPotionTrials = null;
        if (emergencyPotionRequest?.IsCancelled == true) return false;
        var rescue = emergencyPotionRescue ?? emergencyPotionRepair;
        if (rescue is null) return false;
        var opening = emergencyPotionRescue is null ? null : rescue.Actions[0];
        // Keep the actual rescue continuation, including its choices. Merely
        // drinking Energy then following the old losing EndTurn is not a repair.
        ClearTournamentScript(opening is null ? "native emergency card repair" : "native emergency potion rescue");
        tournamentScript = rescue.Actions; tournamentScriptIndex = opening is null ? 0 : 1;
        tournamentScriptAliveStates = rescue.Alive; tournamentScriptEnemyStates = rescue.Enemies;
        tournamentScriptCombat = combat; tournamentScriptRound = combat.RoundNumber;
        tournamentScriptRecord = TerminalRecord.Capture(rescue.State, combat.Players, rescue.State.HasTerminal,
            "emergency-side-boundary");
        tournamentReplanNextRound = !rescue.State.HasWon;
        if (opening is null)
        {
            emergencyPotionRepaired = true;
            ReportSearchLine("emergency card repair: no bottle required; kept native surviving continuation");
            return false;
        }
        ConfirmedPotion = new PotionPlan(opening.Potion!, opening.Target, "native-emergency-rescue", opening.Choices)
            { TurnStartChoices = opening.TurnStartChoices };
        noActionStamp = ""; tournamentDriven++;
        ReportSearchLine($"potion opportunity: {opening.Potion!.Id.Entry} native-emergency-rescue "
            + $"hp={string.Join('/', combat.Players.Select(p => rescue.State.Hp(p.Creature)))} "
            + $"compute={emergencyPotionComputeMs:F1}ms");
        return true;
    }

    private IEnumerable<bool> EmergencyPotionComparisons(KernelSession root,
        IReadOnlyList<Player> actors, IReadOnlyList<KernelTeamSearch.Action>? plan)
    {
        EmergencyLine? baseline = null;
        foreach (var tick in EmergencyReplay(root.ForkForReplay(), root.Party, plan, line => baseline = line)) yield return tick;
        // A distant WIPE does not justify spending at a currently safe boundary.
        if (baseline is null || !root.Party.Any(p => root.Hp(p.Creature) > 0 && baseline.State.Hp(p.Creature) <= 0)) yield break;
        var original = baseline;
        foreach (var defensive in new[] { true, false })
        {
            EmergencyLine? alternative = null;
            foreach (var tick in EmergencyRollout(root.ForkForReplay(), root.Party, actors, defensive, line => alternative = line)) yield return tick;
            if (alternative is not null && BetterEmergencyBoundary(alternative.State, baseline.State, root.Party)) baseline = alternative;
        }
        if (RescuesWithoutTradingSeats(original.State, baseline.State, root.Party)) emergencyPotionRepair = baseline;
        if (root.Party.All(p => root.Hp(p.Creature) <= 0 || baseline.State.Hp(p.Creature) > 0)) yield break;
        var bottles = actors.Where(p => root.CanAct(p) && p.CanUseOrRemovePotions)
            .SelectMany(root.UsablePotions).Where(KernelSession.CanModelPotion)
            .OrderBy(q => KernelPotionPolicy.PreservationValue(q, root.Party));
        foreach (var potion in bottles)
        foreach (var target in root.PotionTargets(potion)
            // Native Touch selects for the recipient. A human keeps the native
            // menu; this rescue witness can replay only delegated bot choices.
            .Where(t => !KernelSession.RequiresPotionChoiceContract(potion)
                || t?.Player is { } recipient && actors.Contains(recipient) && AutoPilot.Drives(recipient.NetId))
            .OrderBy(t => t?.Player is { } p && baseline.State.Hp(p.Creature) <= 0 ? 0 : 1))
        foreach (var plannedOpening in root.PotionActions(potion, target, controlledActors: actors))
        {
            var after = root.ForkForReplay();
            // The recorded physical choice is part of this native transaction,
            // not an unpriced selector answer supplied after drinking the bottle.
            if (!after.ReplayPlanned(plannedOpening, 1, out _, out var opening)) { yield return false; continue; }
            for (var mode = 0; mode < 3; mode++)
            {
                EmergencyLine? candidate = null;
                var trials = mode == 0 ? EmergencyReplay(after.ForkForReplay(), root.Party, plan, line => candidate = line)
                    : EmergencyRollout(after.ForkForReplay(), root.Party, actors,
                        mode == 1, line => candidate = line);
                foreach (var tick in trials) yield return tick;
                if (candidate is null || !RescuesEmergencyBoundary(original.State, baseline.State, candidate.State, root.Party)) continue;
                emergencyPotionRescue = candidate with {
                    Actions = new[] { opening }.Concat(candidate.Actions).ToArray(),
                    Alive = new[] { EmergencyAlive(root, root.Party) }.Concat(candidate.Alive).ToArray(),
                    Enemies = new[] { root.Enemies.ToArray() }.Concat(candidate.Enemies).ToArray() };
                yield break;
            }
        }
    }

    private IEnumerable<bool> EmergencyReplay(KernelSession state, IReadOnlyList<Player> party,
        IReadOnlyList<KernelTeamSearch.Action>? plan, Action<EmergencyLine> complete)
    {
        var actions = new List<KernelTeamSearch.Action>(); var alive = new List<ulong[]>(); var enemies = new List<Creature[]>();
        foreach (var action in plan?.Take(65) ?? [])
        {
            if (state.HasTerminal || state.EnemyPhaseCompleted) break;
            if (actions.Count >= 64) yield break; // a truncated tail is no fatality witness
            alive.Add(EmergencyAlive(state, party)); enemies.Add(state.Enemies.ToArray());
            emergencyPotionActions++;
            // R2 actual 2026-10-12: reverting only this caller to strict
            // ReplayPlanned compiled 0 and reproduced the original cached-Dex
            // assertion RED1; exact 1154-source/10-artifact restore and forced
            // rebuild returned GREEN0. No future exact-replay guard was changed.
            // work/v16-cached-dex-contract-native-20261012-v1/candidate/r2-caller/proof.json
            // System.InvalidOperationException: Post-decision potion: cached WIPE must offer the native 6HP Dexterity rescue before its next card
            if (!state.ForecastCausal(action, 1, out _, out var resolved)) yield break;
            actions.Add(resolved); yield return true;
        }
        foreach (var player in party)
        {
            if (state.HasTerminal || state.EnemyPhaseCompleted) break;
            if (!state.CanAct(player)) continue;
            alive.Add(EmergencyAlive(state, party)); enemies.Add(state.Enemies.ToArray());
            emergencyPotionActions++;
            if (!state.EndTurn(player, 1, out _, out var resolvedEnd)) yield break;
            actions.Add(resolvedEnd); yield return true;
        }
        if (state.HasTerminal || state.EnemyPhaseCompleted) complete(new(state, actions, alive, enemies));
    }

    private IEnumerable<bool> EmergencyRollout(KernelSession state, IReadOnlyList<Player> party,
        IReadOnlyList<Player> actors, bool defensive, Action<EmergencyLine> complete)
    {
        // No whole-fight search, pair/setup investment or unbounded zero-cost
        // card loop. The exact cached tail and two simple policies are witnesses,
        // not an assertion that every possible rescue has been searched.
        // Kernel enums are value types: do not hoist one into Core's generated
        // iterator fields. The mod loader enumerates Core before Kernel loads.
        var run = new KernelPotionOpportunity.CurrentTurnWitness(state, party, actors,
            defensive ? RolloutPolicyKind.Defend : RolloutPolicyKind.Kill);
        while (!run.Done) { run.Step(); emergencyPotionActions++; yield return true; }
        if (run.State.HasTerminal || run.State.EnemyPhaseCompleted)
            complete(new(run.State, run.Result!.Actions, run.Result.AliveStates, run.Result.EnemyStates));
    }

    private static ulong[] EmergencyAlive(KernelSession state, IReadOnlyList<Player> party)
        => party.Where(p => state.Hp(p.Creature) > 0).Select(p => p.NetId).Order().ToArray();
    private static bool BetterEmergencyBoundary(KernelSession a, KernelSession b, IReadOnlyList<Player> party)
        => a.HasWon && !b.HasWon || a.HasWon == b.HasWon &&
            (party.Count(p => a.Hp(p.Creature) > 0) > party.Count(p => b.Hp(p.Creature) > 0)
                || party.Count(p => a.Hp(p.Creature) > 0) == party.Count(p => b.Hp(p.Creature) > 0)
                    && party.Sum(p => a.Hp(p.Creature)) > party.Sum(p => b.Hp(p.Creature)));
    private static bool RescuesWithoutTradingSeats(KernelSession baseline, KernelSession candidate,
        IReadOnlyList<Player> party)
        => party.All(p => baseline.Hp(p.Creature) <= 0 || candidate.Hp(p.Creature) > 0)
            && party.Any(p => baseline.Hp(p.Creature) <= 0 && candidate.Hp(p.Creature) > 0);
    private static bool RescuesEmergencyBoundary(KernelSession original, KernelSession baseline,
        KernelSession candidate, IReadOnlyList<Player> party)
        => RescuesWithoutTradingSeats(baseline, candidate, party)
            && party.All(p => original.Hp(p.Creature) <= 0 || candidate.Hp(p.Creature) > 0);
}
