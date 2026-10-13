using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel.Vendor.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ShopShortcutScenarios
{
    // R2 2026-10-01: six production mutants, exit 1; restored exit 0.
    // work/reward-parallel/shortcut-r2.json records commands and raw logs.
    // Curse shortcut / unavailable-service bypass removed:
    // "Shop shortcuts: unnecessary deck search".
    // Full-deck scan restored / upgraded starters admitted:
    // "Shop shortcuts: scanned outside unupgraded Strikes/Defends".
    // Fallback removed / native removable check removed:
    // "Shop shortcuts: full-deck fallback or native legality lost".
    // R2 prior-value / prior-scope, exit 1, work/reward-parallel/prior-r2.json:
    // "Shop shortcuts: basic Strike/Defend prior must be -18 before and after upgrade";
    // Owner changed the accepted parameter to -15 after the A10 price audit.
    // R2 prior15-r2/prior.log: reverting to -18 exits 1:
    // "Shop shortcuts: basic Strike/Defend prior must be -15 before and after upgrade".
    // "Shop shortcuts: starter prior leaked into Bash or Survivor".
    private static bool forbidSearch;
    private static readonly List<CardModel> scored = new();
    private static void Search()
    { if (forbidSearch) throw new Exception("Shop shortcuts: unnecessary deck search"); }
    private static void Removal(CardModel card) => scored.Add(card);
    internal static void Run()
    {
        RemovalIndexNativeGap();
        var harmony = new Harmony("coopbots.shop-shortcut-test");
        harmony.Patch(AccessTools.Method(typeof(DeckStrength),nameof(DeckStrength.Evaluate)),
            prefix:new HarmonyMethod(typeof(ShopShortcutScenarios),nameof(Search)));
        harmony.Patch(AccessTools.Method(typeof(BotShopPlanner),nameof(BotShopPlanner.RemovalValue)),
            prefix:new HarmonyMethod(typeof(ShopShortcutScenarios),nameof(Removal)));
        try
        {
            var p = ConstructionParallelScenarios.Fixture(); p.Gold = 100;
            foreach (var model in ModelDb.AllCards.Where(c=>c.Rarity==CardRarity.Basic
                && (c.Tags.Contains(CardTag.Strike)||c.Tags.Contains(CardTag.Defend))))
            {
                var basic=model.ToMutable(); basic.Owner=p;
                if (DeckStrength.CommunityPrior(basic)!=-15
                    || DeckStrength.CommunityPrior(CardProfile.Upgraded(basic)!.Value.Card)!=-15)
                    throw new Exception("Shop shortcuts: basic Strike/Defend prior must be -15 before and after upgrade");
            }
            if (DeckStrength.CommunityPrior(ConstructionParallelScenarios.Card("BASH",p))!=-6
                || DeckStrength.CommunityPrior(ConstructionParallelScenarios.Card("SURVIVOR",p))!=-6)
                throw new Exception("Shop shortcuts: starter prior leaked into Bash or Survivor");
            var curse = ConstructionParallelScenarios.Card("CLUMSY",p); p.Deck.AddInternal(curse);
            var inventory = new MerchantInventory(p);
            var removal = new MerchantCardRemovalEntry(p); ShopRemovalAnchorScenarios.Cost(removal,100);
            typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory,removal);
            ShopRemovalAnchorScenarios.Offer(inventory,ConstructionParallelScenarios.Card("PREPARED",p),75);
            forbidSearch = true;
            var choice = BotShopPlanner.Choose(inventory);
            if (choice is null || choice.RemovalIndex < 0 || p.Deck.Cards[choice.RemovalIndex] != curse)
                throw new Exception("Shop shortcuts: affordable curse was not removed immediately");
            p.Deck.RemoveInternal(curse); p.Gold = 0;
            if (BotShopPlanner.Choose(inventory) is not null)
                throw new Exception("Shop shortcuts: unaffordable removal selected");
            forbidSearch = false;
            var upgraded = p.Deck.Cards.First(c=>c.Id.Entry=="DEFEND_SILENT");
            upgraded.UpgradeInternal(); upgraded.FinalizeUpgradeInternal();
            scored.Clear();
            var best = BotShopPlanner.BestRemoval(p.Deck.Cards,p);
            if (best.index < 0 || scored.Count == 0 || scored.Any(c=>c.IsUpgraded
                || c.Id.Entry is not ("STRIKE_SILENT" or "DEFEND_SILENT")))
                throw new Exception("Shop shortcuts: scanned outside unupgraded Strikes/Defends");
            foreach (var c in p.Deck.Cards.Where(c=>c.Id.Entry is "STRIKE_SILENT" or "DEFEND_SILENT").ToArray())
                p.Deck.RemoveInternal(c);
            var candidates = BotShopPlanner.RemovalCandidates(p.Deck.Cards);
            if (!candidates.Any(i=>p.Deck.Cards[i].Id.Entry=="SURVIVOR")
                || candidates.Any(i=>!p.Deck.Cards[i].IsRemovable))
                throw new Exception("Shop shortcuts: full-deck fallback or native legality lost");
            Console.WriteLine("PASS: shop removes affordable curses without search, skips unaffordable removal, restricts starters and restores full-deck fallback.");
        }
        finally { forbidSearch=false; scored.Clear(); harmony.UnpatchAll(harmony.Id); }
    }

    private static void RemovalIndexNativeGap()
    {
        // Actual R2 shop-removal-deck-domain, 2026-10-09: replacing the
        // legal[index] mapping with the old legal-array ordinal produced
        // Rebuild0 / native RED1 at this physical-object assertion:
        // "Shop removal domain: BestRemoval must return the unique native starter's full deck index after the unremovable gap"
        // Exact source and six DLL/PDB artifacts restored: Rebuild0 / GREEN0.
        // work/architecture-refactor-20261009/fixed-budget-performance-v3/shop-gap-native-v1/evidence/proof.json
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Shop removal domain: " + message); }
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var run = RunState.CreateForTest(new[] { human, bot }, seed: "SHOP-REMOVAL-DOMAIN");
        CardModel Populate(Player p)
        {
            p.Deck.AddInternal(run.CreateCard<AscendersBane>(p));
            for (var i = 0; i < 5; i++)
            {
                var strike = run.CreateCard<StrikeIronclad>(p);
                strike.UpgradeInternal(); strike.FinalizeUpgradeInternal(); p.Deck.AddInternal(strike);
            }
            for (var i = 0; i < 3; i++)
            {
                var defend = run.CreateCard<DefendIronclad>(p);
                defend.UpgradeInternal(); defend.FinalizeUpgradeInternal(); p.Deck.AddInternal(defend);
            }
            p.Deck.AddInternal(run.CreateCard<Bash>(p));
            var target = run.CreateCard<DefendIronclad>(p); p.Deck.AddInternal(target);
            return target;
        }
        var target = Populate(bot); var humanTarget = Populate(human);
        bot.Gold = 500; human.Gold = 123;
        var room = new MerchantRoom(); run.PushRoom(room);
        var inventory = new MerchantInventory(bot); room.Inventories.Add(inventory);
        var removal = new MerchantCardRemovalEntry(bot); ShopRemovalAnchorScenarios.Cost(removal, 100);
        typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, removal);
        var original = bot.Deck.Cards.ToArray(); var humanDeck = human.Deck.Cards.ToArray();
        var shuffle = run.Rng.Shuffle.CaptureState(); var selections = run.Rng.CombatCardSelection.CaptureState();
        var manager = RunManager.Instance;
        string[] properties = ["State", "NetService", "RunLocationTargetedBuffer", "ActionQueueSet", "ActionExecutor", "RewardSynchronizer"];
        var saved = properties.ToDictionary(name => name, name => AccessTools.Property(typeof(RunManager), name).GetValue(manager));
        var humanWasDriven = AutoPilot.IsAutopiloted(human.NetId);
        var net = (NetSingleplayerGameService)RuntimeHelpers.GetUninitializedObject(typeof(NetSingleplayerGameService));
        var queues = new ActionQueueSet(run.Players);
        AccessTools.Property(typeof(RunManager), "State").SetValue(manager, run);
        AccessTools.Property(typeof(RunManager), "NetService").SetValue(manager, net);
        AccessTools.Property(typeof(RunManager), "RunLocationTargetedBuffer").SetValue(manager, new RunLocationTargetedMessageBuffer(net));
        AccessTools.Property(typeof(RunManager), "ActionQueueSet").SetValue(manager, queues);
        AccessTools.Property(typeof(RunManager), "ActionExecutor").SetValue(manager, new ActionExecutor(queues));
        AccessTools.Property(typeof(RunManager), "RewardSynchronizer").SetValue(manager, RuntimeHelpers.GetUninitializedObject(typeof(RewardSynchronizer)));
        try
        {
            // Full suites may inherit a handed-over host from another fixture.
            // Restore that state after this ordinary-human native menu witness.
            AutoPilot.Set(human.NetId, false);
            // An ordinary human reaches the native filter; only its presentation
            // answer is supplied. Production bot prefixes must not replace it.
            var selector = new RemovalMenuSelector(humanTarget);
            CardModel[] nativeSelected;
            using (CardSelectCmd.PushSelector(selector))
                nativeSelected = CardSelectCmd.FromDeckForRemoval(human,
                    new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)).GetAwaiter().GetResult().ToArray();
            Check(selector.Options.Length == original.Length - 1 && !selector.Options.Contains(humanDeck[0])
                && selector.Options.All(card => card.IsRemovable) && nativeSelected.Single() == humanTarget,
                "native removal menu must exclude the first unremovable Bane and select the real later starter object");
            Check(!original[0].IsRemovable && target.IsRemovable
                && original.Where(card => card.Rarity == CardRarity.Basic && !card.IsUpgraded
                    && (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend))).Single() == target,
                "native gap board must have one physical unupgraded basic target after a different removable card");
            var best = BotShopPlanner.BestRemoval(bot.Deck.Cards, bot);
            Check(best.index == original.Length - 1 && ReferenceEquals(bot.Deck.Cards[best.index], target),
                "BestRemoval must return the unique native starter's full deck index after the unremovable gap");
            var choice = BotShopPlanner.Choose(inventory);
            Check(choice is not null && choice.RemovalIndex == best.index,
                "ordinary removal offer must carry the actual full deck index into its message");

            var incoming = run.CreateCard<Inflame>(bot);
            var cardEntry = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), CardType.Power);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(cardEntry, new CardCreationResult(incoming));
            var first = new PurchaseComparison.Offer(choice!.Index, best.index, removal, choice.Value, choice.Cost, false, false);
            var second = new PurchaseComparison.Offer(1, -1, cardEntry, 0, 74, false, false);
            var secondValue = (double)AccessTools.Method(typeof(BotShopPlanner), "SecondValue")
                .Invoke(null, new object[] { second, first, bot, best, bot.Potions.Count() })!;
            Check(bot.Deck.Cards.SequenceEqual(original) && human.Deck.Cards.SequenceEqual(humanDeck)
                && bot.Gold == 500 && human.Gold == 123 && run.Rng.Shuffle.CaptureState().Equals(shuffle)
                && run.Rng.CombatCardSelection.CaptureState().Equals(selections),
                "native menu and read-only first/second removal valuation must preserve both decks gold and both RNG states");

            var message = new BotShopMessage { Sequence = 891, Bot = bot.NetId, Entry = choice.Index,
                Removal = choice.RemovalIndex, Price = choice.Cost, Gold = bot.Gold, Key = "remove",
                State = (string)AccessTools.Method(typeof(BotShopDriver), "Fingerprint").Invoke(null, new object[] { inventory })!,
                Location = new RunLocation(0, null, 2) };
            var execute = AccessTools.Method(typeof(BotShopDriver), "Execute");
            bool Execute() => ((Task<bool>)execute.Invoke(null, new object[] { message, run })!).GetAwaiter().GetResult();
            Check(Execute() && bot.Gold == 500 - message.Price && removal.Used && bot.ExtraFields.CardShopRemovalsUsed == 1
                && !bot.Deck.Cards.Contains(target) && bot.Deck.Cards.SequenceEqual(original.Where(card => !ReferenceEquals(card, target)))
                && human.Deck.Cards.SequenceEqual(humanDeck) && human.Gold == 123,
                "actual native removal must pay once remove only the chosen later starter and preserve Bane and the earlier Bash");
            var actualAfterRemovalValue = (double)AccessTools.Method(typeof(BotShopPlanner), "CardGold")
                .Invoke(null, new object?[] { incoming, bot, null })!;
            Check(Math.Abs(secondValue - actualAfterRemovalValue) < 1e-8
                && run.Rng.Shuffle.CaptureState().Equals(shuffle) && run.Rng.CombatCardSelection.CaptureState().Equals(selections)
                && !Execute() && bot.Gold == 500 - message.Price && bot.ExtraFields.CardShopRemovalsUsed == 1,
                "SecondValue must match the physically removed full deck and native payment must preserve RNG and reject replay");
            Console.WriteLine($"DIAG shop-removal-domain nativeOptions={selector.Options.Length} index={best.index} target={target.Id.Entry} previous={original[^2].Id.Entry} value={best.value:F4} paid={message.Price} second={secondValue:F6} actualSecond={actualAfterRemovalValue:F6} rng=unchanged");
            Console.WriteLine("PASS: native removal gap maps the unique later starter to the full deck and pays for exactly that object; SecondValue matches actual native removal.");
        }
        finally
        {
            AutoPilot.Set(human.NetId, humanWasDriven);
            foreach (var pair in saved) AccessTools.Property(typeof(RunManager), pair.Key).SetValue(manager, pair.Value);
        }
    }

    private sealed class RemovalMenuSelector(CardModel target) : ICardSelector
    {
        internal CardModel[] Options = [];
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        { Options = options.ToArray(); return Task.FromResult<IEnumerable<CardModel>>(new[] { target }); }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException("Removal fixture has no reward screen");
    }
}
