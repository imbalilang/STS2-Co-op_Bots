using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace CoopBots;

public static class LobbyUi
{
    private const string PanelName = "CoopBotsPanel";

    public static void Attach(NCharacterSelectScreen screen)
    {
        if (screen.GetNodeOrNull<Control>(PanelName) is not null || screen.Lobby is null)
            return;

        var lobby = screen.Lobby;
        var panel = new PanelContainer
        {
            Name = PanelName,
            CustomMinimumSize = new Vector2(390, 0),
            AnchorLeft = 1,
            AnchorRight = 1,
            OffsetLeft = -430,
            OffsetTop = 145,
            OffsetRight = -30,
            OffsetBottom = 460,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        var root = new VBoxContainer { Name = "Content" };
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);
        panel.AddChild(margin);
        screen.AddChild(panel);

        var title = new Label { Text = "联机机器人 / CO-OP BOTS" };
        title.AddThemeFontSizeOverride("font_size", 22);
        root.AddChild(title);

        var characterSelect = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var characters = ModelDb.AllCharacters.OrderBy(character => character.Id.Entry).ToList();
        foreach (var character in characters)
            characterSelect.AddItem(character.Title.GetFormattedText());

        var difficultySelect = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (BotDifficulty difficulty in Enum.GetValues<BotDifficulty>())
            difficultySelect.AddItem(difficulty.Chinese());
        difficultySelect.Select((int)BotDifficulty.Normal);

        root.AddChild(Row("角色", characterSelect));
        root.AddChild(Row("难度", difficultySelect));

        var buttons = new HBoxContainer();
        var add = new Button { Text = "+ 添加机器人", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var remove = new Button { Text = "− 移除最后一个", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        buttons.AddChild(add);
        buttons.AddChild(remove);
        root.AddChild(buttons);

        var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(status);

        void Refresh(string? message = null)
        {
            var botIds = LobbyBotService.BotIds(lobby);
            status.Text = message ?? (botIds.Count == 0
                ? "房主可添加 1–3 个机器人"
                : $"已添加 {botIds.Count} 个：" + string.Join("，", botIds.Select(BotRegistry.DisplayName)));
            var host = lobby.NetService.Type == NetGameType.Host;
            add.Disabled = !host || LobbyBotService.PlayerCount(lobby) >= 4;
            remove.Disabled = !host || botIds.Count == 0;
            characterSelect.Disabled = !host;
            difficultySelect.Disabled = !host;
        }

        add.Pressed += () =>
        {
            var character = characters[Math.Clamp(characterSelect.Selected, 0, characters.Count - 1)];
            var difficulty = (BotDifficulty)Math.Clamp(difficultySelect.Selected, 0, 3);
            Refresh(LobbyBotService.Add(lobby, character, difficulty, out var error) ? null : error);
        };
        remove.Pressed += () => Refresh(LobbyBotService.RemoveLast(lobby, out var error) ? null : error);
        LobbyBotService.SubscribeToPlayerChanges(lobby, () => Refresh());
        Refresh();
    }

    private static HBoxContainer Row(string label, Control control)
    {
        var row = new HBoxContainer();
        var text = new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) };
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
