# Scenariusz demo (test manualny)

Kroki do odtworzenia na żywo w 5–10 minut. Zakładają skonfigurowany Client ID
(patrz README) oraz uruchomiony backend (5188) i frontend (4200).

## Przygotowanie danych testowych

Na koncie Microsoft, którym się logujesz, przygotuj w **kalendarzu Outlook**
(w ostatnich 7 dniach):

| Wydarzenie | Tytuł (przykład) | Czego dowodzi |
|---|---|---|
| Zwykłe spotkanie | `Spotkanie z Kowalski — przegląd umowy` | dopasowanie po nazwie klienta |
| Spotkanie prywatne (oznacz jako prywatne) | `Wizyta u lekarza` | filtr prywatnych |
| Spotkanie 4-minutowe | `Szybki telefon` | filtr krótszych niż 5 min |
| Wydarzenie całodniowe | `Urlop` | filtr całodniowych |
| Spotkanie bez dopasowania | `Cotygodniowy standup` | karta „sprawdź to" |
| Spotkanie pasujące do 2 spraw | `Analiza Beta` | niejednoznaczność (Fuzja Alfa/Beta i Beta Logistics) |

W **OneDrive** (edytuj pliki, żeby miały świeżą datę modyfikacji):

| Plik | Czego dowodzi |
|---|---|
| `Umowa_NovaTech_v2.docx` | dopasowanie mimo separatorów i sufiksu wersji |
| `notatki_grzegrzolka.xlsx` | dopasowanie mimo braku polskich znaków |
| `plan_wakacji.docx` | brak dopasowania → „sprawdź to" |
| dowolny plik `.png`/`.pdf` | filtr formatów (nie pojawi się) |
| jeden z powyższych edytowany 2× tego samego dnia | agregacja do jednej sugestii |

## Przebieg demo

1. **Konfiguracja Azure** — pokaż w portalu Entra ID rejestrację aplikacji
   (typ kont, platforma SPA `http://localhost:4200`, uprawnienia delegowane).
2. **Logowanie** — `http://localhost:4200` → „Zaloguj przez Microsoft" → konto testowe.
3. **Sprawy** — zakładka „Sprawy": pokaż listę i wyjaśnienie zasady dopasowania.
4. **Synchronizacja** — zakładka „Sugestie" → „Synchronizuj". Zwróć uwagę na etapy
   postępu, a po zakończeniu na **raport**: ile pobrano, ile odfiltrowano per reguła,
   agregację i wynik dopasowania. Porównaj z przygotowanymi danymi.
5. **Karty** — pokaż: dopasowaną (zielona plakietka sprawy), „sprawdź to" bez sprawy
   (z wyjaśnieniem czego brakuje) i niejednoznaczną (z listą pasujących spraw).
6. **Zatwierdzenie** — na dopasowanej karcie „Zatwierdź" → toast z potwierdzeniem;
   kafelki w nagłówku się aktualizują.
7. **Weryfikacja wpisu** — zakładka „Wpisy czasu": wpis pogrupowany po dniu, z sumą,
   źródłem i znacznikiem „z sugestii". (Opcjonalnie: `GET /api/time-entries` w pliku .http.)
8. **Edycja** — na karcie „sprawdź to" → „Edytuj" → wybierz sprawę, popraw czas →
   „Zapisz i zatwierdź".
9. **Odrzucenie** — odrzuć inną kartę → toast z „Cofnij" (pokaż, że działa) → odrzuć ponownie.
10. **Zatwierdź wszystkie dopasowane** — jeśli zostały karty z jednoznaczną sprawą,
    jeden przycisk zapisuje wszystkie.
11. **Powtórna synchronizacja** — kliknij „Synchronizuj" jeszcze raz:
    - raport pokaże `pominięto N (już istniały)`, `utworzono 0`,
    - odrzucona sugestia **nie wraca** na listę,
    - dzięki cache delta sync jest wyraźnie szybszy niż pierwszy.
12. **Odwracalność** — w „Wpisy czasu" usuń jeden wpis → sugestia wraca do oczekujących;
    w filtrze „odrzucone" przywróć odrzuconą.
13. **Odświeżanie po zmianie źródła** — zmień w OneDrive nazwę pliku z oczekującej
    sugestii (np. dopisz nazwę klienta) → „Synchronizuj" → karta pokazuje nowy tytuł
    i nowe dopasowanie, a raport linijkę „zaktualizowano 1". Odrzuconych i zatwierdzonych
    sync nie dotyka.
14. **Zarządzanie sprawami** — w „Sprawach" dodaj nową sprawę z własnym słowem kluczowym,
    nazwij tak spotkanie/plik i pokaż automatyczne dopasowanie po synchronizacji.
    Zdezaktywuj sprawę i pokaż, że przestaje brać udział w dopasowaniu (bez usuwania —
    wpisy czasu wskazują na sprawy).
15. **Motywy** — przełącz jasny / niebieski / ciemny w nagłówku; wybór zostaje zapamiętany.

## Materiał do notatki końcowej

Fakty zebrane w trakcie projektu (do samodzielnego opisania):

- Program Microsoft 365 Developer nie przyznaje już darmowych sandboxów E5 —
  środowisko zbudowano z własnej dzierżawy Entra ID (tylko rejestracja aplikacji)
  i zwykłego konta Microsoft z Outlookiem i OneDrive (dane).
- Prywatne konto Microsoft nie ma własnego katalogu Entra — dzierżawę trzeba było
  utworzyć przed jakąkolwiek rejestracją aplikacji.
- API `/me/drive/recent` jest wycofywane — wybrano zapytanie delta z filtrowaniem
  po stronie klienta (uzasadnienie w README).
- Pierwszy przebieg delta przechodzi cały dysk OneDrive (przy dużym dysku ~30 s) —
  rozwiązane przez cache `deltaLink`; pełny przebieg pozostaje kosztem pierwszego syncu.
