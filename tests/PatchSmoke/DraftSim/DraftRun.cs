using System.Reflection;
using CoopBots;
using CoopBots.Building;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace DeckSim;

/// <summary>One deck as it stood at the end of an act, with the score it earned there.</summary>
internal sealed record DeckSnapshot(string Stage, int Size, int Curses, int Upgrades, DeckScore Score, string Deck);

/// <summary>Everything one simulated run produced. This is the unit the report aggregates.</summary>
internal sealed record DraftRunResult(
    string Character,
    ulong Seed,
    string Route,
    int FinalSize,
    int Curses,
    int Upgrades,
    int RewardsTaken,
    int RewardsSkipped,
    int Removals,
    int Smiths,
    int CursesAdded,
    int GoldEarned,
    int GoldSpent,
    DeckSnapshot[] Trajectory,
    DeckScore Final,
    string FinalDeck);

/// <summary>
/// One A10 run drafted to the end, without combat and without HP.
///
/// The pipeline only ever does what changes the deck: reward picks, shop
/// removals, curses and camps. Every *decision* is the shipped one —
/// <see cref="BotBrain.ChooseCardReward"/> for rewards,
/// <see cref="BotShopPlanner.RemovalValue"/> and the shop's own price rule for
/// removals, <see cref="BotBrain.SelectCards"/> for the smith target — so a
/// change to the drafting logic changes these runs, which is the entire point:
/// a pipeline that reimplemented the choices would only ever measure itself.
///
/// What is modelled rather than simulated is stated where it is used: the path
/// (<see cref="ActPath"/>), the gold (<see cref="ActPath.GoldReward"/>) and the
/// curse events. What is not modelled at all is combat, HP, relics, potions and
/// the map graph.
/// </summary>
internal static class DraftRunner
{
    internal static DraftRunResult Run(string characterName, ulong seed, SimConfig config)
    {
        var character = ResolveCharacter(characterName);
        var rng = new Rng(seed, "coopbots-deck-sim");
        var state = new State(characterName, seed, config, rng);

        var player = NewDraftedPlayer(character, seed, config, state);
        var trajectory = new List<DeckSnapshot>();

        for (var act = 0; act < ActPath.Acts; act++)
        {
            // The act index is what every depth-aware rule in the mod reads:
            // RunDepth.BloatFactor, ShopThresholdFactor, the smith HP floor.
            // It has to move with the simulation or those rules are tested flat.
            player.RunState.CurrentActIndex = act;
            var blueprint = ActPath.Blueprint[act];
            for (var node = 0; node < blueprint.Length; node++)
            {
                var visits = new PathContext(act, Remaining(blueprint, node));
                Visit(player, blueprint[node], visits, state);
            }
            // Double Boss (A10): the act ends with a second boss floor.
            for (var boss = 0; boss < ActPath.BossesInAct(act); boss++)
                Visit(player, MapPointType.Boss, new PathContext(act, []), state);

            trajectory.Add(Snapshot(player, $"act {act + 1}", config, state.Seed));
        }

        var final = Snapshot(player, "final", config, state.Seed);
        return new DraftRunResult(
            characterName, seed, Archetypes.Describe(player.Deck.Cards.ToList(), player),
            // Curses and upgrades are read off the finished deck, not off the
            // tallies: the report has to say what the deck ended up holding, and
            // curses added minus curses removed is not the same number.
            final.Size, final.Curses, final.Upgrades,
            state.RewardsTaken, state.RewardsSkipped, state.Removals, state.Smiths, state.CursesAdded,
            state.GoldEarned, state.GoldSpent, trajectory.ToArray(), final.Score, final.Deck);
    }

    /// <summary>Nodes still walkable after the current one, so "is a shop still ahead" is real.</summary>
    private static MapPointType[] Remaining(MapPointType[] blueprint, int index)
        => blueprint.Skip(index + 1).ToArray();

    /// <summary>
    /// A player whose deck has been replaced by <paramref name="cardCount"/> copies of one
    /// shipped card, then <paramref name="alsoId"/> copies of another. Used to calibrate the
    /// scorer on decks whose correct ordering is not in doubt.
    /// </summary>
    internal static List<CardModel> DeckOf(Player player, string cardId, int cardCount, string? alsoId = null, int alsoCount = 0)
    {
        var deck = new List<CardModel>();
        Add(deck, player, cardId, cardCount);
        if (alsoId is not null) Add(deck, player, alsoId, alsoCount);
        return deck;
    }

    internal static Player NewPlayer(string character, ulong seed)
        => NewDraftedPlayer(ResolveCharacter(character), seed, SimConfig.Default, new State(character, seed, SimConfig.Default, new Rng(seed, "fixture")));

    private static void Add(List<CardModel> deck, Player player, string cardId, int count)
    {
        var canonical = ModelDb.AllCards.FirstOrDefault(card => string.Equals(card.Id.Entry, cardId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown card '{cardId}'.");
        for (var i = 0; i < count; i++) deck.Add(player.RunState.CreateCard(canonical, player));
    }

    private sealed record PathContext(int Act, MapPointType[] Ahead)
    {
        internal bool ShopAhead => Ahead.Contains(MapPointType.Shop);
        internal bool CampAhead => Ahead.Contains(MapPointType.RestSite);
    }

    /// <summary>Mutable per-run tallies. Kept out of <see cref="DraftRunResult"/> so the loop stays readable.</summary>
    private sealed class State(string character, ulong seed, SimConfig config, Rng rng)
    {
        internal string Character { get; } = character;
        internal ulong Seed { get; } = seed;
        internal SimConfig Config { get; } = config;
        internal Rng Rng { get; } = rng;
        internal int RewardsTaken;
        internal int RewardsSkipped;
        internal int Removals;
        internal int Smiths;
        internal int Upgrades;
        internal int CursesAdded;
        internal int GoldEarned;
        internal int GoldSpent;
    }

    // ---- Player construction ------------------------------------------------

    /// <summary>
    /// A player carrying a real character: real card pool, real starting deck and
    /// a real character id (the mined archetype clusters are keyed by it).
    ///
    /// The player object itself is the test character, because constructing a
    /// real one headlessly dies in <c>Player.PopulateStartingRelics</c> on a
    /// SaveManager that the test process does not have. Swapping the character
    /// model on the constructed player gets everything the draft reads without
    /// that dependency.
    /// </summary>
    private static Player NewDraftedPlayer(CharacterModel character, ulong seed, SimConfig config, State state)
    {
        var player = Player.CreateForNewRun<Deprived>(UnlockState.all,
            BotRegistry.CreateId(BotDifficulty.Pro, 7, (int)(seed % 1000)));
        var characterField = typeof(Player).GetField("<Character>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Player.Character is no longer a backing field; the draft pipeline cannot pick a character.");
        characterField.SetValue(player, character);

        var run = RunState.CreateForTest(new[] { player }, ascensionLevel: config.Ascension10 ? 10 : 0,
            seed: $"COOPBOTS-DECKSIM-{character.Id.Entry}-{seed}");
        foreach (var card in character.StartingDeck) player.Deck.AddInternal(run.CreateCard(card, player));

        if (config.Ascension10)
        {
            player.Gold = (int)Math.Round(character.StartingGold * Ascension10.GoldMultiplier);
            // Ascender's Bane is Eternal, so this curse can never be removed: it is
            // a permanent tax on every draw for the whole run, and the draft has to
            // be judged with it in the deck rather than around it.
            var bane = run.CreateCard<AscendersBane>(player);
            bane.FloorAddedToDeck = 1;
            player.Deck.AddInternal(bane, -1, silent: true);
            state.CursesAdded++;
        }
        else
        {
            player.Gold = character.StartingGold;
        }
        return player;
    }

    private static CharacterModel ResolveCharacter(string name)
        => ModelDb.AllCharacters.FirstOrDefault(c => string.Equals(c.Id.Entry, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown character '{name}'. Known: "
                + string.Join(", ", ModelDb.AllCharacters.Select(c => c.Id.Entry)));

    // ---- The drafting loop --------------------------------------------------

    private static void Visit(Player player, MapPointType type, PathContext path, State state)
    {
        switch (type)
        {
            case MapPointType.Monster:
            case MapPointType.Elite:
            case MapPointType.Boss:
                state.GoldEarned += Pay(player, type, state);
                Reward(player, type, state);
                break;
            case MapPointType.Shop:
                Shop(player, path, state);
                break;
            case MapPointType.RestSite:
                Camp(player, state);
                break;
            case MapPointType.Unknown:
                Event(player, state);
                break;
            case MapPointType.Treasure:
                // Chests hand over relics and potions. Neither changes the deck, so
                // the pipeline has nothing to decide here.
                break;
        }
    }

    private static int Pay(Player player, MapPointType type, State state)
    {
        var (min, max) = ActPath.GoldReward(type);
        if (max <= 0) return 0;
        var gold = state.Rng.NextInt(min, max + 1);
        if (state.Config.Ascension10) gold = (int)Math.Round(gold * Ascension10.GoldMultiplier);
        player.Gold += gold;
        return gold;
    }

    /// <summary>
    /// A post-combat card reward, generated by the game's own factory so the pool,
    /// the rarity odds and the multiplayer card constraints are the shipped ones.
    /// </summary>
    private static void Reward(Player player, MapPointType type, State state)
    {
        var room = type switch
        {
            MapPointType.Elite => RoomType.Elite,
            MapPointType.Boss => RoomType.Boss,
            _ => RoomType.Monster,
        };
        var cards = GenerateReward(player, room, state);
        if (cards.Count == 0) return;

        var chosen = BotBrain.ChooseCardReward(player, cards);
        if (chosen < 0 || chosen >= cards.Count)
        {
            state.RewardsSkipped++;
            return;
        }
        player.Deck.AddInternal(cards[chosen]);
        state.RewardsTaken++;
    }

    /// <summary>
    /// The shipped reward screen: <c>CardFactory.CreateForReward</c> with the
    /// options the game builds for that room. The upgrade roll is taken away from
    /// the factory (<c>NoUpgradeRoll</c>) and rolled here instead, because the
    /// factory's own roll asks <c>AscensionHelper</c>, which reports the
    /// non-ascension odds in a test process. A10 halves them.
    /// </summary>
    private static List<CardModel> GenerateReward(Player player, RoomType room, State state)
    {
        var options = CardCreationOptions.ForRoom(player, room)
            .WithFlags(CardCreationFlags.NoModifyHooks | CardCreationFlags.NoUpgradeRoll);
        var generated = CardFactory.CreateForReward(player, 3, options).Select(result => result.Card).ToList();
        foreach (var card in generated)
            if (state.Rng.NextFloat() <= player.RunState.CurrentActIndex * Ascension10.UpgradeOddsPerAct)
                Upgrade(card);
        return generated;
    }

    /// <summary>
    /// A shop visit. Gold is spent on removals, which is the only purchase that
    /// changes the deck: the price rule (100 + 50 per removal used under
    /// Inflation), the willingness-to-pay rule and the removal target are all the
    /// live ones, so a card the shop would refuse to remove is refused here too.
    /// </summary>
    private static void Shop(Player player, PathContext path, State state)
    {
        var candidates = player.Deck.Cards
            .Select((card, index) => (index, card, value: BotShopPlanner.RemovalValue(card, player)))
            .OrderByDescending(entry => entry.value)
            .ThenBy(entry => entry.index)
            .ToList();
        if (candidates.Count == 0) return;

        var target = candidates[0];
        if (target.value <= 0 || !target.card.IsRemovable) return;

        // Inflation is the A10 level that raises removal prices; without it the
        // live numbers are the game's 75 + 25 per removal.
        var cost = state.Config.Ascension10
            ? Ascension10.RemovalBaseCost + Ascension10.RemovalCostIncrease * player.ExtraFields.CardShopRemovalsUsed
            : 75 + 25 * player.ExtraFields.CardShopRemovalsUsed;
        var worth = Math.Max(0, target.value) * BotShopPlanner.GoldPerDeckValue;
        // The bar falls late in the run and drops to "does not make the deck worse"
        // at the last reachable shop — the same function the live planner calls,
        // fed the path this simulation actually walks.
        var threshold = RunDepth.ShopThresholdFactor(path.Act, lastShopBeforeBoss: !path.ShopAhead);
        if (worth <= cost * threshold || player.Gold < cost) return;

        player.Gold -= cost;
        player.ExtraFields.CardShopRemovalsUsed++;
        player.Deck.RemoveInternal(target.card);
        state.Removals++;
        state.GoldSpent += cost;
    }

    /// <summary>
    /// A camp. The live choice is heal-versus-smith and turns on HP this pipeline
    /// does not model, so the camp always smiths; the target is the production
    /// rule (<c>FromDeckForUpgrade</c> ranks by <see cref="BuildValue.UpgradeDelta"/>).
    /// </summary>
    private static void Camp(Player player, State state)
    {
        if (!state.Config.SmithAtCamp) return;
        var deck = player.Deck.Cards.ToList();
        var picked = BotBrain.SelectCards(player, deck, 1, 1, "FromDeckForUpgrade");
        var card = picked.FirstOrDefault();
        if (card is null || !card.IsUpgradable) return;
        Upgrade(card);
        state.Smiths++;
        state.Upgrades++;
    }

    /// <summary>
    /// An unknown room. The real event models are not driven here; what the
    /// pipeline needs from an event is the deck change it can force, so an event is
    /// modelled as a curse, a purse, a card or nothing.
    /// </summary>
    private static void Event(Player player, State state)
    {
        var roll = state.Rng.NextFloat();
        if (roll < state.Config.CurseEventShare)
        {
            var curse = RandomCurse(player, state);
            if (curse is null) return;
            player.Deck.AddInternal(curse);
            state.CursesAdded++;
            return;
        }
        if (roll < state.Config.CurseEventShare + 0.25)
        {
            var gold = (int)Math.Round(state.Rng.NextInt(20, 51) * (state.Config.Ascension10 ? Ascension10.GoldMultiplier : 1));
            player.Gold += gold;
            state.GoldEarned += gold;
            return;
        }
        if (roll < state.Config.CurseEventShare + 0.45)
        {
            // Event card offers are not encounter rewards, so they use the
            // non-combat options (RegularEncounter odds, no upgrade roll).
            var options = CardCreationOptions
                .ForNonCombatWithDefaultOdds([player.Character.CardPool])
                .WithFlags(CardCreationFlags.NoModifyHooks);
            var cards = CardFactory.CreateForReward(player, 3, options).Select(result => result.Card).ToList();
            var chosen = BotBrain.ChooseCardReward(player, cards);
            if (chosen < 0 || chosen >= cards.Count) { state.RewardsSkipped++; return; }
            player.Deck.AddInternal(cards[chosen]);
            state.RewardsTaken++;
        }
        // The remainder is the relic/potion side of events: no deck change.
    }

    private static CardModel? RandomCurse(Player player, State state)
    {
        var pool = ModelDb.CardPool<CurseCardPool>()
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .Where(card => !string.Equals(card.Id.Entry, Ascension10.StartingCurse, StringComparison.Ordinal))
            .ToList();
        if (pool.Count == 0) return null;
        var canonical = state.Rng.NextItem(pool);
        return canonical is null ? null : player.RunState.CreateCard(canonical, player);
    }

    /// <summary>
    /// The game's upgrade path, run on the live card. <c>CardCmd.Upgrade</c> is the
    /// UI entry point and needs a pile and a combat manager; the two internal steps
    /// it performs are what actually change the card, and are the same pair
    /// <see cref="CardProfile.Upgraded"/> uses on its clone.
    /// </summary>
    private static void Upgrade(CardModel card)
    {
        if (!card.IsUpgradable) return;
        try
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        catch
        {
            // A card whose upgrade cannot be realised is left as it is, the same
            // way the live planner treats an unbuildable upgrade preview.
        }
    }

    private static DeckSnapshot Snapshot(Player player, string stage, SimConfig config, ulong seed)
    {
        var deck = player.Deck.Cards.ToList();
        var reference = config.ReferenceFor(player.Character.Id.Entry, player.RunState.CurrentActIndex);
        var score = DeckScorer.Score(deck, reference, config, seed);
        return new DeckSnapshot(stage, deck.Count,
            deck.Count(card => card.Type == CardType.Curse), deck.Count(card => card.IsUpgraded),
            score, BuildTrace.Describe(player));
    }
}
