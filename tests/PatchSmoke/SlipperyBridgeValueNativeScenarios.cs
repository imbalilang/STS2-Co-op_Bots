using System.Reflection;
using System.Text.Json;
using CoopBots;
using CoopBots.Building;
using CoopBots.Kernel;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

// Constructed four-seat A10 native event controls; historical F35 page/RNG is
// unavailable, so these fixtures do not restore that hidden historical root.
// R2 2026-10-12: independent sole EventHandlers production withdrawals.
// Each command trajectory is core build/test build/RED/restored core/restored
// test/GREEN = [0, 0, 1, 0, 0, 0], with every source and DLL/PDB exactly restored.
// Actual REDs in work/slippery-bridge-native-private-20261012-v1/commands:
// r2-physical-03: "Bridge native value: production risk lookup must retain the exact second upgraded and enchanted deck instance"
// r2-price-03: "Bridge native value: do not pay cumulative HP when every affordable unprotected reroll loses to current removal"
// r2-diagnostics-03: "Bridge native value: cheap Choose+Explain must not request diagnostic-only deck simulations"
// Price RED was driven alone; physical RED is not its evidence. Original
// frozen19 Core also failed the qualified price assertion in 07-baseline-price-cumulative.
// Native HoldOn, Overcome, GetNewRandomCard, Damage, removal and Hook remain original.
// Core role/never-remove ordering is explicit policy; this fixture checks the
// observed protected-role outcome without claiming its distinct withdrawal RED.
internal static class SlipperyBridgeValueNativeScenarios
{
    private static readonly MethodInfo Choose = AccessTools.Method(typeof(BotEventDriver), "ChooseEventOption");

    internal static void Run(string? namedCase = null)
    {
        TestEnvironment.Ensure();
        using var pump = new ConstructionParallelScenarios.Pump();
        using var presentation = new PresentationScope();
        if (namedCase is null or "physical") PhysicalMutableRisk(pump);
        if (namedCase is null or "sequence") NativeRerollAndRemoval(pump);
        if (namedCase is null or "price") PaidMarginalUpperBound(pump);
        if (namedCase is null or "core") NativeCoreProtection(pump);
        if (namedCase is null or "diagnostics") CheapDecisionDiagnostics(pump);
        if (namedCase is not null && namedCase is not ("physical" or "sequence" or "price" or "core" or "diagnostics"))
            throw new ArgumentException("Unknown bridge native case: " + namedCase);
    }

    private static void PhysicalMutableRisk(ConstructionParallelScenarios.Pump pump)
    {
        using var board = new Board("BRIDGE-MUTABLE");
        var player = board.Owner;
        var plain = Add(player, "STRIKE_IRONCLAD");
        var decorated = Add(player, "STRIKE_IRONCLAD");
        decorated.UpgradeInternal(); decorated.FinalizeUpgradeInternal();
        decorated.EnchantInternal(ModelDb.Enchantment<Vigorous>().ToMutable(), 8);
        decorated.Enchantment!.ModifyCard();
        Add(player, "DEFEND_IRONCLAD"); Add(player, "BASH"); Add(player, "POMMEL_STRIKE");
        var bridge = Begin(pump, player);
        for (var holds = 0; !ReferenceEquals(AtRisk(bridge), decorated) && holds < 12; holds++)
            NativeHold(pump, bridge);
        Check(ReferenceEquals(AtRisk(bridge), decorated), "native holds must actually offer the upgraded enchanted second starter");
        var option = bridge.CurrentOptions[EventHandlers.IndexOf(bridge, "OVERCOME")];
        var tip = option.HoverTips.OfType<CardHoverTip>().Single();
        var selected = (CardModel?)AccessTools.Method(typeof(SlipperyBridgeHandler), "AtRisk").Invoke(null, [option]);
        Check(ReferenceEquals(tip.Card, decorated) && ReferenceEquals(selected, decorated)
            && !ReferenceEquals(selected, plain) && selected.CurrentUpgradeLevel == 1
            && selected.Enchantment?.Amount == 8,
            "production risk lookup must retain the exact second upgraded and enchanted deck instance");
        Check(!SlipperyBridgeHandler.IsFiller(decorated),
            "upgraded or enchanted starter is not the vanilla filler shortcut");
        var value = Price(pump, player, decorated);
        Check(double.IsFinite(value.Total) && value.Reason != "not in deck",
            "native hover risk must reach the existing physical removal valuation");
        Console.WriteLine($"NATIVE bridge physical risk={selected!.Id.Entry}+{selected.CurrentUpgradeLevel} enchant={selected.Enchantment!.Id.Entry}:{selected.Enchantment.Amount} removal={value.Total:F3}");
        Console.WriteLine("PASS: bridge production risk retains upgraded enchanted physical duplicate identity.");
    }

    private static void NativeRerollAndRemoval(ConstructionParallelScenarios.Pump pump)
    {
        using var board = new Board("BRIDGE-NATIVE-SEQUENCE");
        var player = board.Owner;
        foreach (var id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "POMMEL_STRIKE", "SHRUG_IT_OFF", "ANGER" }) Add(player, id);
        var bridge = (SlipperyBridge)ModelDb.Event<SlipperyBridge>().ToMutable();
        // Actual BeginEvent runs native initial selection and option generation.
        pump.Run(async () => { await bridge.BeginEvent(player, null, false); return true; });
        var first = AtRisk(bridge);
        var initialDeck = player.Deck.Cards.ToArray();
        var initialRng = RngState(bridge);
        var hold = HoldOption(bridge);
        var preview = OutOfCombatPredictions.EventOption(bridge, hold).Cards.ToArray();
        Check(preview.Length > 0 && preview.All(c => initialDeck.Any(d => ReferenceEquals(d, c))),
            "existing finite reroll preview must contain real native deck references");
        Check(RngState(bridge) == initialRng && player.Creature.CurrentHp == 300,
            "reading the finite preview must not advance native RNG or charge HP");
        var spent = 0;
        for (var index = 0; index < preview.Length; index++)
        {
            var cost = bridge.DynamicVars["HpLoss"].IntValue;
            spent += cost;
            var nativeOption = HoldOption(bridge);
            pump.Run(async () => { await nativeOption.Chosen(); return true; });
            Check(ReferenceEquals(AtRisk(bridge), preview[index])
                && player.Creature.CurrentHp == 300 - spent
                && bridge.DynamicVars["HpLoss"].IntValue == cost + 1
                && player.Deck.Cards.SequenceEqual(initialDeck),
                "each real native paid HoldOn must match cloned-RNG physical reroll and cumulative HP");
            Console.WriteLine($"NATIVE bridge hold={index + 1} paid={cost} cumulative={spent} next={AtRisk(bridge).Id.Entry} rng={RngState(bridge)}");
        }
        var removed = AtRisk(bridge);
        var rngBeforeRemoval = RngState(bridge);
        var overcome = bridge.CurrentOptions[EventHandlers.IndexOf(bridge, "OVERCOME")];
        pump.Run(async () => { await overcome.Chosen(); return true; });
        Check(bridge.IsFinished && player.Deck.Cards.Count == initialDeck.Length - 1
            && !player.Deck.Cards.Any(c => ReferenceEquals(c, removed))
            && player.Deck.Cards.SequenceEqual(initialDeck.Where(c => !ReferenceEquals(c, removed)))
            && player.Creature.CurrentHp == 300 - spent && RngState(bridge) == rngBeforeRemoval,
            "native Overcome must remove only the final physical card and end without extra HP or RNG");
        Check(initialDeck.Any(c => ReferenceEquals(c, first)), "initial native risk remains a recorded physical deck instance");
        Console.WriteLine("PASS: bridge finite preview matches real paid native holds and final physical deletion.");
    }

    private static void PaidMarginalUpperBound(ConstructionParallelScenarios.Pump pump)
    {
        // A single next payment need not exceed every deck improvement. Compare
        // every affordable endpoint after ALL preceding native 3+4+5... costs.
        // Finite constructed run seeds locate a discriminating page; they do not
        // modify event RNG, private risk or payment fields, or restore old F35.
        for (var trial = 0; trial < 8; trial++)
        {
            using var board = new Board("BRIDGE-PRICE-BOUND-" + trial);
            var player = board.Owner;
            foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "POMMEL_STRIKE", "POMMEL_STRIKE", "SHRUG_IT_OFF" }) Add(player, id);
            var prices = player.Deck.Cards.ToDictionary(card => card, card => Price(pump, player, card).Total);
            var bridge = Begin(pump, player); var costs = new List<int>();
            for (var step = 0; step < 24; step++)
            {
                var risk = AtRisk(bridge); var cost = bridge.DynamicVars["HpLoss"].IntValue;
                var preview = OutOfCombatPredictions.EventOption(bridge, HoldOption(bridge)).Cards;
                var paid = 0; var endpoints = new List<(CardModel Card, int Paid, double Gain)>();
                for (var index = 0; index < preview.Count; index++)
                {
                    paid += cost + index;
                    if (player.Creature.CurrentHp - paid < player.Creature.MaxHp * 0.35 || player.Creature.CurrentHp <= paid) break;
                    var card = preview[index];
                    if (!BuildValue.RemovalProtection(card, player.Deck.Cards).Protected && !BotShopPlanner.IsNeverRemoved(card))
                        endpoints.Add((card, paid, prices[card] - prices[risk]));
                }
                var discriminate = risk.Id.Entry == "POMMEL_STRIKE" && !BuildValue.RemovalProtection(risk, player.Deck.Cards).Protected
                    && endpoints.Any(endpoint => endpoint.Gain > 0)
                    && endpoints.All(endpoint => endpoint.Gain - endpoint.Paid < 0)
                    && player.Creature.CurrentHp - cost >= player.Creature.MaxHp * 0.35;
                Console.WriteLine($"NATIVE bridge price premise trial={trial} step={step} risk={risk.Id.Entry} hp={player.Creature.CurrentHp} nextCost={cost} endpoints="
                    + string.Join(";", endpoints.Select(endpoint => $"{endpoint.Card.Id.Entry}:gain={endpoint.Gain:F3},paid={endpoint.Paid},net={endpoint.Gain-endpoint.Paid:F3}")));
                if (discriminate)
                {
                    // Reprice on the actual reached HP/page to qualify the
                    // numerical premise independently of the production choice.
                    var exact = player.Deck.Cards.ToDictionary(card => card, card => Price(pump, player, card).Total);
                    var immediate = exact[risk];
                    Check(endpoints.Any(endpoint => exact[endpoint.Card] > immediate)
                        && endpoints.All(endpoint => exact[endpoint.Card] - immediate - endpoint.Paid < 0),
                        "real affordable price-control endpoints must improve removal alone and lose after their cumulative HP payments");
                    var hpBefore = player.Creature.CurrentHp;
                    Check(costs.SequenceEqual(Enumerable.Range(3, costs.Count)) && hpBefore == 300 - costs.Sum(),
                        "price-control page must arise from native increasing cumulative HP payments, not overwritten event fields or RNG");
                    var deckBefore = player.Deck.Cards.ToArray(); var rngBefore = RngState(bridge);
                    var chosen = Pick(pump, player, bridge);
                    Check(bridge.CurrentOptions[chosen].TextKey.EndsWith("OVERCOME", StringComparison.Ordinal),
                        "do not pay cumulative HP when every affordable unprotected reroll loses to current removal");
                    Check(player.Deck.Cards.SequenceEqual(deckBefore) && player.Creature.CurrentHp == hpBefore && RngState(bridge) == rngBefore,
                        "production async valuation must be read-only before submission");
                    Console.WriteLine($"NATIVE bridge price bound: trial={trial} removeNow={immediate:F3} priorPaid={costs.Sum()} pageHp={hpBefore} choose={bridge.CurrentOptions[chosen].TextKey}");
                    pump.Run(async () => { await bridge.CurrentOptions[chosen].Chosen(); return true; });
                    Check(bridge.IsFinished && player.Creature.CurrentHp == hpBefore
                        && !player.Deck.Cards.Any(c => ReferenceEquals(c, risk)) && RngState(bridge) == rngBefore,
                        "priced Overcome decision must execute through the real native removal endpoint");
                    Console.WriteLine("PASS: bridge expensive hold is refused when cumulative HP exceeds all affordable endpoint improvements.");
                    return;
                }
                if (player.Creature.CurrentHp - cost < player.Creature.MaxHp * 0.35) break;
                costs.Add(cost); NativeHold(pump, bridge);
            }
        }
        throw new InvalidOperationException("Bridge native value: finite real native paid sequences contain no qualifying cumulative-price duplicate draw page");
    }

    private static void NativeCoreProtection(ConstructionParallelScenarios.Pump pump)
    {
        using var board = new Board("BRIDGE-CORE");
        var player = board.Owner;
        foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD" }) Add(player, id);
        var core = Add(player, "BATTLE_TRANCE");
        Check(BuildValue.RemovalProtection(core, player.Deck.Cards).Protected,
            "constructed draw engine must be the existing removal guard's sole draw role");
        var bridge = Begin(pump, player);
        Check(ReferenceEquals(AtRisk(bridge), core), "native initial risk must be the only nonbasic draw engine");
        var preview = OutOfCombatPredictions.EventOption(bridge, HoldOption(bridge)).Cards;
        Check(preview.Any(card => !BuildValue.RemovalProtection(card, player.Deck.Cards).Protected),
            "core control has an affordable unprotected native reroll endpoint");
        var before = player.Deck.Cards.ToArray();
        var chosen = Pick(pump, player, bridge);
        Check(bridge.CurrentOptions[chosen].TextKey.Contains(".options.HOLD_ON_", StringComparison.Ordinal),
            "an affordable unprotected endpoint must preserve the deck's sole draw role");
        pump.Run(async () => { await bridge.CurrentOptions[chosen].Chosen(); return true; });
        Check(player.Creature.CurrentHp == 297 && player.Deck.Cards.SequenceEqual(before)
            && player.Deck.Cards.Any(card => ReferenceEquals(card, core)),
            "real native cheap hold must pay once while keeping the original physical core");
        Console.WriteLine("PASS: bridge preserves a necessary role through one real cheap native hold.");
    }

    // Optional offline pricing of the CLOSED trace. The official save supplies
    // real four-seat cards/props/upgrades/enchantments/relics. Appending the
    // actually removed card reconstructs only the deck MULTISET; its prior
    // position and prior event page/RNG are unknown. No historical replay claim.
    // Output is a finite price observation, not an assertion of win-rate benefit.
    internal static void MeasureClosedHistory(string savedEntry, string observationFile)
    {
        TestEnvironment.Ensure();
        using var pump = new ConstructionParallelScenarios.Pump();
        using var presentation = new PresentationScope();
        var save = JsonSerializer.Deserialize(File.ReadAllText(savedEntry), JsonSerializationUtility.GetTypeInfo<SerializableRun>())!;
        var run = RunState.FromSerializable(save);
        using var scope = new EventNativeScope(run);
        using var trace = JsonDocument.Parse(File.ReadAllText(observationFile));
        foreach (var observation in trace.RootElement.GetProperty("observations").EnumerateArray())
        {
            var player = run.Players.Single(p => p.NetId == observation.GetProperty("player_id").GetUInt64());
            var hpPaid = observation.GetProperty("hp_paid").GetInt32();
            var entryHp = observation.GetProperty("entry_hp").GetInt32();
            player.Creature.SetCurrentHpInternal(entryHp);
            var serialized = JsonSerializer.Deserialize(observation.GetProperty("removed")[0].GetRawText(), JsonSerializationUtility.GetTypeInfo<SerializableCard>())!;
            var lost = CardModel.FromSerializable(serialized); lost.Owner = player; player.Deck.AddInternal(lost);
            var before = player.Deck.Cards.ToArray();
            var prices = before.Select(card => new { Card = card, Value = Price(pump, player, card), Guard = BuildValue.RemovalProtection(card, before) }).ToArray();
            var lostPrice = prices.Single(p => ReferenceEquals(p.Card, lost));
            Console.WriteLine("BRIDGE CLOSED MULTISET PRICE " + JsonSerializer.Serialize(new
            {
                player.NetId, entryHp, hpPaid, Lost = lost.Id.Entry, Upgrade = lost.CurrentUpgradeLevel,
                Enchantment = lost.Enchantment?.Id.Entry, SignedRemove = lostPrice.Value.Total,
                RemoveProtection = lostPrice.Guard.Protected,
                Deck = prices.Select(p => new { Id = p.Card.Id.Entry, Upgrade = p.Card.CurrentUpgradeLevel,
                    Enchantment = p.Card.Enchantment?.Id.Entry, SignedRemove = p.Value.Total, Protected = p.Guard.Protected }),
                HistoricalRootRestored = false,
                Boundary = "Appended removed physical card; original order, per-page risk and original event RNG unknown. HP payment versus marginal estimates does not prove the historical best branch."
            }));
            Check(player.Deck.Cards.SequenceEqual(before) && player.Creature.CurrentHp == entryHp,
                "closed-trace price sampling must not modify native deck or reconstructed entry HP");
        }
    }

    private static BuildValue.Valuation Price(ConstructionParallelScenarios.Pump pump, Player player, CardModel card)
        => pump.Run(() => ConstructionDecision.RunAsync(player,
            () => BuildValue.RemoveForDecision(card, player), () => "bridge-single-price:" + card.Id.Entry));

    private static int Pick(ConstructionParallelScenarios.Pump pump, Player player, SlipperyBridge bridge)
        => pump.Run(() => ConstructionDecision.RunAsync(player,
            () => (int)Choose.Invoke(null, [player, bridge])!,
            () => "bridge-page:" + bridge.DynamicVars["HpLoss"].IntValue + ":" + AtRisk(bridge).Id.Entry));

    private static CardModel Add(Player player, string id)
    {
        var card = ConstructionParallelScenarios.Card(id, player); player.Deck.AddInternal(card); return card;
    }

    private static SlipperyBridge Begin(ConstructionParallelScenarios.Pump pump, Player player)
    {
        var bridge = (SlipperyBridge)ModelDb.Event<SlipperyBridge>().ToMutable();
        pump.Run(async () => { await bridge.BeginEvent(player, null, false); return true; });
        return bridge;
    }
    private static void NativeHold(ConstructionParallelScenarios.Pump pump, SlipperyBridge bridge)
        => pump.Run(async () => { await HoldOption(bridge).Chosen(); return true; });

    private static void CheapDecisionDiagnostics(ConstructionParallelScenarios.Pump pump)
    {
        foreach (var reason in new[] { "unprotected-filler", "lethal-hold", "health-floor" })
        {
            using var board = new Board("BRIDGE-DIAGNOSTICS-" + reason);
            var player = board.Owner;
            foreach (var id in new[] { "STRIKE_IRONCLAD", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "ANGER" }) Add(player, id);
            var bridge = Begin(pump, player);
            if (reason == "unprotected-filler")
            {
                for (var count = 0; !SlipperyBridgeHandler.IsFiller(AtRisk(bridge)) && count < 8; count++) NativeHold(pump, bridge);
                Check(SlipperyBridgeHandler.IsFiller(AtRisk(bridge))
                    && !BuildValue.RemovalProtection(AtRisk(bridge), player.Deck.Cards).Protected,
                    "diagnostic filler control must actually be an unprotected native starter reroll");
            }
            else
            {
                Check(!SlipperyBridgeHandler.IsFiller(AtRisk(bridge)), "diagnostic damage control must begin with a real nonfiller native risk");
                player.Creature.SetCurrentHpInternal(reason == "lethal-hold" ? 3 : 106);
                Check((HoldOption(bridge).WillKillPlayer?.Invoke(player) == true) == (reason == "lethal-hold"),
                    "diagnostic health-floor and lethal premises must be distinct");
            }
            var deck = player.Deck.Cards.ToArray(); var hp = player.Creature.CurrentHp;
            var rng = RngState(bridge); var fingerprint = ConstructionDecision.Fingerprint(player);
            using var observer = new ConstructionObserver();
            var decision = pump.Run(() => ConstructionDecision.RunAsync(player,
                () => ((int)Choose.Invoke(null, [player, bridge])!, EventHandlers.Explain(player, bridge)),
                () => "bridge-cheap-diagnostic:" + reason));
            Check(observer.Requests == 0, "cheap Choose+Explain must not request diagnostic-only deck simulations");
            Check(observer.Logs.Count == 1 && observer.Logs[0].Contains("batches=0 decks=0", StringComparison.Ordinal),
                "fresh cheap construction decision must complete with original batches=0 decks=0");
            Check(bridge.CurrentOptions[decision.Item1].TextKey.EndsWith("OVERCOME", StringComparison.Ordinal)
                && decision.Item2.Contains(reason, StringComparison.Ordinal), "production cheap choice and reason must agree");
            Check(player.Deck.Cards.SequenceEqual(deck) && player.Creature.CurrentHp == hp
                && RngState(bridge) == rng && ConstructionDecision.Fingerprint(player) == fingerprint,
                "cheap native Choose+Explain must preserve HP, physical deck, event and run RNG");
            Console.WriteLine($"NATIVE bridge diagnostic={reason} requests={observer.Requests} {observer.Logs.Single()}");
        }
        Console.WriteLine("PASS: bridge cheap native choices explain without extra construction requests, batches or decks.");
    }

    private sealed class ConstructionObserver : IDisposable
    {
        private static ConstructionObserver? active;
        private readonly Harmony harmony = new("coopbots.bridge-native.construction-observer");
        internal int Requests;
        internal List<string> Logs { get; } = new();
        internal ConstructionObserver()
        {
            if (active is not null) throw new InvalidOperationException("Nested bridge construction observer");
            active = this;
            harmony.Patch(AccessTools.Method(typeof(ConstructionDecision), "Request"), prefix: new HarmonyMethod(typeof(ConstructionObserver), nameof(Request)));
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Info)), prefix: new HarmonyMethod(typeof(ConstructionObserver), nameof(Info)));
        }
        private static void Request() { if (active is not null) active.Requests++; }
        private static void Info(string text)
        { if (active is not null && text.StartsWith("CoopBots construction async:", StringComparison.Ordinal)) active.Logs.Add(text); }
        public void Dispose() { harmony.UnpatchAll(harmony.Id); active = null; }
    }

    private static CardModel AtRisk(SlipperyBridge bridge)
        => bridge.CurrentOptions[EventHandlers.IndexOf(bridge, "OVERCOME")].HoverTips.OfType<CardHoverTip>().Single().Card;
    private static EventOption HoldOption(SlipperyBridge bridge)
        => bridge.CurrentOptions.Single(o => o.TextKey.Contains(".options.HOLD_ON_", StringComparison.Ordinal));
    private static string RngState(SlipperyBridge bridge)
        => JsonSerializer.Serialize(bridge.Rng.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableRng>());
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Bridge native value: " + message); }

    private sealed class Board : IDisposable
    {
        internal Player Owner { get; }
        private readonly EventNativeScope scope;
        internal Board(string seed)
        {
            var party = Enumerable.Range(0, 4).Select(i => Player.CreateForNewRun<Deprived>(UnlockState.all,
                BotRegistry.CreateId(BotDifficulty.Pro, 203, i))).ToArray();
            foreach (var player in party)
            {
                AccessTools.Field(typeof(Player), "<Character>k__BackingField").SetValue(player, ModelDb.Character<Ironclad>());
                player.MaxEnergy = 3; player.Creature.SetMaxHpInternal(300); player.Creature.SetCurrentHpInternal(300);
                foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
            }
            var run = RunState.CreateForTest(party, seed: seed, ascensionLevel: 10);
            Owner = party[2]; scope = new EventNativeScope(run);
        }
        public void Dispose() => scope.Dispose();
    }

    private sealed class EventNativeScope : IDisposable
    {
        private readonly PropertyInfo state = AccessTools.Property(typeof(RunManager), "State");
        private readonly PropertyInfo ascension = AccessTools.Property(typeof(RunManager), nameof(RunManager.AscensionManager));
        private readonly object? oldState, oldAscension;
        private readonly ulong? oldLocal = LocalContext.NetId;
        internal EventNativeScope(RunState run)
        {
            oldState = state.GetValue(RunManager.Instance); oldAscension = ascension.GetValue(RunManager.Instance);
            state.SetValue(RunManager.Instance, run);
            ascension.SetValue(RunManager.Instance, new AscensionManager(run.AscensionLevel));
            LocalContext.NetId = null; // headless spectator; native deletion needs no card preview node
        }
        public void Dispose()
        {
            state.SetValue(RunManager.Instance, oldState); ascension.SetValue(RunManager.Instance, oldAscension); LocalContext.NetId = oldLocal;
        }
    }

    private sealed class PresentationScope : IDisposable
    {
        private readonly Harmony harmony = new("coopbots.bridge-native.presentation");
        internal PresentationScope()
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Title)), prefix: new HarmonyMethod(typeof(PresentationScope), nameof(Title)));
            harmony.Patch(AccessTools.Method(typeof(LocString), "GetFormattedText"), prefix: new HarmonyMethod(typeof(PresentationScope), nameof(Text)));
            harmony.Patch(AccessTools.Method(typeof(EventModel), "GetOptionTitle"), prefix: new HarmonyMethod(typeof(PresentationScope), nameof(OptionText)));
            harmony.Patch(AccessTools.Method(typeof(EventModel), "GetOptionDescription"), prefix: new HarmonyMethod(typeof(PresentationScope), nameof(OptionText)));
            harmony.Patch(AccessTools.Method(typeof(EventOption), "AddLocVars"), prefix: new HarmonyMethod(typeof(PresentationScope), nameof(NoDisplay)));
            // The event runs outside combat. TestEnvironment's general fixture
            // prefix reports combat true; this environment-only postfix restores
            // the actual event fact without stubbing damage or event effects.
            harmony.Patch(AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsInProgress)), postfix: new HarmonyMethod(typeof(PresentationScope), nameof(NoCombat)));
        }
        private static bool Title(CardModel __instance, ref string __result) { __result = __instance.Id.Entry; return false; }
        private static bool Text(ref string __result) { __result = ""; return false; }
        private static bool OptionText(string key, ref LocString? __result) { __result = new LocString("events", key); return false; }
        private static bool NoDisplay() => false;
        private static void NoCombat(ref bool __result) => __result = false;
        public void Dispose() => harmony.UnpatchAll(harmony.Id);
    }
}
