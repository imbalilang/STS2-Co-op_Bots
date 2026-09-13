using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.InCombat.Extensions;

namespace CoopBots.Kernel.Vendor;

/// <summary>
/// Root-scoped, read-only projections of canonical generation pools. Random selection and
/// prediction-owned card creation deliberately remain branch-local.
/// </summary>
internal sealed class RootCombatCardGenerationPoolSnapshot
{
    private sealed record NativeCharacterAttackPoolEntry(
        object CharacterIdentity,
        CardPoolModel Pool,
        object AllCardsIdentity,
        CardModel[] EligibleCards);

    private static readonly System.Reflection.Assembly NativeModelAssembly =
        typeof(CardModel).Assembly;
    private readonly CardPoolModel? _canonicalColorlessPool;
    private readonly object? _canonicalColorlessCardsIdentity;
    private readonly CardMultiplayerConstraint _multiplayerConstraint;
    private readonly IReadOnlyDictionary<Player, CardModel[]> _eligibleColorlessByPlayer;
    private readonly IReadOnlyDictionary<Player, NativeCharacterAttackPoolEntry>
        _eligibleCharacterAttacksByPlayer;

    private RootCombatCardGenerationPoolSnapshot(
        CardPoolModel? canonicalColorlessPool,
        object? canonicalColorlessCardsIdentity,
        CardMultiplayerConstraint multiplayerConstraint,
        IReadOnlyDictionary<Player, CardModel[]> eligibleColorlessByPlayer,
        IReadOnlyDictionary<Player, NativeCharacterAttackPoolEntry>
            eligibleCharacterAttacksByPlayer)
    {
        _canonicalColorlessPool = canonicalColorlessPool;
        _canonicalColorlessCardsIdentity = canonicalColorlessCardsIdentity;
        _multiplayerConstraint = multiplayerConstraint;
        _eligibleColorlessByPlayer = eligibleColorlessByPlayer;
        _eligibleCharacterAttacksByPlayer = eligibleCharacterAttacksByPlayer;
    }

    public static RootCombatCardGenerationPoolSnapshot Capture(
        IReadOnlyList<Player> players,
        CardMultiplayerConstraint multiplayerConstraint)
    {
        CardPoolModel colorlessPool = ModelDb.CardPool<ColorlessCardPool>();
        IEnumerable<CardModel> allCards = colorlessPool.AllCards;
        if (colorlessPool.GetType() != typeof(ColorlessCardPool)
            || allCards is not CardModel[] canonicalCards
            || canonicalCards.Any(card =>
                card.IsMutable || card.GetType().Assembly != typeof(CardModel).Assembly))
        {
            return new RootCombatCardGenerationPoolSnapshot(
                canonicalColorlessPool: null,
                canonicalColorlessCardsIdentity: null,
                multiplayerConstraint,
                new Dictionary<Player, CardModel[]>(ReferenceEqualityComparer.Instance),
                new Dictionary<Player, NativeCharacterAttackPoolEntry>(
                    ReferenceEqualityComparer.Instance));
        }

        Dictionary<Player, CardModel[]> eligibleByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        Dictionary<Player, NativeCharacterAttackPoolEntry> eligibleCharacterAttacksByPlayer =
            new(players.Count, ReferenceEqualityComparer.Instance);
        foreach (Player player in players)
        {
            eligibleByPlayer.Add(
                player,
                player.GetUnlockedCards(colorlessPool, multiplayerConstraint)
                    .FilterForCombatAndPlayerCount(multiplayerConstraint)
                    .ToArray());
            if (TryCaptureNativeCharacterAttackPool(
                    player,
                    multiplayerConstraint,
                    out NativeCharacterAttackPoolEntry characterAttacks))
            {
                eligibleCharacterAttacksByPlayer.Add(player, characterAttacks);
            }
        }

        return new RootCombatCardGenerationPoolSnapshot(
            colorlessPool,
            allCards,
            multiplayerConstraint,
            eligibleByPlayer,
            eligibleCharacterAttacksByPlayer);
    }

    public bool TryGetEligibleCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (_canonicalColorlessPool != null
            && ReferenceEquals(cardPool, _canonicalColorlessPool)
            && cardPool.GetType() == typeof(ColorlessCardPool)
            && ReferenceEquals(cardPool.AllCards, _canonicalColorlessCardsIdentity)
            && multiplayerConstraint == _multiplayerConstraint
            && _eligibleColorlessByPlayer.TryGetValue(player, out CardModel[]? eligible))
        {
            cards = eligible;
            return true;
        }

        cards = [];
        return false;
    }

    public bool TryGetEligibleCharacterAttackCards(
        Player player,
        CardPoolModel cardPool,
        CardMultiplayerConstraint multiplayerConstraint,
        out IReadOnlyList<CardModel> cards)
    {
        if (multiplayerConstraint == _multiplayerConstraint
            && _eligibleCharacterAttacksByPlayer.TryGetValue(
                player,
                out NativeCharacterAttackPoolEntry? entry)
            && ReferenceEquals(player.Character, entry.CharacterIdentity)
            && ReferenceEquals(cardPool, entry.Pool)
            && ReferenceEquals(player.Character.CardPool, entry.Pool)
            && !player.Character.IsMutable
            && player.Character.GetType().Assembly == NativeModelAssembly
            && !cardPool.IsMutable
            && !cardPool.IsMock
            && cardPool.GetType().Assembly == NativeModelAssembly
            && ReferenceEquals(cardPool, ModelDb.GetById<CardPoolModel>(cardPool.Id))
            && ReferenceEquals(cardPool.AllCards, entry.AllCardsIdentity))
        {
            cards = entry.EligibleCards;
            return true;
        }

        cards = [];
        return false;
    }

    private static bool TryCaptureNativeCharacterAttackPool(
        Player player,
        CardMultiplayerConstraint multiplayerConstraint,
        out NativeCharacterAttackPoolEntry entry)
    {
        entry = null!;
        CardPoolModel cardPool = player.Character.CardPool;
        if (player.Character.GetType().Assembly != NativeModelAssembly
            || player.Character.IsMutable
            || !TryGetNativeCanonicalCharacterPoolCards(cardPool, out CardModel[] allCards))
        {
            return false;
        }

        // Preserve Metamorphosis' source order exactly: unlock filtering, then Attack,
        // then the upstream in-combat and player-count predicates.
        CardModel[] eligibleCards = player
            .GetUnlockedCards(cardPool, multiplayerConstraint)
            .Where(static card => card.Type == CardType.Attack)
            .FilterForCombatAndPlayerCount(multiplayerConstraint)
            .ToArray();
        HashSet<CardModel> canonicalPoolCards = new(
            allCards,
            ReferenceEqualityComparer.Instance);
        if (eligibleCards.Any(card =>
                !canonicalPoolCards.Contains(card)
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance)))
        {
            return false;
        }

        entry = new NativeCharacterAttackPoolEntry(
            player.Character,
            cardPool,
            allCards,
            eligibleCards);
        return true;
    }

    internal static bool CanCacheNativeCharacterPoolForTesting(CardPoolModel cardPool)
        => TryGetNativeCanonicalCharacterPoolCards(cardPool, out _);

    private static bool TryGetNativeCanonicalCharacterPoolCards(
        CardPoolModel cardPool,
        out CardModel[] cards)
    {
        if (cardPool.GetType().Assembly == NativeModelAssembly
            && !cardPool.IsMutable
            && !cardPool.IsMock
            && !cardPool.IsColorless
            && ReferenceEquals(cardPool, ModelDb.GetById<CardPoolModel>(cardPool.Id))
            && cardPool.AllCards is CardModel[] allCards
            && AllCardsAreNativeCanonical(allCards))
        {
            cards = allCards;
            return true;
        }

        cards = [];
        return false;
    }

    private static bool AllCardsAreNativeCanonical(IEnumerable<CardModel> cards)
    {
        foreach (CardModel card in cards)
        {
            if (card is null
                || card.GetType().Assembly != NativeModelAssembly
                || card.IsMutable
                || !ReferenceEquals(card, card.CanonicalInstance)
                || !ReferenceEquals(card, ModelDb.GetById<CardModel>(card.Id)))
            {
                return false;
            }
        }
        return true;
    }
}
