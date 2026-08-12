using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>Sąsiad sugestii na osi dnia razem z wolną luką dzielącą ich od siebie.</summary>
/// <param name="SuggestionId">Id sąsiada, jeśli jest sugestią oczekującą; null dla wpisu czasu (podziału z nim nie ma).</param>
/// <param name="Title">Nazwa sąsiada — UI mówi, komu ta luka zostanie odebrana albo z kim podzielona.</param>
/// <param name="GapMinutes">Minuty wolnej luki (0, gdy pozycje przylegają).</param>
/// <param name="CanMerge">
/// Czy scalenie z tym sąsiadem NAPRAWDĘ przejdzie: ta sama sugestia oczekująca, ten sam plik
/// i ten sam dzień rozliczeniowy. Sąsiad bywa znaleziony za północą (luka szuka o dobę w obie
/// strony, bo spotkanie może przechodzić przez dzień) — przycisk „Scal sesje" wystawiony dla
/// takiego sąsiada kończył się błędem „tylko z tego samego dnia", czyli obiecywał operację
/// odrzucaną przez tę samą warstwę, która go zaproponowała.
/// </param>
public record SuggestionNeighbor(int? SuggestionId, string Title, int GapMinutes, bool CanMerge);

/// <summary>Wolne luki wokół sugestii; null po którejś stronie = nie ma czego doliczać.</summary>
public record SuggestionGaps(SuggestionNeighbor? Before, SuggestionNeighbor? After);

/// <summary>Wynik operacji na sugestiach — kontroler tłumaczy status na kod HTTP.</summary>
public record SuggestionOperationResult(
    TimeEntryOperationStatus Status,
    string? Message = null,
    List<Suggestion>? Suggestions = null);

/// <summary>
/// Operacje prawnika na SUGESTIACH (przed zatwierdzeniem): scalanie sesji tego samego
/// dokumentu i doliczanie wolnych luk. Odpowiednik <see cref="TimeEntryOperationsService"/>
/// o krok wcześniej — poprawianie czasu na etapie sugestii oszczędza zatwierdzanie
/// i późniejsze scalanie wpisów.
///
/// Obowiązuje ten sam niezmiennik nadrzędny: KAŻDA MINUTA DOBY NALEŻY DO NAJWYŻEJ
/// JEDNEJ POZYCJI (wpisu albo oczekującej sugestii). Stąd bierze się reguła, o którą
/// chodziło najbardziej: luka jest do doliczenia TYLKO wtedy, gdy jest naprawdę wolna.
/// Jeśli prawnik w tym czasie pracował nad innym dokumentem, ta „przerwa" jest już
/// rozliczona w historii tamtego pliku — i wtedy nie ma czego doliczać, a UI nie
/// pokazuje przycisku. Górny limit (MaxClaimableGapMinutes) odcina dziury, które nie
/// są przerwą w pracy, tylko inną częścią dnia.
///
/// Każda taka poprawka ustawia IsUserAdjusted: od tego momentu sync nie przelicza
/// sugestii ani nie odtwarza sesji z jej zasięgu.
/// </summary>
public class SuggestionOperationsService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    private readonly TimeZoneInfo businessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(optionsAccessor.Value.BusinessTimeZoneId);

    /// <summary>
    /// Scala sesje TEGO SAMEGO dokumentu z tego samego dnia w jedną sugestię.
    /// Czas wyniku = suma czasów składowych (plus wolne luki, gdy includeGaps),
    /// zasięg = od najwcześniejszej kotwicy do najpóźniejszej modyfikacji.
    /// Wykryte przerwy składowych wędrują do wyniku — przycisk „Odejmij przerwę"
    /// ma działać dalej po zatwierdzeniu.
    /// </summary>
    public async Task<SuggestionOperationResult> MergeAsync(
        IReadOnlyList<int> suggestionIds,
        bool includeGaps,
        CancellationToken cancellationToken)
    {
        var ids = suggestionIds.Distinct().ToList();
        if (ids.Count < 2)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalenie wymaga co najmniej dwóch różnych sugestii.");
        }

        var suggestions = await db.Suggestions
            .Where(suggestion => ids.Contains(suggestion.Id))
            .ToListAsync(cancellationToken);
        if (suggestions.Count != ids.Count)
        {
            return new(TimeEntryOperationStatus.NotFound, "Któraś ze wskazanych sugestii nie istnieje.");
        }

        if (suggestions.Any(suggestion => suggestion.Status != SuggestionStatus.Pending))
        {
            return new(TimeEntryOperationStatus.Conflict, "Scalać można wyłącznie sugestie oczekujące.");
        }

        if (suggestions.Any(suggestion => suggestion.Source != SuggestionSource.Document)
            || suggestions.Select(suggestion => suggestion.ExternalId).Distinct().Count() != 1)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalać można wyłącznie sugestie tego samego dokumentu.");
        }

        if (suggestions.Select(suggestion => suggestion.EntryDate).Distinct().Count() != 1)
        {
            return new(TimeEntryOperationStatus.Invalid, "Scalać można wyłącznie sugestie z tego samego dnia.");
        }

        var ordered = suggestions.OrderBy(suggestion => suggestion.StartedAt).ToList();
        var items = await LoadDayItemsAsync(ordered[0].EntryDate, ids, cancellationToken);

        // Sąsiedztwo sprawdzane WYŁĄCZNIE na przerwach MIĘDZY scalanymi sesjami — to
        // jedyny teren, który wynik scalenia zajmuje dodatkowo (jego zasięg to najwyżej
        // suma składowych plus te przerwy). Wcześniej badaliśmy cały przedział od
        // pierwszego początku do ostatniego końca i scalenie potrafiło zostać odrzucone
        // z powodu nakładania, które ISTNIAŁO JUŻ WCZEŚNIEJ między składową a obcą
        // pozycją — sekundowe zachodzenie z zastanych danych blokowało wtedy operację
        // na zawsze, choć scalenie niczego nie pogarszało. UI proponowało ją mimo to,
        // bo CanMerge patrzy właśnie na przerwę między sąsiadami: jedna reguła po obu
        // stronach to koniec przycisków kończących się odmową.
        foreach (var (previous, next) in ordered.Zip(ordered.Skip(1)))
        {
            var gapStartAt = SuggestionSpan.EndOf(previous, businessTimeZone);
            if (gapStartAt >= next.StartedAt)
            {
                continue; // sesje przylegają albo już się nakładają — nie ma nowego terenu
            }

            var blocker = FindBlocker(items, gapStartAt, next.StartedAt);
            if (blocker is not null)
            {
                return new(TimeEntryOperationStatus.Conflict, $"Scalenie blokuje pozycja: {blocker}.");
            }
        }

        var survivor = ordered[0];
        var totalMinutes = ordered.Sum(suggestion => suggestion.DurationMinutes);
        if (includeGaps)
        {
            // Luka liczona od KOŃCA ZASIĘGU poprzedniczki: gdy jest nią wynik wcześniejszego
            // scalenia, jej czas (suma sesji) kończy się przed jej realnym końcem i liczenie
            // po czasie doliczyłoby minuty, które już są w sumie — ten sam kwadrans wchodziłby
            // do rozliczenia dwa razy.
            foreach (var (previous, next) in ordered.Zip(ordered.Skip(1)))
            {
                totalMinutes += FreeGapMinutes(
                    SuggestionSpan.EndOf(previous, businessTimeZone), next.StartedAt) ?? 0;
            }
        }

        survivor.DurationMinutes = totalMinutes;
        survivor.LastActivityAt = ordered.Max(suggestion => suggestion.LastActivityAt);
        survivor.DetectedGapsJson = DetectedGaps.Serialize(ordered
            .SelectMany(suggestion => DetectedGaps.Deserialize(suggestion.DetectedGapsJson))
            .OrderBy(gap => gap.StartAt)
            .ToList());
        // Scalona sugestia ma czas z co najmniej dwóch sesji — „czas do uzupełnienia"
        // przestaje być prawdą, nawet jeśli każda ze składowych miała jeden zapis.
        survivor.NeedsTimeReview = false;
        survivor.IsUserAdjusted = true;

        db.Suggestions.RemoveRange(ordered.Skip(1));

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Suggestions: [survivor]);
    }

    /// <summary>
    /// Rozdziela wolną lukę sąsiadującą z sugestią: <paramref name="minutes"/> minut bierze
    /// ta sugestia, <paramref name="neighborMinutes"/> sąsiednia. Podział jest JAWNY, a nie
    /// „po połowie" — prawnik widzi w interfejsie obie liczby i zmienia je przed zapisem,
    /// bo to on wie, po której stronie przerwy naprawdę pracował. Reszta (gdy suma jest
    /// mniejsza od luki) zostaje wolna: niczego nie dopisujemy za użytkownika.
    ///
    /// Brak obu wartości = cała luka do tej sugestii (skrót „Dolicz całość").
    /// Rozmiar luki liczy serwer na świeżo — klientowi nie ufamy, bo jego liczba mogła
    /// się zdezaktualizować między wyświetleniem przycisku a kliknięciem.
    /// </summary>
    public async Task<SuggestionOperationResult> ClaimGapAsync(
        int suggestionId,
        GapDirection direction,
        int? minutes,
        int? neighborMinutes,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new(TimeEntryOperationStatus.NotFound, "Sugestia nie istnieje.");
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return new(TimeEntryOperationStatus.Conflict, "Czas można poprawiać tylko w sugestii oczekującej.");
        }

        var items = await LoadDayItemsAsync(suggestion.EntryDate, [suggestion.Id], cancellationToken);
        var neighbor = FindNeighbor(items, suggestion, direction);
        if (neighbor is null || neighbor.GapMinutes == 0)
        {
            return new(TimeEntryOperationStatus.Conflict, "Nie ma wolnej przerwy po tej stronie sugestii.");
        }

        var claimedMinutes = minutes ?? neighbor.GapMinutes;
        var forNeighborMinutes = neighborMinutes ?? 0;

        // Ujemny podział skracałby sugestię pod pozorem doliczania przerwy — od skracania
        // jest Edytuj. Kontrakt HTTP już to odrzuca; tu chronimy wywołanie wewnętrzne.
        if (claimedMinutes < 0 || forNeighborMinutes < 0)
        {
            return new(TimeEntryOperationStatus.Invalid, "Podział przerwy nie może być ujemny.");
        }

        if (claimedMinutes + forNeighborMinutes > neighbor.GapMinutes)
        {
            // Komunikat podaje realną długość luki: przy nieaktualnym ekranie user ma
            // od razu wiedzieć, ile czasu naprawdę jest do rozdzielenia.
            return new(TimeEntryOperationStatus.Conflict,
                $"Wolna przerwa ma {neighbor.GapMinutes} min — podział na {claimedMinutes} i {forNeighborMinutes} min się w niej nie mieści.");
        }

        if (claimedMinutes + forNeighborMinutes == 0)
        {
            return new(TimeEntryOperationStatus.Invalid, "Podział po zero minut niczego nie zmienia.");
        }

        if (forNeighborMinutes > 0 && neighbor.SuggestionId is null)
        {
            return new(TimeEntryOperationStatus.Invalid,
                "Po drugiej stronie przerwy jest zatwierdzony wpis czasu — jego czasu nie zmieniamy stąd.");
        }

        // Zapas do reguły z FindNeighbor (luka przez północ nie jest oferowana): nawet
        // legalna luka nie może przesunąć początku na inną dobę niż EntryDate — możliwe
        // tylko w skrajności, gdy pozycja zaczyna się dokładnie o lokalnej północy.
        if (direction == GapDirection.Before && claimedMinutes > 0
            && !StaysWithinEntryDate(suggestion, claimedMinutes))
        {
            return new(TimeEntryOperationStatus.Conflict,
                "Doliczenie przesunęłoby początek sesji na poprzedni dzień — sesje nie przekraczają granicy dnia.");
        }

        var changed = new List<Suggestion>();
        if (claimedMinutes > 0)
        {
            ApplyGap(suggestion, direction, claimedMinutes);
            changed.Add(suggestion);
        }

        if (forNeighborMinutes > 0)
        {
            var other = await db.Suggestions
                .FirstAsync(candidate => candidate.Id == neighbor.SuggestionId, cancellationToken);
            if (direction == GapDirection.After && !StaysWithinEntryDate(other, forNeighborMinutes))
            {
                return new(TimeEntryOperationStatus.Conflict,
                    "Podział przesunąłby początek sąsiedniej sesji na poprzedni dzień — sesje nie przekraczają granicy dnia.");
            }
            // Sąsiad rośnie w przeciwną stronę: luka leży między nimi, więc każdy
            // dokłada ją od swojej strony i nadal się nie nakładają — a niedobrany
            // czas zostaje pośrodku, dalej wolny.
            ApplyGap(other, direction == GapDirection.Before ? GapDirection.After : GapDirection.Before,
                forNeighborMinutes);
            changed.Add(other);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(TimeEntryOperationStatus.Success, Suggestions: changed);
    }

    /// <summary>
    /// Wolne luki wokół podanych sugestii — dane dla UI, żeby przycisk doliczania
    /// pojawiał się WYŁĄCZNIE tam, gdzie jest co doliczyć. Liczone hurtem dla całej
    /// listy (jedno zapytanie na oś dnia), nie po jednej sugestii.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, SuggestionGaps>> LoadGapsAsync(
        IReadOnlyList<Suggestion> suggestions,
        CancellationToken cancellationToken)
    {
        var pending = suggestions.Where(suggestion => suggestion.Status == SuggestionStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            return new Dictionary<int, SuggestionGaps>();
        }

        var from = pending.Min(suggestion => suggestion.EntryDate).AddDays(-1);
        var to = pending.Max(suggestion => suggestion.EntryDate).AddDays(1);
        var items = await LoadItemsAsync(from, to, excludeSuggestionIds: [], cancellationToken);

        var result = new Dictionary<int, SuggestionGaps>();
        foreach (var suggestion in pending)
        {
            // Sugestia nie może sąsiadować sama ze sobą — z osi znika tylko ona.
            var others = items.Where(item => item.SuggestionId != suggestion.Id).ToList();
            var before = FindNeighbor(others, suggestion, GapDirection.Before);
            var after = FindNeighbor(others, suggestion, GapDirection.After);
            if (before is not null || after is not null)
            {
                result[suggestion.Id] = new SuggestionGaps(before, after);
            }
        }

        return result;
    }

    /// <summary>
    /// Minuty wolnej przerwy albo null, gdy przerwy nie ma, bo pozycje zachodzą na siebie.
    ///
    /// PODŁOGA, nie zaokrąglenie: minuta zaokrąglona w górę to minuta, której w przerwie
    /// nie ma. Doliczona wchodziłaby w sąsiada i zostawiała TRWAŁE nakładanie — kilka
    /// sekund wystarczy, żeby kolejne operacje na tej osi były już odrzucane, a przyczyny
    /// nie widać w interfejsie, bo różnica gubi się w wyświetlanych minutach.
    ///
    /// Ujemna różnica daje null, a nie zero: zaokrąglenie zamieniało zachodzenie krótsze
    /// niż pół minuty na „przerwę zerową", czyli meldowało przylegające sesje tam, gdzie
    /// dane są sprzeczne.
    /// </summary>
    private static int? FreeGapMinutes(DateTime startAt, DateTime endAt)
        => TimeAxis.FreeGapMinutes(startAt, endAt);

    /// <summary>
    /// Czy przedział [start, end) dotyka więcej niż jednej doby lokalnej. Koniec równy
    /// północy należy jeszcze do dnia poprzedniego (przedział półotwarty) — luka
    /// 22:00–00:00 nie przechodzi przez północ, luka 23:50–00:10 tak.
    /// </summary>
    private static bool CrossesLocalMidnight(DateTime gapStartAt, DateTime gapEndAt)
    {
        var lastMomentInGap = gapEndAt > gapStartAt ? gapEndAt.AddTicks(-1) : gapStartAt;
        return DateOnly.FromDateTime(gapStartAt) != DateOnly.FromDateTime(lastMomentInGap);
    }

    /// <summary>Czy po przesunięciu początku o <paramref name="minutes"/> w tył sugestia zostaje w swojej dobie.</summary>
    private static bool StaysWithinEntryDate(Suggestion suggestion, int minutes)
        => DateOnly.FromDateTime(suggestion.StartedAt.AddMinutes(-minutes)) == suggestion.EntryDate;

    /// <summary>Doliczone minuty przesuwają początek albo koniec — kotwica sesji zostaje nietknięta (klucz dedupu).</summary>
    private static void ApplyGap(Suggestion suggestion, GapDirection direction, int minutes)
    {
        if (direction == GapDirection.Before)
        {
            suggestion.StartedAt = suggestion.StartedAt.AddMinutes(-minutes);
        }

        suggestion.DurationMinutes += minutes;
        suggestion.NeedsTimeReview = false;
        suggestion.IsUserAdjusted = true;
    }

    /// <summary>
    /// Najbliższy sąsiad po wskazanej stronie razem z dzielącą ich luką — albo null,
    /// gdy luki nie ma (sąsiad przylega), jest zajęta, przekracza limit z konfiguracji
    /// albo sąsiada w ogóle brak.
    /// </summary>
    private SuggestionNeighbor? FindNeighbor(
        IReadOnlyList<DayItem> items,
        Suggestion suggestion,
        GapDirection direction)
    {
        var startAt = suggestion.StartedAt;
        var endAt = SuggestionSpan.EndOf(suggestion, businessTimeZone);

        var neighbor = direction == GapDirection.Before
            ? items.Where(item => item.End <= startAt).OrderByDescending(item => item.End).FirstOrDefault()
            : items.Where(item => item.Start >= endAt).OrderBy(item => item.Start).FirstOrDefault();
        if (neighbor is null)
        {
            return null;
        }

        var gapStartAt = direction == GapDirection.Before ? neighbor.End : endAt;
        var gapEndAt = direction == GapDirection.Before ? startAt : neighbor.Start;
        // ODMOWA dla luki przechodzącej przez lokalną północ — spójnie z silnikiem sesji
        // („sesje nie przechodzą granicy dnia w strefie biznesowej"). Sąsiad bywa znaleziony
        // zza północy (okno ±1 doba), a doliczenie takiej luki cofałoby StartedAt na inną
        // dobę niż EntryDate — oś grupowania, sum dziennych i archiwizacji zakresem dat.
        // Alternatywa (przycięcie do północy) wymagałaby przeliczania EntryDate przy każdej
        // zmianie StartedAt w obu serwisach; nie ma jej, więc luka po prostu nie istnieje.
        if (CrossesLocalMidnight(gapStartAt, gapEndAt))
        {
            return null;
        }
        // Luka zerowa (pozycje przylegają) NIE dyskwalifikuje sąsiada: nie ma czego
        // doliczać, ale sesje tego samego pliku wolno wtedy scalić — UI potrzebuje
        // wiedzieć, że sąsiad w ogóle jest.
        var gapMinutes = FreeGapMinutes(gapStartAt, gapEndAt);
        if (gapMinutes is null || gapMinutes > options.MaxClaimableGapMinutes)
        {
            return null;
        }

        // Wybór najbliższego sąsiada gwarantuje pustkę, ale niezmiennik sprawdzamy
        // wprost — zastane dane mogą zawierać pozycje zachodzące na siebie.
        if (FindBlocker(items, gapStartAt, gapEndAt) is not null)
        {
            return null;
        }

        return new SuggestionNeighbor(
            neighbor.SuggestionId,
            neighbor.Title,
            gapMinutes.Value,
            // Warunki scalenia sprawdzane TU, komplet — dokładnie te same, które
            // weryfikuje MergeAsync. UI nie ma prawa pokazać przycisku, po którym
            // przychodzi odmowa; jeśli scalić się nie da, sąsiad zostaje w danych
            // wyłącznie po to, żeby dało się rozdzielić dzielącą ich przerwę.
            CanMerge: neighbor.SuggestionId is not null
                && neighbor.ExternalId is not null
                && suggestion.Source == SuggestionSource.Document
                && neighbor.ExternalId == suggestion.ExternalId
                && neighbor.EntryDate == suggestion.EntryDate);
    }

    /// <summary>Pozycja na osi dnia: wpis czasu albo oczekująca sugestia.</summary>
    private sealed record DayItem(
        DateTime Start,
        DateTime End,
        string Title,
        DateOnly EntryDate,
        int? SuggestionId,
        string? ExternalId);

    private Task<List<DayItem>> LoadDayItemsAsync(
        DateOnly date,
        IReadOnlyCollection<int> excludeSuggestionIds,
        CancellationToken cancellationToken)
        => LoadItemsAsync(date.AddDays(-1), date.AddDays(1), excludeSuggestionIds, cancellationToken);

    /// <summary>
    /// Wszystkie pozycje z zakresu dat (wpisy + oczekujące sugestie). Zakres poszerzony
    /// o dobę w obie strony, bo spotkanie kalendarzowe może przechodzić przez północ.
    /// </summary>
    private async Task<List<DayItem>> LoadItemsAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<int> excludeSuggestionIds,
        CancellationToken cancellationToken)
    {
        var entries = await db.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to)
            .Select(entry => new { entry.StartedAt, entry.EndedAt, entry.EntryDate, entry.Description })
            .ToListAsync(cancellationToken);

        var pending = await db.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                && suggestion.EntryDate >= from && suggestion.EntryDate <= to
                && !excludeSuggestionIds.Contains(suggestion.Id))
            .Select(suggestion => new
            {
                suggestion.Id,
                suggestion.StartedAt,
                suggestion.DurationMinutes,
                suggestion.LastActivityAt,
                suggestion.EntryDate,
                suggestion.Title,
                suggestion.Source,
                suggestion.ExternalId,
            })
            .ToListAsync(cancellationToken);

        return entries
            .Select(entry => new DayItem(
                entry.StartedAt, entry.EndedAt, $"wpis „{entry.Description}\"", entry.EntryDate, null, null))
            .Concat(pending.Select(suggestion => new DayItem(
                suggestion.StartedAt,
                SuggestionSpan.EndOf(
                    suggestion.StartedAt, suggestion.DurationMinutes, suggestion.LastActivityAt, businessTimeZone),
                suggestion.Title,
                suggestion.EntryDate,
                suggestion.Id,
                suggestion.Source == SuggestionSource.Document ? suggestion.ExternalId : null)))
            .ToList();
    }

    /// <summary>Opis pierwszej pozycji zajmującej przedział [start, end) albo null, gdy wolny.</summary>
    private static string? FindBlocker(IReadOnlyList<DayItem> items, DateTime start, DateTime end)
        => items
            .Where(item => TimeAxis.Overlaps(start, end, item.Start, item.End))
            .OrderBy(item => item.Start)
            .Select(item => BusinessMoment.Describe(item.Title, item.Start, item.End))
            .FirstOrDefault();
}
