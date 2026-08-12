using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Oś czasu: agregacja per dzień dla paska miesiąca i lista pozycji jednego dnia.
/// Pasek miesiąca zasilają DWA zapytania grupujące (sugestie i wpisy) na cały zakres —
/// nie 31 osobnych żądań; scalanie wyników w pamięci jest tanie (maks. 31 kluczy).
/// </summary>
public class TimelineService(
    AppDbContext db,
    IOptions<SuggestionOptions> optionsAccessor,
    EntryGapService entryGaps,
    SessionLabelService sessionLabels)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;
    public async Task<List<TimelineDayDto>> GetRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var pendingByDate = await db.Suggestions
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                && suggestion.EntryDate >= from && suggestion.EntryDate <= to)
            .GroupBy(suggestion => suggestion.EntryDate)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var entriesByDate = await db.TimeEntries
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to)
            .GroupBy(entry => entry.EntryDate)
            .Select(group => new
            {
                Date = group.Key,
                Active = group.Count(entry => entry.ArchivedAt == null),
                Archived = group.Count(entry => entry.ArchivedAt != null),
            })
            .ToListAsync(cancellationToken);

        // Zwracamy tylko dni z pozycjami — puste dni frontend wygasza sam,
        // a odpowiedź nie rośnie z długością zakresu.
        return pendingByDate
            .Select(day => (day.Date, Pending: day.Count, Active: 0, Archived: 0))
            .Concat(entriesByDate.Select(day => (day.Date, Pending: 0, day.Active, day.Archived)))
            .GroupBy(day => day.Date)
            .Select(group => new TimelineDayDto(
                group.Key,
                group.Sum(day => day.Pending),
                group.Sum(day => day.Active),
                group.Sum(day => day.Archived)))
            .OrderBy(day => day.Date)
            .ToList();
    }

    public async Task<List<TimelineItemDto>> GetDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var pendingSuggestions = await db.Suggestions
            .Include(suggestion => suggestion.Case)
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending && suggestion.EntryDate == date)
            .ToListAsync(cancellationToken);

        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .Include(entry => entry.Suggestions)
            .Where(entry => entry.EntryDate == date)
            .ToListAsync(cancellationToken);

        // Koniec pozycji to koniec ZASIĘGU sugestii: po scaleniu sesji czas (suma) jest
        // krótszy niż odcinek, który sugestia realnie zajmuje, a oś czasu ma pokazywać
        // zajęty teren — inaczej scalona sesja wyglądałaby na krótszą, niż jest.
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

        return pendingSuggestions
            .Select(suggestion => new TimelineItemDto(
                Type: "suggestion",
                suggestion.Id,
                suggestion.Source,
                suggestion.StartedAt,
                SuggestionSpan.EndOf(suggestion, businessTimeZone),
                suggestion.DurationMinutes,
                suggestion.Title,
                suggestion.Case?.Name,
                suggestion.Case?.CaseNumber,
                suggestion.Case?.ClientName,
                Status: "pending",
                ExternalId: suggestion.Source == SuggestionSource.Document ? suggestion.ExternalId : null))
            .Concat(entries.Select(entry => new TimelineItemDto(
                Type: "timeEntry",
                entry.Id,
                entry.Source,
                entry.StartedAt,
                entry.EndedAt,
                entry.DurationMinutes,
                entry.Description,
                entry.Case?.Name,
                entry.Case?.CaseNumber,
                entry.Case?.ClientName,
                Status: entry.ArchivedAt == null ? "active" : "archived",
                ExternalId: entry.Source == SuggestionSource.Document
                    ? entry.Suggestions.FirstOrDefault()?.ExternalId
                    : null)))
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }

    /// <summary>
    /// Chronologia modyfikacji pliku z append-only dziennika DocumentActivity:
    /// godzina każdej wersji (strefa biznesowa), rozmiar i przerwa od poprzedniej.
    /// Dane już są w bazie — to czysty odczyt, bez wywołań Graph.
    ///
    /// Przerwy dostają DWA znaczniki zamiast jednego „mieści się w przedziale
    /// wykrywanym". Jeden przedział znaczył, że przerwa 20 min była opisana, a 110 min
    /// nie — dłuższa i ważniejsza wyglądała jak zwykły odstęp między zapisami. Progi
    /// zostają po stronie serwera, bo to ta sama konfiguracja, którą tnie sesje silnik.
    ///
    /// IsCurrent dostaje KAŻDY wiersz o najnowszym numerze wersji, a nie tylko ostatni
    /// wiersz listy. Ma to konkretny skutek dla podglądu zmian: treści bieżącej wersji
    /// Graph nie wydaje spod adresu /versions/{id}/content (odpowiada 400 invalidRequest),
    /// bo jest nią po prostu aktualna treść pliku. Przy ciągłej edycji ta sama, jeszcze
    /// niezapieczętowana wersja trafia do dziennika wiele razy (kolejne momenty tego
    /// samego numeru) — oznaczanie samego ostatniego wiersza sprawiało, że wcześniejsze
    /// próbki tej wersji dostawały adres wersji i porównanie kończyło się błędem.
    ///
    /// IsSameVersionAsPrevious mówi UI, że wiersz to kolejna próbka tej samej wersji:
    /// Graph trzyma jeden snapshot na numer wersji, więc stanu pośredniego nie ma
    /// i nie ma czego z czym porównywać.
    /// </summary>
    /// <summary>
    /// Chronologia pliku razem ze WSZYSTKIMI pozycjami, które z niej powstały. Stan
    /// pozycji podawał wcześniej ten, kto historię otwierał, więc ta sama historia
    /// mówiła co innego zależnie od miejsca: w archiwum widać było, że fragment jest
    /// rozliczony, a w sugestiach ta sama praca wyglądała na nierozliczoną. Teraz stan
    /// każdej sesji przychodzi z bazy i jest jeden, niezależnie od tego, skąd się patrzy.
    /// </summary>
    public async Task<DocumentHistoryDto> GetDocumentHistoryAsync(
        string externalId,
        CancellationToken cancellationToken)
        => new(
            await GetDocumentActivityAsync(externalId, cancellationToken),
            await GetDocumentSessionsAsync(externalId, cancellationToken));

    /// <summary>
    /// Pozycje jednego pliku: wpisy czasu (rozliczone i nie) oraz sugestie, które nie
    /// stały się wpisem. Sugestia ZATWIERDZONA jest pominięta celowo — reprezentuje ją
    /// wpis, a policzona podwójnie kolorowałaby te same zapisy dwoma stanami naraz.
    /// </summary>
    private async Task<List<DocumentSessionDto>> GetDocumentSessionsAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

        var suggestions = await db.Suggestions
            .Where(suggestion => suggestion.Source == SuggestionSource.Document
                && suggestion.ExternalId == externalId)
            .ToListAsync(cancellationToken);

        var entries = await db.TimeEntries
            .Where(entry => entry.Suggestions.Any(suggestion => suggestion.ExternalId == externalId))
            .Include(entry => entry.Suggestions)
            .Include(entry => entry.Adjustments)
            .ToListAsync(cancellationToken);

        var labels = await sessionLabels.LoadAsync(suggestions, cancellationToken);
        var gapsByEntry = await entryGaps.LoadAsync(entries, cancellationToken);

        var sessions = entries.Select(entry => new DocumentSessionDto(
            entry.ArchivedAt is null ? "unsettled" : "settled",
            entry.StartedAt,
            entry.EndedAt,
            SuggestionId: null,
            entry.Id,
            LabelOfEntry(entry, labels),
            DetectedGapDto.FromEntryGaps(gapsByEntry.GetValueOrDefault(entry.Id, []))))
            .ToList();

        sessions.AddRange(suggestions
            .Where(suggestion => suggestion.Status != SuggestionStatus.Approved)
            .Select(suggestion => new DocumentSessionDto(
                KindOf(suggestion.Status),
                suggestion.StartedAt,
                SuggestionSpan.EndOf(suggestion, businessTimeZone),
                suggestion.Id,
                TimeEntryId: null,
                labels.GetValueOrDefault(suggestion.Id),
                DetectedGapDto.FromJson(suggestion.DetectedGapsJson))));

        return sessions.OrderBy(session => session.StartAt).ToList();
    }

    private static string KindOf(SuggestionStatus status) => status switch
    {
        SuggestionStatus.Rejected => "rejected",
        SuggestionStatus.Archived => "archived",
        _ => "pending",
    };

    /// <summary>Numer wpisu to numer jego najwcześniejszej sesji — jak na karcie wpisu.</summary>
    private static string? LabelOfEntry(TimeEntry entry, IReadOnlyDictionary<int, string> labels)
    {
        var first = entry.Suggestions.OrderBy(suggestion => suggestion.StartedAt).FirstOrDefault();
        return first is null ? null : labels.GetValueOrDefault(first.Id);
    }

    public async Task<List<DocumentActivityDto>> GetDocumentActivityAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

        var activities = (await db.DocumentActivities
                .AsNoTracking()
                .Where(activity => activity.ExternalId == externalId)
                .ToListAsync(cancellationToken))
            .OrderBy(activity => activity.OccurredAt)
            .ToList();

        // Najnowsza wersja to ta z ostatniego wiersza — dziennik jest posortowany po
        // momencie, a numery wersji Graph rosną razem z czasem.
        var currentVersionId = activities.Count == 0 ? null : activities[^1].VersionId;

        var result = new List<DocumentActivityDto>(activities.Count);
        DateTime? previousUtc = null;
        for (var index = 0; index < activities.Count; index++)
        {
            var activity = activities[index];
            var occurredUtc = AsUtc(activity.OccurredAt);
            int? gapMinutes = previousUtc is DateTime previous
                ? (int)Math.Round((occurredUtc - previous).TotalMinutes)
                : null;

            result.Add(new DocumentActivityDto(
                activity.VersionId,
                TimeZoneInfo.ConvertTimeFromUtc(occurredUtc, businessTimeZone),
                activity.Size,
                gapMinutes,
                IsSessionBreak: gapMinutes > options.SessionContinuationGapMinutes,
                SplitsSession: gapMinutes > options.SessionFlaggedGapMinutes,
                IsCurrent: activity.VersionId == currentVersionId,
                IsSameVersionAsPrevious: index > 0 && activities[index - 1].VersionId == activity.VersionId));
            previousUtc = occurredUtc;
        }

        return result;
    }

    /// <summary>Po odczycie z SQLite Kind bywa Unspecified — czasy trzymamy w UTC.</summary>
    private static DateTime AsUtc(DateTime dateTime) => dateTime.Kind == DateTimeKind.Utc
        ? dateTime
        : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
}
