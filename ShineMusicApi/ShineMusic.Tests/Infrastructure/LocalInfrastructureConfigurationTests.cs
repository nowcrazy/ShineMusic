using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Npgsql;
using ShineMusic.Common.Configuration;
using StackExchange.Redis;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalInfrastructureConfigurationCollection
{
    public const string Name = "Local infrastructure configuration";
}

[Collection(LocalInfrastructureConfigurationCollection.Name)]
public sealed class LocalInfrastructureConfigurationTests
{
    private static readonly string PgSecret = new('A', 64);
    private static readonly string RedisSecret = new('b', 64);
    private static readonly string RabbitSecret = new('C', 64);
    private static readonly string[] MappedKeys =
        ["Database:ConnectionString", "Redis:ConnectionString", "RabbitMQ:HostName", "RabbitMQ:Password"];

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public void Development_maps_only_allowed_credentials_after_base_appsettings(string environmentName)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        var builder = new ConfigurationBuilder().AddJsonFile(repository.File("appsettings.json"));
        var baseSource = Assert.Single(builder.Sources);

        var returned = builder.AddLocalInfrastructureEnvironment(repository.Root, environmentName);

        Assert.Same(builder, returned);
        Assert.Same(baseSource, builder.Sources[0]);
        Assert.IsType<MemoryConfigurationSource>(builder.Sources[1]);
        using var configuration = (ConfigurationRoot)builder.Build();
        AssertLocalPostgresConnectionString(configuration[MappedKeys[0]], PgSecret);
        Assert.Equal($"127.0.0.1:6379,password={RedisSecret}", configuration[MappedKeys[1]]);
        Assert.Equal("127.0.0.1", configuration[MappedKeys[2]]);
        Assert.Equal(RabbitSecret, configuration[MappedKeys[3]]);
        Assert.Null(configuration["POSTGRES_PASSWORD"]);
        Assert.Null(configuration["REDIS_PASSWORD"]);
        Assert.Null(configuration["RABBITMQ_DEFAULT_PASS"]);
        Assert.Null(configuration["Auth:SigningKey"]);
    }

    [Fact]
    public void Configuration_manager_applies_inserted_local_source_immediately_without_build()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(repository.File("appsettings.json"));
        Assert.Equal("base-database", configuration[MappedKeys[0]]);

        configuration.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        // WebApplicationBuilder.Configuration is a live ConfigurationManager;
        // insertion must rebuild its providers without a separate Build call.
        AssertLocalPostgresConnectionString(configuration[MappedKeys[0]], PgSecret);
        Assert.Equal($"127.0.0.1:6379,password={RedisSecret}", configuration[MappedKeys[1]]);
        Assert.Equal("127.0.0.1", configuration[MappedKeys[2]]);
        Assert.Equal(RabbitSecret, configuration[MappedKeys[3]]);
    }

    [Fact]
    public void Configuration_manager_preserves_existing_and_later_explicit_overrides()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        repository.WriteJson("appsettings.Development.json", "development");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(repository.File("appsettings.json"));
        configuration.AddJsonFile(repository.File("appsettings.Development.json"));

        configuration.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.Equal("development-database", configuration[MappedKeys[0]]);
        Assert.Equal("development-redis", configuration[MappedKeys[1]]);
        Assert.Equal("development-host", configuration[MappedKeys[2]]);
        Assert.Equal("development-rabbit", configuration[MappedKeys[3]]);

        configuration.AddInMemoryCollection(MappedKeys.ToDictionary(key => key, _ => (string?)"later-explicit-value"));

        Assert.All(MappedKeys, key => Assert.Equal("later-explicit-value", configuration[key]));
    }

    [Theory]
    [InlineData("runtime-explicit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("base-database")]
    public void Configuration_manager_preserves_runtime_setter_values_when_inserting_local_source(string? runtimeValue)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(repository.File("appsettings.json"));
        configuration["Probe:Runtime"] = "explicit";
        configuration["Database:ConnectionString"] = runtimeValue;

        configuration.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.Equal("explicit", configuration["Probe:Runtime"]);
        Assert.Equal(runtimeValue, configuration["Database:ConnectionString"]);
        Assert.Equal($"127.0.0.1:6379,password={RedisSecret}", configuration["Redis:ConnectionString"]);
        Assert.Equal(RabbitSecret, configuration["RabbitMQ:Password"]);
    }

    [Theory]
    [InlineData("earlier-runtime", "later-explicit")]
    [InlineData(null, "later-explicit")]
    [InlineData("earlier-runtime", null)]
    [InlineData("earlier-runtime", "earlier-runtime")]
    public void Configuration_manager_keeps_explicit_sources_added_after_runtime_assignments(
        string? runtimeValue, string? explicitValue)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(repository.File("appsettings.json"));
        configuration["Probe:Runtime"] = "explicit";
        configuration["Database:ConnectionString"] = runtimeValue;
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = explicitValue
        });
        Assert.Equal(explicitValue, configuration["Database:ConnectionString"]);

        configuration.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.Equal(explicitValue, configuration["Database:ConnectionString"]);
        Assert.Equal("explicit", configuration["Probe:Runtime"]);
        Assert.Equal($"127.0.0.1:6379,password={RedisSecret}", configuration["Redis:ConnectionString"]);
    }

    [Fact]
    public void Configuration_manager_preserves_runtime_overrides_with_existing_explicit_sources()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        repository.WriteJson("appsettings.Development.json", "development");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(repository.File("appsettings.json"));
        configuration.AddJsonFile(repository.File("appsettings.Development.json"));
        configuration.AddCommandLine(["--Database:ConnectionString=cli-database", "--RabbitMQ:HostName=cli-host"]);
        configuration["Database:ConnectionString"] = "runtime-explicit";
        configuration["RabbitMQ:Password"] = null;

        configuration.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.Equal("runtime-explicit", configuration["Database:ConnectionString"]);
        Assert.Null(configuration["RabbitMQ:Password"]);
        Assert.Equal("development-redis", configuration["Redis:ConnectionString"]);
        Assert.Equal("cli-host", configuration["RabbitMQ:HostName"]);

        configuration.AddInMemoryCollection(MappedKeys.ToDictionary(key => key, _ => (string?)"later-explicit-value"));

        Assert.All(MappedKeys, key => Assert.Equal("later-explicit-value", configuration[key]));
    }

    [Fact]
    public void Explicit_development_json_environment_and_cli_keep_their_priority()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        repository.WriteJson("appsettings.Development.json", "development");
        var prefix = $"SHINEMUSIC_TEST_{Guid.NewGuid():N}_";
        var environmentName = prefix + "RabbitMQ__HostName";
        Environment.SetEnvironmentVariable(environmentName, "explicit-environment");
        try
        {
            var builder = new ConfigurationBuilder()
                .AddJsonFile(repository.File("appsettings.json"))
                .AddJsonFile(repository.File("appsettings.Development.json"))
                .AddEnvironmentVariables(prefix)
                .AddCommandLine(["--RabbitMQ:Password=explicit-command-line"]);
            var sources = builder.Sources.ToArray();

            builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

            Assert.Same(sources[0], builder.Sources[0]);
            Assert.IsType<MemoryConfigurationSource>(builder.Sources[1]);
            Assert.Equal(sources.Skip(1), builder.Sources.Skip(2));
            using var configuration = (ConfigurationRoot)builder.Build();
            Assert.Equal("development-database", configuration[MappedKeys[0]]);
            Assert.Equal("development-redis", configuration[MappedKeys[1]]);
            Assert.Equal("explicit-environment", configuration[MappedKeys[2]]);
            Assert.Equal("explicit-command-line", configuration[MappedKeys[3]]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Fact]
    public void Explicit_user_secret_like_source_keeps_priority()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        repository.WriteJson("appsettings.json", "base");
        // User secrets are a later JSON source in the default host configuration.
        repository.WriteJson("secrets.json", "user-secret");
        var builder = new ConfigurationBuilder()
            .AddJsonFile(repository.File("appsettings.json"))
            .AddJsonFile(repository.File("secrets.json"));

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.Equal("user-secret-database", configuration[MappedKeys[0]]);
        Assert.Equal("user-secret-redis", configuration[MappedKeys[1]]);
        Assert.Equal("user-secret-host", configuration[MappedKeys[2]]);
        Assert.Equal("user-secret-rabbit", configuration[MappedKeys[3]]);
    }

    [Fact]
    public void Without_base_json_all_existing_explicit_sources_remain_higher_priority()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        var explicitValues = MappedKeys.ToDictionary(key => key, _ => (string?)"explicit-value");
        var builder = new ConfigurationBuilder().AddInMemoryCollection(explicitValues);
        var explicitSource = Assert.Single(builder.Sources);

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.IsType<MemoryConfigurationSource>(builder.Sources[0]);
        Assert.Same(explicitSource, builder.Sources[1]);
        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.All(MappedKeys, key => Assert.Equal("explicit-value", configuration[key]));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Outside_development_invalid_local_environment_is_not_loaded(string environmentName)
    {
        using var repository = new TemporaryRepository();
        System.IO.File.WriteAllText(repository.File(".env"), "POSTGRES_PASSWORD=invalid-not-for-production");
        var builder = new ConfigurationBuilder().AddInMemoryCollection();
        var original = Assert.Single(builder.Sources);

        builder.AddLocalInfrastructureEnvironment(repository.Root, environmentName);

        Assert.Same(original, Assert.Single(builder.Sources));
        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.All(MappedKeys, key => Assert.Null(configuration[key]));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Missing_repository_marker_or_environment_file_does_not_add_a_source(bool marker, bool env)
    {
        using var repository = new TemporaryRepository(marker);
        if (env) repository.WriteEnvironment();
        var builder = new ConfigurationBuilder().AddInMemoryCollection();
        var original = Assert.Single(builder.Sources);

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        Assert.Same(original, Assert.Single(builder.Sources));
    }

    [Fact]
    public void Comments_whitespace_case_and_duplicate_keys_match_the_existing_loader()
    {
        using var repository = new TemporaryRepository();
        System.IO.File.WriteAllText(repository.File(".env"), $"""
              # local fixture only

            POSTGRES_PASSWORD=earlier-value-is-replaced
              postgres_password = {PgSecret}
            REDIS_PASSWORD = {RedisSecret}
            RABBITMQ_DEFAULT_PASS={RabbitSecret}
            Auth:SigningKey=untrusted-extra-setting
            Database__ConnectionString=untrusted-extra-connection
            unrelated malformed line
            """);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.Contains(PgSecret, configuration[MappedKeys[0]]!);
        Assert.Contains(RedisSecret, configuration[MappedKeys[1]]!);
        Assert.Equal(RabbitSecret, configuration[MappedKeys[3]]);
        Assert.Null(configuration["Auth:SigningKey"]);
        Assert.Null(configuration["Database__ConnectionString"]);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("short-password")]
    [InlineData("Abc123!")]
    [InlineData("two words")]
    [InlineData(@"path\secret")]
    [InlineData("key=value")]
    [InlineData("自定义密码")]
    public void Rabbitmq_accepts_nonempty_custom_passwords_without_changing_their_contents(string password)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment("RABBITMQ_DEFAULT_PASS", password);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.Equal(password, configuration["RabbitMQ:Password"]);
        Assert.Contains(PgSecret, configuration["Database:ConnectionString"]!);
        Assert.Contains(RedisSecret, configuration["Redis:ConnectionString"]!);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void Rabbitmq_does_not_require_a_fixed_password_length(int length)
    {
        using var repository = new TemporaryRepository();
        var password = new string('G', length);
        repository.WriteEnvironment("RABBITMQ_DEFAULT_PASS", password);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        Assert.Equal(password, configuration["RabbitMQ:Password"]);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("short-password")]
    [InlineData("a;b")]
    [InlineData("key=value")]
    [InlineData("a\"b")]
    [InlineData(@"path\secret")]
    [InlineData("two words")]
    [InlineData("secret;Host=other-host;Port=1;Database=other-db;Username=other-user")]
    public void Postgres_accepts_custom_passwords_without_injecting_connection_settings(string password)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment("POSTGRES_PASSWORD", password);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        AssertLocalPostgresConnectionString(configuration["Database:ConnectionString"], password);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("short-password")]
    [InlineData("Abc123!")]
    [InlineData("key=value")]
    [InlineData(@"path\secret")]
    [InlineData("a;b")]
    [InlineData("two words")]
    public void Redis_accepts_custom_passwords_without_changing_their_contents(string password)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment("REDIS_PASSWORD", password);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");

        using var configuration = (ConfigurationRoot)builder.Build();
        var options = ConfigurationOptions.Parse(Assert.IsType<string>(configuration["Redis:ConnectionString"]));
        Assert.Equal(password, options.Password);
        var endpoint = Assert.IsType<System.Net.IPEndPoint>(Assert.Single(options.EndPoints));
        Assert.Equal(System.Net.IPAddress.Loopback, endpoint.Address);
        Assert.Equal(6379, endpoint.Port);
    }

    [Theory]
    [InlineData("short,allowAdmin=true")]
    [InlineData("a,b")]
    public void Redis_passwords_with_commas_fail_without_disclosing_credentials(string password)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment("REDIS_PASSWORD", password);
        var builder = new ConfigurationBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddLocalInfrastructureEnvironment(repository.Root, "Development"));

        Assert.Contains("REDIS_PASSWORD", exception.Message);
        Assert.DoesNotContain(password, exception.ToString());
        Assert.DoesNotContain(PgSecret, exception.ToString());
        Assert.DoesNotContain(RedisSecret, exception.ToString());
        Assert.DoesNotContain(RabbitSecret, exception.ToString());
        Assert.Empty(builder.Sources);
    }

    public static IEnumerable<object[]> InvalidSecrets()
    {
        foreach (var key in new[] { "POSTGRES_PASSWORD", "REDIS_PASSWORD", "RABBITMQ_DEFAULT_PASS" })
        foreach (var value in new[] { "", " ", "\t", " \t " })
            yield return [key, value];
    }

    [Theory]
    [MemberData(nameof(InvalidSecrets))]
    public void Empty_or_whitespace_passwords_fail_without_disclosing_other_credentials(string name, string invalidValue)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment(name, invalidValue);
        var builder = new ConfigurationBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddLocalInfrastructureEnvironment(repository.Root, "Development"));

        Assert.Contains(name, exception.Message);
        Assert.DoesNotContain(PgSecret, exception.ToString());
        Assert.DoesNotContain(RedisSecret, exception.ToString());
        Assert.DoesNotContain(RabbitSecret, exception.ToString());
        Assert.Empty(builder.Sources);
    }

    [Theory]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("REDIS_PASSWORD")]
    [InlineData("RABBITMQ_DEFAULT_PASS")]
    public void Missing_required_keys_fail_without_disclosing_other_credentials(string missingName)
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment(omittedName: missingName);
        var builder = new ConfigurationBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddLocalInfrastructureEnvironment(repository.Root, "Development"));

        Assert.Contains(missingName, exception.Message);
        Assert.DoesNotContain(PgSecret, exception.ToString());
        Assert.DoesNotContain(RedisSecret, exception.ToString());
        Assert.DoesNotContain(RabbitSecret, exception.ToString());
        Assert.Empty(builder.Sources);
    }

    [Fact]
    public void Loading_does_not_change_process_environment_variables()
    {
        using var repository = new TemporaryRepository();
        repository.WriteEnvironment();
        string[] names =
        [
            "POSTGRES_PASSWORD", "REDIS_PASSWORD", "RABBITMQ_DEFAULT_PASS",
            "Database__ConnectionString", "Redis__ConnectionString", "RabbitMQ__HostName", "RabbitMQ__Password"
        ];
        var before = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var builder = new ConfigurationBuilder();

        builder.AddLocalInfrastructureEnvironment(repository.Root, "Development");
        using var configuration = (ConfigurationRoot)builder.Build();

        // Compare without putting any pre-existing environment values in assertion output.
        Assert.True(names.All(name => before[name] == Environment.GetEnvironmentVariable(name)),
            "Loading local configuration must not alter the process environment.");
    }

    private static void AssertLocalPostgresConnectionString(string? connectionString, string expectedPassword)
    {
        var connection = new NpgsqlConnectionStringBuilder(Assert.IsType<string>(connectionString));
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(5432, connection.Port);
        Assert.Equal("shinemusic", connection.Database);
        Assert.Equal("shinemusic", connection.Username);
        Assert.Equal(expectedPassword, connection.Password);
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"shinemusic-local-configuration-{Guid.NewGuid():N}");

        public TemporaryRepository(bool marker = true)
        {
            Directory.CreateDirectory(Root);
            if (!marker) return;
            Directory.CreateDirectory(File("ShineMusicApi"));
            System.IO.File.WriteAllText(File(Path.Combine("ShineMusicApi", "ShineMusic.slnx")), "<Solution />");
        }

        public string File(string relative) => Path.Combine(Root, relative);

        public void WriteEnvironment(string? replacedName = null, string? replacedValue = null, string? omittedName = null)
        {
            var values = new Dictionary<string, string>
            {
                ["POSTGRES_PASSWORD"] = PgSecret,
                ["REDIS_PASSWORD"] = RedisSecret,
                ["RABBITMQ_DEFAULT_PASS"] = RabbitSecret
            };
            if (replacedName is not null) values[replacedName] = replacedValue!;
            if (omittedName is not null) values.Remove(omittedName);
            System.IO.File.WriteAllLines(File(".env"), values.Select(pair => $"{pair.Key}={pair.Value}"));
        }

        public void WriteJson(string name, string prefix) => System.IO.File.WriteAllText(File(name),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Database = new { ConnectionString = $"{prefix}-database" },
                Redis = new { ConnectionString = $"{prefix}-redis" },
                RabbitMQ = new { HostName = $"{prefix}-host", Password = $"{prefix}-rabbit" }
            }));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
