using MegaCrit.Sts2.Core.Random;

namespace DeckSim;

/// <summary>
/// Does a feature add anything to a model that already knows the deck's upgrade
/// count?
///
/// Comparing two AUCs computed on their own cannot answer that: `upgrades` and a
/// candidate are correlated, so a candidate scoring 0.55 next to a ruler scoring
/// 0.63 may be the same signal measured twice, and a candidate scoring 0.50 may
/// still carry information the ruler does not. The question "does this add
/// something" is a question about the *difference between two models*, and it has
/// to be asked that way.
///
/// So: fit a logistic model on `upgrades` alone, fit the same model with the
/// candidate added, and compare held-out AUC on identical folds — a paired
/// comparison. Repeats give the spread of that difference.
///
/// Everything is deterministic: folds come from a fixed seed, so two runs on the
/// same CSV produce the same numbers.
/// </summary>
internal static class IncrementalAuc
{
    internal sealed record Result(string Feature, double BaseAuc, double WithAuc, double Delta, double Lo, double Hi, int N);

    private const int Folds = 5;
    private const int Repeats = 12;
    private const double LearningRate = 0.5;
    private const int Epochs = 400;
    private const double L2 = 0.01;

    /// <summary>
    /// Reads a per-deck feature CSV and tests every numeric column against
    /// <paramref name="baseColumn"/>. Rows whose value is `inf` are dropped for
    /// that column only — an infinite convergence point is "never", which no
    /// linear model can consume, and silently mapping it to a large number would
    /// invent a spacing the data does not have.
    /// </summary>
    internal static IReadOnlyList<Result> Run(string csvPath, string baseColumn, Action<string>? log = null)
    {
        var table = Csv.Read(csvPath);
        if (!table.Columns.TryGetValue("win", out var winIndex))
            throw new InvalidOperationException($"{csvPath} has no 'win' column");
        if (!table.Columns.TryGetValue(baseColumn, out var baseIndex))
            throw new InvalidOperationException($"{csvPath} has no '{baseColumn}' column");

        var results = new List<Result>();
        foreach (var (name, index) in table.Columns.OrderBy(pair => pair.Value))
        {
            if (name is "win" or "players" or "run_hash" or "character" or "win_rate") continue;
            if (name == baseColumn) continue;
            if (!table.Numeric(index)) continue;
            var result = Compare(table, winIndex, baseIndex, index, name);
            if (result is not null) results.Add(result);
        }
        log?.Invoke($"  {results.Count} features tested against '{baseColumn}' on {table.Rows.Count} decks");
        return results.OrderByDescending(result => result.Delta).ToList();
    }

    private static Result? Compare(Csv.Table table, int winIndex, int baseIndex, int featureIndex, string name)
    {
        // Only rows where both models have the data, so the pair is compared on the
        // same decks. Dropping a row from one model and not the other would make
        // the difference partly a difference of samples.
        var rows = new List<(double Base, double Feature, bool Win)>();
        foreach (var row in table.Rows)
        {
            var b = row[baseIndex];
            var f = row[featureIndex];
            if (double.IsNaN(b) || double.IsInfinity(b) || double.IsNaN(f) || double.IsInfinity(f)) continue;
            rows.Add((b, f, row[winIndex] > 0.5 ? true : false));
        }
        if (rows.Count < 40 || rows.All(r => r.Win) || rows.All(r => !r.Win)) return null;

        var deltas = new List<double>();
        double baseTotal = 0, withTotal = 0;
        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var rng = new Rng((ulong)(repeat + 1) * 7919UL, "coopbots-incremental");
            var order = Enumerable.Range(0, rows.Count).OrderBy(_ => rng.NextInt(int.MaxValue)).ToList();
            double baseAuc = 0, withAuc = 0;
            for (var fold = 0; fold < Folds; fold++)
            {
                var test = order.Where((_, position) => position % Folds == fold).ToHashSet();
                var train = order.Where((_, position) => position % Folds != fold).ToList();
                var baseModel = Fit(train.Select(i => new[] { rows[i].Base }).ToList(),
                    train.Select(i => rows[i].Win).ToList());
                var withModel = Fit(train.Select(i => new[] { rows[i].Base, rows[i].Feature }).ToList(),
                    train.Select(i => rows[i].Win).ToList());
                if (baseModel is null || withModel is null) continue;

                var testList = test.ToList();
                baseAuc += Auc(baseModel, testList.Select(i => new[] { rows[i].Base }).ToList(), testList.Select(i => rows[i].Win).ToList());
                withAuc += Auc(withModel, testList.Select(i => new[] { rows[i].Base, rows[i].Feature }).ToList(), testList.Select(i => rows[i].Win).ToList());
            }
            baseAuc /= Folds;
            withAuc /= Folds;
            baseTotal += baseAuc;
            withTotal += withAuc;
            deltas.Add(withAuc - baseAuc);
        }

        deltas.Sort();
        var mean = deltas.Average();
        return new Result(name, baseTotal / Repeats, withTotal / Repeats, mean,
            deltas[(int)(deltas.Count * 0.05)], deltas[Math.Min(deltas.Count - 1, (int)(deltas.Count * 0.95))],
            rows.Count);
    }

    // ---- Logistic regression ------------------------------------------------

    /// <summary>
    /// Plain batch gradient descent on standardized columns with a small L2 term.
    /// Deliberately simple: the job is to answer "does this column move held-out
    /// AUC", and a fancier fit would make the answer harder to reproduce, not more
    /// true. Null when the fit cannot be made (one class present, no variance).
    /// </summary>
    private static double[]? Fit(List<double[]> features, List<bool> labels)
    {
        if (features.Count == 0 || labels.Distinct().Count() < 2) return null;
        var width = features[0].Length;
        var means = new double[width];
        var scales = new double[width];
        for (var column = 0; column < width; column++)
        {
            means[column] = features.Average(row => row[column]);
            var variance = features.Average(row => (row[column] - means[column]) * (row[column] - means[column]));
            scales[column] = Math.Sqrt(variance);
            if (scales[column] < 1e-9) return null;
        }

        var weights = new double[width];
        var bias = 0.0;
        var n = features.Count;
        for (var epoch = 0; epoch < Epochs; epoch++)
        {
            var gradient = new double[width];
            var biasGradient = 0.0;
            for (var index = 0; index < n; index++)
            {
                var z = bias;
                for (var column = 0; column < width; column++)
                    z += weights[column] * (features[index][column] - means[column]) / scales[column];
                var error = 1.0 / (1 + Math.Exp(-Math.Clamp(z, -30, 30))) - (labels[index] ? 1 : 0);
                biasGradient += error;
                for (var column = 0; column < width; column++)
                    gradient[column] += error * (features[index][column] - means[column]) / scales[column];
            }
            bias -= LearningRate * biasGradient / n;
            for (var column = 0; column < width; column++)
                weights[column] -= LearningRate * (gradient[column] / n + L2 * weights[column]);
        }
        // The standardizer travels with the weights so scoring can standardize the
        // same way; [0..w) weights, then means, then scales, then the bias.
        return weights.Concat(means).Concat(scales).Append(bias).ToArray();
    }

    private static double Auc(double[] packed, List<double[]> features, List<bool> labels)
    {
        var width = features[0].Length;
        var scores = new List<(double Score, bool Win)>(features.Count);
        for (var index = 0; index < features.Count; index++)
        {
            var z = packed[packed.Length - 1];
            for (var column = 0; column < width; column++)
                z += packed[column] * (features[index][column] - packed[width + column]) / packed[2 * width + column];
            scores.Add((z, labels[index]));
        }
        double greater = 0, ties = 0;
        foreach (var a in scores.Where(entry => entry.Win))
        foreach (var b in scores.Where(entry => !entry.Win))
        {
            if (a.Score > b.Score) greater++;
            else if (Math.Abs(a.Score - b.Score) < 1e-12) ties++;
        }
        var positives = scores.Count(entry => entry.Win);
        var negatives = scores.Count - positives;
        return positives == 0 || negatives == 0 ? 0.5 : (greater + ties / 2) / (positives * (double)negatives);
    }
}
