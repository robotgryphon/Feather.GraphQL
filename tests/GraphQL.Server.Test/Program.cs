using GraphQL;
using GraphQL.Server.Test.GraphQL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGraphQL(builder => builder
        .AddSchema<TestSchema>()
        .AddSystemTextJson()
        .UseApolloTracing(enableMetrics: true)
        .AddErrorInfoProvider(opt => opt.ExposeExceptionDetails = true)
);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();

app.UseWebSockets();
app.UseGraphQL<TestSchema>();
app.UseGraphQLGraphiQL();
