using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Contoso.Enrollments.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Enrollments.Tests;

[TestClass]
public class ApiTests
{
    private TestApp app = null!;
    private HttpClient client = null!;
    [TestInitialize] public void Setup() { foreach (var (key, value) in Settings) Environment.SetEnvironmentVariable(key, value); app = new(); client = app.CreateClient(); }
    internal static readonly Dictionary<string, string> Settings = new() { ["ASPNETCORE_ENVIRONMENT"] = "Testing", ["ConnectionStrings__Enrollments"] = "Server=unused;Database=unused", ["AzureAd__TenantId"] = "test", ["AzureAd__Audience"] = "test-audience", ["Students__BaseUrl"] = "https://students.test/", ["Students__Scope"] = "api://students/.default", ["Academics__BaseUrl"] = "https://academics.test/", ["Academics__Scope"] = "api://academics/.default" };
    [TestCleanup] public void Cleanup() { client?.Dispose(); app?.Dispose(); foreach (var key in Settings.Keys) Environment.SetEnvironmentVariable(key, null); }
    private void Auth(string scopes = "enrollments.read enrollments.write", string role = "Registrar", string? idtyp = null)
    {
        var claims = new List<Claim> { new("scp", scopes), new("roles", role) };
        if (idtyp != null) claims.Add(new("idtyp", idtyp));
        var token = new JwtSecurityToken("https://issuer.test", "test-audience", claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(TestApp.Key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    [TestMethod]
    public async Task SignedTokensEnforceDelegatedScopesAndRoles()
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.token.value"); Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        var wrongKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("This-is-another-test-only-signing-key-987654321"));
        var claims = new[] { new Claim("scp", "enrollments.read"), new Claim("roles", "Reader") };
        var badSignature = new JwtSecurityToken("https://issuer.test", "test-audience", claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(wrongKey, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(badSignature));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        var expired = new JwtSecurityToken("https://issuer.test", "test-audience", claims, expires: DateTime.UtcNow.AddMinutes(-10), signingCredentials: new SigningCredentials(TestApp.Key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(expired));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        Auth("enrollments.readextra", "Registrar"); Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        Auth("enrollments.read", "Reader"); Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId = 1, courseId = 2, grade = (string?)null })).StatusCode);
        Auth("enrollments.read enrollments.write", "Registrar", "app");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId = 1, courseId = 2, grade = (string?)null })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/enrollments")).StatusCode);
    }
    [TestMethod]
    public async Task CrudValidatesReferencesAndEnforcesUniqueness()
    {
        Auth();
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId = 1, courseId = 2, grade = "E" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId = 1, courseId = 2 })).StatusCode);
        var body = new { studentId = 1, courseId = 2, grade = (string?)null };
        var created = await client.PostAsJsonAsync("/api/v1/enrollments", body);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var payload = await created.Content.ReadAsStringAsync();
        using var createdJson = System.Text.Json.JsonDocument.Parse(payload);
        CollectionAssert.AreEquivalent(new[] { "id", "studentId", "courseId", "grade" }, createdJson.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(1, createdJson.RootElement.GetProperty("studentId").GetInt32());
        Assert.AreEqual(2, createdJson.RootElement.GetProperty("courseId").GetInt32());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, createdJson.RootElement.GetProperty("grade").ValueKind);
        var dto = System.Text.Json.JsonSerializer.Deserialize<EnrollmentDto>(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); Assert.IsNotNull(dto);
        Assert.AreEqual($"/api/v1/enrollments/{dto.Id}", created.Headers.Location!.OriginalString);
        CollectionAssert.AreEqual(new[] { "/api/v1/students/1", "/api/v1/courses/2" }, app.Handler.Paths.ToArray());
        Assert.IsTrue(app.Handler.Authenticated);
        CollectionAssert.AreEqual(new[] { "api://students/.default", "api://academics/.default" }, app.Credential.Scopes.ToArray());
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/enrollments/{dto.Id}", new { grade = "A" })).StatusCode);
        var updated = await client.GetFromJsonAsync<EnrollmentDto>($"/api/v1/enrollments/{dto.Id}"); Assert.AreEqual("A", updated!.Grade);
        var page = await client.GetFromJsonAsync<Page>("/api/v1/enrollments?studentId=1&page=1"); Assert.AreEqual(1, page!.TotalCount); Assert.AreEqual(50, page.PageSize);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/enrollments?page=0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/enrollments/{dto.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/enrollments/{dto.Id}")).StatusCode);
    }
    [TestMethod]
    public async Task MissingAndUnavailableReferencesAreDistinct()
    {
        Auth(); var body = new { studentId = 3, courseId = 4, grade = "B" };
        app.Handler.Status = HttpStatusCode.NotFound;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
        app.Handler.Status = HttpStatusCode.Forbidden;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
        app.Handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
        app.Handler.Status = HttpStatusCode.OK; app.Handler.CourseStatus = HttpStatusCode.NotFound;
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
        app.Handler.CourseStatus = null; app.Handler.WrongId = true;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/enrollments", body)).StatusCode);
    }
    private sealed record Page(int TotalCount, int PageSize);
}

public sealed class TestApp(string? sqlConnectionString = null) : WebApplicationFactory<Program>
{
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("This-is-a-test-only-long-signing-key-123456789"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public LookupHandler Handler { get; } = new();
    public FakeCredential Credential { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (sqlConnectionString is null) connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Enrollments"] = "Server=unused;Database=unused",
            ["AzureAd:TenantId"] = "test",
            ["AzureAd:Audience"] = "test-audience",
            ["Students:BaseUrl"] = "https://students.test/",
            ["Students:Scope"] = "api://students/.default",
            ["Academics:BaseUrl"] = "https://academics.test/",
            ["Academics:Scope"] = "api://academics/.default"
        }));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<EnrollmentDb>>(); s.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<EnrollmentDb>>();
            s.AddDbContext<EnrollmentDb>(o =>
            {
                if (sqlConnectionString is null) o.UseSqlite(connection);
                else o.UseSqlServer(sqlConnectionString);
            });
            s.RemoveAll<TokenCredential>(); s.AddSingleton<TokenCredential>(Credential);
            s.RemoveAll<IDuplicateError>();
            if (sqlConnectionString is null) s.AddSingleton<IDuplicateError, SqliteDuplicate>();
            else s.AddSingleton<IDuplicateError, SqlDuplicateError>();
            s.AddHttpClient("references").ConfigurePrimaryHttpMessageHandler(() => Handler);
            s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = "https://issuer.test" }; metadata.SigningKeys.Add(Key);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                o.TokenValidationParameters.ValidIssuer = "https://issuer.test";
            });
            if (sqlConnectionString is null)
            {
                using var provider = s.BuildServiceProvider(); using var scope = provider.CreateScope(); scope.ServiceProvider.GetRequiredService<EnrollmentDb>().Database.EnsureCreated();
            }
        });
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) connection.Dispose(); }
}
public sealed class LookupHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode? CourseStatus { get; set; }
    public bool WrongId { get; set; }
    public bool Authenticated { get; private set; }
    public List<string> Paths { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Paths.Add(request.RequestUri!.AbsolutePath); Authenticated = request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-token";
        var id = int.Parse(request.RequestUri.Segments.Last(), System.Globalization.CultureInfo.InvariantCulture);
        var isCourse = request.RequestUri.AbsolutePath.Contains("/courses/");
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", isCourse ? "course.json" : "student.json")))!;
        fixture["id"] = WrongId ? id + 1 : id;
        return Task.FromResult(new HttpResponseMessage(isCourse ? CourseStatus ?? Status : Status) { Content = JsonContent.Create(fixture) });
    }
}
public sealed class FakeCredential : TokenCredential
{
    public List<string> Scopes { get; } = [];
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) { Scopes.AddRange(requestContext.Scopes); return new("test-token", DateTimeOffset.UtcNow.AddMinutes(5)); }
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
public sealed class SqliteDuplicate : IDuplicateError
{
    public bool IsDuplicate(DbUpdateException error) => error.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
