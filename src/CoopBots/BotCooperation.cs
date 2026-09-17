using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Pause remains authoritative even when the team automatically accelerates.
public sealed class CooperationGate
{
    private object? _combat;
    public bool Paused { get; private set; }
    public void Observe(object? combat, int round)
    {
        if (!ReferenceEquals(combat, _combat)) { Reset(); _combat = combat; }
    }
    public void TogglePause() => Paused = !Paused;
    public void Reset() { _combat = null; Paused = false; }
}

internal static class BotCooperation
{
    internal static readonly CooperationGate Gate = new();
    private static Label? _status;
    private static Button? _pause;
    private static PanelContainer? _panel;
    private static OptionButton? _focus;
    private static Label? _advice;
    private static Label? _callout;
    private static DateTime _nextAdviceAt;
    private static string _enemySignature = "";
    private static List<uint?> _focusIds = new();
    internal static uint? FocusTarget { get; private set; }
    internal static string LastAction = "";
    internal static string Callout = "";
    private static long _calloutUntilMs;
    internal static void SetCallout(string text)
    {
        Callout = text ?? "";
        _calloutUntilMs = System.Environment.TickCount64 + 6000;
    }

    internal static Player? Leader(RunState state, ulong hostId) =>
        state.Players.FirstOrDefault(p => !BotRegistry.IsBot(p.NetId));

    internal static void Refresh(RunState state)
    {
        var combat = state.Players.FirstOrDefault()?.Creature.CombatState;
        Gate.Observe(combat, combat?.RoundNumber ?? 0);
        var isHost = RunManager.Instance.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host;
        var humansFinished = HumansFinished(state);
        var run = NRun.Instance;
        if (run is null || !state.Players.Any(p => BotRegistry.IsBot(p.NetId))) return;
        if (_panel is not null && GodotObject.IsInstanceValid(_panel) && _panel.GetParent() != run)
        {
            _panel.QueueFree(); _panel = null;
        }
        if (_panel is null || !GodotObject.IsInstanceValid(_panel))
        {
            var chinese = BotUiTheme.Chinese();
            _panel = BotUiTheme.Panel("CoopBotsCombatControls");
            _panel.ZIndex = 100;
            _panel.AnchorLeft = 1; _panel.AnchorRight = 1;
            _panel.OffsetLeft = -460; _panel.OffsetRight = -20;
            _panel.OffsetTop = 105; _panel.OffsetBottom = 105;
            _panel.GrowHorizontal = Control.GrowDirection.Begin;
            _panel.Theme = run.Theme;
            var margin = BotUiTheme.Margin(12);
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 8);
            margin.AddChild(column);
            // The callout asks the player for a specific hit, so it is the first
            // thing seen rather than a line inside the status block.
            _callout = BotUiTheme.Text(string.Empty, 15, BotUiTheme.Accent);
            _callout.Name = "CoopBotsCallout";
            _callout.Visible = false;
            _callout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _callout.CustomMinimumSize = new Vector2(420, 0);
            _callout.MouseFilter = Control.MouseFilterEnum.Ignore;
            column.AddChild(_callout);
            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 10);
            header.AddChild(BotUiTheme.Text("Co-op Bots", 13, BotUiTheme.Muted));
            _status = BotUiTheme.Text(string.Empty, 13, BotUiTheme.Ink);
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(_status);
            column.AddChild(header);
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 8);
            _pause = new Button { Text = chinese ? "暂停" : "Pause", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _pause.Pressed += () => { Gate.TogglePause(); if (!Gate.Paused) BotRuntime.ResumeAfterPause(); };
            buttons.AddChild(_pause);
            column.AddChild(buttons);
            _focus = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _focus.ItemSelected += index => FocusTarget = index >= 0 && index < _focusIds.Count ? _focusIds[(int)index] : null;
            column.AddChild(_focus);
            _advice = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Muted);
            _advice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _advice.MouseFilter = Control.MouseFilterEnum.Ignore;
            column.AddChild(_advice);
            _panel.AddChild(margin);
            run.AddChild(_panel);
            DraggablePanel.Attach(_panel, header);
        }
        var active = combat is not null && CombatManager.Instance.IsInProgress;
        if (_callout is not null && GodotObject.IsInstanceValid(_callout))
        {
            _callout.Visible = Callout.Length > 0 && System.Environment.TickCount64 < _calloutUntilMs;
            if (_callout.Visible) _callout.Text = "[Bot 请求] " + Callout;
        }
        var mapOpen = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.IsOpen == true;
        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
        _panel.Visible = active && !mapOpen || HumanAdviceUi.Enabled && (mapOpen || relics is not null);
        _advice!.Visible = HumanAdviceUi.Enabled;
        _pause!.Visible = _focus!.Visible = active && !mapOpen && isHost;
        // The button is the state: a static "暂停" label said nothing about
        // whether the team was already paused.
        _pause.Text = Gate.Paused
            ? (BotUiTheme.Chinese() ? "继续" : "Resume")
            : (BotUiTheme.Chinese() ? "暂停" : "Pause");
        if (HumanAdviceUi.Enabled && _panel.Visible && DateTime.UtcNow >= _nextAdviceAt && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null
            && RunManager.Instance.ActionQueueSet.IsEmpty)
        {
            _nextAdviceAt = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                var human = MultiHumanCooperation.LocalHuman(state.Players, RunManager.Instance.NetService.NetId);
                if (human is not null)
                {
                    if (mapOpen) _advice!.Text = HumanCoopAdvisor.RouteAdvice(state, human);
                    else if (!active && relics is not null) _advice!.Text = HumanAdviceUi.RelicText(relics, human);
                    else if (human.Creature.IsAlive && !CombatManager.Instance.IsPlayerReadyToEndTurn(human))
                    {
                        var move = HumanCoopAdvisor.CombatAdvice(human, state.Players);
                        _advice!.Text = move.HasValue ? $"建议：{move.Value.Card.Title}" +
                            (move.Value.Target?.Monster is { } monster ? " → " + monster.Title.GetFormattedText()
                                : move.Value.Target?.Player is { } ally ? " → " + (ally == human ? "你" : BotRegistry.IsBot(ally.NetId)
                                    ? BotRegistry.DisplayName(ally.NetId) : "真人队友") : "") +
                            (HumanCoopAdvisor.SetupValue(move.Value, state.Players) > 0 ? "\n可先施加状态，为团队铺垫。" : "\n按当前手牌、能量与生存压力估计。")
                            : "当前没有明确的出牌建议；队友会自主行动。";
                    }
                    else _advice!.Text = "队友将按每次行动后的实际局面重新规划。";
                }
            }
            catch (Exception) { _advice!.Text = "当前局面暂无法可靠估值，请自行决策。"; }
        }
        if (mapOpen || !active) _status!.Text = "队友建议（仅供参考，由你决定）";
        if (!active || mapOpen) return;
        if (!isHost)
        {
            _status!.Text = "AI 由房主统一调度。\n" +
                (humansFinished ? "真人均已结束回合：AI 收尾。" : "AI 全队按难度节奏分步规划。") +
                "暂停和集火由房主操作。";
            return;
        }
        var enemies = combat!.HittableEnemies.Where(e => e.IsAlive).ToList();
        var signature = string.Join(',', enemies.Select(e => e.CombatId));
        if (_enemySignature != signature || _focus!.ItemCount == 0)
        {
            _enemySignature = signature;
            _focus!.Clear(); _focusIds = new() { null };
            _focus.AddItem("集火：队友自行判断");
            foreach (var enemy in enemies)
            {
                _focusIds.Add(enemy.CombatId);
                _focus.AddItem("集火：" + enemy.Monster!.Title.GetFormattedText());
            }
            var selected = _focusIds.IndexOf(FocusTarget);
            if (selected < 0) { FocusTarget = null; selected = 0; }
            _focus.Select(selected);
        }
        var zh = BotUiTheme.Chinese();
        _pause!.Text = Gate.Paused ? (zh ? "继续" : "Resume") : (zh ? "暂停" : "Pause");
        // Planning is bounded in every phase: no long think, and no replay of a
        // stored script. Say that rather than promising a full-fight optimum or
        // back-to-back scripted play the team no longer attempts.
        _status!.Text = (Gate.Paused
            ? (zh ? "已暂停：不再提交新的 Bot 行动" : "Paused: no new bot actions")
            : humansFinished
                ? (zh ? "机器人行动中" : "Bots are acting")
                : (zh ? "协作节奏：等待真人决策" : "Co-op pace: waiting for the humans"))
            + (string.IsNullOrEmpty(LastAction) ? "" : "\n" + LastAction);
    }

    internal static bool HumansFinished(RunState state) => MultiHumanCooperation.HumansReady(state.Players,
        CombatManager.Instance.IsPlayerReadyToEndTurn);

    /// <summary>
    /// Every human is dead, so the rest of this fight belongs to the team alone.
    /// Kept as a plain predicate for callers that describe the state; it no longer
    /// changes the planner budget (the bounded search runs in every phase).
    /// </summary>
    internal static bool AllHumansDown(IRunState state)
        => state.Players.Where(p => !BotRegistry.IsBot(p.NetId)).All(p => !p.Creature.IsAlive);

    internal static void Reset() { Gate.Reset(); LastAction = ""; FocusTarget = null; _enemySignature = ""; _nextAdviceAt = DateTime.MinValue; Callout = ""; _calloutUntilMs = 0; }
}

