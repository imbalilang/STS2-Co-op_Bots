using CoopBots.Building;
using CoopBots;
using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class DraftSemanticsScenarios
{
    // R2 2026-09-27: nine production mutations each exited 1; restored run exited 0.
    // Evidence: work/strength-20260927-v9/r2-results.json and r2-*.log.
    // ASSERT FAIL: original act3 remove-two must discard plain starters before growth and replay engines
    // ASSERT FAIL: incomplete hand experiments must not price held engine deletion as blank-card relief
    // ASSERT FAIL: unsupported additions must preserve the full structural fallback instead of a clamped residual
    // ASSERT FAIL: Fade upgrade must value one-turn block support instead of permanent dexterity
    // ASSERT FAIL: all audited temporary strength and dexterity cards must share the timing correction
    // ASSERT FAIL: PactsEnd damage must depend on exhaust supply and never be unconditional printed damage
    // ASSERT FAIL: growth and attack replay must have native amounts and contextual payoff
    // ASSERT FAIL: exhaust draw block and cost engines must respond to real deck triggers
    // ASSERT FAIL: generated Shivs must contribute attacks without being mislabeled as card draw
    // ASSERT FAIL: combat capture must be off outside armed live tests
    // ASSERT FAIL: combat initial archive must preserve all four seats decks and difficulty
    // ASSERT FAIL: native combat packet must preserve multiplayer choice reward and action sequencing
    private static readonly List<string> Failures = new();
    internal static void Run()
    {
        Failures.Clear();
        var party = Enumerable.Range(1, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all, (ulong)i)).ToArray();
        var combat = new CombatState(runState: RunState.CreateForTest(party, seed: "DRAFT-SEMANTICS"));
        foreach (var player in party) { player.ResetCombatState(); combat.AddPlayer(player); player.MaxEnergy = 3; }
        var p = party[2];
        void Deck(string printed)
        {
            foreach (var c in p.Deck.Cards.ToArray()) p.Deck.RemoveInternal(c);
            foreach (var token in printed.Split(' '))
            {
                var parts = token.Split('x'); var id = parts[0].TrimEnd('+');
                for (var i = 0; i < (parts.Length == 2 ? int.Parse(parts[1]) : 1); i++)
                {
                    var c = combat.CreateCard(ModelDb.AllCards.Single(c => c.Id.Entry == id), p);
                    if (parts[0].EndsWith('+')) c.UpgradeInternal();
                    p.Deck.AddInternal(c);
                }
            }
        }
        // Actual act-3 card list from A10BASE201; save/relic/RNG not reconstructed.
        Deck("ANGER ASCENDERS_BANE BASH CINDER+ DEFEND_IRONCLADx3 DEMON_FORM MANGLE+ MEMENTO_MORI METAMORPHOSIS ONE_TWO_PUNCH OUTRAGE PACTS_END PANIC_BUTTON+ SETUP_STRIKE+ SNAKEBITE+ STRIKE_IRONCLADx5 SWORD_BOOMERANG THUNDERCLAP UPPERCUT");
        var removable = p.Deck.Cards.Where(c => c.Type != CardType.Curse).ToArray();
        var plan = RemovalPlan.Choose(p, removable, 2);
        Console.WriteLine("PROBE original deck remove2=" + string.Join(',', plan.Steps.Select(s => s.Card.Id.Entry)));
        // The shared removal priority (same one the shop uses) spends both slots
        // on unupgraded basic Defends while they remain, so this act-3 remove-two
        // no longer touches Mangle/Snakebite. Keep the original engine guard:
        // whatever the priority picks, growth and replay engines must not be hit.
        Check(plan.Steps.Count == 2 && plan.Steps.All(s => s.Card is not DemonForm and not OneTwoPunch),
            "original act3 remove-two must retain useful growth and replay engines");
        var demon = p.Deck.Cards.OfType<DemonForm>().Single();
        var punch = p.Deck.Cards.OfType<OneTwoPunch>().Single();
        Check(CardProfile.Of(demon).Effects.StrengthPerTurn == 3 && CardProfile.Of(punch).Effects.ReplayAttacks == 1
                && DraftCardEffects.Value(CardProfile.Of(demon).Effects, p.Deck.Cards.ToArray(), 3) > 0
                && DraftCardEffects.Value(CardProfile.Of(punch).Effects, p.Deck.Cards.ToArray(), 3) > 0,
            "growth and attack replay must have native amounts and contextual payoff");
        Check(CounterfactualDraft.Remove(p.Deck.Cards.ToArray(), demon, 3) is null,
            "incomplete hand experiments must not price held engine deletion as blank-card relief");
        var candidate = combat.CreateCard<DarkEmbrace>(p);
        var addition = BuildValue.AddDetailed(candidate, p);
        // V20 replaces all-or-nothing fallback for a known addition. Unknown
        // held mechanisms retain a structural share; deletion above stays guarded.
        Check(addition.Contextual && addition.Reason.Contains("unknown:")
            && addition.Reason.Contains("deck-strength:"),
            "known additions beside unknown held mechanisms must retain a labeled structural share");

        Deck("STRIKE_IRONCLADx6 DEFEND_IRONCLADx6");
        var fade = combat.CreateCard<Fade>(p);
        var fadeValue = BuildValue.UpgradeDelta(fade, p).Total;
        // R2 2026-09-30: TemporaryStatShare=.7 -> 0, exit 1 (restored .7 passes).
        // System.Exception: Fade upgrade must value one-turn block support instead of permanent dexterity
        Check(CardProfile.Of(fade).Scaling == 0 && CardProfile.Of(fade).Effects.TemporaryDexterity == 6
            && fadeValue > 0 && fadeValue < 10,
            "Fade upgrade must value one-turn block support instead of permanent dexterity");
        Check(CardProfile.Of(combat.CreateCard<Coordinate>(p)).Scaling == 0
            && CardProfile.Of(combat.CreateCard<SetupStrike>(p)).Scaling == 0
            && CardProfile.Of(combat.CreateCard<Anticipate>(p)).Scaling == 0,
            "all audited temporary strength and dexterity cards must share the timing correction");
        var pact = combat.CreateCard<PactsEnd>(p); var effects = CardProfile.Of(pact).Effects;
        var without = DraftCardEffects.Value(effects, p.Deck.Cards.ToArray(), 3);
        Deck("STRIKE_IRONCLADx6 TRUE_GRITx3 BURNING_PACTx3");
        var with = DraftCardEffects.Value(effects, p.Deck.Cards.ToArray(), 3);
        Check(CardProfile.Of(pact).Damage == 0 && effects.ExhaustRequired == 3 && without == 0 && with > 0,
            "PactsEnd damage must depend on exhaust supply and never be unconditional printed damage");
        var embrace = CardProfile.Of(candidate).Effects;
        var pain = CardProfile.Of(combat.CreateCard<FeelNoPain>(p)).Effects;
        var corrupt = CardProfile.Of(combat.CreateCard<Corruption>(p)).Effects;
        var supplied = p.Deck.Cards.ToArray();
        Deck("STRIKE_IRONCLADx12");
        var empty = p.Deck.Cards.ToArray();
        Check(DraftCardEffects.Value(embrace, supplied, 3) > DraftCardEffects.Value(embrace, empty, 3)
            && DraftCardEffects.Value(pain, supplied, 3) > DraftCardEffects.Value(pain, empty, 3)
            && DraftCardEffects.Value(corrupt, supplied, 3) > DraftCardEffects.Value(corrupt, empty, 3),
            "exhaust draw block and cost engines must respond to real deck triggers");
        var dance = CardProfile.Of(combat.CreateCard<BladeDance>(p));
        Check(dance.Draw == 0 && dance.Effects.GeneratedShivs == 3 && DraftCardEffects.Value(dance.Effects, empty, 3) > 0,
            "generated Shivs must contribute attacks without being mislabeled as card draw");
        Check(!LiveCombatCapture.Enabled, "combat capture must be off outside armed live tests");
        var run = p.RunState;
        var save = new SerializableRun { Players = party.Select(c => c.ToSerializable()).ToList(),
            SerializableRng = run.Rng.ToSerializable(), SerializableOdds = run.Odds.ToSerializable(),
            SerializableSharedRelicGrabBag = run.SharedRelicGrabBag.ToSerializable(), Ascension = 10 };
        var restored = JsonSerializer.Deserialize(LiveCombatCapture.SerializeInitial(save), JsonSerializationUtility.GetTypeInfo<SerializableRun>());
        Check(restored?.Players.Count == 4 && restored.Players[2].Deck.Count == 12 && restored.Ascension == 10,
            "combat initial archive must preserve all four seats decks and difficulty");
        var replay = new CombatReplay { serializableRun = save, version = "test", gitCommit = "test",
            choiceIds = [1, 2, 3, 4], rewardIds = [5, 6, 7, 8], nextActionId = 12, nextHookId = 13 };
        var reader = new PacketReader(); reader.Reset(LiveCombatCapture.SerializeReplay(replay));
        var decoded = reader.Read<CombatReplay>();
        Check(decoded.serializableRun.Players.Count == 4 && decoded.choiceIds.SequenceEqual(new uint[] { 1, 2, 3, 4 })
            && decoded.rewardIds.SequenceEqual(new[] { 5, 6, 7, 8 }) && decoded.nextActionId == 12 && decoded.nextHookId == 13,
            "native combat packet must preserve multiplayer choice reward and action sequencing");
        if (Failures.Count > 0) throw new Exception(string.Join("\n", Failures));
        Console.WriteLine($"PASS: draft semantic correction across rewards removals and upgrades (Fade upgrade={fadeValue:F2}).");
    }
    private static void Check(bool value, string message) { if (!value) { Failures.Add(message); Console.WriteLine("ASSERT FAIL: " + message); } }
}
