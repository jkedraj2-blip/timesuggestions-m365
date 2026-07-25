using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;

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

// CORS zawężony do originów z konfiguracji (frontend deweloperski) — celowo nie AllowAnyOrigin.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Migracja przy starcie — baza plikowa SQLite ma być zawsze aktualna bez ręcznych kroków.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();

app.MapControllers();

app.Run();
