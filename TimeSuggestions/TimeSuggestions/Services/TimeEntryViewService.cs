using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Składa wpis czasu w kształt dla interfejsu razem z tym, czego sama encja nie wie:
/// przerwami w zasięgu (z dziennika wersji) i etykietą sesji („edycja 3").
/// Jedno miejsce, bo wpis wraca z pięciu różnych operacji i każda musiałaby powtórzyć
/// te same dwa zapytania — a wpis bez przerw wygląda w interfejsie jak wpis, który
/// przerw nie ma.
/// </summary>
public class TimeEntryViewService(
    EntryGapService entryGaps,
    SessionLabelService sessionLabels,
    IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    public async Task<TimeEntryDto> BuildAsync(
        TimeEntry entry,
        string? notice,
        CancellationToken cancellationToken)
        => (await BuildManyAsync([entry], notice, cancellationToken))[0];

    public async Task<List<TimeEntryDto>> BuildManyAsync(
        IReadOnlyList<TimeEntry> entries,
        string? notice,
        CancellationToken cancellationToken)
    {
        var gaps = await entryGaps.LoadAsync(entries, cancellationToken);
        var labels = await sessionLabels.LoadAsync(
            entries.SelectMany(entry => entry.Suggestions).ToList(), cancellationToken);

        return entries
            .Select(entry => TimeEntryDto.FromEntity(
                entry,
                notice,
                gaps.GetValueOrDefault(entry.Id),
                LabelOf(entry, labels),
                TimeEntryOperationsService.RoundToIncrement(
                    entry.DurationMinutes, options.BillingIncrementMinutes)))
            .ToList();
    }

    /// <summary>
    /// Etykieta wpisu to etykieta jego NAJWCZEŚNIEJSZEJ sugestii — tej samej, z której
    /// bierze się tytuł źródła. Po scaleniu wpis obejmuje kilka sesji, a numer ma
    /// wskazywać tę, od której praca się zaczęła.
    /// </summary>
    private static string? LabelOf(TimeEntry entry, IReadOnlyDictionary<int, string> labels)
    {
        var first = entry.Suggestions.OrderBy(suggestion => suggestion.StartedAt).FirstOrDefault();
        return first is null ? null : labels.GetValueOrDefault(first.Id);
    }
}
