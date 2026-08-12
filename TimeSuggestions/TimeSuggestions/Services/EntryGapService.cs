using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Przerwa leżąca w godzinach wpisu razem z informacją, czy wchodzi do rozliczanego czasu.
/// </summary>
public record EntryGap(DateTime StartAt, DateTime EndAt, int Minutes, bool Counted);

/// <summary>
/// Przerwy w zasięgu wpisu liczone Z DZIENNIKA <see cref="DocumentActivity"/>, nie ze stanu
/// zapisanego przy wpisie. Powód jest praktyczny i wynika z użycia: po scaleniu sesji wpis
/// obejmuje kilka odcinków pracy, a przerwy MIĘDZY nimi nie były nigdzie zapisane — prawnik
/// widział w historii wersji „przerwa 36 min", ale nie miał czym jej rozliczyć ani nawet
/// dowiedzieć się, że nie jest liczona. Liczenie z dziennika daje to za darmo także dla
/// wpisów scalonych ZANIM ta funkcja powstała: fakty leżą w bazie od początku.
///
/// Domyślny stan przerwy bierze się z tego samego progu, którym tnie sesje silnik:
/// przerwa do <c>SessionFlaggedGapMinutes</c> leży WEWNĄTRZ sesji, więc jej minuty są
/// w czasie brutto i są rozliczane (do odjęcia); dłuższa rozdziela sesje, więc nie jest
/// rozliczana (do doliczenia). Dziennik korekt jest nadrzędny: ostatnia korekta na tym
/// zakresie decyduje, bo to jawna decyzja człowieka.
///
/// Wpisy kalendarzowe i dane sprzed dziennika nie mają z czego liczyć przerw — wtedy
/// wracamy do listy zapisanej przy wpisie (<c>DetectedGapsJson</c>), żeby „Odejmij
/// przerwę" działało jak dotąd.
/// </summary>
public class EntryGapService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    private readonly TimeZoneInfo businessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(optionsAccessor.Value.BusinessTimeZoneId);

    public async Task<Dictionary<int, IReadOnlyList<EntryGap>>> LoadAsync(
        IReadOnlyList<TimeEntry> entries,
        CancellationToken cancellationToken)
    {
        var externalIdByEntry = entries.ToDictionary(entry => entry.Id, DocumentExternalId);
        var externalIds = externalIdByEntry.Values.OfType<string>().Distinct().ToList();

        var activitiesByFile = externalIds.Count == 0
            ? []
            : (await db.DocumentActivities
                .AsNoTracking()
                .Where(activity => externalIds.Contains(activity.ExternalId))
                .Select(activity => new { activity.ExternalId, activity.OccurredAt })
                .ToListAsync(cancellationToken))
            .GroupBy(activity => activity.ExternalId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(activity => TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(activity.OccurredAt, DateTimeKind.Utc), businessTimeZone))
                    .Distinct()
                    .OrderBy(moment => moment)
                    .ToList());

        return entries.ToDictionary(
            entry => entry.Id,
            entry =>
            {
                var externalId = externalIdByEntry[entry.Id];
                var moments = externalId is null
                    ? []
                    : activitiesByFile.GetValueOrDefault(externalId, []);
                return Build(entry, moments);
            });
    }

    /// <summary>Przerwy jednego wpisu — publiczne, bo operacje na przerwie walidują ten sam zbiór.</summary>
    public async Task<IReadOnlyList<EntryGap>> LoadForEntryAsync(TimeEntry entry, CancellationToken cancellationToken)
        => (await LoadAsync([entry], cancellationToken))[entry.Id];

    private static string? DocumentExternalId(TimeEntry entry)
        => entry.Source == SuggestionSource.Document
            ? entry.Suggestions.OrderBy(suggestion => suggestion.StartedAt).FirstOrDefault()?.ExternalId
            : null;

    private IReadOnlyList<EntryGap> Build(TimeEntry entry, IReadOnlyList<DateTime> allMoments)
    {
        var inSpan = allMoments
            .Where(moment => moment >= entry.StartedAt && moment <= entry.EndedAt)
            .ToList();

        var candidates = (inSpan.Count >= 2 ? FromJournal(inSpan) : FromStoredGaps(entry)).ToList();

        // Zabezpieczenie przejścia między źródłami: przerwa, na której ktoś już wykonał
        // korektę, musi zostać na liście nawet jeśli dziennik jej nie odtwarza (zaległa
        // wersja mogła zmienić kształt sesji po fakcie). Bez tego zniknęłaby razem ze
        // swoim stanem i dałoby się odjąć te same minuty drugi raz — a to już błąd
        // w rozliczeniu, nie w wyświetlaniu.
        var adjusted = FromStoredGaps(entry)
            .Where(stored => !candidates.Any(gap => gap.StartAt == stored.StartAt && gap.EndAt == stored.EndAt))
            .Where(stored => entry.Adjustments.Any(adjustment =>
                adjustment.GapStartAt == stored.StartAt && adjustment.GapEndAt == stored.EndAt));
        candidates.AddRange(adjusted);

        return candidates
            .Select(candidate => candidate with { Counted = ApplyAdjustments(entry, candidate) })
            .OrderBy(gap => gap.StartAt)
            .ToList();
    }

    /// <summary>Odstępy między kolejnymi zapisami w zasięgu wpisu, od progu ciągłości w górę.</summary>
    private IEnumerable<EntryGap> FromJournal(IReadOnlyList<DateTime> moments)
    {
        for (var index = 1; index < moments.Count; index++)
        {
            var startAt = moments[index - 1];
            var endAt = moments[index];
            var minutes = (int)Math.Round((endAt - startAt).TotalMinutes);
            if (minutes <= options.SessionContinuationGapMinutes)
            {
                continue; // ciągła praca, nie przerwa
            }

            yield return new EntryGap(startAt, endAt, minutes, minutes <= options.SessionFlaggedGapMinutes);
        }
    }

    private static IEnumerable<EntryGap> FromStoredGaps(TimeEntry entry)
        => DetectedGaps.Deserialize(entry.DetectedGapsJson)
            .Select(gap => new EntryGap(gap.StartAt, gap.EndAt, gap.Minutes, Counted: true));

    /// <summary>
    /// Decyzja człowieka bije regułę progu. Liczy się OSTATNIA korekta na tym zakresie:
    /// odjęcie po doliczeniu ma znaczyć „jednak nie", a nie „już to raz robiłeś".
    /// </summary>
    private static bool ApplyAdjustments(TimeEntry entry, EntryGap gap)
    {
        var last = entry.Adjustments
            .Where(adjustment => adjustment.GapStartAt == gap.StartAt && adjustment.GapEndAt == gap.EndAt)
            .OrderBy(adjustment => adjustment.CreatedAt)
            .ThenBy(adjustment => adjustment.Id)
            .LastOrDefault();

        return last?.Kind switch
        {
            AdjustmentKind.GapAddition => true,
            AdjustmentKind.DetectedGapSubtraction => false,
            _ => gap.Counted,
        };
    }
}
