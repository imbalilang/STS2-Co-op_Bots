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
    // Bits 16-17 hold the speed tier; bit 18 is a separate cheat flag. Keeping
    // the flag out of the tier field means an old save that encoded Smart=2 or
    // Genius=3 can never be read back as the new cheated tier.
    private const int TierShift = 16;
    private const ulong CheatMask = 1UL << 18;

    public static bool IsBot(ulong playerId) => (playerId & PrefixMask) == Prefix;

    public static BotDifficulty Difficulty(ulong playerId)
    {
        if (!IsBot(playerId))
            return BotDifficulty.Pro;

        if ((playerId & CheatMask) != 0)
            return BotDifficulty.Cheated;

        // 0 = Flash, 1 = Pro. Values 2 and 3 only exist in saves written before
        // the tier rework (Smart/Genius); both were full-strength bots, so they
        // clamp to Pro rather than silently becoming a cheat.
        return ((playerId >> TierShift) & 0x3UL) == 0 ? BotDifficulty.Flash : BotDifficulty.Pro;
    }

    public static bool IsCheated(ulong playerId) => IsBot(playerId) && (playerId & CheatMask) != 0;

    public static int Serial(ulong playerId) => (int)((playerId >> 8) & 0xFFUL);

    public static ulong CreateId(BotDifficulty difficulty, int serial, int slot)
    {
        var tier = difficulty == BotDifficulty.Flash ? 0UL : 1UL;
        var cheat = difficulty.Cheats() ? CheatMask : 0UL;
        return Prefix | (tier << TierShift) | cheat
            | ((ulong)(serial & 0xFF) << 8) | (uint)(slot & 0xFF);
    }

    public static string DisplayName(ulong playerId)
    {
        var difficulty = Difficulty(playerId);
        return $"Bot {Serial(playerId)} · {difficulty.Chinese()}";
    }
}
