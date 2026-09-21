namespace CoopBots.Kernel.Vendor;

internal static class ReachableHandValue
{
    internal static int Calculate(
        ReadOnlySpan<(int Energy, int Stars, int Value)> cards,
        int energyCapacity,
        int starCapacity)
    {
        int totalEnergy = 0;
        int totalStars = 0;
        long totalValue = 0;
        foreach (var card in cards)
        {
            totalEnergy += card.Energy;
            totalStars += card.Stars;
            totalValue += card.Value;
        }
        energyCapacity = Math.Min(Math.Max(0, energyCapacity), totalEnergy);
        starCapacity = Math.Min(Math.Max(0, starCapacity), totalStars);
        if (energyCapacity == totalEnergy && starCapacity == totalStars
            && energyCapacity >= 0 && starCapacity >= 0 && totalValue <= int.MaxValue)
            return (int)totalValue;

        int width = checked(starCapacity + 1);
        int length = checked((energyCapacity + 1) * width);
        Span<int> best = length <= 256 ? stackalloc int[length] : new int[length];
        best.Clear();
        foreach (var card in cards)
        {
            for (int energy = energyCapacity; energy >= card.Energy; energy--)
            for (int stars = starCapacity; stars >= card.Stars; stars--)
            {
                int index = energy * width + stars;
                best[index] = Math.Max(best[index],
                    best[(energy - card.Energy) * width + stars - card.Stars] + card.Value);
            }
        }
        return best[^1];
    }
}
