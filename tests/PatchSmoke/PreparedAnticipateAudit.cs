using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using DeckSim;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Diagnostic community anchor, not a rule that Prepared must win every deck.
// Ranking and mechanism attribution are reported separately. Ablations retain
// card identity, so paired sample order and community priors do not change.
internal static class PreparedAnticipateAudit
{
    private static bool hidePreparedDraw;
    private static bool hidePreparedCleanup;
    private static bool hideAnticipateDexterity;
    private static void Profile(CardModel card, ref CardProfile.Facts __result)
    {
        if (hidePreparedDraw && card is Prepared) __result = __result with { Draw = 0 };
        if (hideAnticipateDexterity && card is Anticipate)
            __result = __result with { Effects = __result.Effects with { TemporaryDexterity = 0 } };
    }
    private static void Cleanup(CardModel c, ref bool __result)
    {
        if (hidePreparedCleanup && c is Prepared) __result = false;
    }

    internal static void Run(string? outputPath = null)
    {
        var harmony = new Harmony("coopbots.audit.prepared-anticipate");
        var profile = AccessTools.Method(typeof(CardProfile), nameof(CardProfile.Of));
        var cleanup = AccessTools.Method(typeof(SustainedDeckValue), nameof(SustainedDeckValue.Cleanup));
        harmony.Patch(profile, postfix: new HarmonyMethod(typeof(PreparedAnticipateAudit), nameof(Profile)));
        harmony.Patch(cleanup, postfix: new HarmonyMethod(typeof(PreparedAnticipateAudit), nameof(Cleanup)));
        var rows = new List<object>();
        try
        {
            foreach (var seats in new[] { 1, 4 })
            foreach (var scenario in new[] { "starter", "wounds", "sly", "block-heavy", "afterimage-wounds" })
            {
                var ps = Enumerable.Range(0, seats).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
                    BotRegistry.CreateId(BotDifficulty.Pro, 86, i))).ToArray();
                var run = RunState.CreateForTest(ps, ascensionLevel: 10, seed: "PREPARED-ANTICIPATE");
                foreach (var q in ps)
                {
                    typeof(Player).GetField("<Character>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(q, ModelDb.AllCharacters.Single(c => c.Id.Entry == "SILENT"));
                    q.MaxEnergy = 3; q.Gold = 100; q.Creature.SetMaxHpInternal(70); q.Creature.SetCurrentHpInternal(70);
                    foreach (var card in q.Character.StartingDeck) q.Deck.AddInternal(run.CreateCard(card, q));
                    q.Deck.AddInternal(run.CreateCard<AscendersBane>(q));
                }
                var p = ps[0];
                if (scenario is "wounds" or "afterimage-wounds")
                    for (var i = 0; i < 3; i++) p.Deck.AddInternal(run.CreateCard<Wound>(p));
                if (scenario == "afterimage-wounds") p.Deck.AddInternal(run.CreateCard<Afterimage>(p));
                if (scenario == "sly")
                {
                    p.Deck.AddInternal(run.CreateCard<Reflex>(p));
                    p.Deck.AddInternal(run.CreateCard<Tactician>(p));
                }
                if (scenario == "block-heavy")
                    for (var i = 0; i < 4; i++) p.Deck.AddInternal(run.CreateCard<Deflect>(p));

                var prepared = run.CreateCard<Prepared>(p); var anticipate = run.CreateCard<Anticipate>(p);
                object Measure(CardModel card)
                {
                    using var scope = DeckStrength.BeginEvaluation();
                    var d = p.Deck.Cards.ToArray(); var b = DeckStrength.Evaluate(d, p);
                    var a = DeckStrength.Evaluate(d.Append(card).ToArray(), p);
                    var up = BuildValue.UpgradeDelta(card, p);
                    var removal = BuildValue.Remove(card, p, d.Append(card).ToArray());
                    return new { card = card.Id.Entry, rarity = card.Rarity.ToString(), cost = CardProfile.Of(card).Cost,
                        add = a.Total-b.Total, combat = a.Combat-b.Combat, prior = a.Prior-b.Prior,
                        growth = a.Growth-b.Growth, robustness=a.Robustness-b.Robustness,
                        wither=a.Wither-b.Wither, chains=a.Chains-b.Chains, lambda=a.RobustnessWeight,
                        upgrade = up.Total, remove = removal.Total,
                        reason = BuildValue.Add(card,p).Reason };
                }
                var values = new[] { Measure(prepared), Measure(anticipate) };
                var inventory = new MerchantInventory(p);
                foreach (var card in new CardModel[] { prepared, anticipate })
                {
                    var entry = new MerchantCardEntry(p, inventory, [], CardType.Skill);
                    typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
                    typeof(MerchantEntry).GetField("_cost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(entry, 50);
                    ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
                }
                var choice = BotShopPlanner.Choose(inventory);
                var shop = choice is null ? "save" : BotShopPlanner.Key(inventory.AllEntries.ElementAt(choice.Index));
                double CardCombat(CardModel candidate)
                {
                    // Exclude community priors and forecasts for attribution.
                    var d = p.Deck.Cards.ToArray(); var a = d.Append(candidate).ToArray();
                    // Use the same ordinary + endurance-loss term as production.
                    // The historical audit used the retired mixed pollution path.
                    return DeckStrength.CombatScale*6*(.65*(DeckRobustness.Evaluate(a,p,3).Total-DeckRobustness.Evaluate(d,p,3).Total)
                        + .35*(CounterfactualDraft.Strength(a,3)-CounterfactualDraft.Strength(d,3)));
                }
                var normal = CardCombat(prepared);
                hidePreparedDraw = true;
                var drawWhileHidden = CardProfile.Of(prepared).Draw;
                var noDraw = CardCombat(prepared); hidePreparedDraw = false;
                hidePreparedCleanup = true;
                var cleanupWhileHidden = SustainedDeckValue.Cleanup(prepared);
                var noCleanup = CardCombat(prepared); hidePreparedCleanup = false;
                var slyCards = p.Deck.Cards.Where(c => c.Keywords.Contains(CardKeyword.Sly)).ToArray();
                foreach (var card in slyCards) card.RemoveKeyword(CardKeyword.Sly);
                var slyCountWhileHidden = p.Deck.Cards.Count(c => c.Keywords.Contains(CardKeyword.Sly));
                var noSly = CardCombat(prepared);
                foreach (var card in slyCards) card.AddKeyword(CardKeyword.Sly);
                var anticipateNormal = CardCombat(anticipate);
                hideAnticipateDexterity = true;
                var anticipateNoDexterity = CardCombat(anticipate); hideAnticipateDexterity = false;
                var upgrade = CardProfile.Upgraded(prepared,3)!.Value.Card;
                var noDrawUpgrade = CardProfile.NetDraw(prepared,CardProfile.Of(prepared));
                var full = p.Deck.Cards.Append(prepared).ToArray();
                var trace = SustainedDeckValue.OutcomeEstimate(full,3);
                var projectedPlays = trace.Sum(t => t.Plays.GetValueOrDefault(prepared));
                var row = new { seats, scenario, deck = p.Deck.Cards.Select(c => c.Id.Entry), values, shop,
                    preparedGrossDraw = CardProfile.Of(prepared).Draw, preparedNetDraw = noDrawUpgrade,
                    preparedUpgradeGrossDraw = CardProfile.Of(upgrade).Draw,
                    preparedUpgradeNetDraw = CardProfile.NetDraw(upgrade,CardProfile.Of(upgrade)),
                    mechanism = new { normal, noDraw, noCleanup, noSly, drawContribution=normal-noDraw,
                        cleanupContribution=normal-noCleanup, slyContribution=normal-noSly, projectedPlays },
                    controls = new { drawWhileHidden, cleanupWhileHidden, slyCountWhileHidden,
                        originalSlyCount=slyCards.Length, anticipateNormal, anticipateNoDexterity,
                        anticipateDexterityContribution=anticipateNormal-anticipateNoDexterity } };
                rows.Add(row);
                Console.WriteLine("PREPARED AUDIT " + JsonSerializer.Serialize(row));
            }
        }
        finally
        {
            hidePreparedDraw = hidePreparedCleanup = hideAnticipateDexterity = false;
            harmony.Unpatch(profile,HarmonyPatchType.Postfix,harmony.Id);
            harmony.Unpatch(cleanup,HarmonyPatchType.Postfix,harmony.Id);
        }
        var path=outputPath ?? Path.Combine(DraftSimHarness.RepoRoot(),"work/prepared-anticipate-audit/native-audit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,JsonSerializer.Serialize(new { diagnosticOnly=true, rows }));
        Console.WriteLine("AUDIT COMPLETE: measurements saved; completion is not a mechanism acceptance verdict.");
    }
}
