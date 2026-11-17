using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Contoso.Enrollments.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
var config = builder.Configuration;
string Required(string key) => config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing configuration: {key}");
var connection = Required("ConnectionStrings:Enrollments");
if (args.Contains("--migrate"))
{
    if (args.Length != 1) throw new ArgumentException("Usage: --migrate (demo seed is not supported)");
    await using var db = new EnrollmentDb(new DbContextOptionsBuilder<EnrollmentDb>().UseSqlServer(connection).Options);
    await db.Database.MigrateAsync();
    return;
}
if (args.Contains("--seed-demo")) throw new ArgumentException("Demo seed is not supported");
var tenant = Required("AzureAd:TenantId");
var audience = Required("AzureAd:Audience");
foreach (var service in new[] { "Students", "Academics" })
{
    if (!Uri.TryCreate(Required($"{service}:BaseUrl"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.AbsolutePath.EndsWith('/') || url.Query.Length != 0 || url.Fragment.Length != 0 || !string.IsNullOrEmpty(url.UserInfo)) throw new InvalidOperationException($"{service}:BaseUrl must be an HTTPS URL ending in / without credentials, query or fragment");
    if (!Required($"{service}:Scope").StartsWith("api://", StringComparison.Ordinal) || !config[$"{service}:Scope"]!.EndsWith("/.default", StringComparison.Ordinal)) throw new InvalidOperationException($"{service}:Scope must be api://<applicationId>/.default");
}
builder.Services.AddDbContext<EnrollmentDb>(o => o.UseSqlServer(connection));
builder.Services.AddSingleton<TokenCredential, DefaultAzureCredential>();
builder.Services.AddHttpClient("references", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<IReferenceLookup, ReferenceLookup>();
builder.Services.AddSingleton<IDuplicateError, SqlDuplicateError>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
    o.Audience = audience;
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters { RoleClaimType = "roles", ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("read", p => p.RequireAuthenticatedUser().RequireAssertion(c => Delegated(c.User, "enrollments.read") && (c.User.IsInRole("Reader") || c.User.IsInRole("Registrar"))))
    .AddPolicy("write", p => p.RequireAuthenticatedUser().RequireAssertion(c => Delegated(c.User, "enrollments.write") && c.User.IsInRole("Registrar")));
static bool Delegated(System.Security.Claims.ClaimsPrincipal user, string scope) =>
    !user.HasClaim("idtyp", "app") && user.FindAll("scp").Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal));
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    var connectionString = Required("APPLICATIONINSIGHTS_CONNECTION_STRING");
    builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("contoso-enrollments"))
        .UseAzureMonitor(o => { o.ConnectionString = connectionString; o.Credential = new DefaultAzureCredential(); });
}
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous();
app.MapGet("/health/ready", async (EnrollmentDb db, CancellationToken ct) =>
{
    try { await db.Enrollments.AsNoTracking().Take(1).ToListAsync(ct); return Results.Ok(); }
    catch (Exception ex) when (ex is SqlException or InvalidOperationException or DbUpdateException) { return Results.StatusCode(503); }
}).AllowAnonymous();
var api = app.MapGroup("/api/v1/enrollments");
api.MapGet("/", async (int? page, int? studentId, int? courseId, EnrollmentDb db, CancellationToken ct) =>
{
    var p = page ?? 1;
    if (p < 1 || p > 100000 || studentId is <= 0 || courseId is <= 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Invalid pagination or reference filter."] });
    var q = db.Enrollments.AsNoTracking();
    if (studentId.HasValue) q = q.Where(x => x.StudentId == studentId);
    if (courseId.HasValue) q = q.Where(x => x.CourseId == courseId);
    var count = await q.CountAsync(ct);
    var items = await q.OrderBy(x => x.Id).Skip((p - 1) * 50).Take(50).Select(x => new EnrollmentDto(x.Id, x.StudentId, x.CourseId, x.Grade)).ToListAsync(ct);
    return Results.Ok(new { items, page = p, pageSize = 50, totalCount = count });
}).RequireAuthorization("read");
api.MapGet("/{id:int}", async (int id, EnrollmentDb db, CancellationToken ct) =>
{
    var e = await db.Enrollments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return e is null ? Results.NotFound() : Results.Ok(new EnrollmentDto(e.Id, e.StudentId, e.CourseId, e.Grade));
}).RequireAuthorization("read");
api.MapPost("/", async (CreateEnrollment request, EnrollmentDb db, IReferenceLookup lookup, IDuplicateError duplicate, CancellationToken ct) =>
{
    if (request.StudentId <= 0 || request.CourseId <= 0 || !ValidGrade(request.Grade)) return Invalid();
    foreach (var (service, id) in new[] { ("Students", request.StudentId), ("Academics", request.CourseId) })
    {
        var result = await lookup.FindAsync(service, id, ct);
        if (result == LookupResult.Missing) return Results.ValidationProblem(new Dictionary<string, string[]> { [service] = ["Referenced record does not exist."] });
        if (result == LookupResult.Unavailable) return Results.Problem(statusCode: 503, title: "Reference service unavailable");
    }
    var e = new Enrollment { StudentId = request.StudentId, CourseId = request.CourseId, Grade = request.Grade };
    db.Enrollments.Add(e);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (duplicate.IsDuplicate(ex)) { return Results.Problem(statusCode: 409, title: "Enrollment already exists"); }
    return Results.Created($"/api/v1/enrollments/{e.Id}", new EnrollmentDto(e.Id, e.StudentId, e.CourseId, e.Grade));
}).RequireAuthorization("write");
api.MapPut("/{id:int}", async (int id, UpdateEnrollment request, EnrollmentDb db, CancellationToken ct) =>
{
    if (!ValidGrade(request.Grade)) return Invalid();
    var e = await db.Enrollments.FindAsync([id], ct);
    if (e is null) return Results.NotFound();
    e.Grade = request.Grade; e.Version++;
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Problem(statusCode: 409, title: "Concurrent modification"); }
    return Results.NoContent();
}).RequireAuthorization("write");
api.MapDelete("/{id:int}", async (int id, EnrollmentDb db, CancellationToken ct) =>
{
    var e = await db.Enrollments.FindAsync([id], ct);
    if (e is null) return Results.NotFound();
    db.Enrollments.Remove(e);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Problem(statusCode: 409, title: "Concurrent modification"); }
    return Results.NoContent();
}).RequireAuthorization("write");
app.Run();
static bool ValidGrade(string? grade) => grade is null or "A" or "B" or "C" or "D" or "F";
static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Positive IDs and grade A, B, C, D, F or null are required."] });
public sealed record EnrollmentDto(int Id, int StudentId, int CourseId, string? Grade);
public sealed record CreateEnrollment([property: JsonRequired] int StudentId, [property: JsonRequired] int CourseId, [property: JsonRequired] string? Grade);
public sealed record UpdateEnrollment([property: JsonRequired] string? Grade);
public partial class Program;

public interface IDuplicateError { bool IsDuplicate(DbUpdateException error); }
public sealed class SqlDuplicateError : IDuplicateError { public bool IsDuplicate(DbUpdateException error) => error.InnerException is SqlException sql && sql.Number is 2601 or 2627; }



