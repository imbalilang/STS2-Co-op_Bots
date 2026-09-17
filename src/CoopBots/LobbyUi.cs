using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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

    public static void Attach(NCharacterSelectScreen screen)
    {
        if (screen.GetNodeOrNull<Control>(PanelName) is not null || screen.Lobby is null)
            return;

        var lobby = screen.Lobby;
        var zh = BotUiTheme.Chinese();
        var characters = ModelDb.AllCharacters.OrderBy(character => character.Id.Entry).ToList();

        var panel = new PanelContainer
        {
            Name = PanelName,
            // Anchored to the right edge, height taken from the content: a fixed
            // rect clipped the roster once the third bot was added.
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
        var hint = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Accent);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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

        var status = BotUiTheme.Text(string.Empty, 12, BotUiTheme.Muted);
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(status);
        root.AddChild(BotUiTheme.Text($"{ModEntry.Version}"
            + (zh ? " · 所有真人需为同一版本" : " · all humans must match"), 11, BotUiTheme.Muted));

        void Refresh(string? message = null, bool error = false)
        {
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
        LobbyBotService.SubscribeToPlayerChanges(lobby, () => Refresh());
        Refresh();
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
