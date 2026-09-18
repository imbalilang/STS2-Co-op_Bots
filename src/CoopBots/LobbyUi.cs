using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace CoopBots;

/// <summary>
/// The lobby panel: pick a character and a difficulty, add or remove bots, and
/// see the team assembled so far. It inherits the screen's theme so labels and
/// buttons match the game, and draws its colours from the cover art, so it reads
/// as part of the mod rather than a debug overlay.
/// </summary>
public static class LobbyUi
{
    private const string PanelName = "CoopBotsPanel";

    private static PanelContainer? panel;
    private static StartRunLobby? bound;
    private static Action? refresh;

    /// <summary>
    /// The character-select screen is one cached node that every room — and
    /// singleplayer — reuses, so the panel is bound to whichever lobby is open
    /// right now instead of being built once per process. Building it once left
    /// the previous room's seat count and roster on screen after leaving a room
    /// and hosting again, and put this multiplayer panel on the singleplayer
    /// screen as well.
    /// </summary>
    public static void Attach(NCharacterSelectScreen screen)
    {
        var lobby = screen.Lobby;
        if (lobby is null || lobby.NetService.Type == NetGameType.Singleplayer)
        {
            Detach(screen);
            return;
        }

        if (ReferenceEquals(bound, lobby) && panel is not null && GodotObject.IsInstanceValid(panel))
        {
            refresh?.Invoke();
            return;
        }

        Detach(screen);
        Build(screen, lobby);
    }

    /// <summary>Drops the panel and its lobby reference when the screen closes.</summary>
    public static void Detach(NCharacterSelectScreen screen)
    {
        refresh = null;
        bound = null;
        var previous = panel;
        panel = null;
        // Detached from the tree before the free so the name is reusable in the
        // same frame: the next room builds its panel right after this.
        if (previous is not null && GodotObject.IsInstanceValid(previous))
        {
            previous.GetParent()?.RemoveChild(previous);
            previous.QueueFree();
        }
        if (screen.GetNodeOrNull<Control>(PanelName) is { } stale)
        {
            screen.RemoveChild(stale);
            stale.QueueFree();
        }
    }

    private static void Build(NCharacterSelectScreen screen, StartRunLobby lobby)
    {
        var zh = BotUiTheme.Chinese();
        var characters = ModelDb.AllCharacters.OrderBy(character => character.Id.Entry).ToList();

        panel = new PanelContainer
        {
            Name = PanelName,
            // Anchored to the right edge and grown from the content: FitToContent
            // replaces this rect with the card's own minimum, so the panel is
            // exactly as wide and tall as its text instead of a fixed 446px box
            // with an empty gutter to the right of every line. The rect here is
            // only the shape used before that first fit.
            AnchorLeft = 1, AnchorRight = 1,
            OffsetLeft = -470, OffsetRight = -24,
            OffsetTop = 150, OffsetBottom = 150,
            GrowHorizontal = Control.GrowDirection.Begin,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Theme = screen.Theme,
        };
        panel.AddThemeStyleboxOverride("panel", BotUiTheme.Card());
        screen.AddChild(panel);

        var margin = BotUiTheme.Margin(16);
        var root = new VBoxContainer { Name = "Content" };
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);
        panel.AddChild(margin);

        var header = BotUiTheme.Header(zh ? "联机机器人" : "Co-op Bots",
            zh ? "官方多人房间的 AI 队友" : "AI teammates for the official lobby");
        root.AddChild(header);
        DraggablePanel.Attach(panel, header);
        root.AddChild(new HSeparator());

        var characterSelect = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var character in characters)
            characterSelect.AddItem(character.Title.GetFormattedText());

        var difficultySelect = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (BotDifficulty tier in Enum.GetValues<BotDifficulty>())
            difficultySelect.AddItem(zh ? tier.Chinese() : tier.English());
        difficultySelect.Select((int)BotDifficulty.Pro);
        root.AddChild(Row(zh ? "角色" : "Character", characterSelect));
        root.AddChild(Row(zh ? "难度" : "Difficulty", difficultySelect));

        // What the selected tier actually changes, so the choice is not a mystery.
        // Deliberately not autowrapped: a wrapped label reports a near-zero minimum
        // width, which would let the content-sized card collapse into a narrow
        // column and wrap the description instead of being as wide as the text.
        var hint = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Accent);
        root.AddChild(hint);

        var add = new Button
        {
            Text = zh ? "+ 添加机器人" : "+ Add bot",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = zh ? "以当前角色与难度加入一个 Bot（大厅最多 4 人）"
                : "Add a bot with the selected character and tier (4 seats max)",
        };
        var remove = new Button
        {
            Text = zh ? "− 移除" : "− Remove",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = zh ? "移除最后加入的机器人" : "Remove the most recently added bot",
        };
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        actions.AddChild(add);
        actions.AddChild(remove);
        root.AddChild(actions);
        root.AddChild(new HSeparator());

        var rosterTitle = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Muted);
        root.AddChild(rosterTitle);
        var roster = new VBoxContainer { Name = "Roster" };
        roster.AddThemeConstantOverride("separation", 4);
        root.AddChild(roster);

        // Same reason as the hint: this line carries the one message the panel
        // shows, and it is the widest thing on it, so it defines the width.
        var status = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Muted);
        root.AddChild(status);
        root.AddChild(BotUiTheme.Text($"{ModEntry.Version}"
            + (zh ? " · 所有真人需为同一版本" : " · all humans must match"), 11, BotUiTheme.Muted));

        void Refresh(string? message = null, bool error = false)
        {
            // A lobby that outlived its panel can still raise one more event on
            // the way out; the panel only ever paints the lobby it is bound to.
            if (!ReferenceEquals(bound, lobby)) return;
            foreach (var child in roster.GetChildren())
            {
                roster.RemoveChild(child);
                child.QueueFree();
            }

            var bots = LobbyBotService.Bots(lobby);
            var seats = LobbyBotService.PlayerCount(lobby);
            rosterTitle.Text = zh ? $"队伍 · 席位 {seats}/4" : $"Team · seats {seats}/4";
            if (bots.Count == 0)
                roster.AddChild(BotUiTheme.Text(zh ? "还没有机器人" : "No bots yet", 12, BotUiTheme.Muted));
            foreach (var (id, character) in bots)
            {
                var botTier = BotRegistry.Difficulty(id);
                roster.AddChild(BotUiTheme.Text(
                    $"• Bot {BotRegistry.Serial(id)} · {(zh ? botTier.Chinese() : botTier.English())} · {character}",
                    13, botTier.Cheats() ? BotUiTheme.Accent : BotUiTheme.Ink));
            }

            var host = lobby.NetService.Type == NetGameType.Host;
            add.Disabled = !host || seats >= 4;
            remove.Disabled = !host || bots.Count == 0;
            characterSelect.Disabled = !host;
            difficultySelect.Disabled = !host;
            status.Text = message ?? (host
                ? string.Empty
                : zh ? "只有房主可以添加或移除机器人" : "Only the host can add or remove bots");
            status.AddThemeColorOverride("font_color", error ? BotUiTheme.Error : BotUiTheme.Muted);
            hint.Text = Selected() is { } tier ? (zh ? tier.Describe() : tier.DescribeEnglish()) : string.Empty;
            FitToContent();
        }

        BotDifficulty? Selected()
        {
            var tiers = Enum.GetValues<BotDifficulty>();
            var index = Math.Clamp(difficultySelect.Selected, 0, tiers.Length - 1);
            return tiers[index];
        }

        add.Pressed += () =>
        {
            var character = characters[Math.Clamp(characterSelect.Selected, 0, characters.Count - 1)];
            Refresh(LobbyBotService.Add(lobby, character, Selected() ?? BotDifficulty.Pro, out var error) ? null : error,
                error: true);
        };
        remove.Pressed += () => Refresh(LobbyBotService.RemoveLast(lobby, out var error) ? null : error, error: true);
        difficultySelect.ItemSelected += _ => Refresh();
        LobbyBotService.SubscribeToPlayerChanges(lobby, () => refresh?.Invoke());
        bound = lobby;
        refresh = () => Refresh();
        Refresh();
    }

    /// <summary>
    /// Shrinks the card to the exact size its content needs, so the panel reads as
    /// a fitted card rather than a wide box with the text left-aligned inside it.
    /// A container only reports the minimum its new children created on the next
    /// layout pass, so the size is applied once more deferred — the same two-step
    /// the in-combat panel uses when it collapses to its header.
    /// </summary>
    private static void FitToContent()
    {
        if (panel is null || !GodotObject.IsInstanceValid(panel)) return;
        panel.ResetSize();
        var target = panel;
        Callable.From(() =>
        {
            if (target is not null && GodotObject.IsInstanceValid(target)) target.ResetSize();
        }).CallDeferred();
    }

    private static HBoxContainer Row(string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var text = BotUiTheme.Text(label, 14, BotUiTheme.Muted);
        text.CustomMinimumSize = new Vector2(66, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(text);
        row.AddChild(control);
        return row;
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
internal static class CharacterSelectUiPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) => LobbyUi.Attach(__instance);
}

// Leaving the room tears the lobby down but not the screen node, so the panel
// has to let go of it here rather than wait for the screen to be freed.
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuClosed))]
internal static class CharacterSelectClosedUiPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) => LobbyUi.Detach(__instance);
}
