using CoopBots;
using CoopBots.Building;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// R2 2026-10-03: deliberate production faults, all exit 1, restored.
// Commands and raw logs: work/decision-followup-20261003/r2-results.json.
// defence: System.Exception: Night decisions: cheap defence must see non-block team mitigation
// last-energy: System.Exception: Night decisions: mutually exclusive last-energy actions must compare legal hand outcomes
// investment-control: System.Exception: Night decisions: last energy must protect three allies in Tactical
// schedule: System.Exception: Night decisions: Tactical must get the second funded continuation
// boss-budget: System.Exception: Night decisions: only boss opening gets twice the budget
// team-pressure: System.Exception: Night decisions: three small hits must count as meaningful total team pressure
// transform-rng: System.Exception: Night decisions: preview must follow native curse pool and sequential RNG
// duplication: System.Exception: Night decisions: BingBong must duplicate both transformed curses
// transform-delta: System.Exception: Night decisions: transform value must be complete deck delta without removal bonus
// rng-isolation: System.Exception: Night decisions: preview must follow native curse pool and sequential RNG
// camp-coordination: System.Exception: Night decisions: helper must retain own rest when patient can self-rest to full at boss entry
// boss-heal: System.Exception: Night decisions: Pantograph only counts before a known boss fight
// mend-execution: System.Exception: Night decisions: executed MEND must retain the patient from the team plan
// refinement-boundary: System.Exception: Night decisions: refinement must freeze the entire current turn before searching
// refinement-search: System.Exception: Night decisions: idle slices must find and publish a better complete next-turn line
// refinement-tie: System.Exception: Night decisions: idle slices must find and publish a better complete next-turn line
// refinement-state: System.Exception: Night decisions: precomputed suffix must require the same actual boundary state
// refinement-cursor: System.Exception: Night decisions: live splice must start the improved suffix at its first action
// refinement-runtime: System.Exception: Night decisions: runtime ticks must fund refinement before pacing and busy returns
internal static partial class NightDecisionScenarios
{
    static void Check(bool yes, string reason) { if (!yes) throw new Exception("Night decisions: " + reason); }
    static bool Format(ref string __result) { __result = ""; return false; }
    internal static void Initialize()
    {
        TestEnvironment.Ensure();
        new Harmony("coopbots.night-decisions").Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(typeof(NightDecisionScenarios), nameof(Format)));
    }
    internal static void Building()
    {
        Initialize();
        var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 125, i))).ToArray();
        var state = RunState.CreateForTest(party, seed: "EPA0PVNNSQKP");
        foreach (var p in party)
        {
            AutoPilot.Set(p.NetId, true); p.MaxEnergy = 3;
            p.Creature.SetMaxHpInternal(80); p.Creature.SetCurrentHpInternal(80);
            for (var i = 0; i < 4; i++) p.Deck.AddInternal(state.CreateCard<StrikeSilent>(p));
            for (var i = 0; i < 4; i++) p.Deck.AddInternal(state.CreateCard<DefendSilent>(p));
        }
        try
        {
            var owner = party[0]; owner.AddRelicInternal(ModelDb.Relic<BingBong>().ToMutable());
            var curses = new[] { state.CreateCard<Doubt>(owner), state.CreateCard<Doubt>(owner) };
            foreach (var curse in curses) owner.Deck.AddInternal(curse);
            var rng = new Rng(owner, ModelDb.Event<Trial>().Id); rng.NextInt(3);
            var before = owner.Deck.Cards.ToArray(); var counter = rng.ToSerializable().counter;
            var forced = TransformPlan.Choose(owner, curses, 2, rng, forced: true);
            var nativeRng = new Rng(rng.ToSerializable());
            var native = curses.Select(c => MegaCrit.Sts2.Core.Factories.CardFactory.CreateRandomCardForTransform(c, false, nativeRng)).ToArray();
            Check(forced.Steps.Select(s => s.Result).SequenceEqual(native.Select(c => c.Id.Entry)), "preview must follow native curse pool and sequential RNG");
            Check(forced.Deck.Count == before.Length + 2 && forced.Deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse) == 4,
                "BingBong must duplicate both transformed curses");
            Check(Math.Abs(forced.Value - (BuildValue.Deck(forced.Deck, owner) - BuildValue.Deck(before, owner))) < 1e-7,
                "transform value must be complete deck delta without removal bonus");
            var best = TransformPlan.Choose(owner, before, 2, rng);
            Check(best.Value > forced.Value && !best.Steps.All(s => s.Card is Doubt), "known bad curse replacements must lose to useful transforms");
            Check(rng.ToSerializable().counter == counter && owner.Deck.Cards.SequenceEqual(before), "prediction must preserve live RNG and deck");
            Console.WriteLine($"NIGHT transform forced={forced.Value:F2} best={best.Value:F2} outputs={string.Join(',', forced.Steps.Select(s => s.Result))}");

            var patient = party[1]; owner.Creature.SetCurrentHpInternal(60);
            owner.AddRelicInternal(ModelDb.Relic<TinyMailbox>().ToMutable());
            patient.Creature.SetMaxHpInternal(76); patient.Creature.SetCurrentHpInternal(14);
            patient.AddRelicInternal(ModelDb.Relic<RegalPillow>().ToMutable());
            patient.AddRelicInternal(ModelDb.Relic<Pantograph>().ToMutable());
            IReadOnlyList<RestSiteOption> Options(Player p) => new RestSiteOption[] {
                new HealRestSiteOption(p), new SmithRestSiteOption(p), new MendRestSiteOption(p) };
            var camp = BotRestSitePatch.ChooseTeam(owner, Options(owner), Options, bossNext: true);
            Console.WriteLine($"NIGHT camp helper={Options(owner)[camp.Index].OptionId} patient={camp.Patient} own={owner.NetId}");
            // 2026-10-04: absolute scenario weighting raises this deck's best
            // upgrade to 10.84, so Smith beats self-rest. Both fulfil the owner
            // request: spend the helper's action on itself, not redundant Mend.
            // R2: bypass ChooseTeam for the old individual selector -> exit 1:
            // System.Exception: Night decisions: helper must retain self-rest or upgrade when patient can self-rest to full at boss entry
            // Evidence: work/decision-consistency-20261004/camp-r2.json; restored exit 0.
            Check(camp.Index == 0 && camp.Patient == owner.NetId || camp.Index == 1 && camp.Patient == 0,
                "helper must retain self-rest or upgrade when patient can self-rest to full at boss entry");
            Check(BotRestSitePatch.EntryHeal(patient, true) == 25 && BotRestSitePatch.EntryHeal(patient, false) == 0,
                "Pantograph only counts before a known boss fight");
            patient.Creature.SetCurrentHpInternal(1);
            Check(decimal.Floor(HealRestSiteOption.GetHealAmount(patient)) + BotRestSitePatch.EntryHeal(patient, true) + 1 == 63,
                "logged pillow patient reaches 63, not full, without help");
            BotRestSitePatch.PlannedPatients[owner.NetId] = party[2].NetId;
            Check(BotRestSitePatch.BestMendTarget(owner) == party[2]
                && MendRestSiteOptionFixture.RemoteChoice(owner).AsPlayerId() == party[2].NetId,
                "executed MEND must retain the patient from the team plan");
            Console.WriteLine("PASS: native transformation transactions and camp self-recovery opportunity costs.");
        }
        finally { BotRestSitePatch.ResetPlanning(); foreach (var p in party) AutoPilot.Set(p.NetId, false); }
    }
}
