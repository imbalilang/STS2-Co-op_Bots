namespace CoopBots;

public enum BotDifficulty : byte
{
    Dumb = 0,
    Normal = 1,
    Smart = 2,
    Genius = 3,
}

public static class BotDifficultyNames
{
    public static string Chinese(this BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Dumb => "蠢",
        BotDifficulty.Normal => "普通",
        BotDifficulty.Smart => "聪明",
        BotDifficulty.Genius => "天才",
        _ => "普通",
    };
}
