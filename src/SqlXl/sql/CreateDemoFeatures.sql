/*=============================================================================
  SqlXL Demo Features — CreateDemoFeatures.sql

  Configures the Tier 3 custom BulkOpFeature for the RBAC demo:
    "Assign User Roles" — bulk-assign roles to users via a denormalized
    Excel template, even though the domain model is three normalized tables
    (Users, Roles, UserRoles).

  This script runs AFTER CreateDemoDatabase.sql and CreateInfrastructure.sql.
  It requires both the SqlXlDemo domain tables and the SqlXl schema to exist.

  Embedded resource — executed by: sqlxl demo --connection "..."
=============================================================================*/

USE SqlXlDemo;
GO

/*=============================================================================
  CUSTOM STAGING TABLE
  Shape designed for the data-entry person, not the database.
  One row per user, up to four role name slots.
=============================================================================*/

IF OBJECT_ID('SqlXl.Staging_UserRoleAssignments', 'U') IS NOT NULL
    DROP TABLE SqlXl.Staging_UserRoleAssignments;
GO

CREATE TABLE SqlXl.Staging_UserRoleAssignments (
    ID        INT          IDENTITY(1,1) NOT NULL,
    RequestID NVARCHAR(36) NOT NULL,
    UserName  NVARCHAR(100) NOT NULL,
    Role1     NVARCHAR(100) NULL,
    Role2     NVARCHAR(100) NULL,
    Role3     NVARCHAR(100) NULL,
    Role4     NVARCHAR(100) NULL,
    CONSTRAINT PK_Staging_UserRoleAssignments PRIMARY KEY (ID)
);
GO

/*=============================================================================
  META_COLUMNS
  Tells SqlXl about each editable column in the staging table.
  Staging table must exist first (CHECK constraint validates column existence).
  Role1-4 are marked IsForeignKey=YES so GetDropDownOptionsForFeature
  picks them up and populates the DropdownOptions sheet.
=============================================================================*/

DELETE FROM SqlXl.Meta_Columns
WHERE SchemaName = 'SqlXl' AND TableName = 'Staging_UserRoleAssignments';

INSERT INTO SqlXl.Meta_Columns
    (SchemaName, TableName, ColumnName, SqlDataType, IsNullable, IsPrimaryKey, IsForeignKey)
VALUES
    ('SqlXl', 'Staging_UserRoleAssignments', 'UserName', 'nvarchar', 'NO',  'NO', 'NO'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role1',    'nvarchar', 'YES', 'NO', 'YES'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role2',    'nvarchar', 'YES', 'NO', 'YES'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role3',    'nvarchar', 'YES', 'NO', 'YES'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role4',    'nvarchar', 'YES', 'NO', 'YES');
GO

/*=============================================================================
  COLUMN UI CONFIGURATIONS
  Provides the SELECT that drives the dropdown options for the Role columns.
  Returns the role name as both Value and Text — the user types/picks the name,
  and the processing sproc resolves it to a RoleID.
=============================================================================*/

DELETE FROM SqlXl.ColumnUIConfigurations
WHERE SchemaName = 'SqlXl' AND TableName = 'Staging_UserRoleAssignments';

INSERT INTO SqlXl.ColumnUIConfigurations
    (SchemaName, TableName, ColumnName, DropdownSelectStatement, UIHint)
VALUES
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role1',
     'SELECT RoleName AS Value, RoleName AS Text FROM dbo.Roles',
     'select'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role2',
     'SELECT RoleName AS Value, RoleName AS Text FROM dbo.Roles',
     'select'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role3',
     'SELECT RoleName AS Value, RoleName AS Text FROM dbo.Roles',
     'select'),
    ('SqlXl', 'Staging_UserRoleAssignments', 'Role4',
     'SELECT RoleName AS Value, RoleName AS Text FROM dbo.Roles',
     'select');
GO

/*=============================================================================
  PROCESSING SPROC
  Reads staged rows, resolves names to IDs, replaces all role assignments
  for each user mentioned.

  Contract: called by SqlXl infrastructure with @RequestID as the only param.
  Returns: SELECT IsSuccessful='true', UsersProcessed=N, RolesAssigned=N
=============================================================================*/

IF OBJECT_ID('dbo.UserRoleAssignments_ProcessFromStaging', 'P') IS NOT NULL
    DROP PROCEDURE dbo.UserRoleAssignments_ProcessFromStaging;
GO

CREATE PROCEDURE dbo.UserRoleAssignments_ProcessFromStaging
    @RequestID NVARCHAR(36)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UsersProcessed INT = 0;
    DECLARE @RolesAssigned  INT = 0;

    -- Cursor over every staged row for this request
    DECLARE @UserName NVARCHAR(100),
            @Role1    NVARCHAR(100), @Role2 NVARCHAR(100),
            @Role3    NVARCHAR(100), @Role4 NVARCHAR(100);

    DECLARE row_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT UserName, Role1, Role2, Role3, Role4
        FROM SqlXl.Staging_UserRoleAssignments
        WHERE RequestID = @RequestID;

    OPEN row_cursor;
    FETCH NEXT FROM row_cursor INTO @UserName, @Role1, @Role2, @Role3, @Role4;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Resolve username -> UserID (hard error if user not found)
        DECLARE @UserID INT;
        SELECT @UserID = UserID FROM dbo.Users WHERE Username = @UserName;
        IF @UserID IS NULL
            RAISERROR('User not found: "%s". Check dbo.Users.Username for valid values.', 16, 1, @UserName);

        -- Replace all current role assignments for this user
        DELETE FROM dbo.UserRoles WHERE UserID = @UserID;

        -- Collect the non-null role names for this row
        DECLARE @RequestedRoles TABLE (RoleName NVARCHAR(100));
        INSERT INTO @RequestedRoles (RoleName)
        SELECT rn FROM (VALUES (@Role1), (@Role2), (@Role3), (@Role4)) AS v(rn)
        WHERE rn IS NOT NULL AND LTRIM(RTRIM(rn)) != '';

        -- Validate: any provided role name must exist in dbo.Roles
        DECLARE @UnknownRole NVARCHAR(100);
        SELECT TOP 1 @UnknownRole = rr.RoleName
        FROM @RequestedRoles rr
        LEFT JOIN dbo.Roles r ON r.RoleName = rr.RoleName
        WHERE r.RoleID IS NULL;

        IF @UnknownRole IS NOT NULL
            RAISERROR('Role not found for user "%s": "%s". Check dbo.Roles.RoleName for valid values.', 16, 1, @UserName, @UnknownRole);

        -- Insert the new role assignments
        INSERT INTO dbo.UserRoles (UserID, RoleID)
        SELECT @UserID, r.RoleID
        FROM @RequestedRoles rr
        JOIN dbo.Roles r ON r.RoleName = rr.RoleName;

        SET @RolesAssigned  = @RolesAssigned  + @@ROWCOUNT;
        SET @UsersProcessed = @UsersProcessed + 1;

        -- Reset table variable for next row
        DELETE FROM @RequestedRoles;
        SET @UserID = NULL;

        FETCH NEXT FROM row_cursor INTO @UserName, @Role1, @Role2, @Role3, @Role4;
    END;

    CLOSE row_cursor;
    DEALLOCATE row_cursor;

    SELECT
        IsSuccessful   = 'true',
        UsersProcessed = @UsersProcessed,
        RolesAssigned  = @RolesAssigned;
END;
GO

/*=============================================================================
  BULKOPFEATURE ROW
  Ties everything together. The GetRowsToEdit_SelectStatement returns an empty
  result set with the right column shape — this is the INSERT-style pattern
  where the template starts blank and the user fills it in.
=============================================================================*/

-- Remove any previous run of this script
DELETE FROM SqlXl.BulkOpFeatures
WHERE StagingSchemaName = 'SqlXl' AND StagingTableName = 'Staging_UserRoleAssignments';

INSERT INTO SqlXl.BulkOpFeatures (
    UserFriendlyFeatureName,
    InsertUpdateDeleteOrCustom,
    DomainSchemaName,
    DomainTableName,
    StagingSchemaName,
    StagingTableName,
    GetRowsToChooseFrom_SelectStatement,
    GetRowsToEdit_SelectStatement,
    SprocToProcessPerfectStagedData,
    MenuDisplayRanking
)
VALUES (
    'Assign User Roles',
    'Custom',
    'dbo',
    'UserRoles',
    'SqlXl',
    'Staging_UserRoleAssignments',

    -- Used by Tier 3 UI to let user pick which records to edit (not used by CLI)
    'SELECT UserID, Username FROM dbo.Users ORDER BY Username',

    -- Template SELECT: zero rows, correct column shape.
    -- Pipe syntax: [DbColumnName|ExcelDisplayName]
    'SELECT
        CAST(NULL AS NVARCHAR(100)) AS [UserName|UserName],
        CAST(NULL AS NVARCHAR(100)) AS [Role1|Role1],
        CAST(NULL AS NVARCHAR(100)) AS [Role2|Role2],
        CAST(NULL AS NVARCHAR(100)) AS [Role3|Role3],
        CAST(NULL AS NVARCHAR(100)) AS [Role4|Role4]
     WHERE 1=0',

    'UserRoleAssignments_ProcessFromStaging',
    99  -- display last in any menu
);
GO
