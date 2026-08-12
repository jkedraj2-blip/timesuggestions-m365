using System.ComponentModel.DataAnnotations;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Contracts;

/// <summary>
/// Przerwa w zasięgu pozycji (czasy strefy biznesowej). <paramref name="Counted"/> mówi,
/// czy jej minuty wchodzą do rozliczanego czasu — od tego zależy, czy interfejs oferuje
/// „Odejmij przerwę", czy „Dolicz przerwę". Przerwa wewnątrz sesji jest liczona (siedzi
/// w czasie brutto), przerwa między scalonymi sesjami nie jest.
/// </summary>
public record DetectedGapDto(DateTime StartAt, DateTime EndAt, int Minutes, bool Counted)
{
    /// <summary>Przerwy sesji zapisane przy sugestii — zawsze wewnątrz sesji, więc liczone.</summary>
    public static IReadOnlyList<DetectedGapDto> FromJson(string? json)
        => DetectedGaps.Deserialize(json)
            .Select(gap => new DetectedGapDto(gap.StartAt, gap.EndAt, gap.Minutes, Counted: true))
            .ToList();

    public static IReadOnlyList<DetectedGapDto> FromEntryGaps(IReadOnlyList<EntryGap> gaps)
        => gaps.Select(gap => new DetectedGapDto(gap.StartAt, gap.EndAt, gap.Minutes, gap.Counted)).ToList();
}

/// <summary>Sugestia w kształcie dla interfejsu — spłaszczone dane sprawy zamiast pełnej encji.</summary>
public record SuggestionDto(
    int Id,
    SuggestionSource Source,
    string Title,
    DateTime StartedAt,
    int DurationMinutes,
    int? CaseId,
    string? CaseName,
    string? CaseNumber,
    string? ClientName,
    bool IsAmbiguous,
    IReadOnlyList<string> MatchCandidates,
    string ProposedDescription,
    SuggestionStatus Status,
    IReadOnlyList<DetectedGapDto> DetectedGaps,
    // Czas wyliczony z JEDNEGO zapisu — UI prosi o wpisanie go ręcznie zamiast
    // podsuwać zgadywaną wartość do zatwierdzenia.
    bool NeedsTimeReview,
    // Id pliku z Graph (tylko dokumenty) — po nim karta pobiera chronologię
    // modyfikacji; dla spotkań null, identyfikatory kalendarza nie mają tu zastosowania.
    string? SourceExternalId,
    // Ostatnia znana modyfikacja w czasie STREFY BIZNESOWEJ — tej samej osi co
    // StartedAt. Encja trzyma tę wartość w UTC (razem z kotwicą sesji), ale interfejs
    // musi dostać obie godziny na jednej osi: inaczej sesja o jednym zapisie pokazuje
    // "początek 22:58, ostatnia zmiana 20:58" i wygląda, jakby skończyła się przed
    // rozpoczęciem, choć to dokładnie ten sam moment.
    DateTime LastActivityAt,
    // Czas poprawiony ręcznie (scalenie, doliczona luka) — sync go już nie przelicza.
    bool IsUserAdjusted,
    // Wolne luki wokół sugestii; null = nie ma czego doliczać po żadnej ze stron.
    SuggestionGaps? Gaps,
    // „edycja 3" — numer sesji w całej historii pliku; null dla pozycji kalendarzowych.
    string? SessionLabel = null)
{
    /// <summary>
    /// Dla sugestii niejednoznacznych przekazujemy pasujące sprawy —
    /// UI mówi użytkownikowi konkretnie "pasuje do X i Y", a nie tylko "sprawdź to".
    /// Numer i klient czytane na żywo z nawigacji Case (bez snapshotu w sugestii).
    /// </summary>
    /// <param name="businessTimeZone">
    /// Strefa biznesowa do sprowadzenia <see cref="Suggestion.LastActivityAt"/> (UTC)
    /// na tę samą oś co StartedAt. Wymagana — pominięcie jej było źródłem godzin
    /// rozjeżdżających się o offset strefy.
    /// </param>
    public static SuggestionDto FromEntity(
        Suggestion suggestion,
        TimeZoneInfo businessTimeZone,
        IReadOnlyList<string>? matchCandidates = null,
        SuggestionGaps? gaps = null,
        string? sessionLabel = null) => new(
        suggestion.Id,
        suggestion.Source,
        suggestion.Title,
        suggestion.StartedAt,
        suggestion.DurationMinutes,
        suggestion.CaseId,
        suggestion.Case?.Name,
        suggestion.Case?.CaseNumber,
        suggestion.Case?.ClientName,
        suggestion.IsAmbiguous,
        matchCandidates ?? [],
        suggestion.ProposedDescription,
        suggestion.Status,
        DetectedGapDto.FromJson(suggestion.DetectedGapsJson),
        suggestion.NeedsTimeReview,
        suggestion.Source == SuggestionSource.Document ? suggestion.ExternalId : null,
        ToBusinessLocal(suggestion.LastActivityAt, businessTimeZone),
        suggestion.IsUserAdjusted,
        gaps,
        sessionLabel);

    /// <summary>
    /// LastActivityAt jest w encji zawsze w UTC, ale po odczycie z SQLite Kind bywa
    /// Unspecified — a BusinessTime.ToBusinessLocal traktuje Unspecified jako "już
    /// lokalny" (tak przychodzi kalendarz z Graph) i zwróciłby wartość nietkniętą.
    /// Dlatego oznaczamy Kind jawnie przed konwersją.
    /// </summary>
    private static DateTime ToBusinessLocal(DateTime utc, TimeZoneInfo businessTimeZone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), businessTimeZone);
}

/// <summary>Żądanie scalenia kilku sugestii tego samego dokumentu w jedną.</summary>
public class MergeSuggestionsRequest : IValidatableObject
{
    /// <summary>Tyle sesji jednego pliku jednego dnia i tak nie powstaje — limit chroni przed absurdalnym żądaniem.</summary>
    public const int MaxMergeCount = 50;

    [Required(ErrorMessage = "Lista sugestii do scalenia jest wymagana.")]
    public List<int> SuggestionIds { get; set; } = [];

    /// <summary>true = dolicz wolne luki między scalanymi sesjami do czasu wyniku.</summary>
    public bool IncludeGaps { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SuggestionIds.Count < 2)
        {
            yield return new ValidationResult(
                "Scalenie wymaga co najmniej dwóch sugestii.", [nameof(SuggestionIds)]);
        }

        if (SuggestionIds.Count > MaxMergeCount)
        {
            yield return new ValidationResult(
                "Scalenie może objąć najwyżej 50 sugestii.", [nameof(SuggestionIds)]);
        }
    }
}

/// <summary>
/// Żądanie rozdzielenia wolnej luki: ile minut bierze ta sugestia, ile sąsiednia.
/// Obie wartości pochodzą wprost od użytkownika (widzi je i zmienia przed zapisem),
/// ale rozmiar luki i tak przelicza serwer — klient podaje podział, nie fakt.
/// </summary>
public class ClaimSuggestionGapRequest : IValidatableObject
{
    [Required(ErrorMessage = "Kierunek doliczenia przerwy jest wymagany.")]
    public Services.GapDirection? Direction { get; set; }

    /// <summary>Minuty dla tej sugestii; brak wartości = cała wolna luka (skrót „Dolicz całość").</summary>
    [Range(0, Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        ErrorMessage = "Doliczany czas musi mieścić się w zakresie 0–480 minut.")]
    public int? Minutes { get; set; }

    /// <summary>Minuty dla sąsiedniej sugestii; reszta luki zostaje wolna.</summary>
    [Range(0, Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        ErrorMessage = "Czas dla sąsiada musi mieścić się w zakresie 0–480 minut.")]
    public int? NeighborMinutes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Sam NeighborMinutes bez Minutes znaczyłby „całą lukę tutaj I jeszcze tyle
        // sąsiadowi" — sprzeczność, która skończyłaby się odmową dopiero w serwisie.
        if (Minutes is null && NeighborMinutes is > 0)
        {
            yield return new ValidationResult(
                "Podział przerwy wymaga podania minut dla obu stron.", [nameof(Minutes)]);
        }
    }
}

/// <summary>Wartości finalne wpisu — edycja to zatwierdzenie z poprawionymi wartościami (jeden endpoint).</summary>
public class ApproveSuggestionRequest
{
    /// <summary>Górna granica czasu jednego wpisu — pełna doba.</summary>
    public const int MaxEntryDurationMinutes = 1440;

    public const int MaxDescriptionLength = 500;

    public int CaseId { get; set; }

    [Range(1, MaxEntryDurationMinutes, ErrorMessage = "Czas trwania musi mieścić się w zakresie 1–1440 minut.")]
    public int DurationMinutes { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Opis czynności jest wymagany.")]
    [MaxLength(MaxDescriptionLength, ErrorMessage = "Opis czynności może mieć najwyżej 500 znaków.")]
    public string Description { get; set; } = string.Empty;
}

public record CaseDto(int Id, string Name, string CaseNumber, string ClientName, IReadOnlyList<string> Keywords, bool IsActive)
{
    public static CaseDto FromEntity(Case legalCase) => new(
        legalCase.Id,
        legalCase.Name,
        legalCase.CaseNumber,
        legalCase.ClientName,
        legalCase.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries),
        legalCase.IsActive);
}

/// <summary>
/// Dane sprawy przy tworzeniu i edycji — słowa kluczowe jako lista, sklejane do formatu bazy.
/// Pola tekstowe są przycinane po stronie backendu (settery); wartości z samych białych
/// znaków stają się puste i odpadają na [Required]. Walidacja [Required]/[MaxLength]
/// działa już na wartościach przyciętych.
/// </summary>
public class CaseWriteRequest : IValidatableObject
{
    public const int MaxNameLength = 200;

    public const int MaxCaseNumberLength = 100;

    public const int MaxKeywordLength = 100;

    private string name = string.Empty;
    private string caseNumber = string.Empty;
    private string clientName = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Nazwa sprawy jest wymagana.")]
    [MaxLength(MaxNameLength, ErrorMessage = "Nazwa sprawy może mieć najwyżej 200 znaków.")]
    public string Name { get => name; set => name = value?.Trim() ?? string.Empty; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Numer sprawy jest wymagany.")]
    [MaxLength(MaxCaseNumberLength, ErrorMessage = "Numer sprawy może mieć najwyżej 100 znaków.")]
    public string CaseNumber { get => caseNumber; set => caseNumber = value?.Trim() ?? string.Empty; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Nazwa klienta jest wymagana.")]
    [MaxLength(MaxNameLength, ErrorMessage = "Nazwa klienta może mieć najwyżej 200 znaków.")]
    public string ClientName { get => clientName; set => clientName = value?.Trim() ?? string.Empty; }

    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Średnik to separator formatu bazy — słowo kluczowe z ';' odrzucamy jawnym błędem
    /// zamiast po cichu modyfikować dane użytkownika. MVC nie odrzuca null wewnątrz
    /// kolekcji, więc puste elementy łapiemy tu jawnie (400 zamiast 500).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var keyword in Keywords)
        {
            if (keyword is null)
            {
                yield return new ValidationResult(
                    "Lista słów kluczowych zawiera pusty element.", [nameof(Keywords)]);
                continue;
            }

            var trimmed = keyword.Trim();
            if (trimmed.Contains(';'))
            {
                // Komunikat nie odbija pełnej wartości wejściowej (niezaufane dane
                // dowolnej długości) — tylko krótki ucięty podgląd.
                yield return new ValidationResult(
                    $"Słowo kluczowe nie może zawierać średnika: \"{TruncateForMessage(trimmed)}\".",
                    [nameof(Keywords)]);
            }

            if (trimmed.Length > MaxKeywordLength)
            {
                yield return new ValidationResult(
                    "Pojedyncze słowo kluczowe może mieć najwyżej 100 znaków.", [nameof(Keywords)]);
            }
        }
    }

    private static string TruncateForMessage(string value)
    {
        if (value.Length <= 32)
        {
            return value;
        }

        // Cięcie nie może rozdzielić pary zastępczej UTF-16 — samotny surogat
        // zostałby zserializowany jako U+FFFD i zniekształcił podgląd.
        var length = char.IsHighSurrogate(value[31]) ? 31 : 32;
        return $"{value[..length]}…";
    }

    /// <summary>
    /// Format przechowywania: pojedyncza kolumna rozdzielana średnikiem (bez osobnej
    /// tabeli — YAGNI). Metoda, nie właściwość: walidacja modelu MVC czyta wszystkie
    /// publiczne właściwości, a getter rzucał NRE dla null w Keywords zanim
    /// Validate() zdążył zwrócić czytelny błąd 400.
    /// </summary>
    public string JoinKeywords() => string.Join(';', Keywords
        .Where(keyword => keyword is not null)
        .Select(keyword => keyword.Trim())
        .Where(keyword => keyword.Length > 0));
}

/// <summary>Liczniki dla kafelków podsumowania w nagłówku aplikacji.</summary>
public record SummaryDto(
    int PendingCount,
    int ApprovedCount,
    int RejectedCount,
    int UnsettledMinutes,
    int TodayLoggedMinutes,
    DateTime? LastSyncAt);

/// <summary>
/// Zakres rozliczenia hurtowego. Przedział domknięty dat DZIENNYCH (EntryDate jest
/// datą w strefie biznesowej, więc porównanie DateOnly nie ma problemów strefowych).
/// </summary>
public class ArchiveTimeEntriesRequest : IValidatableObject
{
    public const int MaxRangeDays = 366;

    [Required(ErrorMessage = "Data początkowa zakresu jest wymagana.")]
    public DateOnly? From { get; set; }

    [Required(ErrorMessage = "Data końcowa zakresu jest wymagana.")]
    public DateOnly? To { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is DateOnly from && To is DateOnly to)
        {
            if (to < from)
            {
                yield return new ValidationResult(
                    "Data końcowa nie może być wcześniejsza niż początkowa.", [nameof(To)]);
            }
            else if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            {
                yield return new ValidationResult(
                    "Zakres rozliczenia może obejmować najwyżej 366 dni.", [nameof(To)]);
            }
        }
    }
}

/// <summary>Żądanie scalenia wpisów jednej sesji dokumentu w jeden wpis.</summary>
public class MergeTimeEntriesRequest : IValidatableObject
{
    /// <summary>Rozsądny limit liczby scalanych wpisów — więcej sesji jednego pliku jednego dnia nie istnieje.</summary>
    public const int MaxMergeCount = 50;

    [Required(ErrorMessage = "Lista wpisów do scalenia jest wymagana.")]
    public List<int> TimeEntryIds { get; set; } = [];

    /// <summary>true = dolicz wolne luki między sesjami (zapis GapAddition per lukę).</summary>
    public bool IncludeGaps { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TimeEntryIds.Count < 2)
        {
            yield return new ValidationResult(
                "Scalenie wymaga co najmniej dwóch wpisów.", [nameof(TimeEntryIds)]);
        }

        if (TimeEntryIds.Count > MaxMergeCount)
        {
            yield return new ValidationResult(
                "Scalenie może objąć najwyżej 50 wpisów.", [nameof(TimeEntryIds)]);
        }
    }
}

/// <summary>
/// Żądanie odjęcia wykrytej przerwy — zakres identyfikuje przerwę z listy wykrytych
/// przerw wpisu (backend waliduje pochodzenie, klientowi nie ufa).
/// </summary>
public class SubtractGapRequest
{
    [Required(ErrorMessage = "Początek przerwy jest wymagany.")]
    public DateTime? GapStartAt { get; set; }

    [Required(ErrorMessage = "Koniec przerwy jest wymagany.")]
    public DateTime? GapEndAt { get; set; }
}

/// <summary>Szybka korekta czasu wpisu o ±N minut.</summary>
public class AdjustTimeEntryRequest : IValidatableObject
{
    [Range(-Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        ErrorMessage = "Korekta musi mieścić się w zakresie ±480 minut.")]
    public int Minutes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Minutes == 0)
        {
            yield return new ValidationResult("Korekta o zero minut nic nie zmienia.", [nameof(Minutes)]);
        }
    }
}

/// <summary>Wpisy pogrupowane po dniach z sumami — widok "Wpisy czasu" pokazuje je od razu policzone.</summary>
public record TimeEntriesResponse(int TotalMinutes, IReadOnlyList<TimeEntryDayDto> Days);

public record TimeEntryDayDto(DateOnly Date, int TotalMinutes, IReadOnlyList<TimeEntryDto> Entries);

public record TimeEntryDto(
    int Id,
    int CaseId,
    string? CaseName,
    string? CaseNumber,
    string? ClientName,
    DateOnly EntryDate,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationMinutes,
    string Description,
    bool CreatedFromSuggestion,
    SuggestionSource Source,
    IReadOnlyList<int> SuggestionIds,
    string? SourceTitle,
    DateTime? SourceStartedAt,
    // Id pliku z Graph (dla wpisów dokumentowych) — UI po nim rozpoznaje, które wpisy
    // wolno zaznaczyć do scalenia; backend i tak waliduje po swojej stronie.
    string? SourceExternalId,
    DateTime? ArchivedAt,
    IReadOnlyList<DetectedGapDto> DetectedGaps,
    // Etykieta sesji („edycja 3") licząca sesje w całej historii pliku;
    // null dla wpisów kalendarzowych, które sesji edycji nie mają.
    string? SessionLabel = null,
    // Czas po zaokrągleniu do jednostki rozliczeniowej. Liczy go SERWER, żeby etykieta
    // przycisku („Zaokrąglij do 1 godz.") nie mogła obiecać innej wartości niż ta, którą
    // operacja zapisze — jednostka jest w konfiguracji backendu i frontend jej nie zna.
    // Równy DurationMinutes = nie ma czego zaokrąglać.
    int RoundedDurationMinutes = 0,
    // Suma korekt z zaokrąglania (dodatnia = dołożono, ujemna = zdjęto); 0 = nie
    // zaokrąglano. Osobno od reszty, bo to inna decyzja niż przerwy i niż korekta ±15,
    // a wrzucona do jednego worka z przerwami zamieniała się w komunikat, że wpis ma
    // „nieliczone przerwy", których nikt nigdy nie widział w historii wersji.
    int RoundingMinutes = 0,
    // Zdanie o tym, co stało się z godzinami wpisu przy zatwierdzaniu (przycięcie do
    // sąsiada, pozostałe pokrycie). Wypełniane tylko w odpowiedzi na zatwierdzenie.
    string? Notice = null)
{
    /// <summary>
    /// SourceTitle/SourceStartedAt to kotwica wpisu w realnym zdarzeniu (tytuł spotkania
    /// lub nazwa pliku) — opis mógł zostać nadpisany przez użytkownika przy zatwierdzaniu.
    /// Po scaleniu sesji wpis ma wiele sugestii: kotwicą jest najwcześniejsza z nich.
    /// Przerwy przychodzą z <see cref="EntryGapService"/> (liczone z dziennika wersji
    /// razem ze stanem „liczona/nieliczona"); brak listy = wpis bez przerw do pokazania.
    /// </summary>
    public static TimeEntryDto FromEntity(
        TimeEntry entry,
        string? notice = null,
        IReadOnlyList<EntryGap>? gaps = null,
        string? sessionLabel = null,
        int roundedDurationMinutes = 0)
    {
        var firstSuggestion = entry.Suggestions.OrderBy(suggestion => suggestion.StartedAt).FirstOrDefault();

        return new(
            entry.Id,
            entry.CaseId,
            entry.Case?.Name,
            entry.Case?.CaseNumber,
            entry.Case?.ClientName,
            entry.EntryDate,
            entry.StartedAt,
            entry.EndedAt,
            entry.DurationMinutes,
            entry.Description,
            entry.CreatedFromSuggestion,
            entry.Source,
            entry.Suggestions.Select(suggestion => suggestion.Id).ToList(),
            firstSuggestion?.Title,
            firstSuggestion?.StartedAt,
            entry.Source == SuggestionSource.Document ? firstSuggestion?.ExternalId : null,
            entry.ArchivedAt,
            gaps is null ? [] : DetectedGapDto.FromEntryGaps(gaps),
            sessionLabel,
            roundedDurationMinutes == 0 ? entry.DurationMinutes : roundedDurationMinutes,
            entry.Adjustments
                .Where(adjustment => adjustment.Kind == AdjustmentKind.Rounding)
                .Sum(adjustment => adjustment.Minutes),
            notice);
    }
}
