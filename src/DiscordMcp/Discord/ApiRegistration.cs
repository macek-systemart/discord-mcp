using DiscordMcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordMcp.Discord;

public static class ApiRegistration
{
    /// <summary>
    /// <see cref="IDiscordApi"/> = <see cref="CachingDiscordApi"/> over <see cref="DiscordApi"/>, plus the tool runner.
    /// Requires <see cref="Http.HttpRegistration.AddDiscordHttp"/> and <see cref="AppHost.AddDiscordOptions"/>.
    /// </summary>
    public static IServiceCollection AddDiscordApi(this IServiceCollection services)
    {
        services.AddSingleton<DiscordApi>();
        services.AddSingleton<IDiscordApi>(sp => ActivatorUtilities.CreateInstance<CachingDiscordApi>(sp, sp.GetRequiredService<DiscordApi>()));
        services.AddSingleton<ToolRunner>();
        return services;
    }
}
