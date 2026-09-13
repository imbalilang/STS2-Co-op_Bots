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
            _panel = new PanelContainer { Name = "CoopBotsCombatControls", ZIndex = 100,
                AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -460, OffsetRight = -20,
                OffsetTop = 105, OffsetBottom = 200, MouseFilter = Control.MouseFilterEnum.Stop };
            var column = new VBoxContainer();
            // The callout asks the player for a specific hit, so it must be the
            // first thing seen rather than a line inside the status block: its
            // own larger, coloured label above everything else.
            _callout = new Label { Name = "CoopBotsCallout", Visible = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(420, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
            _callout.AddThemeColorOverride("font_color", new Color(1f, 0.78f, 0.20f));
            _callout.AddThemeFontSizeOverride("font_size", 19);
            column.AddChild(_callout);
            _status = new Label { Text = "Co-op Bots", AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(420, 45) };
            column.AddChild(_status);
            var buttons = new HBoxContainer();
            _pause = new Button { Text = "暂停" };
            _pause.Pressed += () => { Gate.TogglePause(); if (!Gate.Paused) BotRuntime.ResumeAfterPause(); };
            buttons.AddChild(_pause); column.AddChild(buttons);
            _focus = new OptionButton();
            _focus.ItemSelected += index => FocusTarget = index >= 0 && index < _focusIds.Count ? _focusIds[(int)index] : null;
            column.AddChild(_focus);
            _advice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(420, 65), MouseFilter = Control.MouseFilterEnum.Ignore };
            column.AddChild(_advice);
            _panel.AddChild(column); run.AddChild(_panel);
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
                (humansFinished ? "真人均已结束回合：AI 快速收尾。" : "AI 全队约每秒一次行动；真人均结束后加速。") +
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
        _pause!.Text = Gate.Paused ? "继续" : "暂停";
        _status!.Text = Gate.Paused ? "已暂停：不再提交新的 Bot 行动"
            : (humansFinished ? "快速收尾：全队每 0.2 秒一次行动" : "协作节奏：全队每秒一次行动") +
                (string.IsNullOrEmpty(LastAction) ? "" : "\n" + LastAction);
    }

    internal static bool HumansFinished(RunState state) => MultiHumanCooperation.HumansReady(state.Players,
        CombatManager.Instance.IsPlayerReadyToEndTurn);

    internal static void Reset() { Gate.Reset(); LastAction = ""; FocusTarget = null; _enemySignature = ""; _nextAdviceAt = DateTime.MinValue; Callout = ""; _calloutUntilMs = 0; }
}

