using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Contoso.Enrollments.Api;

public sealed class Enrollment
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public int CourseId { get; set; }
    public string? Grade { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class EnrollmentDb(DbContextOptions<EnrollmentDb> options) : DbContext(options)
{
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        var e = model.Entity<Enrollment>();
        e.HasIndex(x => new { x.StudentId, x.CourseId }).IsUnique();
        e.Property(x => x.Grade).HasMaxLength(1);
        e.Property(x => x.Version).IsConcurrencyToken();
        e.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Enrollment_StudentId", "StudentId > 0");
            t.HasCheckConstraint("CK_Enrollment_CourseId", "CourseId > 0");
            t.HasCheckConstraint("CK_Enrollment_Grade", "Grade IS NULL OR Grade IN ('A','B','C','D','F')");
        });
    }
}

public sealed class DesignFactory : IDesignTimeDbContextFactory<EnrollmentDb>
{
    public EnrollmentDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<EnrollmentDb>()
        .UseSqlServer("Server=localhost;Database=EnrollmentsDesign;Integrated Security=true;TrustServerCertificate=true").Options);
}
