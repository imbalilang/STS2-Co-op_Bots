using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
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
    private static VBoxContainer? _body;
    private static Button? _collapse;
    // Collapsed is a presentation preference, not fight state: it survives every
    // room change and panel rebuild for the whole process, and Reset() never
    // touches it. A callout or a Refresh must not pop the panel back open. It
    // starts true so a run opens with a title bar instead of a card over the
    // board; expanding it lasts until the next run starts.
    private static bool _collapsed = true;
    private static DateTime _nextAdviceAt;
    private static string _enemySignature = "";
    private static List<uint?> _focusIds = new();
    // Top of the panel in the corner it prefers; it leaves that spot only to get
    // out of an enemy intent's way, and returns as soon as it can.
    private const float PanelTop = 105f;
    private const float PanelWidth = 440f;
    private static float _panelTop = PanelTop;
    // Once the player drags the fight panel it owns the placement: the automatic
    // intent dodge stops moving it. Only an actual rebuild clears this; a reset on
    // a run or combat change leaves the on-screen panel where the player put it.
    private static bool _panelManual;
    private static string _intentSignature = "";
    internal static uint? FocusTarget { get; private set; }
    internal static string LastAction = "";
    internal static string Callout = "";
    private static long _calloutUntilMs;
    private static ulong _calloutActor;
    private static uint _calloutTarget;
    private static string[] _calloutCards = Array.Empty<string>();

    /// <summary>
    /// A callout carries who should act and on what, not just the sentence: the
    /// client holding that hand points at the cards with it.
    /// </summary>
    internal static void SetCallout(string text, ulong actor = 0, uint target = 0, IReadOnlyList<string>? cards = null)
    {
        Callout = text ?? "";
        _calloutActor = actor;
        _calloutTarget = target;
        _calloutCards = cards is null ? Array.Empty<string>() : cards.ToArray();
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
            // A fresh panel has not been placed by hand yet, so the intent dodge
            // is free to move it and it keeps its default corner.
            _panelManual = false;
            var chinese = BotUiTheme.Chinese();
            _panel = BotUiTheme.Panel("CoopBotsCombatControls");
            _panel.ZIndex = 100;
            _panel.AnchorLeft = 1; _panel.AnchorRight = 1;
            _panel.OffsetLeft = -460; _panel.OffsetRight = -20;
            _panel.OffsetTop = PanelTop; _panel.OffsetBottom = PanelTop;
            // The begin (left) edge is the one we hold still while the card
            // collapses and expands, so a later minimum-size recompute grows the
            // card to the right instead of dragging the title bar sideways.
            _panel.GrowHorizontal = Control.GrowDirection.End;
            _panel.Theme = run.Theme;
            var margin = BotUiTheme.Margin(12);
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 8);
            margin.AddChild(column);
            // The header is the drag grip and stays on screen when the panel is
            // collapsed; only the label and the toggle live here so a collapsed
            // panel is a compact title bar instead of an empty 440px card.
            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 10);
            var title = BotUiTheme.Text("Co-op Bots", 13, BotUiTheme.Muted);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(title);
            _collapse = new Button { Text = string.Empty, MouseFilter = Control.MouseFilterEnum.Stop };
            _collapse.Pressed += () => { _collapsed = !_collapsed; ApplyCollapsedState(); };
            header.AddChild(_collapse);
            column.AddChild(header);
            _body = new VBoxContainer();
            _body.AddThemeConstantOverride("separation", 8);
            column.AddChild(_body);
            // The callout asks the player for a specific hit, so it is the first
            // thing seen rather than a line inside the status block.
            _callout = BotUiTheme.Text(string.Empty, 15, BotUiTheme.Accent);
            _callout.Name = "CoopBotsCallout";
            _callout.Visible = false;
            _callout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _callout.CustomMinimumSize = new Vector2(420, 0);
            _callout.MouseFilter = Control.MouseFilterEnum.Ignore;
            _body.AddChild(_callout);
            _status = BotUiTheme.Text(string.Empty, 13, BotUiTheme.Ink);
            _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _body.AddChild(_status);
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 8);
            _pause = new Button { Text = chinese ? "暂停" : "Pause", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _pause.Pressed += () => { Gate.TogglePause(); if (!Gate.Paused) BotRuntime.ResumeAfterPause(); };
            buttons.AddChild(_pause);
            _body.AddChild(buttons);
            _focus = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _focus.ItemSelected += index => FocusTarget = index >= 0 && index < _focusIds.Count ? _focusIds[(int)index] : null;
            _body.AddChild(_focus);
            _advice = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Muted);
            _advice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _advice.MouseFilter = Control.MouseFilterEnum.Ignore;
            _body.AddChild(_advice);
            _panel.AddChild(margin);
            run.AddChild(_panel);
            DraggablePanel.Attach(_panel, header, () => _panelManual = true);
            ApplyCollapsedState();
        }
        var active = combat is not null && CombatManager.Instance.IsInProgress;
        if (_callout is not null && GodotObject.IsInstanceValid(_callout))
        {
            _callout.Visible = Callout.Length > 0 && System.Environment.TickCount64 < _calloutUntilMs;
            if (_callout.Visible) _callout.Text = "[Bot 请求] " + Callout;
        }
        var mapOpen = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance?.IsOpen == true;
        var relics = RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics;
        UpdateCalloutArrow(state, combat, active);
        _panel.Visible = active && !mapOpen || HumanAdviceUi.Enabled && (mapOpen || relics is not null);
        _advice!.Visible = HumanAdviceUi.Enabled;
        _pause!.Visible = _focus!.Visible = active && !mapOpen && isHost;
        ClearOfIntents(active && _panel.Visible ? combat : null);
        // The button is the state: a static "暂停" label said nothing about
        // whether the team was already paused.
        _pause.Text = Gate.Paused
            ? (BotUiTheme.Chinese() ? "继续" : "Resume")
            : (BotUiTheme.Chinese() ? "暂停" : "Pause");
        // A collapsed panel hides the advice line, so do not spend a valuation on
        // it until the player expands the body again.
        if (HumanAdviceUi.Enabled && !_collapsed && _panel.Visible && DateTime.UtcNow >= _nextAdviceAt && RunManager.Instance.ActionExecutor.CurrentlyRunningAction is null
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

    /// <summary>
    /// Applies the remembered collapse preference to whatever panel is currently
    /// on screen. Called when the toggle is pressed and whenever Refresh rebuilds
    /// the panel, so a room change never loses the player's choice.
    /// </summary>
    private static void ApplyCollapsedState()
    {
        if (_panel is null || !GodotObject.IsInstanceValid(_panel)) return;
        if (_collapse is not null && GodotObject.IsInstanceValid(_collapse))
            _collapse.Text = _collapsed
                ? (BotUiTheme.Chinese() ? "展开" : "Expand")
                : (BotUiTheme.Chinese() ? "收起" : "Collapse");
        if (_body is not null && GodotObject.IsInstanceValid(_body))
            _body.Visible = !_collapsed;
        ApplyCollapsedLayout();
        // A container recomputes its minimum size a frame after a child is hidden
        // or shown, so settle the real size once more to make the card shrink to
        // the header (or grow back) rather than keep a stale box.
        Callable.From(ApplyCollapsedLayout).CallDeferred();
    }

    /// <summary>
    /// Sizes the panel to the current body visibility on one path for both
    /// directions. Collapsing shrinks the card to its header minimum in both
    /// dimensions; expanding restores the full width. GrowHorizontal.End holds
    /// the left edge fixed, so the corner never needs restoring.
    /// </summary>
    private static void ApplyCollapsedLayout()
    {
        if (_panel is null || !GodotObject.IsInstanceValid(_panel)) return;
        // A zero axis means "the minimum the panel currently needs"; width is
        // forced to PanelWidth only when expanded. set_size already clamps to
        // the combined minimum, so no ResetSize is needed.
        _panel.Size = new Vector2(_collapsed ? 0 : PanelWidth, 0);
    }

    /// <summary>
    /// A new run opens with the panel collapsed, so it does not cover the board
    /// before the player has asked for it. Deliberately not part of
    /// <see cref="Reset"/>: that also runs on every combat change, and the
    /// player's expand has to survive the rest of the run they made it in.
    /// </summary>
    internal static void OnRunStart() => _collapsed = true;

    internal static bool HumansFinished(RunState state) => MultiHumanCooperation.HumansReady(state.Players,
        CombatManager.Instance.IsPlayerReadyToEndTurn);

    /// <summary>
    /// Keeps the callout's arrow in step with the callout itself: it is drawn for
    /// the peer whose hand it is, and it goes away when they play the card, when
    /// the callout expires, or when the target dies.
    /// </summary>
    private static void UpdateCalloutArrow(RunState state, ICombatState? combat, bool active)
    {
        if (!active || Callout.Length == 0 || System.Environment.TickCount64 >= _calloutUntilMs)
        {
            CalloutArrow.Clear();
            return;
        }
        var target = combat?.Enemies.FirstOrDefault(enemy => enemy.CombatId is not null && enemy.CombatId != 0
            && enemy.CombatId == _calloutTarget && enemy.IsAlive);
        if (target is null) { CalloutArrow.Clear(); return; }
        var local = MultiHumanCooperation.LocalHuman(state.Players, RunManager.Instance.NetService.NetId);
        var mine = local is not null && local.NetId == _calloutActor;
        CalloutArrow.Update(mine ? local : null, target, mine ? _calloutCards : Array.Empty<string>());
    }

    /// <summary>
    /// Our card is the intrusive element on this screen; an enemy's attack tell is
    /// not. When a tall boss stands far enough right that its intent bubbles reach
    /// our corner, the panel drops below them instead of covering them. Within one
    /// round it only ever moves down, so a bobbing intent icon cannot make it
    /// flicker; a new round re-opens the question, so the corner comes back once
    /// the enemies no longer reach it.
    /// </summary>
    private static void ClearOfIntents(ICombatState? combat)
    {
        if (_panel is null || !GodotObject.IsInstanceValid(_panel)) return;
        // The player placed the panel themselves; it stays where they put it.
        if (_panelManual) return;
        try { DodgeIntents(combat); }
        // Combat nodes can be torn down between the state read and the queries
        // above; the panel then just keeps its corner, as it did before.
        catch (Exception) { _intentSignature = string.Empty; _panelTop = PanelTop; }
        if (_panel is null || !GodotObject.IsInstanceValid(_panel)) return;
        if (Mathf.Abs(_panel.OffsetTop - _panelTop) > 0.5f)
            _panel.OffsetTop = _panel.OffsetBottom = _panelTop;
    }

    private static void DodgeIntents(ICombatState? combat)
    {
        var signature = combat is null
            ? string.Empty
            : combat.RoundNumber + ":" + string.Join(',', combat.Enemies.Select(enemy => enemy.CombatId));
        if (signature != _intentSignature)
        {
            _intentSignature = signature;
            _panelTop = PanelTop;
        }
        var rect = _panel!.GetGlobalRect();
        if (IntentBubbles(combat, rect) is { } band
            && band.Position.Y < rect.End.Y && band.End.Y > rect.Position.Y + 12f)
        {
            var candidate = Mathf.Ceil((_panelTop + band.End.Y + 12f - rect.Position.Y) / 4f) * 4f;
            var limit = Mathf.Max(PanelTop, _panel.GetViewportRect().Size.Y - rect.Size.Y - 12f);
            if (candidate > _panelTop + 2f && candidate <= limit) _panelTop = candidate;
        }
    }

    /// <summary>
    /// The union of the intent bubbles that could end up behind the panel: only
    /// the enemies standing in the panel's own column, so the panel moves just
    /// far enough to clear the ones it would actually hide.
    /// </summary>
    private static Rect2? IntentBubbles(ICombatState? combat, Rect2 panel)
    {
        var room = NCombatRoom.Instance;
        if (combat is null || room is null) return null;
        Rect2? band = null;
        foreach (var enemy in combat.Enemies)
        {
            var node = room.GetCreatureNode(enemy);
            if (node is null || !GodotObject.IsInstanceValid(node)) continue;
            var container = node.IntentContainer;
            if (container is null || !GodotObject.IsInstanceValid(container) || !container.Visible) continue;
            foreach (var child in container.GetChildren())
            {
                if (child is not Control bubble || !bubble.Visible) continue;
                var rect = bubble.GetGlobalRect();
                if (rect.Size.X <= 1f || rect.Size.Y <= 1f) continue;
                if (rect.End.X <= panel.Position.X || rect.Position.X >= panel.End.X) continue;
                band = band.HasValue ? band.Value.Merge(rect) : rect;
            }
        }
        return band;
    }

    /// <summary>
    /// Every human is dead, so the rest of this fight belongs to the team alone.
    /// Kept as a plain predicate for callers that describe the state; it no longer
    /// changes the planner budget (the bounded search runs in every phase).
    /// </summary>
    internal static bool AllHumansDown(IRunState state)
        => state.Players.Where(p => !BotRegistry.IsBot(p.NetId)).All(p => !p.Creature.IsAlive);

    internal static void Reset() { Gate.Reset(); LastAction = ""; FocusTarget = null; _enemySignature = ""; _nextAdviceAt = DateTime.MinValue; Callout = ""; _calloutUntilMs = 0; _calloutActor = 0; _calloutTarget = 0; _calloutCards = Array.Empty<string>(); CalloutArrow.Clear(); _intentSignature = ""; _panelTop = PanelTop; }
}

