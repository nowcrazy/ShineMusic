using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace ShineMusic.Common.Configuration;

/// <summary>Loads local development infrastructure credentials without changing process environment variables.</summary>
public static class LocalInfrastructureConfigurationExtensions
{
    private static readonly string[] SecretNames =
        ["POSTGRES_PASSWORD", "REDIS_PASSWORD", "RABBITMQ_DEFAULT_PASS"];

    /// <summary>
    /// Adds the marked repository's existing .env after base appsettings.json. Explicit development,
    /// user-secret, environment-variable and command-line sources retain their higher priority.
    /// </summary>
    public static IConfigurationBuilder AddLocalInfrastructureEnvironment(
        this IConfigurationBuilder configuration, string repositoryRoot, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return configuration;

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var envPath = Path.Combine(root, ".env");
        if (!File.Exists(Path.Combine(root, "ShineMusicApi", "ShineMusic.slnx")) || !File.Exists(envPath))
            return configuration;

        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(envPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            var separator = line.IndexOf('=');
            if (separator < 1)
                continue;
            var name = line[..separator].Trim();
            if (SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                secrets[name] = line[(separator + 1)..].Trim();
        }

        foreach (var name in SecretNames)
        {
            if (!secrets.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"The local .env must contain a non-empty value for {name}.");
        }

        // StackExchange.Redis splits connection strings on commas without a password escape syntax.
        if (secrets["REDIS_PASSWORD"].Contains(','))
            throw new InvalidOperationException("The local .env value for REDIS_PASSWORD cannot contain a comma because Redis connection strings use commas as separators.");

        var database = new DbConnectionStringBuilder
        {
            ConnectionString = "Host=127.0.0.1;Port=5432;Database=shinemusic;Username=shinemusic"
        };
        database["Password"] = secrets["POSTGRES_PASSWORD"];

        var source = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = database.ConnectionString,
                ["Redis:ConnectionString"] = $"127.0.0.1:6379,password={secrets["REDIS_PASSWORD"]}",
                ["RabbitMQ:HostName"] = "127.0.0.1",
                ["RabbitMQ:Password"] = secrets["RABBITMQ_DEFAULT_PASS"]
            }
        };

        var insertionIndex = 0;
        for (var index = 0; index < configuration.Sources.Count; index++)
        {
            if (configuration.Sources[index] is JsonConfigurationSource json &&
                string.Equals(Path.GetFileName(json.Path), "appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                insertionIndex = index + 1;
                break;
            }
        }
        // Inserting into a live ConfigurationManager rebuilds its providers and drops
        // indexer assignments. Its initially empty memory provider records those writes;
        // snapshot only them so base appsettings values do not override the local source.
        var runtimeOverrides = configuration is ConfigurationManager manager
            ? CaptureRuntimeOverrides(manager)
            : [];
        configuration.Sources.Insert(insertionIndex, source);
        if (configuration is ConfigurationManager liveConfiguration)
        {
            foreach (var (key, value) in runtimeOverrides)
                liveConfiguration[key] = value;
        }
        return configuration;
    }

    private static KeyValuePair<string, string?>[] CaptureRuntimeOverrides(ConfigurationManager configuration)
    {
        foreach (var (source, provider) in configuration.Sources.Zip(((IConfigurationRoot)configuration).Providers))
        {
            if (source is MemoryConfigurationSource { InitialData: null } &&
                provider is MemoryConfigurationProvider memory)
                // A later explicit source may already have superseded a runtime assignment.
                return memory.Where(pair => configuration[pair.Key] == pair.Value).ToArray();
        }
        return [];
    }
}
