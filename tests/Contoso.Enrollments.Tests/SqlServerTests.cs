using Contoso.Enrollments.Api;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var command = new SqlCommand($"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]", master);
            await command.ExecuteNonQueryAsync();
        }
    }
}
