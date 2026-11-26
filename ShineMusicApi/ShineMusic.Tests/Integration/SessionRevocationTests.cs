using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Services;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

[Collection(L3aCollection.Name)]
public sealed class SessionRevocationTests : IClassFixture<SessionRevocationFactory>
{
    private readonly SessionRevocationFactory _factory;

    public SessionRevocationTests(SessionRevocationFactory factory) => _factory = factory;

    [Fact]
    public async Task Revoking_a_refresh_session_immediately_invalidates_its_access_token()
    {
        var user = await _factory.CreateUserAsync("revoke@example.test", "Revocation-Password-123!");
        var session = _factory.AddSession(user, [RoleNames.User]);
        Assert.True(await new UserService(_factory.Db).IsSessionActiveAsync(user.Id, session.SessionId, DateTime.UtcNow));
        using var client = _factory.CreateClient();

        using (var initial = await GetMeAsync(client, session.AccessToken))
            Assert.True(initial.StatusCode == HttpStatusCode.NoContent, await FailureDetailsAsync(initial, _factory.Logs));
        _factory.RevokeSession(user.Id, session.SessionId);

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(client, session.AccessToken));
        using var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Password_change_revokes_other_devices_and_keeps_the_current_session()
    {
        var user = await _factory.CreateUserAsync("password@example.test", "Original-Password-123!");
        var current = _factory.AddSession(user, [RoleNames.User]);
        var otherDevice = _factory.AddSession(user, [RoleNames.User]);
        using var client = _factory.CreateClient();

        using var change = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/password")
        {
            Content = JsonContent.Create(new { currentPassword = "Original-Password-123!", newPassword = "Updated-Password-456!" })
        };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        using var changed = await client.SendAsync(change);
        Assert.True(changed.StatusCode == HttpStatusCode.NoContent, await FailureDetailsAsync(changed, _factory.Logs));

        Assert.Equal(HttpStatusCode.NoContent, await GetMeStatusAsync(client, current.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(client, otherDevice.AccessToken));

        using var currentRefresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = current.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, currentRefresh.StatusCode);
        using var otherRefresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = otherDevice.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, otherRefresh.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("lock")]
    public async Task Administrative_role_or_lock_change_immediately_invalidates_old_access(string action)
    {
        var admin = await _factory.CreateUserAsync($"admin-{action}@example.test", "Admin-Password-123!");
        var target = await _factory.CreateUserAsync($"target-{action}@example.test", "Target-Password-123!");
        await _factory.AddRoleAsync(admin.Id, RoleNames.Admin);
        await _factory.AddRoleAsync(target.Id, RoleNames.Admin);
        var adminSession = _factory.AddSession(admin, [RoleNames.Admin]);
        var targetSession = _factory.AddSession(target, [RoleNames.Admin]);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSession.AccessToken);

        using var response = action == "role"
            ? await client.PostAsJsonAsync($"/api/v1/admin/users/{target.Id}/role", new AdminSetRoleRequest(RoleNames.User))
            : await client.PostAsync($"/api/v1/admin/users/{target.Id}/lock", content: null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await FailureDetailsAsync(response, _factory.Logs));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetMeStatusAsync(client, targetSession.AccessToken));
        using var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = targetSession.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Expired_temporary_admin_token_loses_admin_role_and_session_is_revoked()
    {
        var user = await _factory.CreateUserAsync("temp-admin@example.test", "Temp-Admin-Password-123!");
        await _factory.AddRoleAsync(user.Id, RoleNames.TempAdmin);
        user.TempAdminUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        await _factory.Db.Updateable(user).UpdateColumns(item => new { item.TempAdminUntilUtc }).ExecuteCommandAsync();
        var session = _factory.AddSession(user, [RoleNames.User, RoleNames.TempAdmin, RoleNames.Admin]);
        using var client = _factory.CreateClient();

        using (var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me"))
        {
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var profile = await client.SendAsync(profileRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, profile.StatusCode);
        }
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/users?page=1&pageSize=10");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var adminResponse = await client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, adminResponse.StatusCode);
    }

    private static async Task<HttpStatusCode> GetMeStatusAsync(HttpClient client, string accessToken)
    {
        using var response = await GetMeAsync(client, accessToken);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client, string accessToken)
    {
        // DELETE /me/avatar is authenticated and avoids unrelated SQLite DateTimeOffset materialization.
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/avatar");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<string> FailureDetailsAsync(HttpResponseMessage response, TestLogProvider logs) =>
        $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{string.Join("\n", logs.Messages)}";
}

public sealed class SessionRevocationFactory : WebApplicationFactory<Program>
{
    private const string SigningKey = "Session-Revocation-Integration-Test-Key-0123456789-abcdef";
    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private readonly string _previousWorkingDirectory = Environment.CurrentDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shine-session-revocation-" + Guid.NewGuid().ToString("N"));

    public SqlSugarClient Db { get; }
    public TestLogProvider Logs { get; } = new();
    private TokenService TokenService { get; } = new(Options.Create(new AuthOptions
    {
        SigningKey = SigningKey,
        Issuer = "ShineMusic",
        Audience = "ShineMusic.Client",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30
    }));

    public SessionRevocationFactory()
    {
        foreach (var (key, value) in new[] { ("Auth__SigningKey", SigningKey), ("Worker__Mode", "none") })
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        Environment.CurrentDirectory = ApiDirectory();
        Directory.CreateDirectory(_root);
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(_root, "identity.db")};Pooling=False",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.DbTableName = JsonNamingPolicy.SnakeCaseLower.ConvertName(entity.DbTableName),
                EntityService = (property, column) =>
                {
                    column.DbColumnName = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
                    // SQLite's DateTimeOffset support differs from PostgreSQL; keep a nullable storage column for auth queries.
                    if (property.Name == nameof(ApplicationUser.LockoutEnd)) column.IsNullable = true;
                }
            }
        });
        Db.CodeFirst.InitTables(typeof(ApplicationUser), typeof(ApplicationRole), typeof(ApplicationUserRole),
            typeof(RefreshToken), typeof(AdminAuditEntry), typeof(LoginAuditEntry));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ApiDirectory());
        builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(Logs); });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStartupBootstrap>();
            services.AddSingleton<IStartupBootstrap, NoopStartupBootstrap>();
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ISqlSugarClient>();
            services.AddSingleton<ISqlSugarClient>(Db);
        });
    }

    public async Task<ApplicationUser> CreateUserAsync(string email, string password)
    {
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = email,
            EmailConfirmed = true,
            LockoutEnd = null
        };
        var result = await new UserService(Db).CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        await AddRoleAsync(user.Id, RoleNames.User);
        return user;
    }

    public async Task AddRoleAsync(Guid userId, string roleName)
    {
        var userService = new UserService(Db);
        await userService.EnsureRoleAsync(roleName);
        var result = await userService.AddToRoleAsync(userId, roleName);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    public (Guid SessionId, string AccessToken, string RefreshToken) AddSession(ApplicationUser user, string[] roles)
    {
        var sessionId = Guid.NewGuid();
        var refresh = TokenService.CreateRefreshToken();
        var access = TokenService.CreateAccessToken(user, roles, sessionId);
        Db.Insertable(new RefreshToken
        {
            UserId = user.Id,
            SessionId = sessionId,
            TokenHash = refresh.Hash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = TokenService.GetRefreshTokenExpiry()
        }).ExecuteCommand();
        return (sessionId, access.Token, refresh.Token);
    }

    public void RevokeSession(Guid userId, Guid sessionId) => Db.Updateable<RefreshToken>()
        .SetColumns(token => token.RevokedAtUtc == DateTime.UtcNow)
        .Where(token => token.UserId == userId && token.SessionId == sessionId && token.RevokedAtUtc == null)
        .ExecuteCommand();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Db.Dispose();
        Environment.CurrentDirectory = _previousWorkingDirectory;
        foreach (var (key, value) in _previousEnvironment) Environment.SetEnvironmentVariable(key, value);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string ApiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var file = Path.Combine(directory.FullName, "ShineMusic.Api", "appsettings.json");
            if (File.Exists(file)) return Path.GetDirectoryName(file)!;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Cannot locate the API project configuration.");
    }
}

public sealed class TestLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IEnumerable<string> Messages => _messages.ToArray();
    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, _messages);
    public void Dispose() { }

    private sealed class TestLogger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
