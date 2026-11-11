using Curto.Api.Infrastructure;

// Docker HEALTHCHECK entry point: no web host, just a probe against the local server.
if (args.Contains(HealthProbe.Flag, StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    return await HealthProbe.RunAsync(HealthProbe.ResolveUrl(Environment.GetEnvironmentVariable), client, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddCurtoEnvironmentAliases();
builder.Services.AddCurto(builder.Configuration);

var app = builder.Build();
await app.InitialiseAsync(app.Lifetime.ApplicationStopping);
app.UseCurto();
app.MapCurto();

await app.RunAsync();
return 0;

/// <summary>Entry point. Declared public so integration tests can host the whole application with <c>WebApplicationFactory</c>.</summary>
#pragma warning disable CA1515 // Consider making public types internal: required by WebApplicationFactory<TEntryPoint> in a public test fixture.
public partial class Program;
#pragma warning restore CA1515
