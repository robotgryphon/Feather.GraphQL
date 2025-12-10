using Feather.GraphQL.Tests;
using Feather.GraphQL.Tests.StarWars;
using GraphQL;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<KestrelServerOptions>(options => options.AllowSynchronousIO = true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddStarWarsSchema();
builder.Services.AddGraphQL(b1 => b1
        .AddAutoSchema<StarWarsQuery>()
        .UseApolloTracing(enableMetrics: true)
        .AddErrorInfoProvider(opt => opt.ExposeExceptionDetails = builder.Environment.IsDevelopment())
        .AddSystemTextJson());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseWebSockets();

app.UseGraphQL();

app.Run();
