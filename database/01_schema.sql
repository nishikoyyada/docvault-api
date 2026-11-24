IF DB_ID('DocVault') IS NULL CREATE DATABASE DocVault;
GO
USE DocVault;
GO

CREATE TABLE dbo.Document (
    DocumentId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Document PRIMARY KEY NONCLUSTERED,
    SeqId          BIGINT IDENTITY(1,1) NOT NULL,          -- clustered key + paging cursor
    Title          NVARCHAR(200) NOT NULL,
    Category       VARCHAR(50)   NOT NULL,
    CurrentVersion INT           NOT NULL,
    CreatedBy      VARCHAR(100)  NOT NULL,
    CreatedOn      DATETIME2(0)  NOT NULL CONSTRAINT DF_Document_CreatedOn DEFAULT SYSUTCDATETIME(),
    UpdatedOn      DATETIME2(0)  NOT NULL CONSTRAINT DF_Document_UpdatedOn DEFAULT SYSUTCDATETIME(),
    IsDeleted      BIT           NOT NULL CONSTRAINT DF_Document_IsDeleted DEFAULT 0
);
CREATE UNIQUE CLUSTERED INDEX CX_Document_SeqId ON dbo.Document (SeqId);
CREATE NONCLUSTERED INDEX IX_Document_Category ON dbo.Document (Category, SeqId)
    INCLUDE (Title, CurrentVersion, UpdatedOn) WHERE IsDeleted = 0;

CREATE TABLE dbo.DocumentVersion (
    DocumentId    UNIQUEIDENTIFIER NOT NULL CONSTRAINT FK_Version_Document REFERENCES dbo.Document(DocumentId),
    VersionNo     INT           NOT NULL,
    BlobName      VARCHAR(300)  NOT NULL,
    FileName      NVARCHAR(260) NOT NULL,
    ContentType   VARCHAR(100)  NOT NULL,
    SizeBytes     BIGINT        NOT NULL,
    Sha256        CHAR(64)      NOT NULL,
    UploadedBy    VARCHAR(100)  NOT NULL,
    UploadedOn    DATETIME2(0)  NOT NULL CONSTRAINT DF_Version_UploadedOn DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_DocumentVersion PRIMARY KEY (DocumentId, VersionNo)
);
CREATE NONCLUSTERED INDEX IX_DocumentVersion_Sha256 ON dbo.DocumentVersion (Sha256);

CREATE TABLE dbo.AuditLog (
    AuditId    BIGINT IDENTITY(1,1) PRIMARY KEY,
    DocumentId UNIQUEIDENTIFIER NOT NULL,
    Action     VARCHAR(30)  NOT NULL,   -- Upload / NewVersion / Download / Delete
    VersionNo  INT          NULL,
    UserName   VARCHAR(100) NOT NULL,
    ActionOn   DATETIME2(0) NOT NULL CONSTRAINT DF_Audit_ActionOn DEFAULT SYSUTCDATETIME()
);
CREATE NONCLUSTERED INDEX IX_AuditLog_DocumentId ON dbo.AuditLog (DocumentId, ActionOn);
GO
