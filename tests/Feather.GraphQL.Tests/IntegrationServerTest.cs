using System.Diagnostics.CodeAnalysis;
using Feather.GraphQL.Tests;
using Feather.GraphQL.Tests.StarWars;
using Feather.GraphQL.Tests.StarWars.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GraphQL.Integration.Tests.Helpers;

[TestFixture]
[SuppressMessage("ReSharper", "UseConfigureAwaitFalse")]
public abstract class IntegrationServerTest
{
    protected WebApplication Server;

    [OneTimeSetUp]
    public async Task BeforeAll()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddDebug();

        builder.WebHost.UseKestrelCore();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.UseUrls("https://localhost:5000");

        builder.Services.AddHttpLogging();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddSingleton<StarWarsData>();
        builder.Services.AddSingleton<StarWarsMutation>();
        builder.Services.AddTransient<CharacterInterface>();
        builder.Services.AddTransient<DroidType>();
        builder.Services.AddTransient<EpisodeEnum>();
        builder.Services.AddTransient<HumanType>();
        builder.Services.AddTransient<HumanInputType>();

        builder.Services.AddGraphQL(b1 => b1
                .AddAutoSchema<StarWarsQuery>()
                .UseApolloTracing(enableMetrics: true)
                .AddErrorInfoProvider(opt => opt.ExposeExceptionDetails = builder.Environment.IsDevelopment())
                .AddSystemTextJson());

        var app = builder.Build();
        this.Server = app;

        app.UseRouting();
        app.UseHttpLogging();
        app.UseWebSockets();
        app.UseGraphQL();

        await app.StartAsync();
    }

    [OneTimeTearDown]
    public async Task AfterAll()
    {
        await Server.StopAsync().ConfigureAwait(false);
        await Server.DisposeAsync();
    }
}
