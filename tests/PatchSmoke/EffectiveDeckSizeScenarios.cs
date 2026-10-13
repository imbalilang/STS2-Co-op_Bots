using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// The effective deck size: one count, shared by DeckStrength's total-side penalty and
// RewardTakeBar's skip value. See EffectiveDeckSize for why self-removing cards count
// half and why draw is a divisor gated on energy.
internal static class EffectiveDeckSizeScenarios
{
    private static void Check(bool ok, string why)
    {
        if (!ok) throw new Exception("EffectiveDeckSize: " + why);
    }

    internal static void Run()
    {
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, 11);
        var run = RunState.CreateForTest([bot], seed: "EFFECTIVE-SIZE");
        var combat = new CombatState(runState: run);
        bot.ResetCombatState(); combat.AddPlayer(bot);
        bot.MaxEnergy = 3; // Deprived's 100-energy default would erase the energy gate.
        List<CardModel> Deck() => bot.Deck.Cards.ToList();
        void Clear()
        {
            foreach (var card in bot.Deck.Cards.ToArray()) bot.Deck.RemoveInternal(card);
        }
        void Give<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) bot.Deck.AddInternal(combat.CreateCard<T>(bot));
        }

        // (1) A card that leaves the deck after one play weighs half. Inflame is a Power
        // and costs the same as Strike, so cost and draw cannot explain the difference.
        Clear(); Give<StrikeIronclad>(20);
        var vanilla = EffectiveDeckSize.Effective(Deck(), bot, 3);
        Check(Math.Abs(vanilla - 20) < 0.001, $"20 vanilla cards must weigh 20, got {vanilla:F2}");
        Clear(); Give<StrikeIronclad>(12); Give<Inflame>(8);
        var powers = EffectiveDeckSize.Effective(Deck(), bot, 3);
        Check(Math.Abs(powers - 16) < 0.001, $"8 Power cards must weigh half each (want 16), got {powers:F2}");
        Clear(); Give<StrikeIronclad>(16); Give<BladeDance>(4);
        var exhausts = EffectiveDeckSize.Effective(Deck(), bot, 3);
        Check(Math.Abs(exhausts - 18) < 0.001, $"4 Exhaust cards must weigh half each (want 18), got {exhausts:F2}");
        Clear(); Give<StrikeIronclad>(18); Give<AscendersBane>(2);
        var ethereals = EffectiveDeckSize.Effective(Deck(), bot, 3);
        Check(Math.Abs(ethereals - 19) < 0.001,
            $"2 Ethereal cards must weigh half each (want 19), got {ethereals:F2}");

        // (2) Pressure starts at 16 EFFECTIVE cards, not at a raw count.
        Clear(); Give<StrikeIronclad>(16);
        Check(EffectiveDeckSize.TakeBarPressure(Deck(), bot) == 0, "16 effective cards must not raise the bar");
        Check(EffectiveDeckSize.TotalPenalty(Deck(), bot, 3) == 0, "16 effective cards must not pay a total penalty");
        Clear(); Give<StrikeIronclad>(17);
        Check(EffectiveDeckSize.TakeBarPressure(Deck(), bot) > 0, "17 effective cards must raise the bar");
        // A deck whose 8 Powers make it effectively 16 is NOT pressured even at 20 raw cards.
        Clear(); Give<StrikeIronclad>(12); Give<Inflame>(8);
        Check(EffectiveDeckSize.TakeBarPressure(Deck(), bot) == 0,
            "a 20-card deck that is effectively 16 must not be pressured for its raw count");

        // (3) Draw shortens the count -- but only as far as the energy can cash in.
        Clear(); Give<StrikeIronclad>(20);
        Check(Math.Abs(EffectiveDeckSize.Effective(Deck(), bot, 3)
                    - EffectiveDeckSize.Effective(Deck(), bot, 9)) < 0.001,
            "without draw, energy must not change the effective count");
        Clear(); Give<StrikeIronclad>(15); Give<Acrobatics>(5);
        var poor = EffectiveDeckSize.Effective(Deck(), bot, 3);
        var rich = EffectiveDeckSize.Effective(Deck(), bot, 9);
        Check(rich < poor,
            $"the same draw must shorten the count more once energy can pay for it ({poor:F2} vs {rich:F2})");

        // (4) The live acceptance. Live 2026-10-06 (seed 8W0NNPPSPFTX, floor 31, the act-2
        // boss reward) FAN_OF_KNIVES was SKIPPED: total -0.4 against a 1.5 bar. The deck is
        // reconstructed from the run history's floor_added_to_deck for the 22 cards that
        // were already there, plus the three basics DARV's Astrolabe later transformed away.
        var silent = Player.CreateForNewRun<Deprived>(UnlockState.all, 12);
        // A10, and still act index 1: the live reward was taken at the act-2 boss screen,
        // where DeckRobustness reads .90 rather than the non-ascension .55. Getting this
        // wrong moves the robustness term by ~6 and the whole acceptance with it.
        var silentRun = RunState.CreateForTest([silent], ascensionLevel: 10, seed: "FAN-OF-KNIVES-ACCEPT");
        silentRun.CurrentActIndex = 1;

        var side = new CombatState(runState: silentRun);
        silent.ResetCombatState(); side.AddPlayer(silent);
        silent.MaxEnergy = 3;
        foreach (var card in silent.Deck.Cards.ToArray()) silent.Deck.RemoveInternal(card);
        void Add<T>(int count) where T : CardModel
        {
            for (var i = 0; i < count; i++) silent.Deck.AddInternal(side.CreateCard<T>(silent));
        }
        Add<AscendersBane>(1);
        Add<DefendSilent>(2);
        Add<Neutralize>(1);
        Add<StrikeSilent>(2);
        Add<Survivor>(1);
        Add<BladeDance>(1);
        Add<Snakebite>(1);
        Add<DeadlyPoison>(3);
        Add<CalculatedGamble>(2);
        Add<Envenom>(1);
        Add<Acrobatics>(1);
        Add<BladeOfInk>(1);
        Add<PiercingWail>(1);
        Add<BladeSymphony>(1);
        Add<EscapePlan>(1);
        Add<HiddenDaggers>(1);
        Add<NoxiousFumes>(1);
        // The three basics the Astrolabe transform consumed. Silent starts on 5 Strike +
        // 5 Defend; the run records removing two Strikes and one Defend, and the final
        // deck holds 2 Strike + 2 Defend -- so the transformed three were 1 Strike + 2 Defend.
        Add<StrikeSilent>(1);
        Add<DefendSilent>(2);
        Check(silent.Deck.Cards.Count == 25, $"the live deck must be 25 cards, got {silent.Deck.Cards.Count}");
        // The five cards the run history records as upgraded by floor 31. Without them the
        // fixture's combat term runs ~7 points above the live log's and the acceptance
        // would be measuring the reconstruction rather than the model.
        void Upgrade(string entry)
        {
            var card = silent.Deck.Cards.FirstOrDefault(c => c.Id.Entry == entry);
            Check(card is not null, $"the live deck must still hold {entry}");
            card!.UpgradeInternal();
        }
        Upgrade("STRIKE_SILENT");
        Upgrade("SNAKEBITE");
        Upgrade("ENVENOM");
        Upgrade("PIERCING_WAIL");
        Upgrade("BLADE_SYMPHONY");

        var effective = EffectiveDeckSize.Effective(silent.Deck.Cards.ToList(), silent, 3);
        var bar = BuildValue.RewardTakeBar(silent);
        var offer = BuildValue.Add(side.CreateCard<FanOfKnives>(silent), silent);
        Check(offer.Total > bar,
            $"FAN_OF_KNIVES must clear the bar on the live 25-card deck "
            + $"(effective={effective:F2}, total={offer.Total:F2}, bar={bar:F2}): {offer.Reason}");
        // Both discounts have to be live on the real deck, or the acceptance above is
        // passing for some other reason: 7 self-removing cards weigh 21.5, and the draw
        // discount must pull it further. (Measured 20.22 on 2026-10-07.)
        Check(effective < 22,
            $"the live deck's 25 cards must act smaller than 22 (weights alone give 21.5), got {effective:F2}");

        Console.WriteLine($"PASS: effective deck size -- self-removing half weight, draw gated on energy, "
            + $"onset at 16, and FAN_OF_KNIVES clears the bar on the live 25-card deck "
            + $"(effective={effective:F2}, total={offer.Total:F2}, bar={bar:F2}).");
        Console.WriteLine($"PROBE fok: {offer.Reason}");
    }
}
