using System.Globalization;

namespace DeckSim;

/// <summary>
/// The smallest CSV reader that can open the per-deck export.
///
/// Hand-written rather than pulled in as a dependency because the only file it has
/// to read is one this repository writes, with a header and no quoting: if that
/// ever stops being true, this should fail loudly rather than guess.
/// </summary>
internal static class Csv
{
    internal sealed record Table(List<string> Header, Dictionary<string, int> Columns, List<double[]> Rows, List<bool> NumericColumns)
    {
        internal bool Numeric(int column) => NumericColumns[column];
    }

    internal static Table Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InvalidOperationException($"{path} has no rows");
        var header = lines[0].Split(',');
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < header.Length; index++)
            columns[header[index].Trim()] = index;

        var rows = new List<double[]>(lines.Length - 1);
        var parsed = new int[header.Length];
        for (var line = 1; line < lines.Length; line++)
        {
            if (string.IsNullOrWhiteSpace(lines[line])) continue;
            var cells = lines[line].Split(',');
            var values = new double[header.Length];
            for (var index = 0; index < header.Length; index++)
            {
                var cell = index < cells.Length ? cells[index].Trim() : string.Empty;
                if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    values[index] = value;
                    parsed[index]++;
                }
                else
                {
                    // `inf` is a real value the export emits ("never converged"), so
                    // it becomes a missing cell rather than poisoning the column:
                    // the rows that did converge are still usable. A column that
                    // never parses at all is an identity column, and the caller
                    // skips it.
                    values[index] = double.NaN;
                }
            }
            rows.Add(values);
        }
        var numeric = parsed.Select(count => count > 0).ToList();
        return new Table(header.ToList(), columns, rows, numeric);
    }
}
