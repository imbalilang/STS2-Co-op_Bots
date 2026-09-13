namespace CoopBots;

/// <summary>
/// Bot configuration is encoded into a reserved synthetic player id. The id is
/// included in the game's own lobby/run messages, so every peer can recover the
/// same difficulty without adding a second network protocol.
/// </summary>
public static class BotRegistry
{
    private const ulong Prefix = 0xB07B_0000_0000_0000UL;
    private const ulong PrefixMask = 0xFFFF_0000_0000_0000UL;

    public static bool IsBot(ulong playerId) => (playerId & PrefixMask) == Prefix;

    public static BotDifficulty Difficulty(ulong playerId)
    {
        if (!IsBot(playerId))
            return BotDifficulty.Normal;

        return (BotDifficulty)((playerId >> 16) & 0x3UL);
    }

    public static int Serial(ulong playerId) => (int)((playerId >> 8) & 0xFFUL);

    public static ulong CreateId(BotDifficulty difficulty, int serial, int slot)
        => Prefix | ((ulong)difficulty << 16) | ((ulong)(serial & 0xFF) << 8) | (uint)(slot & 0xFF);

    public static string DisplayName(ulong playerId)
    {
        var difficulty = Difficulty(playerId);
        return $"Bot {Serial(playerId)} · {difficulty.Chinese()}";
    }
}
