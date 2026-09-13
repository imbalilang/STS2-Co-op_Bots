namespace CoopBots;

/// <summary>
/// Difficulty is now purely a speed/thinking preset plus an optional cheat. Every
/// tier runs the same full algorithm; the old "intentionally dumb" behaviours are
/// gone, so a slower tier is a slower team, not a worse one.
/// </summary>
public enum BotDifficulty : byte
{
    /// <summary>0.5s between cards, half the thinking budget.</summary>
    Flash = 0,
    /// <summary>1.5s between cards, half again more thinking budget.</summary>
    Pro = 1,
    /// <summary>Pro speed and thinking, plus 3x gold.</summary>
    Cheated = 2,
}

public static class BotDifficultyNames
{
    public static string Chinese(this BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Flash => "极速",
        BotDifficulty.Pro => "专业",
        BotDifficulty.Cheated => "作弊",
        _ => "专业",
    };

    /// <summary>Milliseconds between two card plays for this tier.</summary>
    public static int CardIntervalMs(this BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Flash => 500,
        _ => 1500,
    };

    /// <summary>Multiplier applied to the kernel search budget.</summary>
    public static double ThinkingScale(this BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Flash => 0.5,
        _ => 1.5,
    };

    public static bool Cheats(this BotDifficulty difficulty) => difficulty == BotDifficulty.Cheated;

    // The team plans as one unit, so it runs at the strongest tier present:
    // a Pro bot must not be dragged down to Flash pacing because it shares the
    // lobby with one, and a mixed team is an edge case anyway.
    public static BotDifficulty Strongest(this IEnumerable<BotDifficulty> tiers)
    {
        var strongest = BotDifficulty.Flash;
        foreach (var tier in tiers)
            if (Rank(tier) > Rank(strongest)) strongest = tier;
        return strongest;
    }

    private static int Rank(BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Flash => 0,
        BotDifficulty.Pro => 1,
        _ => 2,
    };
}
