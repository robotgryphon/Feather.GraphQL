namespace Feather.GraphQL.Http;

internal static class IConfigurableExtensions
{
    public static TOptions New<TOptions>(this Action<TOptions> configure) =>
        configure.AndReturn(Activator.CreateInstance<TOptions>());

    public static TOptions AndReturn<TOptions>(this Action<TOptions> configure, TOptions options)
    {
        configure(options);
        return options;
    }
}
