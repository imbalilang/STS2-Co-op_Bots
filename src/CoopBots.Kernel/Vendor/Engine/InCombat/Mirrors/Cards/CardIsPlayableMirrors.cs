using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using CoopBots.Kernel.Vendor.Engine.Common;
using CoopBots.Kernel.Vendor.Engine.Common.Mirrors;
using CoopBots.Kernel.Vendor.Engine.InCombat.Simulation;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors.Cards;

using Registry = MethodMirrorRegistry<CardModel, CardIsPlayableMirrorContext, bool>;

// Mirrors CardModel.IsPlayable while replacing vanilla overrides that read live combat state.
internal static class CardIsPlayableMirrors
{
    private static readonly MethodInfo IsPlayableGetterMethod =
        AccessTools.PropertyGetter(typeof(CardModel), "IsPlayable")
        ?? throw new UnreachableException("Could not find CardModel.IsPlayable getter.");

    private static readonly MirrorMethodSpec IsPlayable = new(
        typeof(CardModel),
        IsPlayableGetterMethod.Name,
        BindingFlags.Instance | BindingFlags.NonPublic,
        []);

    private static readonly Registry Registry = CreateRegistry();

    public static bool Invoke(CombatPredictionSimulator simulator, PredictedCard card)
    {
        var context = new CardIsPlayableMirrorContext
        {
            Simulator = simulator,
            Card = card
        };

        // CardModel.IsPlayable is `=> true` unless a card overrides it, so `true` is the exact
        // answer for every non-overriding card. Going through Invoke (instead of falling back to
        // the original getter) means an override we have not mirrored is reported as
        // MethodNotMirrored instead of silently answering from live state: the getter reads the
        // owner's real hand, and on a detached preview clone it usually reports no owner at all,
        // so the search would treat a conditional card as always playable and only find out at
        // deployment.
        return Registry.Invoke(card.Preview, context, defaultResult: true).Value;
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(IsPlayable);

        registry.Register<Clash>(HandleClash);
        registry.Register<GrandFinale>(HandleGrandFinale);
        registry.Register<HighFive>(HandleHighFive);

        return registry;
    }

    private static bool HandleClash(Clash card, CardIsPlayableMirrorContext context)
    {
        return context.State.GetPlayerCombatState(card.Owner).Hand.Cards
            .All(handCard => handCard.Preview.Type == CardType.Attack);
    }

    private static bool HandleGrandFinale(GrandFinale card, CardIsPlayableMirrorContext context)
    {
        return context.State.GetPlayerCombatState(card.Owner).DrawPile.IsEmpty;
    }

    private static bool HandleHighFive(HighFive card, CardIsPlayableMirrorContext context)
    {
        return context.State.GetOsty(card.Owner) is { } osty && context.State.GetCreature(osty).IsAlive;
    }
}

internal sealed class CardIsPlayableMirrorContext : CombatCardMirrorContext<CardModel>
{
    protected override AbstractModel GetDispatchSource(CardModel _) => OriginalCard;
}
