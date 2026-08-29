using CensusScope.Core.Models;
using CensusScope.Core.Providers;

namespace CensusScope.Core.Services;

/// <summary>
/// One shaded area of the choropleth: the geographic id and name, the computed indicator
/// value (null = no data), and the ready-to-show display string ("—" when null).
/// </summary>
public sealed record MapArea(string Id, string Name, double? Value, string Display);

/// <summary>
/// Everything the map view needs to render one choropleth: per-area values, the class
/// scheme (breaks, labels, counts), and the citation strings.
/// </summary>
public sealed class MapData
{
    /// <summary>Legend label of the mapped indicator, e.g. "Population 65 and over (%)".</summary>
    public required string VariableLabel { get; init; }

    /// <summary>Map title: "{variable label} — {dataset display name}".</summary>
    public required string Title { get; init; }

    /// <summary>The statistical universe (denominator population) of the dataset.</summary>
    public required string Universe { get; init; }

    /// <summary>Source attribution built from the catalog's source name and the dataset's table.</summary>
    public required string Source { get; init; }

    /// <summary>Value format shared with <see cref="DataCell"/> ("percent", "currency", ...).</summary>
    public required string Format { get; init; }

    /// <summary>All areas of the requested level, including those without data.</summary>
    public required IReadOnlyList<MapArea> Areas { get; init; }

    /// <summary>Ascending inner break points (k-1 values for k classes).</summary>
    public required IReadOnlyList<double> Breaks { get; init; }

    /// <summary>k human-readable class labels, e.g. "12.3% – 18.9%", formatted per <see cref="Format"/>.</summary>
    public required IReadOnlyList<string> ClassLabels { get; init; }

    /// <summary>Number of areas falling in each class (parallel to <see cref="ClassLabels"/>).</summary>
    public required IReadOnlyList<int> ClassCounts { get; init; }

    /// <summary>Number of areas without a computable value.</summary>
    public required int NoDataCount { get; init; }

    /// <summary>Human-readable classification note, e.g. "Quantile classification, 5 classes".</summary>
    public required string MethodNote { get; init; }
}

/// <summary>
/// Builds choropleth data: fetches the raw variable values for every unit of a level,
/// derives the indicator per area (share, direct value, or density), and classifies the
/// values into quantile classes for the legend.
/// </summary>
public sealed class MapService
{
    /// <summary>Target class count; fewer are used when the data has fewer distinct values.</summary>
    private const int MaxClasses = 5;

    /// <summary>
    /// Percent shares are suppressed below this denominator: source values are rounded or
    /// suppressed, so a share over a tiny base fabricates precision.
    /// </summary>
    private const double MinPercentDenominator = 100;

    /// <summary>
    /// Builds the full map dataset for one indicator at one geographic level.
    /// </summary>
    /// <param name="provider">Country data source the values are fetched from.</param>
    /// <param name="dataset">The dataset the indicator belongs to (title, universe, source table).</param>
    /// <param name="mapVar">The indicator definition (numerators, denominator, kind, format).</param>
    /// <param name="levelCode">Geographic level to map, e.g. "state" or "PR".</param>
    /// <param name="parentId">Optional ancestor id restricting the mapped units.</param>
    /// <param name="landKm2ById">Land area in km² per geographic id; required for kind "density".</param>
    /// <param name="progress">Optional progress messages for the UI status bar.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MapData> BuildAsync(
        IDemographicProvider provider, DatasetDef dataset, MapVariableDef mapVar,
        string levelCode, string? parentId, IReadOnlyDictionary<string, double?>? landKm2ById,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (mapVar.Numerator.Count == 0)
            throw new InvalidOperationException($"Map variable '{mapVar.Key}' has no numerator variables.");
        if (mapVar.Kind == "percent" && string.IsNullOrEmpty(mapVar.Denominator))
            throw new InvalidOperationException($"Map variable '{mapVar.Key}' has kind \"percent\" but no denominator.");

        // The provider fetches numerators and denominator in one request.
        var codes = new List<string>(mapVar.Numerator);
        if (mapVar.Denominator is { Length: > 0 } denominator && !codes.Contains(denominator))
            codes.Add(denominator);

        var areaValues = await provider.GetAreaValuesAsync(levelCode, parentId, codes, progress, ct)
            .ConfigureAwait(false);

        progress?.Report("Classifying values...");
        var areas = new List<MapArea>(areaValues.Count);
        var floorSuppressed = 0;
        foreach (var area in areaValues)
        {
            var (value, belowFloor) = Compute(mapVar, area, landKm2ById);
            if (belowFloor) floorSuppressed++;
            // Below-floor areas get an explanatory display instead of a bare em dash, so the
            // tooltip distinguishes the app's own floor from source-suppressed data.
            var display = belowFloor
                ? "— (base under " + (int)MinPercentDenominator + ")"
                : new DataCell(value, null, null, mapVar.Format).Display;
            areas.Add(new MapArea(area.GeoId, area.Name, value, display));
        }

        var sorted = areas
            .Where(a => a.Value is not null)
            .Select(a => a.Value!.Value)
            .OrderBy(v => v)
            .ToList();

        var breaks = QuantileBreaks(sorted, MaxClasses);
        var classCount = sorted.Count == 0 ? 0 : breaks.Count + 1;
        var (labels, counts) = BuildClasses(sorted, breaks, classCount, mapVar.Format);

        return new MapData
        {
            VariableLabel = mapVar.Label,
            Title = $"{mapVar.Label} — {dataset.DisplayName}",
            Universe = dataset.Universe,
            Source = $"{provider.Catalog.SourceName}, {dataset.SourceTable}",
            Format = mapVar.Format,
            Areas = areas,
            Breaks = breaks,
            ClassLabels = labels,
            ClassCounts = counts,
            NoDataCount = areas.Count - sorted.Count,
            MethodNote = $"Quantile classification, {classCount} {(classCount == 1 ? "class" : "classes")}"
                + (floorSuppressed > 0
                    ? $"; {floorSuppressed} area{(floorSuppressed == 1 ? "" : "s")} not shaded (denominator under {(int)MinPercentDenominator})"
                    : ""),
        };
    }

    /// <summary>
    /// Derives one area's indicator value, or null when it cannot be computed honestly.
    /// <c>BelowFloor</c> is true only when a percent share was suppressed by the app's own
    /// small-denominator floor (published data exists but the base is under
    /// <see cref="MinPercentDenominator"/>) — callers disclose those separately.
    /// </summary>
    private static (double? Value, bool BelowFloor) Compute(
        MapVariableDef mapVar, AreaValue area, IReadOnlyDictionary<string, double?>? landKm2ById)
    {
        switch (mapVar.Kind)
        {
            case "percent":
            {
                var numerator = SumNumerators(mapVar, area);
                var denominator = area.Values.GetValueOrDefault(mapVar.Denominator!);
                if (numerator is null || denominator is null)
                    return (null, false);
                if (denominator < MinPercentDenominator)
                    return (null, true);
                return (numerator.Value / denominator.Value * 100, false);
            }
            case "direct":
                return (area.Values.GetValueOrDefault(mapVar.Numerator[0]), false);
            case "density":
            {
                var numerator = SumNumerators(mapVar, area);
                var land = landKm2ById?.GetValueOrDefault(area.GeoId);
                if (numerator is null || land is null || land <= 0)
                    return (null, false);
                return (numerator.Value / land.Value, false);
            }
            default:
                throw new InvalidOperationException(
                    $"Map variable '{mapVar.Key}' has unknown kind \"{mapVar.Kind}\".");
        }
    }

    /// <summary>Sums the numerator variables; any missing/suppressed part makes the whole sum null.</summary>
    private static double? SumNumerators(MapVariableDef mapVar, AreaValue area)
    {
        double sum = 0;
        foreach (var code in mapVar.Numerator)
        {
            var value = area.Values.GetValueOrDefault(code);
            if (value is null)
                return null; // a partial sum would silently understate the indicator
            sum += value.Value;
        }
        return sum;
    }

    /// <summary>
    /// Inner quantile break points for up to <paramref name="maxClasses"/> classes over
    /// ascending values. Equal breaks are deduped (ties collapse classes rather than
    /// producing empty ones), and a break equal to the maximum is dropped for the same reason.
    /// </summary>
    private static List<double> QuantileBreaks(IReadOnlyList<double> sorted, int maxClasses)
    {
        var breaks = new List<double>();
        if (sorted.Count == 0)
            return breaks;

        var k = Math.Min(maxClasses, sorted.Distinct().Count());
        for (var i = 1; i < k; i++)
        {
            var q = Quantile(sorted, (double)i / k);
            if (q >= sorted[^1])
                continue;
            // Keep the break only if the class it closes — (previous break, q] — holds data;
            // otherwise the tie collapses into the class above. Interpolated quantiles can
            // land in an empty gap when duplicates dominate (e.g. [-1.5,-1.5,-1.5,0.3,2.1]).
            var floor = breaks.Count == 0 ? double.NegativeInfinity : breaks[^1];
            if (sorted.Any(v => v > floor && v <= q))
                breaks.Add(q);
        }
        return breaks;
    }

    /// <summary>Linear-interpolation quantile (R type 7) of ascending values at probability p.</summary>
    private static double Quantile(IReadOnlyList<double> sorted, double p)
    {
        var position = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>Builds legend labels ("lo – hi" per class) and per-class area counts.</summary>
    private static (IReadOnlyList<string> Labels, IReadOnlyList<int> Counts) BuildClasses(
        IReadOnlyList<double> sorted, IReadOnlyList<double> breaks, int classCount, string format)
    {
        if (classCount == 0)
            return ([], []);

        var labels = new List<string>(classCount);
        for (var c = 0; c < classCount; c++)
        {
            var lower = c == 0 ? sorted[0] : breaks[c - 1];
            var upper = c == classCount - 1 ? sorted[^1] : breaks[c];
            labels.Add(lower == upper
                ? DataCell.FormatValue(lower, format)
                : DataCell.FormatValue(lower, format) + " – " + DataCell.FormatValue(upper, format));
        }

        // Class membership: values at or below a break belong to the lower class.
        var counts = new int[classCount];
        foreach (var value in sorted)
        {
            var index = 0;
            while (index < breaks.Count && value > breaks[index])
                index++;
            counts[index]++;
        }
        return (labels, counts);
    }
}
