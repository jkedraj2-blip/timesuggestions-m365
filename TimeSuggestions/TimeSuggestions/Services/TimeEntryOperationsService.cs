using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

public enum TimeEntryOperationStatus
{
    Success,
    NotFound,

    /// <summary>Wpis rozliczony (zarchiwizowany) — operacje na nim są zablokowane, jak przy edycji.</summary>
    Archived,

    /// <summary>Konflikt reguł (np. niezmiennik nakładania) — komunikat mówi, co blokuje.</summary>
    Conflict,

    /// <summary>Żądanie poprawne składniowo, ale bez sensu domenowego (np. unmerge wpisu bez scalenia).</summary>
    Invalid,
}

/// <summary>Jawny wynik operacji — kontroler tłumaczy status na kod HTTP, komunikaty po polsku.</summary>
public record TimeEntryOperationResult(
    TimeEntryOperationStatus Status,
    string? Message = null,
    List<TimeEntry>? Entries = null);

/// <summary>Kierunek doliczania przerwy względem wpisu na globalnej osi dnia.</summary>
public enum GapDirection
{
    Before,
    After,
}

/// <summary>
/// Operacje prawnika na wpisach czasu: scalanie sesji, korekty przerw i szybkie korekty.
///
/// Niezmiennik nadrzędny, implementowany wprost w <see cref="FindBlocker"/>:
/// KAŻDA MINUTA DOBY NALEŻY DO NAJWYŻEJ JEDNEGO WPISU CZASU. Wszystkie reguły
/// anty-podwójne z niego wynikają: przerwa doliczona do wpisu A przestaje być wolna
/// dla wpisu B (bo weszła w przedział A), scalenie "z przerwami" jest legalne tylko
/// dla przerw wolnych, a walidacja odbywa się na backendzie — klientowi nie ufamy.
/// W niezmienniku uczestniczą wpisy każdego źródła (dokumentowe i kalendarzowe)
/// oraz oczekujące sugestie (jako rezerwacje minut do rozstrzygnięcia).
///
/// Wpisy zarchiwizowane: żadna operacja nie działa (rozliczonego czasu nie wolno
/// po cichu zmieniać). DurationMinutes po każdej mutacji = suma sesji + suma korekt,
/// przeliczana i zapisywana — korekty są dziennikiem audytowym.
/// </summary>
public class TimeEntryOperationsService(
    AppDbContext db,
    IOptions<SuggestionOptions> optionsAccessor,
    EntryGapService entryGaps)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    private readonly TimeZoneInfo businessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(optionsAccessor.Value.BusinessTimeZoneId);

    /// <summary>
    /// Scala wpisy tej samej sesji dokumentu w jeden. Warunki: ≥2 wpisy, ten sam
    /// dokument, ten sam dzień, żaden nie zarchiwizowany, a między pierwszym
    /// a ostatnim nie ma ŻADNEJ innej pozycji (wpisu ani oczekującej sugestii,
    /// dowolnego źródła) — inaczej 409 z tytułem blokującej pozycji.
    /// includeGaps dolicza luki między sesjami (wolne z definicji po teście
    /// sąsiedztwa) i zapisuje GapAddition per lukę. Scalenie jest odwracalne
    /// (unmerge) do momentu archiwizacji.
    /// </summary>
    public async Task<TimeEntryOperationResult> MergeAsync(
        IReadOnlyList<int> timeEntryIds,
        bool includeGaps,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var ids = timeEntryIds.Distinct().ToList();
        if (ids.Count < 2)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalenie wymaga co najmniej dwóch różnych wpisów.");
        }

        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .Include(entry => entry.Suggestions)
            .Include(entry => entry.Adjustments)
            .Where(entry => ids.Contains(entry.Id))
            .ToListAsync(cancellationToken);
        if (entries.Count != ids.Count)
        {
            return new(TimeEntryOperationStatus.NotFound, "Któryś ze wskazanych wpisów nie istnieje.");
        }

        if (entries.Any(entry => entry.ArchivedAt is not null))
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega scalaniu.");
        }

        // Ten sam dokument: wszystkie wpisy dokumentowe i wszystkie sugestie składowe
        // wskazują jeden plik. Scalanie międzyźródłowe jest zabronione z definicji.
        var externalIds = entries
            .SelectMany(entry => entry.Suggestions)
            .Select(suggestion => suggestion.ExternalId)
            .Distinct()
            .ToList();
        if (entries.Any(entry => entry.Source != SuggestionSource.Document)
            || entries.Any(entry => entry.Suggestions.Count == 0)
            || externalIds.Count != 1)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalać można wyłącznie wpisy tego samego dokumentu.");
        }

        if (entries.Select(entry => entry.EntryDate).Distinct().Count() != 1)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalać można wyłącznie wpisy z tego samego dnia.");
        }

        var ordered = entries.OrderBy(entry => entry.StartedAt).ToList();
        // Najpóźniejszy KONIEC, nie koniec ostatniego po starcie: wpis z podniesionym
        // czasem potrafi zawierać w sobie następny (pole „Czas" przy zatwierdzaniu nie ma
        // górnej granicy), a wynik kończący się wcześniej niż składowa wypuszczałby jej
        // minuty z zasięgu — stawałyby się „wolne" na osi dnia, choć są rozliczone.
        var spanEnd = ordered.Max(entry => entry.EndedAt);

        // Sąsiedztwo sprawdzane na PRZERWACH MIĘDZY scalanymi wpisami, a nie na całym
        // przedziale: przerwy to jedyny teren, który wynik zajmuje dodatkowo. Badanie
        // całego przedziału odrzucało scalenie także wtedy, gdy obca pozycja zachodziła
        // na składową JUŻ WCZEŚNIEJ — scalenie tego nie pogarszało, a operacja była
        // zablokowana na stałe (patrz ta sama poprawka w SuggestionOperationsService).
        var dayItems = await LoadDayItemsAsync(ordered[0].EntryDate, ids, cancellationToken);
        foreach (var (previous, next) in ordered.Zip(ordered.Skip(1)))
        {
            if (previous.EndedAt >= next.StartedAt)
            {
                continue; // wpisy przylegają albo już się nakładają — nie ma nowego terenu
            }

            var gapBlocker = FindBlockerInItems(dayItems, previous.EndedAt, next.StartedAt);
            if (gapBlocker is not null)
            {
                return new(TimeEntryOperationStatus.Conflict, $"Scalenie blokuje pozycja: {gapBlocker}.");
            }
        }

        var survivor = ordered[0];
        var gapAdditions = new List<TimeEntryAdjustment>();
        var totalMinutes = ordered.Sum(entry => entry.DurationMinutes);

        if (includeGaps)
        {
            foreach (var (previous, next) in ordered.Zip(ordered.Skip(1)))
            {
                var gapMinutes = FreeGapMinutes(previous.EndedAt, next.StartedAt) ?? 0;
                if (gapMinutes <= 0)
                {
                    continue;
                }

                totalMinutes += gapMinutes;
                gapAdditions.Add(new TimeEntryAdjustment
                {
                    TimeEntry = survivor,
                    Minutes = gapMinutes,
                    Kind = AdjustmentKind.GapAddition,
                    GapStartAt = previous.EndedAt,
                    GapEndAt = next.StartedAt,
                    CreatedAt = nowUtc,
                });
            }
        }

        // Wynik: jeden wpis obejmujący cały przedział; wykryte przerwy składowych
        // łączone (przycisk "Odejmij przerwę" ma dalej działać), korekty składowych
        // przenoszone na wynik (dziennik audytowy nie może zniknąć przy scaleniu).
        survivor.EndedAt = spanEnd;
        survivor.DurationMinutes = totalMinutes;
        survivor.DetectedGapsJson = DetectedGaps.Serialize(entries
            .SelectMany(entry => DetectedGaps.Deserialize(entry.DetectedGapsJson))
            .OrderBy(gap => gap.StartAt)
            .ToList());
        db.TimeEntryAdjustments.AddRange(gapAdditions);

        foreach (var component in ordered.Skip(1))
        {
            foreach (var suggestion in component.Suggestions.ToList())
            {
                suggestion.TimeEntry = survivor;
            }

            foreach (var adjustment in component.Adjustments.ToList())
            {
                adjustment.TimeEntry = survivor;
            }

            db.TimeEntries.Remove(component);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: [survivor]);
    }

    /// <summary>
    /// Odwraca scalenie: przywraca wpisy składowe z ich sesji (dane sesji żyją
    /// w sugestiach i dzienniku DocumentActivity — scalenie ich nie zmieniało).
    /// Korekty scalonego wpisu przepadają świadomie: dotyczyły bytu, który przestaje
    /// istnieć, a sesje wracają w swojej pierwotnej postaci.
    /// </summary>
    public async Task<TimeEntryOperationResult> UnmergeAsync(
        int timeEntryId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(e => e.Case)
            .Include(e => e.Suggestions)
            .FirstOrDefaultAsync(e => e.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        if (entry.ArchivedAt is not null)
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega rozdzieleniu.");
        }

        if (entry.Suggestions.Count < 2)
        {
            return new(TimeEntryOperationStatus.Invalid, "Wpis nie powstał ze scalenia — nie ma czego rozdzielać.");
        }

        var restored = new List<TimeEntry>();
        foreach (var suggestion in entry.Suggestions.OrderBy(s => s.StartedAt).ToList())
        {
            var component = new TimeEntry
            {
                CaseId = entry.CaseId,
                Case = entry.Case,
                EntryDate = suggestion.EntryDate,
                StartedAt = suggestion.StartedAt,
                EndedAt = SuggestionSpan.EndOf(suggestion, businessTimeZone),
                DurationMinutes = suggestion.DurationMinutes,
                Description = entry.Description,
                CreatedFromSuggestion = true,
                Source = suggestion.Source,
                DetectedGapsJson = suggestion.DetectedGapsJson,
                CreatedAt = nowUtc,
            };
            suggestion.TimeEntry = component;
            db.TimeEntries.Add(component);
            restored.Add(component);
        }

        db.TimeEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: restored);
    }

    /// <summary>
    /// Przełącza przerwę leżącą w godzinach wpisu między „liczona" a „nieliczona".
    /// Zakres musi pochodzić z <see cref="EntryGapService"/> (czyli z historii wersji),
    /// nie z wartości wymyślonych przez klienta, a kierunek musi zgadzać się z jej
    /// obecnym stanem — dzięki temu operacja jest idempotentna bez osobnej reguły
    /// „już to robiłeś": drugie kliknięcie tego samego przycisku po prostu nie ma
    /// przycisku, bo interfejs pokazuje wtedy ten przeciwny.
    ///
    /// Minuty przerwy leżą w zasięgu wpisu, więc ich doliczenie nie może zabrać czasu
    /// żadnej innej pozycji — niezmiennik nakładania zostaje nietknięty.
    /// </summary>
    public async Task<TimeEntryOperationResult> SetGapCountedAsync(
        int timeEntryId,
        DateTime gapStartAt,
        DateTime gapEndAt,
        bool counted,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(e => e.Case)
            .Include(e => e.Suggestions)
            .Include(e => e.Adjustments)
            .FirstOrDefaultAsync(e => e.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        if (entry.ArchivedAt is not null)
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega korektom.");
        }

        var gap = (await entryGaps.LoadForEntryAsync(entry, cancellationToken))
            .FirstOrDefault(candidate => candidate.StartAt == gapStartAt && candidate.EndAt == gapEndAt);
        if (gap is null)
        {
            return new(TimeEntryOperationStatus.Invalid, "Ta przerwa nie pochodzi z historii tego wpisu.");
        }

        if (gap.Counted == counted)
        {
            return new(TimeEntryOperationStatus.Conflict, counted
                ? "Ta przerwa jest już wliczona w czas wpisu."
                : "Ta przerwa nie jest wliczona w czas wpisu.");
        }

        var newDuration = counted ? entry.DurationMinutes + gap.Minutes : entry.DurationMinutes - gap.Minutes;
        if (newDuration <= 0)
        {
            return new(TimeEntryOperationStatus.Conflict, "Po odjęciu przerwy czas wpisu spadłby do zera.");
        }

        // Limit blokuje wyłącznie WZROST: zatwierdzenie dopuszcza do 1440 min, więc wpis
        // powyżej 480 istnieje legalnie — odmowa przy ZMNIEJSZANIU zamykałaby drogę
        // w dół komunikatem o przekroczonym limicie (ta sama reguła w Round i Adjust).
        if (newDuration > entry.DurationMinutes && newDuration > SuggestionOptions.MaxDocumentDurationMinutes)
        {
            return new(TimeEntryOperationStatus.Conflict,
                $"Po doliczeniu przerwy czas wpisu przekroczyłby limit {SuggestionOptions.MaxDocumentDurationMinutes} minut.");
        }

        entry.Adjustments.Add(new TimeEntryAdjustment
        {
            Minutes = counted ? gap.Minutes : -gap.Minutes,
            Kind = counted ? AdjustmentKind.GapAddition : AdjustmentKind.DetectedGapSubtraction,
            GapStartAt = gapStartAt,
            GapEndAt = gapEndAt,
            CreatedAt = nowUtc,
        });
        entry.DurationMinutes = newDuration;

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: [entry]);
    }

    /// <summary>
    /// Zaokrągla czas wpisu do wielokrotności jednostki rozliczeniowej
    /// (<c>BillingIncrementMinutes</c>). Do NAJBLIŻSZEJ wielokrotności, a przy dokładnej
    /// połowie w DÓŁ: zaokrąglanie w górę „z automatu" powiększa rachunek klienta, a to
    /// ta sama reguła, przez którą wyleciał domyślny czas dokumentu — gdy wybieramy za
    /// użytkownika, mylimy się na niekorzyść kancelarii. Minimum to jedna jednostka,
    /// bo wpis na zero minut nie istnieje. Etykieta przycisku podaje wynik z góry, więc
    /// kierunek zaokrąglenia nie jest niespodzianką.
    /// </summary>
    public async Task<TimeEntryOperationResult> RoundAsync(
        int timeEntryId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(e => e.Case)
            .Include(e => e.Suggestions)
            .Include(e => e.Adjustments)
            .FirstOrDefaultAsync(e => e.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        if (entry.ArchivedAt is not null)
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega korektom.");
        }

        var rounded = RoundToIncrement(entry.DurationMinutes, options.BillingIncrementMinutes);
        if (rounded == entry.DurationMinutes)
        {
            return new(TimeEntryOperationStatus.Invalid,
                $"Czas wpisu jest już wielokrotnością {options.BillingIncrementMinutes} min.");
        }

        // Tylko wzrost — zaokrąglenie W DÓŁ wpisu powyżej limitu jest zmniejszeniem.
        if (rounded > entry.DurationMinutes && rounded > SuggestionOptions.MaxDocumentDurationMinutes)
        {
            return new(TimeEntryOperationStatus.Conflict,
                $"Po zaokrągleniu czas wpisu przekroczyłby limit {SuggestionOptions.MaxDocumentDurationMinutes} minut.");
        }

        db.TimeEntryAdjustments.Add(new TimeEntryAdjustment
        {
            TimeEntry = entry,
            Minutes = rounded - entry.DurationMinutes,
            Kind = AdjustmentKind.Rounding,
            CreatedAt = nowUtc,
        });
        entry.DurationMinutes = rounded;

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: [entry]);
    }

    /// <summary>Najbliższa wielokrotność jednostki, połowa w dół, minimum jedna jednostka.</summary>
    public static int RoundToIncrement(int minutes, int incrementMinutes)
    {
        if (incrementMinutes <= 0)
        {
            return minutes;
        }

        var units = (int)Math.Ceiling(minutes / (double)incrementMinutes - 0.5);
        return Math.Max(1, units) * incrementMinutes;
    }

    /// <summary>
    /// Dolicza wolną lukę między wpisem a sąsiednią pozycją na globalnej osi dnia.
    /// Luka liczona SERWEROWO (klient nie przysyła minut) i tylko jeśli wolna —
    /// sąsiadem jest najbliższa pozycja (wpis dowolnego źródła albo oczekująca
    /// sugestia) tego samego dnia. Doliczona luka wchodzi w przedział wpisu,
    /// więc z mocy niezmiennika przestaje być dostępna dla innych wpisów.
    /// </summary>
    public async Task<TimeEntryOperationResult> ClaimGapAsync(
        int timeEntryId,
        GapDirection direction,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(e => e.Case)
            .Include(e => e.Suggestions)
            .Include(e => e.Adjustments)
            .FirstOrDefaultAsync(e => e.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        if (entry.ArchivedAt is not null)
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega korektom.");
        }

        var items = await LoadDayItemsAsync(entry.EntryDate, excludeEntryIds: [entry.Id], cancellationToken);

        DateTime gapStartAt;
        DateTime gapEndAt;
        if (direction == GapDirection.Before)
        {
            var neighborEnd = items
                .Where(item => item.End <= entry.StartedAt)
                .Select(item => (DateTime?)item.End)
                .Max();
            if (neighborEnd is null)
            {
                return new(TimeEntryOperationStatus.Conflict, "Brak sąsiedniej pozycji przed wpisem — nie ma przerwy do doliczenia.");
            }

            gapStartAt = neighborEnd.Value;
            gapEndAt = entry.StartedAt;
        }
        else
        {
            var neighborStart = items
                .Where(item => item.Start >= entry.EndedAt)
                .Select(item => (DateTime?)item.Start)
                .Min();
            if (neighborStart is null)
            {
                return new(TimeEntryOperationStatus.Conflict, "Brak sąsiedniej pozycji po wpisie — nie ma przerwy do doliczenia.");
            }

            gapStartAt = entry.EndedAt;
            gapEndAt = neighborStart.Value;
        }

        var gapMinutes = FreeGapMinutes(gapStartAt, gapEndAt) ?? 0;
        if (gapMinutes <= 0)
        {
            return new(TimeEntryOperationStatus.Conflict, "Między pozycjami nie ma wolnej przerwy.");
        }

        // Wybór najbliższego sąsiada gwarantuje pustkę przedziału, ale niezmiennik
        // weryfikujemy wprost — zastane dane (sprzed walidacji nakładania) mogłyby
        // zawierać pozycje zachodzące na siebie.
        var blocker = FindBlockerInItems(items, gapStartAt, gapEndAt);
        if (blocker is not null)
        {
            return new(TimeEntryOperationStatus.Conflict, $"Przerwa nie jest wolna — blokuje ją pozycja: {blocker}.");
        }

        if (direction == GapDirection.Before)
        {
            entry.StartedAt = gapStartAt;
        }
        else
        {
            entry.EndedAt = gapEndAt;
        }

        db.TimeEntryAdjustments.Add(new TimeEntryAdjustment
        {
            TimeEntry = entry,
            Minutes = gapMinutes,
            Kind = AdjustmentKind.GapAddition,
            GapStartAt = gapStartAt,
            GapEndAt = gapEndAt,
            CreatedAt = nowUtc,
        });
        entry.DurationMinutes += gapMinutes;

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: [entry]);
    }

    /// <summary>Szybka korekta ±N minut. Wynik musi zostać w przedziale (0, MaxDocumentDurationMinutes].</summary>
    public async Task<TimeEntryOperationResult> AdjustAsync(
        int timeEntryId,
        int minutes,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(e => e.Case)
            .Include(e => e.Suggestions)
            .Include(e => e.Adjustments)
            .FirstOrDefaultAsync(e => e.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        if (entry.ArchivedAt is not null)
        {
            return new(TimeEntryOperationStatus.Archived, "Wpis rozliczony nie podlega korektom.");
        }

        var newDuration = entry.DurationMinutes + minutes;
        if (newDuration <= 0)
        {
            return new(TimeEntryOperationStatus.Conflict, "Po korekcie czas wpisu spadłby do zera.");
        }

        // Tylko wzrost — korekta ujemna wpisu powyżej limitu musi przechodzić.
        if (newDuration > entry.DurationMinutes && newDuration > SuggestionOptions.MaxDocumentDurationMinutes)
        {
            return new(TimeEntryOperationStatus.Conflict,
                $"Po korekcie czas wpisu przekroczyłby limit {SuggestionOptions.MaxDocumentDurationMinutes} minut.");
        }

        db.TimeEntryAdjustments.Add(new TimeEntryAdjustment
        {
            TimeEntry = entry,
            Minutes = minutes,
            Kind = AdjustmentKind.QuickAdjustment,
            CreatedAt = nowUtc,
        });
        entry.DurationMinutes = newDuration;

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Entries: [entry]);
    }

    /// <summary>Pozycja na osi dnia: wpis (każdego źródła i stanu) albo oczekująca sugestia.</summary>
    private sealed record DayItem(DateTime Start, DateTime End, string Title);

    /// <summary>
    /// Wszystkie pozycje sąsiednich dni (wpisy + oczekujące sugestie) — dzień ±1,
    /// bo spotkanie kalendarzowe może przechodzić przez północ, choć sesje nie.
    /// </summary>
    private async Task<List<DayItem>> LoadDayItemsAsync(
        DateOnly date,
        IReadOnlyCollection<int> excludeEntryIds,
        CancellationToken cancellationToken)
    {
        var from = date.AddDays(-1);
        var to = date.AddDays(1);

        var entries = await db.TimeEntries
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to
                && !excludeEntryIds.Contains(entry.Id))
            .Select(entry => new { entry.StartedAt, entry.EndedAt, entry.Description })
            .ToListAsync(cancellationToken);

        var pending = await db.Suggestions
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                && suggestion.EntryDate >= from && suggestion.EntryDate <= to)
            .Select(suggestion => new
            {
                suggestion.StartedAt,
                suggestion.DurationMinutes,
                suggestion.LastActivityAt,
                suggestion.Title,
            })
            .ToListAsync(cancellationToken);

        return entries
            .Select(entry => new DayItem(entry.StartedAt, entry.EndedAt, $"wpis „{entry.Description}\""))
            .Concat(pending.Select(suggestion => new DayItem(
                suggestion.StartedAt,
                SuggestionSpan.EndOf(
                    suggestion.StartedAt, suggestion.DurationMinutes, suggestion.LastActivityAt, businessTimeZone),
                $"oczekująca sugestia „{suggestion.Title}\"")))
            .ToList();
    }

    /// <summary>
    /// Minuty wolnej przerwy albo null przy zachodzeniu pozycji — PODŁOGA, nie
    /// zaokrąglenie. Ta sama reguła i to samo uzasadnienie co w
    /// <see cref="SuggestionOperationsService"/>: minuta zaokrąglona w górę to minuta,
    /// której w przerwie nie ma, a doliczona zostawia trwałe nakładanie blokujące
    /// późniejsze operacje na tej osi.
    /// </summary>
    private static int? FreeGapMinutes(DateTime startAt, DateTime endAt)
        => TimeAxis.FreeGapMinutes(startAt, endAt);

    /// <summary>
    /// Niezmiennik nakładania w jednym miejscu: zwraca opis pierwszej pozycji
    /// zachodzącej na przedział [start, end) albo null, gdy przedział jest wolny.
    /// </summary>
    private static string? FindBlockerInItems(IReadOnlyList<DayItem> items, DateTime start, DateTime end)
        => items
            .Where(item => TimeAxis.Overlaps(start, end, item.Start, item.End))
            .OrderBy(item => item.Start)
            .Select(item => BusinessMoment.Describe(item.Title, item.Start, item.End))
            .FirstOrDefault();
}
