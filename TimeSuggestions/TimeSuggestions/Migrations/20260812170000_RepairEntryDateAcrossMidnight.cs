using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <summary>
    /// Jednorazowa naprawa danych: doliczenie przerwy przez lokalną północ przesuwało
    /// StartedAt na poprzedni dzień bez przeliczenia EntryDate (rozjazd zaobserwowany
    /// na sugestii 83 i wpisie 16 — StartedAt 11.08, EntryDate 12.08). EntryDate jest
    /// osią grupowania, sum dziennych i archiwizacji zakresem dat, a przy każdym innym
    /// tworzeniu pozycji jest wprost datą lokalną ze StartedAt (oba źródła), więc
    /// wyrównanie EntryDate = date(StartedAt) przywraca niezmiennik bez utraty danych.
    /// Dotyczy też pozycji zarchiwizowanych — aplikacja ich nie poprawi (archiwum
    /// blokuje operacje), a rozjazd pokazywał je w dniu, w którym nie było pracy.
    /// Sama operacja jest od tej pory odmawiana (luka przez północ nie jest oferowana).
    /// </summary>
    public partial class RepairEntryDateAcrossMidnight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE Suggestions SET EntryDate = date(StartedAt) WHERE date(StartedAt) <> EntryDate;");
            migrationBuilder.Sql(
                "UPDATE TimeEntries SET EntryDate = date(StartedAt) WHERE date(StartedAt) <> EntryDate;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Naprawa danych bez zmiany schematu — stanu sprzed rozjazdu nie da się
            // (i nie ma po co) odtworzyć.
        }
    }
}
