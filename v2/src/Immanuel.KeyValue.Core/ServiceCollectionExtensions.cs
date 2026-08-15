using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store and everything it needs. All three are singletons: the factory caches
    /// one database handle per app key, and nothing here holds per-request state.
    /// </summary>
    /// <param name="contentRootPath">Base folder that a relative
    /// <see cref="KeyValueOptions.DataDirectory"/> is resolved against.</param>
    public static IServiceCollection AddKeyValueStore(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.Configure<KeyValueOptions>(configuration.GetSection(KeyValueOptions.SectionName));

        services.AddSingleton(sp => new SqliteStoreFactory(
            sp.GetRequiredService<IOptions<KeyValueOptions>>(),
            contentRootPath,
            sp.GetRequiredService<ILogger<SqliteStoreFactory>>()));

        services.AddSingleton<AppKeyCatalog>();
        services.AddSingleton<KeyValueStore>();

        return services;
    }
}
