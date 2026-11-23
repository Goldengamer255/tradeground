using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Tradeground.Web;
using Tradeground.Web.Services;

// The browser app's starting point: runs in the browser, on WebAssembly.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API lives on the same origin that served this app, so relative URLs just work.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<TradegroundApi>();
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<Seats>();
builder.Services.AddScoped<Toasts>();
builder.Services.AddScoped<HowTo>();

await builder.Build().RunAsync();
