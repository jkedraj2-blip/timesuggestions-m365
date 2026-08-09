using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;
using TimeSuggestions.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Enumy jako czytelne nazwy ("calendar", "pending") zamiast liczb — frontend
    // nie musi znać wewnętrznej numeracji, a payloady są samoopisujące.
    options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddOpenApi();

builder.Services.Configure<SuggestionOptions>(
    builder.Configuration.GetSection(SuggestionOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<ArchiveService>();
builder.Services.AddScoped<SummaryService>();

// CORS zawężony do originów z konfiguracji (frontend deweloperski) — celowo nie AllowAnyOrigin.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Migracja przy starcie — baza plikowa SQLite ma być zawsze aktualna bez ręcznych
// kroków. Przy oczekujących migracjach najpierw powstaje kopia pliku bazy
// (szczegóły i procedura odtworzenia: DatabaseMigrator oraz README).
using (var scope = app.Services.CreateScope())
{
    DatabaseMigrator.MigrateWithBackup(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Celowo bez UseHttpsRedirection: aplikacja działa na profilu http (localhost),
// który nie ma endpointu https — przekierowanie psułoby preflighty CORS.
app.UseCors();

app.MapControllers();

app.Run();

/// <summary>Marker dla WebApplicationFactory — testy integracyjne uruchamiają pełny pipeline HTTP.</summary>
public partial class Program;
