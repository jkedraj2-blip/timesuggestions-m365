namespace TimeSuggestions.Services;

/// <summary>
/// Godzina w komunikacie dla użytkownika. Sekundy pokazujemy WYŁĄCZNIE wtedy, gdy są
/// niezerowe — i to jest cały sens tej klasy.
///
/// Komunikaty formatowane sztywno jako HH:mm opisywały decyzje podejmowane na
/// sekundach: „Ten czas nachodzi na wpis (00:07–03:31)" przy sugestii kończącej się
/// 00:07:20 i wpisie zaczynającym się 00:07:10 wyglądał jak błąd aplikacji, bo obie
/// wartości na ekranie były równe. Komunikat, którego nie da się sprawdzić wzrokiem,
/// jest gorszy od braku komunikatu: prawnik nie ma jak ustalić, co ma poprawić.
/// </summary>
public static class BusinessMoment
{
    public static string Format(DateTime moment)
        => moment.Second == 0 ? moment.ToString("HH\\:mm") : moment.ToString("HH\\:mm\\:ss");

    /// <summary>„nazwa (09:00–10:30)" — jeden kształt opisu pozycji we wszystkich odmowach.</summary>
    public static string Describe(string title, DateTime startAt, DateTime endAt)
        => $"{title} ({Format(startAt)}–{Format(endAt)})";
}
