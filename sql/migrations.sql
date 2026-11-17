IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117174400_Initial'
)
BEGIN
    CREATE TABLE [Enrollments] (
        [Id] int NOT NULL IDENTITY,
        [StudentId] int NOT NULL,
        [CourseId] int NOT NULL,
        [Grade] nvarchar(1) NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_Enrollments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Enrollment_CourseId] CHECK (CourseId > 0),
        CONSTRAINT [CK_Enrollment_Grade] CHECK (Grade IS NULL OR Grade IN ('A','B','C','D','F')),
        CONSTRAINT [CK_Enrollment_StudentId] CHECK (StudentId > 0)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117174400_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Enrollments_StudentId_CourseId] ON [Enrollments] ([StudentId], [CourseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261117174400_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261117174400_Initial', N'10.0.13');
END;

COMMIT;
GO

