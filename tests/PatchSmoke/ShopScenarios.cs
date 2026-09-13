using System.Reflection;
using System.Runtime.CompilerServices;
using CoopBots;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

internal static class ShopScenarios
{
    internal static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Shop regression: " + message); }
        var bot = Player.CreateForNewRun<Deprived>(UnlockState.all, BotRegistry.CreateId(BotDifficulty.Pro, 1, 1));
        var human = Player.CreateForNewRun<Deprived>(UnlockState.all, 1);
        var state = RunState.CreateForTest(new[] { human, bot }, seed: "SHOP");
        var combat = new CombatState(runState: state);
        var room = new MerchantRoom(); state.PushRoom(room);
        var inventory = new MerchantInventory(bot); room.Inventories.Add(inventory);
        bot.Gold = 200; human.Gold = 123;
        bot.Creature.SetCurrentHpInternal(50);
        var assembly = typeof(BotBrain).Assembly;
        var planner = assembly.GetType("CoopBots.BotShopPlanner")!;
        var driver = assembly.GetType("CoopBots.BotShopDriver")!;
        object? Choose() => planner.GetMethod("Choose", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { inventory });
        int Index(object? choice) => choice is null ? -1 : (int)choice.GetType().GetProperty("Index")!.GetValue(choice)!;
        string Fingerprint() => (string)driver.GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { inventory })!;
        var card = state.CreateCard<Lift>(bot);
        var entry = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), CardType.Skill);
        typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(entry, new CardCreationResult(card));
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 40);
        ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(entry);
        Check(Index(Choose()) == 0, "Affordable team protection should be considered for purchase.");
        bot.Gold = 20; Check(Choose() is null, "Never use the human's gold to afford a bot item."); bot.Gold = 200;
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 1000);
        Check(Choose() is null, "Do not buy above budget.");
        typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, 40);
        var member = ModelDb.Relic<MembershipCard>().ToMutable(); member.Owner = bot;
        Check(member.ModifyMerchantPrice(bot, entry, 100) == 50, "Virtual bot owner receives membership discount without local-human identity.");
        Check(member.ModifyMerchantPrice(human, entry, 100) == 100, "Bot membership never discounts another player's purchases.");

        var message = new BotShopMessage { Sequence = 7, Bot = bot.NetId, Entry = 0, Removal = -1, Price = 40,
            Gold = bot.Gold, Key = card.Id.Entry, State = Fingerprint(), Location = new RunLocation(0, null, 2) };
        var writer = new PacketWriter(); message.Serialize(writer);
        var reader = new PacketReader(); reader.Reset(writer.Buffer); var decoded = new BotShopMessage(); decoded.Deserialize(reader);
        Check(decoded.Bot == bot.NetId && decoded.Sequence == 7 && decoded.Price == 40 && decoded.State == message.State
            && decoded.Key == card.Id.Entry && decoded.Location.Equals(message.Location) && !decoded.ShouldBroadcast,
            "Network round trip preserves actor, price, precondition and location without client broadcasting.");

        var execute = driver.GetMethod("Execute", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Execute(BotShopMessage m) => ((Task<bool>)execute.Invoke(null, new object[] { m, state })!).GetAwaiter().GetResult();
        decoded.Gold = 199; Check(!Execute(decoded) && bot.Gold == 200, "Reject stale balance without mutation.");
        decoded.Gold = 200; decoded.Bot = human.NetId;
        Check(!Execute(decoded) && human.Gold == 123, "Reject a human as the transaction actor.");
        decoded.Bot = bot.NetId; decoded.Price = 39;
        Check(!Execute(decoded), "Reject stale price before changing inventory.");

        // Supply only the normally initialized reward synchronizer. Its local send
        // methods must be suppressed inside BotShopDriver's async transaction scope.
        var property = typeof(RunManager).GetProperty("RewardSynchronizer")!;
        var previous = property.GetValue(RunManager.Instance);
        property.SetValue(RunManager.Instance, RuntimeHelpers.GetUninitializedObject(typeof(RewardSynchronizer)));
        try
        {
            Check(Execute(message), "Execute a real MerchantCardEntry through its purchase wrapper.");
            Check(bot.Gold == 160 && human.Gold == 123 && bot.Deck.Cards.Contains(card) && !entry.IsStocked,
                "Purchase grants exactly one bot card and charges only its owner.");
            Check(!Execute(message) && bot.Gold == 160, "Replaying the instruction cannot purchase twice.");
            var copyBot = Player.CreateForNewRun<Deprived>(UnlockState.all, bot.NetId);
            var copyHuman = Player.CreateForNewRun<Deprived>(UnlockState.all, human.NetId);
            var copyState = RunState.CreateForTest(new[] { copyHuman, copyBot }, seed: "SHOP");
            copyBot.Gold = 200; copyHuman.Gold = 123; copyBot.Creature.SetCurrentHpInternal(50);
            var copyRoom = new MerchantRoom(); copyState.PushRoom(copyRoom);
            var copyInventory = new MerchantInventory(copyBot); copyRoom.Inventories.Add(copyInventory);
            var copyEntry = new MerchantCardEntry(copyBot, copyInventory, Array.Empty<CardModel>(), CardType.Skill);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(copyEntry, new CardCreationResult(copyState.CreateCard<Lift>(copyBot)));
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(copyEntry, 40);
            ((List<MerchantCardEntry>)copyInventory.CharacterCardEntries).Add(copyEntry);
            Check(((Task<bool>)execute.Invoke(null, new object[] { message, copyState })!).GetAwaiter().GetResult(),
                "Independent peer state can apply the same host instruction.");
            var copyFingerprint = (string)driver.GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { copyInventory })!;
            Check(copyFingerprint == Fingerprint() && copyHuman.Gold == 123,
                "Independent peers finish with identical purchased inventory, deck and balances.");
            Check(driver.GetField("Scope", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                .GetType().GetProperty("Value")!.GetValue(driver.GetField("Scope", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)) is null,
                "Async purchase identity must not leak into later human rewards.");
        }
        finally { property.SetValue(RunManager.Instance, previous); }

        var curse = state.CreateCard<Injury>(bot); bot.Deck.AddInternal(curse);
        var removal = new MerchantCardRemovalEntry(bot);
        typeof(MerchantInventory).GetProperty("CardRemovalEntry")!.SetValue(inventory, removal);
        Check(Index(Choose()) == 1, "Curse removal should beat saving when affordable.");
        var removeMessage = new BotShopMessage { Sequence = 8, Bot = bot.NetId, Entry = 1,
            Removal = bot.Deck.Cards.ToList().IndexOf(curse), Price = removal.Cost, Gold = bot.Gold,
            Key = "remove", State = Fingerprint(), Location = message.Location };
        var beforeGold = bot.Gold;
        Check(Execute(removeMessage) && !bot.Deck.Cards.Contains(curse) && removal.Used
            && bot.Gold == beforeGold - removeMessage.Price && bot.ExtraFields.CardShopRemovalsUsed == 1,
            "Real removal charges the bot, removes the chosen curse, and increments future removal pricing.");
        Check(!Execute(removeMessage), "A removal cannot be paid twice in one visit.");
        var nextRemoval = new MerchantCardRemovalEntry(bot);
        Check(nextRemoval.Cost > removeMessage.Price, "Next shop's removal price reflects previous purchases.");

        // Construct stocked entries without discovery UI/save services; purchase code is real.
        T Stock<T>(object model, int cost) where T : MerchantEntry
        {
            var item = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(MerchantEntry).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, bot);
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, cost);
            typeof(T).GetProperty("Model")!.SetValue(item, model);
            return item;
        }
        var potionEntry = Stock<MerchantPotionEntry>(ModelDb.Potion<BlockPotion>().ToMutable(), 30);
        ((List<MerchantPotionEntry>)inventory.PotionEntries).Add(potionEntry);
        bot.Gold = 300; bot.Creature.SetCurrentHpInternal(20);
        Check(Index(Choose()) == 1, "Low-health bot should buy a supported defensive potion with an open slot.");
        var potionMessage = new BotShopMessage { Sequence = 9, Bot = bot.NetId, Entry = 1, Removal = -1, Price = 30,
            Gold = bot.Gold, Key = potionEntry.Model!.Id.Entry, State = Fingerprint(), Location = message.Location };
        property.SetValue(RunManager.Instance, RuntimeHelpers.GetUninitializedObject(typeof(RewardSynchronizer)));
        try
        {
            Check(Execute(potionMessage) && bot.Gold == 270 && bot.Potions.Any(p => p is BlockPotion) && !potionEntry.IsStocked,
                "Real potion purchase fills the bot's slot and charges its own balance.");
            var relicEntry = Stock<MerchantRelicEntry>(ModelDb.Relic<BagOfMarbles>().ToMutable(), 120);
            inventory.AddRelicEntry(relicEntry);
            var relicMessage = new BotShopMessage { Sequence = 10, Bot = bot.NetId, Entry = 1, Removal = -1, Price = 120,
                Gold = bot.Gold, Key = relicEntry.Model!.Id.Entry, State = Fingerprint(), Location = message.Location };
            Check(Execute(relicMessage) && bot.Gold == 150 && bot.Relics.Any(r => r is BagOfMarbles) && !relicEntry.IsStocked,
                "Real relic purchase assigns the relic to its bot owner and consumes stock.");
            var membershipEntry = Stock<MerchantRelicEntry>(ModelDb.Relic<MembershipCard>().ToMutable(), 80);
            inventory.AddRelicEntry(membershipEntry);
            var discountTarget = new MerchantCardEntry(bot, inventory, Array.Empty<CardModel>(), CardType.Skill);
            typeof(MerchantCardEntry).GetProperty("CreationResult")!.SetValue(discountTarget, new CardCreationResult(state.CreateCard<DefendIronclad>(bot)));
            typeof(MerchantEntry).GetField("_cost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(discountTarget, 40);
            ((List<MerchantCardEntry>)inventory.CharacterCardEntries).Add(discountTarget);
            var membershipMessage = new BotShopMessage { Sequence = 11, Bot = bot.NetId, Entry = 3, Removal = -1, Price = 80,
                Gold = bot.Gold, Key = membershipEntry.Model!.Id.Entry, State = Fingerprint(), Location = message.Location };
            Check(Execute(membershipMessage) && bot.Gold == 70 && discountTarget.Cost == 20,
                "Membership is charged at its pre-purchase price and discounts subsequent goods for the bot.");
            while (bot.HasOpenPotionSlots) bot.AddPotionInternal(ModelDb.Potion<BlockPotion>().ToMutable(), silent: true);
            var fullSlotPotion = Stock<MerchantPotionEntry>(ModelDb.Potion<BlockPotion>().ToMutable(), 10);
            ((List<MerchantPotionEntry>)inventory.PotionEntries).Add(fullSlotPotion);
            Check(Index(Choose()) != inventory.AllEntries.ToList().IndexOf(fullSlotPotion),
                "Never select a potion purchase when all potion slots are full.");
        }
        finally { property.SetValue(RunManager.Instance, previous); }

        var relicValue = assembly.GetType("CoopBots.HumanCoopAdvisor")!.GetMethod("RelicValue", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Value(RelicModel relic) => ((ValueTuple<double, string>)relicValue.Invoke(null, new object[] { relic, bot })!).Item1;
        var marbles = ModelDb.Relic<BagOfMarbles>(); var before = Value(marbles);
        for (var i = 0; i < 5; i++) human.Deck.AddInternal(combat.CreateCard<StrikeIronclad>(human));
        Check(Value(marbles) > before, "Shared Vulnerable relic value must respond to allied attacks.");
        Check(Value(ModelDb.Relic<RedMask>()) > 0 && Value(ModelDb.Relic<MassiveScroll>()) > 0,
            "Shared Weak and multiplayer card reward relics must not default to zero.");
        Console.WriteLine("PASS: real shop card/potion/relic/removal purchases, independent peer replay, owner-only gold, stale-state/replay rejection, full slots, removal inflation, membership repricing and team relic value.");
    }
}
