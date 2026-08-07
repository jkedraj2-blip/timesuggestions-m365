namespace TimeSuggestions.Services;

/// <summary>
/// Konwersje między czasem lokalnym strefy biznesowej a UTC z jawną obsługą zmiany
/// czasu. Czas lokalny pozostaje podstawą StartedAt/EntryDate (poprawne biznesowo),
/// ale RÓŻNICE czasu wolno liczyć wyłącznie na jednoznacznych instantach UTC —
/// różnica lokalnych DateTime kłamie w noc zmiany czasu (np. 01:30–03:30 przy
/// wiosennej zmianie to 60 realnych minut, nie 120).
/// </summary>
public static class BusinessTime
{
    /// <summary>
    /// Sprowadza wartość z JSON do czasu lokalnego strefy biznesowej niezależnie od
    /// formatu wejścia: bez sufiksu (Kind=Unspecified) = już lokalny, tak przysyła
    /// Graph przy Prefer: outlook.timezone; "Z" (Kind=Utc) i jawny offset
    /// (Kind=Local po deserializacji System.Text.Json) są przeliczane.
    /// </summary>
    public static DateTime ToBusinessLocal(DateTime value, TimeZoneInfo timeZone) => value.Kind switch
    {
        DateTimeKind.Utc => TimeZoneInfo.ConvertTimeFromUtc(value, timeZone),
        DateTimeKind.Local => TimeZoneInfo.ConvertTimeFromUtc(value.ToUniversalTime(), timeZone),
        _ => value,
    };

    /// <summary>
    /// Jednoznaczny instant UTC dla wartości z JSON (Kind interpretowany jak wyżej).
    /// Konwencje nocy zmiany czasu — jawne, bo TimeZoneInfo domyślnie wybiera inaczej
    /// (czas standardowy) albo rzuca (czas nieistniejący):
    /// - czas NIEJEDNOZNACZNY (jesienne 02:30) = PIERWSZE wystąpienie, czyli większy offset;
    /// - czas NIEISTNIEJĄCY (wiosenne 02:30) = offset PO zmianie, jakby zegar już przeskoczył.
    /// </summary>
    public static DateTime ToUtcInstant(DateTime value, TimeZoneInfo timeZone)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Local)
        {
            return value.ToUniversalTime();
        }

        if (timeZone.IsAmbiguousTime(value))
        {
            var firstOccurrenceOffset = timeZone.GetAmbiguousTimeOffsets(value).Max();
            return DateTime.SpecifyKind(value - firstOccurrenceOffset, DateTimeKind.Utc);
        }

        if (timeZone.IsInvalidTime(value))
        {
            // Offset odczytany godzinę po czasie z luki — standardowa luka trwa godzinę,
            // więc value+1h leży już po zmianie.
            var offsetAfterGap = timeZone.GetUtcOffset(value.AddHours(1));
            return DateTime.SpecifyKind(value - offsetAfterGap, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(value, timeZone);
    }
}
