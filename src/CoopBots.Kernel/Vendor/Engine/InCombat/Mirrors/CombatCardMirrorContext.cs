using MegaCrit.Sts2.Core.Models;
using CoopBots.Kernel.Vendor.Engine.Common;

namespace CoopBots.Kernel.Vendor.Engine.InCombat.Mirrors;

internal abstract class CombatCardMirrorContext<TBase> : CombatMirrorContext<TBase>
    where TBase : AbstractModel
{
    public required PredictedCard Card { get; init; }

    public CardModel OriginalCard => Card.Original;

    public CardModel PreviewCard => Card.Preview;

    public CardModel MutablePreviewCard => Card.MutablePreview;
}

internal abstract class CombatCardMirrorContext : CombatCardMirrorContext<AbstractModel>;
