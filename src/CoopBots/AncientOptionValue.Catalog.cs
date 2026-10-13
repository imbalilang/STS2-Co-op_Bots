namespace CoopBots;

internal static partial class AncientOptionValue
{
    // Native v0.111.0 option inventory; regression scans AllPossibleOptions.
    internal static readonly IReadOnlyDictionary<string, string[]> Catalog = new Dictionary<string, string[]>
    {
        ["Darv"] = ["Astrolabe", "BlackStar", "CallingBell", "DustyTome", "Ectoplasm", "EmptyCage", "PandorasBox", "PhilosophersStone", "RunicPyramid", "SneckoEye", "Sozu", "VelvetChoker"],
        ["Neow"] = ["ArcaneScroll", "BoomingConch", "CursedPearl", "DowsingRod", "FishingRod", "GoldenPearl", "HeftyTablet", "Kaleidoscope", "LargeCapsule", "LavaRock", "LeadPaperweight", "LeafyPoultice", "LostCoffer", "MassiveScroll", "NeowsBones", "NeowsSacrifice", "NeowsTalisman", "NeowsTorment", "NewLeaf", "NutritiousOyster", "PhialHolster", "Pomander", "PrecariousShears", "PreciseScissors", "ScrollBoxes", "SilkenTress", "SilverCrucible", "SmallCapsule", "StoneHumidifier", "WingedBoots"],
        ["Nonupeipe"] = ["BeautifulBracelet", "BlessedAntler", "BrilliantScarf", "DelicateFrond", "DiamondDiadem", "FurCoat", "Glitter", "JewelryBox", "LoomingFruit", "SignetRing"],
        ["Orobas"] = ["AlchemicalCoffer", "ArchaicTooth", "Driftwood", "ElectricShrymp", "GlassEye", "PrismaticGem", "RadiantPearl", "SandCastle", "SeaGlass", "TouchOfOrobas"],
        ["Pael"] = ["PaelsBlood", "PaelsClaw", "PaelsEye", "PaelsFlesh", "PaelsGrowth", "PaelsHorn", "PaelsLegion", "PaelsTears", "PaelsTooth", "PaelsWing"],
        ["Tanx"] = ["Claws", "Crossbow", "IronClub", "MeatCleaver", "Sai", "SpikedGauntlets", "TanxsWhistle", "ThrowingAxe", "TriBoomerang", "WarHammer"],
        ["Tezcatara"] = ["BiiigHug", "GoldenCompass", "NutritiousSoup", "PumpkinCandle", "SealOfGold", "Storybook", "ToastyMittens", "ToyBox", "VeryHotCocoa", "YummyCookie"],
        ["Vakuu"] = ["BloodSoakedRose", "ChoicesParadox", "DistinguishedCape", "Fiddle", "JeweledMask", "LordsParasol", "MusicBox", "PreservedFog", "SereTalon", "WhisperingEarring"],
    };
}
