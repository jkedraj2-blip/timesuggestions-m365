using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Etykieta „edycja 3" dla pliku, nad którym pracowano w kilku sesjach. Jeden dokument
/// daje tyle sugestii, ile było sesji pracy, a wszystkie noszą tę samą nazwę pliku —
/// lista wyglądała jak zduplikowana i nie dało się poznać, o którą pracę chodzi.
///
/// Numer biegnie przez CAŁĄ historię pliku, bez dzielenia na dni. Umowa jest pisana
/// tygodniami, więc „która to część dnia" nie opisuje niczego, co prawnik ma w głowie,
/// a numeracja dzienna łamała się dokładnie tam, gdzie kolejność jest najmniej
/// oczywista: sesja zaczęta przed północą trafia datą do następnego dnia i dostawała
/// numer z innej puli niż sesja sprzed godziny.
///
/// Numer dostaje KAŻDA sesja, licząc od pierwszej. Pomijanie plików z jedną sesją
/// znaczyło, że brak plakietki raz mówi „ten plik ma jedną edycję", a raz „ta pozycja
/// wypadła z numeracji" — dwa różne fakty nie do odróżnienia na ekranie.
///
/// Numer jest LICZONY, nie zapisywany, i to jest sedno: numer w tytule rozjeżdżałby się
/// przy każdej zmianie (synchronizacja nadpisuje tytuł ze źródła, scalenie usuwa
/// składowe). Porządek daje kotwica sesji, czyli czas pierwszej wersji — ta sama
/// wartość, po której sync rozpoznaje sesję.
///
/// Liczymy po sugestiach we WSZYSTKICH stanach, nie tylko oczekujących. Inaczej
/// zatwierdzenie „edycji 2" zmieniałoby „edycję 3" w „edycję 2", czyli ten sam numer
/// wskazywałby raz jedną, raz drugą pracę. Po scaleniu numer wyniku to numer
/// wcześniejszej ze scalanych sesji (zostaje jej kotwica), a sesje po niej przesuwają
/// się o jeden — bo realnie jest ich o jedną mniej.
/// </summary>
public class SessionLabelService(AppDbContext db)
{
    public async Task<Dictionary<int, string>> LoadAsync(
        IReadOnlyList<Suggestion> suggestions,
        CancellationToken cancellationToken)
    {
        var documents = suggestions.Where(suggestion => suggestion.Source == SuggestionSource.Document).ToList();
        if (documents.Count == 0)
        {
            return [];
        }

        var externalIds = documents.Select(suggestion => suggestion.ExternalId).Distinct().ToList();

        // Jedno zapytanie na całą listę zamiast jednego na sugestię; z każdej sesji
        // bierzemy wyłącznie to, co ustawia kolejność — historia pliku bywa długa.
        var related = await db.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.Source == SuggestionSource.Document
                && externalIds.Contains(suggestion.ExternalId))
            .Select(suggestion => new
            {
                suggestion.Id,
                suggestion.ExternalId,
                suggestion.SessionAnchor,
            })
            .ToListAsync(cancellationToken);

        var labels = new Dictionary<int, string>();
        foreach (var group in related.GroupBy(suggestion => suggestion.ExternalId))
        {
            var ordered = group
                .OrderBy(suggestion => suggestion.SessionAnchor)
                .ThenBy(suggestion => suggestion.Id)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                labels[ordered[index].Id] = $"edycja {index + 1}";
            }
        }

        return labels;
    }
}
