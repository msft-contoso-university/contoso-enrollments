using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Contoso.Enrollments.Api;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Enrollments.Tests;

[TestClass]
public sealed class SqlServerTests
{
    [TestMethod]
    public async Task MigrationsUniqueIndexAndConcurrencyUseRealSqlServer()
    {
        var source = Environment.GetEnvironmentVariable("ENROLLMENTS_TEST_SQL");
        if (string.IsNullOrEmpty(source)) { Assert.Inconclusive("SQL Server integration requires ENROLLMENTS_TEST_SQL (CI supplies a loopback container)."); return; }
        var options = new SqlConnectionStringBuilder(source);
        var endpoint = options.DataSource.Split(',')[0];
        if (endpoint != "127.0.0.1" && endpoint != "localhost") throw new InvalidOperationException("SQL test server must be loopback");
        options.InitialCatalog = "master";
        var dbName = "EnrollmentsTest_" + Guid.NewGuid().ToString("N");
        await using var master = new SqlConnection(options.ConnectionString); await master.OpenAsync();
        await using (var command = new SqlCommand($"CREATE DATABASE [{dbName}]", master)) await command.ExecuteNonQueryAsync();
        try
        {
            options.InitialCatalog = dbName;
            var dbOptions = new DbContextOptionsBuilder<EnrollmentDb>().UseSqlServer(options.ConnectionString).Options;
            await using var first = new EnrollmentDb(dbOptions); await first.Database.MigrateAsync();
            first.Enrollments.Add(new Enrollment { StudentId = 11, CourseId = 12 }); await first.SaveChangesAsync();
            await using var second = new EnrollmentDb(dbOptions);
            var a = await first.Enrollments.SingleAsync(); var b = await second.Enrollments.SingleAsync();
            a.Grade = "A"; a.Version++; await first.SaveChangesAsync();
            b.Grade = "B"; b.Version++;
            await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(async () => await second.SaveChangesAsync());
            await using var duplicate = new EnrollmentDb(dbOptions);
            duplicate.Enrollments.Add(new Enrollment { StudentId = 11, CourseId = 12 });
            var error = await Assert.ThrowsExactlyAsync<DbUpdateException>(async () => await duplicate.SaveChangesAsync());
            Assert.IsTrue(new SqlDuplicateError().IsDuplicate(error));
            await CaptureAuthenticatedHttpResponses(options.ConnectionString);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var command = new SqlCommand($"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]", master);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CaptureAuthenticatedHttpResponses(string connectionString)
    {
        var previous = ApiTests.Settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in ApiTests.Settings) Environment.SetEnvironmentVariable(key, value);
        try
        {
            await using var app = new TestApp(connectionString);
            using var client = app.CreateClient();
            var token = new JwtSecurityToken("https://issuer.test", "test-audience",
                [new Claim("scp", "enrollments.read enrollments.write"), new Claim("roles", "Registrar")],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(TestApp.Key, SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            var captures = new List<(string Name, string Schema, JsonElement Body)>();

            async Task<JsonElement> Capture(string name, string schema, HttpResponseMessage response, HttpStatusCode status)
            {
                using (response)
                {
                    Assert.AreEqual(status, response.StatusCode);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
                    var body = document.RootElement.Clone();
                    captures.Add((name, schema, body));
                    return body;
                }
            }

            var created = await Capture("created-null", "Enrollment",
                await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId = 21, courseId = 22, grade = (string?)null }), HttpStatusCode.Created);
            var id = created.GetProperty("id").GetInt32();
            Assert.IsGreaterThan(0, id);
            var detail = await Capture("detail-null", "Enrollment", await client.GetAsync($"/api/v1/enrollments/{id}"), HttpStatusCode.OK);
            Assert.AreEqual(JsonValueKind.Null, detail.GetProperty("grade").ValueKind);
            var page = await Capture("page-null", "EnrollmentPage", await client.GetAsync("/api/v1/enrollments?studentId=21&page=1"), HttpStatusCode.OK);
            Assert.AreEqual(id, page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());
            using var updated = await client.PutAsJsonAsync($"/api/v1/enrollments/{id}", new { grade = "A" });
            Assert.AreEqual(HttpStatusCode.NoContent, updated.StatusCode);
            detail = await Capture("detail-graded", "Enrollment", await client.GetAsync($"/api/v1/enrollments/{id}"), HttpStatusCode.OK);
            Assert.AreEqual("A", detail.GetProperty("grade").GetString());
            await Capture("page-graded", "EnrollmentPage", await client.GetAsync("/api/v1/enrollments?studentId=21&page=1"), HttpStatusCode.OK);
            Assert.IsTrue(app.Handler.Authenticated);
            CollectionAssert.AreEqual(new[] { "/api/v1/students/21", "/api/v1/courses/22" }, app.Handler.Paths);

            var directory = Environment.GetEnvironmentVariable("CONTOSO_PRODUCER_SAMPLES");
            if (directory is not null)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                    throw new InvalidOperationException("CONTOSO_PRODUCER_SAMPLES must be an absolute output directory.");
                Directory.CreateDirectory(directory);
                foreach (var capture in captures)
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, capture.Name + ".json"),
                        JsonSerializer.Serialize(new { schema = capture.Schema, body = capture.Body }));
                }
            }
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
