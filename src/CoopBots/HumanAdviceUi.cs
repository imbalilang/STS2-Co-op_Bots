using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

internal static class HumanAdviceUi
{
    // Temporarily disabled to avoid duplicate planning on the game thread.
    // Keep shared card/relic valuation available to the bots themselves.
    internal static bool Enabled => false;

    internal static Player? Human()
    {
        var state = RunManager.Instance.DebugOnlyGetState();
        if (state is null || !state.Players.Any(p => BotRegistry.IsBot(p.NetId))) return null;
        var me = LocalContext.GetMe(state);
        return me is not null && !BotRegistry.IsBot(me.NetId) ? me : null;
    }

    internal static void Show(Control screen, string text)
    {
        if (!Enabled) return;
        var label = screen.GetNodeOrNull<Label>("CoopBotsAdvice");
        if (label is null)
        {
            label = new Label { Name = "CoopBotsAdvice", ZIndex = 100, AnchorLeft = .5f, AnchorRight = .5f,
                OffsetLeft = -480, OffsetRight = 480, OffsetTop = 125, OffsetBottom = 225,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeFontSizeOverride("font_size", 22);
            label.AddThemeColorOverride("font_color", new Color(1, .9f, .65f));
            screen.AddChild(label);
        }
        label.Text = text;
    }

    internal static string RelicText(IReadOnlyList<RelicModel> relics, Player human)
    {
        var ranked = relics.Select(r => (Relic: r, Value: HumanCoopAdvisor.RelicValue(r, human)))
            .OrderByDescending(r => r.Value.Score).ToList();
        if (ranked.Count == 0) return "没有可选遗物。";
        if (ranked[0].Value.Score <= 0) return "遗物建议：这些遗物尚无可靠专属估值；请优先检查生存、启动与构筑配合。";
        var top = ranked[0];
        return $"值得优先考虑：{top.Relic.Title.GetFormattedText()} — {top.Value.Reason}。\n仅为已识别效果的估计，未识别遗物不代表更差；由你选择。";
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.RefreshOptions))]
internal static class HumanCardAdvicePatch
{
    private static void Postfix(NCardRewardSelectionScreen __instance, IReadOnlyList<CardCreationResult> options)
    {
        if (!HumanAdviceUi.Enabled) return;
        try
        {
            var human = HumanAdviceUi.Human();
            if (human is null) return;
            var ranked = options.Select(o => (Card: o.Card, Value: TeamCoordinator.CardValue(o.Card, human)))
                .OrderByDescending(o => o.Value.Score).ToList();
            HumanAdviceUi.Show(__instance, ranked.Count == 0 ? "暂无选牌建议" :
                $"选牌建议：{ranked[0].Card.Title} — {ranked[0].Value.Reason}。\n这是牌组结构估计，未完整模拟卡牌组合；由你选择。" );
        }
        catch (Exception error) { MegaCrit.Sts2.Core.Logging.Log.Warn("CoopBots card advice unavailable: " + error.GetType().Name); }
    }
}

[HarmonyPatch(typeof(NChooseARelicSelection), nameof(NChooseARelicSelection._Ready))]
internal static class HumanRelicAdvicePatch
{
    private static void Postfix(NChooseARelicSelection __instance, IReadOnlyList<RelicModel> ____relics)
    {
        if (!HumanAdviceUi.Enabled) return;
        try
        {
            var human = HumanAdviceUi.Human();
            if (human is not null) HumanAdviceUi.Show(__instance, HumanAdviceUi.RelicText(____relics, human));
        }
        catch (Exception error) { MegaCrit.Sts2.Core.Logging.Log.Warn("CoopBots relic advice unavailable: " + error.GetType().Name); }
    }
}
