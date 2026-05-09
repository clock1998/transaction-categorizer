using TransactionCategorizer;
using TransactionCategorizer.Components;
using TransactionCategorizer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<PiiRedactor>();

builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.Section));

builder.Services.Configure<BudgetCategoriesOptions>(
    builder.Configuration.GetSection(BudgetCategoriesOptions.Section));

builder.Services.AddScoped<GeminiExtractor>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHealthChecks("/health");

app.Run();
