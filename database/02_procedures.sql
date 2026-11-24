USE DocVault;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Document_Create
    @DocumentId UNIQUEIDENTIFIER, @Title NVARCHAR(200), @Category VARCHAR(50),
    @BlobName VARCHAR(300), @FileName NVARCHAR(260), @ContentType VARCHAR(100),
    @SizeBytes BIGINT, @Sha256 CHAR(64), @UserName VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRAN;
        INSERT dbo.Document (DocumentId, Title, Category, CurrentVersion, CreatedBy)
        VALUES (@DocumentId, @Title, @Category, 1, @UserName);

        INSERT dbo.DocumentVersion (DocumentId, VersionNo, BlobName, FileName, ContentType, SizeBytes, Sha256, UploadedBy)
        VALUES (@DocumentId, 1, @BlobName, @FileName, @ContentType, @SizeBytes, @Sha256, @UserName);

        INSERT dbo.AuditLog (DocumentId, Action, VersionNo, UserName)
        VALUES (@DocumentId, 'Upload', 1, @UserName);
    COMMIT;
END
GO

-- Adds a new version. UPDLOCK/HOLDLOCK stops two uploads getting the same version number.
CREATE OR ALTER PROCEDURE dbo.usp_Document_AddVersion
    @DocumentId UNIQUEIDENTIFIER, @BlobName VARCHAR(300), @FileName NVARCHAR(260),
    @ContentType VARCHAR(100), @SizeBytes BIGINT, @Sha256 CHAR(64), @UserName VARCHAR(100),
    @NewVersion INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRAN;
        SELECT @NewVersion = CurrentVersion + 1
        FROM dbo.Document WITH (UPDLOCK, HOLDLOCK)
        WHERE DocumentId = @DocumentId AND IsDeleted = 0;

        IF @NewVersion IS NULL
        BEGIN
            ROLLBACK;
            THROW 50404, 'Document not found.', 1;
        END

        INSERT dbo.DocumentVersion (DocumentId, VersionNo, BlobName, FileName, ContentType, SizeBytes, Sha256, UploadedBy)
        VALUES (@DocumentId, @NewVersion, @BlobName, @FileName, @ContentType, @SizeBytes, @Sha256, @UserName);

        UPDATE dbo.Document SET CurrentVersion = @NewVersion, UpdatedOn = SYSUTCDATETIME()
        WHERE DocumentId = @DocumentId;

        INSERT dbo.AuditLog (DocumentId, Action, VersionNo, UserName)
        VALUES (@DocumentId, 'NewVersion', @NewVersion, @UserName);
    COMMIT;
END
GO

-- Keyset pagination: pass the last SeqId you received to get the next page.
CREATE OR ALTER PROCEDURE dbo.usp_Document_List
    @Category VARCHAR(50) = NULL, @Search NVARCHAR(100) = NULL,
    @AfterSeqId BIGINT = 0, @PageSize INT = 20
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@PageSize)
           d.SeqId, d.DocumentId, d.Title, d.Category, d.CurrentVersion, d.UpdatedOn,
           v.FileName, v.SizeBytes, v.ContentType
    FROM dbo.Document d
    JOIN dbo.DocumentVersion v ON v.DocumentId = d.DocumentId AND v.VersionNo = d.CurrentVersion
    WHERE d.IsDeleted = 0
      AND d.SeqId > @AfterSeqId
      AND (@Category IS NULL OR d.Category = @Category)
      AND (@Search IS NULL OR d.Title LIKE N'%' + @Search + N'%')
    ORDER BY d.SeqId
    OPTION (RECOMPILE);   -- optional filters: build the right plan for each call
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Document_Get
    @DocumentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DocumentId, Title, Category, CurrentVersion, CreatedBy, CreatedOn, UpdatedOn
    FROM dbo.Document WHERE DocumentId = @DocumentId AND IsDeleted = 0;

    SELECT v.VersionNo, v.FileName, v.ContentType, v.SizeBytes, v.Sha256, v.UploadedBy, v.UploadedOn
    FROM dbo.DocumentVersion v
    JOIN dbo.Document d ON d.DocumentId = v.DocumentId AND d.IsDeleted = 0
    WHERE v.DocumentId = @DocumentId
    ORDER BY v.VersionNo DESC;
END
GO

-- Returns blob info for one version (NULL @VersionNo = latest) and writes an audit row.
CREATE OR ALTER PROCEDURE dbo.usp_Document_GetVersionForDownload
    @DocumentId UNIQUEIDENTIFIER, @VersionNo INT = NULL, @UserName VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @v INT = @VersionNo;
    IF @v IS NULL
        SELECT @v = CurrentVersion FROM dbo.Document WHERE DocumentId = @DocumentId AND IsDeleted = 0;

    SELECT v.VersionNo, v.BlobName, v.FileName, v.ContentType, v.SizeBytes
    FROM dbo.DocumentVersion v
    JOIN dbo.Document d ON d.DocumentId = v.DocumentId AND d.IsDeleted = 0
    WHERE v.DocumentId = @DocumentId AND v.VersionNo = @v;

    IF @@ROWCOUNT > 0
        INSERT dbo.AuditLog (DocumentId, Action, VersionNo, UserName)
        VALUES (@DocumentId, 'Download', @v, @UserName);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Document_SoftDelete
    @DocumentId UNIQUEIDENTIFIER, @UserName VARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRAN;
        DECLARE @Affected INT;
        UPDATE dbo.Document SET IsDeleted = 1, UpdatedOn = SYSUTCDATETIME()
        WHERE DocumentId = @DocumentId AND IsDeleted = 0;
        SET @Affected = @@ROWCOUNT;

        IF @Affected = 1
            INSERT dbo.AuditLog (DocumentId, Action, UserName) VALUES (@DocumentId, 'Delete', @UserName);
    COMMIT;
    SELECT @Affected AS Affected;   -- 1 = deleted, 0 = not found
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Document_FindByHash
    @Sha256 CHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) v.DocumentId, v.VersionNo
    FROM dbo.DocumentVersion v
    JOIN dbo.Document d ON d.DocumentId = v.DocumentId AND d.IsDeleted = 0
    WHERE v.Sha256 = @Sha256;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_AuditLog_ByDocument
    @DocumentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Action, VersionNo, UserName, ActionOn
    FROM dbo.AuditLog WHERE DocumentId = @DocumentId ORDER BY ActionOn DESC, AuditId DESC;
END
GO
