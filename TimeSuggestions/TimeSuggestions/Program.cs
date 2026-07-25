using System.Text.Json.Serialization;
using TimeSuggestions.Configuration;

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

// CORS zawężony do originów z konfiguracji (frontend deweloperski) — celowo nie AllowAnyOrigin.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();

app.MapControllers();

app.Run();
