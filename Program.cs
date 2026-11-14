using EZEHR;
using EZEHR.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddEzehrCore();
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseEzehrCore();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapNetworkApi();

app.Run();

/// <summary>Exposed so EZEHR.Tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
