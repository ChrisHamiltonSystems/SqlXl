/****
CreateBulkOpsInfrastructure.sql

Creates objects in this order below...
### 0. Schema
### 1. (none) User-Defined Types (UDTs)
### 2. Functions
### 3. Tables 
### 4. (none) Views
### 4. Stored Procedures (Sprocs)
### 5. (none) Triggers
***************************************/

-- Create a separate schema for SqlXl functionality.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'SqlXl')
    EXEC('CREATE SCHEMA SqlXl');
GO

/******************************
User-Defined Function(s)...**/
CREATE OR ALTER FUNCTION [SqlXl].[SingularizeTableName](@TableName NVARCHAR(128))
RETURNS NVARCHAR(128)
AS
BEGIN
    DECLARE @Result NVARCHAR(128) = @TableName;

	-- Standard rules
    IF RIGHT(@TableName, 3) = 'ies'
        SET @Result = LEFT(@TableName, LEN(@TableName) - 3) + 'y'
    ELSE IF RIGHT(@TableName, 3) IN ('ses', 'xes', 'zes', 'ches', 'shes')
        SET @Result = LEFT(@TableName, LEN(@TableName) - 2)
    ELSE IF RIGHT(@TableName, 1) = 's' AND LEN(@TableName) > 1
        SET @Result = LEFT(@TableName, LEN(@TableName) - 1)

    RETURN @Result;
END
go

CREATE OR ALTER FUNCTION [SqlXl].[GenerateSelectStatementToSupportBulkEditingWithFKs]
  (
      @SchemaName NVARCHAR(128),
      @TableName NVARCHAR(128)
  )
  RETURNS NVARCHAR(MAX)
  AS
  BEGIN
      DECLARE @SQL NVARCHAR(MAX) = '';
      DECLARE @FullTableName NVARCHAR(256) = QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName);
      DECLARE @SelectClause NVARCHAR(MAX) = '';
      DECLARE @JoinClause NVARCHAR(MAX) = '';
      DECLARE @HasFKs BIT = 0;

      -- Check if table has any FKs using INFORMATION_SCHEMA
      IF EXISTS (
          SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk
              ON rc.constraint_name = fk.constraint_name
          WHERE fk.table_schema = @SchemaName AND fk.table_name = @TableName
      )
      BEGIN
          SET @HasFKs = 1;
      END

      -- If no FKs, return simple select statement
      IF @HasFKs = 0
      BEGIN
          RETURN 'select * from ' + @FullTableName;
      END

      -- Variables for cursor
      DECLARE @ColumnName NVARCHAR(128);
      DECLARE @OrdinalPosition INT;
      DECLARE @IsNullable VARCHAR(3);
      DECLARE @IsFK BIT;
      DECLARE @PKTable NVARCHAR(256);
      DECLARE @PKColumn NVARCHAR(128);
      DECLARE @ReferencedTableName NVARCHAR(128);
      DECLARE @BestDisplayColumn NVARCHAR(128);

      -- Cursor to process all columns in ordinal order
      DECLARE column_cursor CURSOR FOR
      SELECT
          col.column_name,
          col.ordinal_position,
          col.is_nullable,
          CASE WHEN fk_data.fk_column IS NOT NULL THEN 1 ELSE 0 END as is_fk,
          fk_data.pk_table,
          fk_data.pk_column,
          fk_data.referenced_table_name
      FROM INFORMATION_SCHEMA.COLUMNS col
      LEFT JOIN (
          SELECT
              fk_cols.column_name as fk_column,
              pk.table_schema + '.' + pk.table_name as pk_table,
              pk_cols.column_name as pk_column,
              pk.table_name as referenced_table_name
          FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk
              ON rc.constraint_name = fk.constraint_name
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk
              ON rc.unique_constraint_name = pk.constraint_name
          INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE fk_cols
              ON rc.constraint_name = fk_cols.constraint_name
          INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pk_cols
              ON rc.unique_constraint_name = pk_cols.constraint_name
              AND fk_cols.ordinal_position = pk_cols.ordinal_position
          WHERE fk.table_schema = @SchemaName AND fk.table_name = @TableName
      ) fk_data ON col.column_name = fk_data.fk_column
      WHERE col.table_schema = @SchemaName AND col.table_name = @TableName
      ORDER BY col.ordinal_position;

      OPEN column_cursor;
      FETCH NEXT FROM column_cursor INTO @ColumnName, @OrdinalPosition, @IsNullable, @IsFK, @PKTable, @PKColumn,
  @ReferencedTableName;

      WHILE @@FETCH_STATUS = 0
      BEGIN
          IF @IsFK = 1
          BEGIN
              -- Use GetBestDisplayColumn function to ensure consistency with dropdown logic
              DECLARE @PKTableSchema NVARCHAR(128);
              DECLARE @PKTableName NVARCHAR(128);

              SET @PKTableSchema = SqlXl.ParseSchemaFromReferencedTable(@PKTable);
              SET @PKTableName = SqlXl.ParseTableFromReferencedTable(@PKTable);

              SET @BestDisplayColumn = SqlXl.GetBestDisplayColumn(@PKTableSchema, @PKTableName);

			-- FK display column: pipe encodes "DbColumn|ExcelDisplayName" for import mapping.
			-- Left of pipe = staging column name (CategoryID), right = clean display name (Categories_CategoryName).
			-- Excel header shows right-side only; Metadata tab carries the full mapping.
			SET @SelectClause = @SelectClause +
				CASE WHEN @SelectClause = '' THEN '' ELSE ',' + CHAR(13) + CHAR(10) + '    ' END +
				QUOTENAME(@ColumnName + '|' + @ReferencedTableName + '_' + @BestDisplayColumn) + ' = ' +
				'CONCAT(' + @FullTableName + '.' + QUOTENAME(@ColumnName) + ', '' - '', ' +
				@PKTable + '.' + QUOTENAME(@BestDisplayColumn) + ')';

              -- Add JOIN clause
              SET @JoinClause = @JoinClause + CHAR(13) + CHAR(10) +
                  CASE WHEN @IsNullable = 'NO' THEN 'inner join ' ELSE 'left join ' END +
                  @PKTable + ' on ' + @FullTableName + '.' + QUOTENAME(@ColumnName) + ' = ' +
                  @PKTable + '.' + QUOTENAME(@PKColumn);
          END
          ELSE
          BEGIN
              -- Regular column: pipe with same value both sides so Excel header = DB column name.
              SET @SelectClause = @SelectClause +
                  CASE WHEN @SelectClause = '' THEN '' ELSE ',' + CHAR(13) + CHAR(10) + '    ' END +
                  QUOTENAME(@ColumnName + '|' + @ColumnName) + ' = ' + @FullTableName + '.' + QUOTENAME(@ColumnName);
          END

          FETCH NEXT FROM column_cursor INTO @ColumnName, @OrdinalPosition, @IsNullable, @IsFK, @PKTable, @PKColumn,
   @ReferencedTableName;
      END

      CLOSE column_cursor;
      DEALLOCATE column_cursor;

      -- Build final CREATE VIEW statement
      SET @SQL = 'select' +
                 CHAR(13) + CHAR(10) + '    ' + @SelectClause +
                 CHAR(13) + CHAR(10) + 'from ' + @FullTableName +
                 @JoinClause;

      RETURN @SQL;
  END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateDebugStarter]()
RETURNS NVARCHAR(MAX)
AS
BEGIN
    RETURN 
    N'IF OBJECT_ID(''tempdb..#ZZTemp'') IS NOT NULL
	DROP TABLE #ZZTemp;
--end if 

IF OBJECT_ID(''tempdb..#Messages'') IS NOT NULL
	DROP TABLE #Messages;
--end if 

SELECT * 
INTO #ZZTemp
FROM SqlXl.DebugZZTemp
;--end select-into

CREATE TABLE #Messages (Msg NVARCHAR(MAX));';
END;--end func 
GO

CREATE OR ALTER FUNCTION [SqlXl].PascalCaseToLabel (@Input NVARCHAR(MAX))
/*Examples...SELECT [SqlXl].PascalCaseToLabel('LastName') AS Label1,
       [SqlXl].PascalCaseToLabel('HireDate') AS Label2,
       [SqlXl].PascalCaseToLabel('PascalCaseToLabel') AS Label3;
Result:
Label1	Label2	Label3
Last Name	Hire Date	Pascal Case To Label 
******/
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @Result NVARCHAR(MAX) = '';
    DECLARE @Index INT = 1;
    DECLARE @Char NCHAR(1);

    -- Loop through each character in the string
    WHILE @Index <= LEN(@Input)
    BEGIN
        SET @Char = SUBSTRING(@Input, @Index, 1);

        -- Check if the current character is uppercase and not the first character
        IF @Index > 1 AND @Char COLLATE Latin1_General_BIN LIKE '[A-Z]'
        BEGIN
            SET @Result += ' ' + @Char;
        END
        ELSE
        BEGIN
            SET @Result += @Char;
        END

        SET @Index += 1;
    END

    RETURN @Result;
END --end func 
GO

CREATE OR ALTER FUNCTION [SqlXl].TableExists 
(
@SchemaName nvarchar(128),
@TableName NVARCHAR(128)
)
RETURNS BIT
AS
BEGIN
    DECLARE @result BIT = 0;
    
    -- Check if the table with the given schema exists in the database
    IF EXISTS (SELECT 1 
               FROM sys.tables t
               INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE t.name = @TableName
                 AND s.name = @SchemaName)
    BEGIN
        SET @result = 1;
    END

	--Allow this exception for now (technical debt)...
	if @TableName = 'NotApplicableForBulkDelete'
	begin
		SET @result = 1;
	end 
    
    RETURN @result;
END;--end func
go 

CREATE OR ALTER FUNCTION [SqlXl].SprocExists 
(
    @SchemaName NVARCHAR(128),
    @SprocName NVARCHAR(128)
)
RETURNS BIT
AS
BEGIN
    DECLARE @result BIT = 0;

    -- Check if the stored procedure with the given schema exists in the database
    IF EXISTS (SELECT 1 
               FROM sys.procedures p
               INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
               WHERE p.name = @SprocName
                 AND s.name = @SchemaName)
    BEGIN
        SET @result = 1;
    END

    RETURN @result;
END; -- end func
GO

CREATE OR ALTER FUNCTION [SqlXl].ColumnExists 
(
    @SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
    @ColumnName NVARCHAR(128)
)
RETURNS BIT
AS
BEGIN
    DECLARE @result BIT = 0;

    -- Check if the column exists in the specified table and schema
    IF EXISTS (SELECT 1 
               FROM sys.columns c
               INNER JOIN sys.tables t ON c.object_id = t.object_id
               INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE t.name = @TableName
                 AND s.name = @SchemaName
                 AND c.name = @ColumnName)
    BEGIN
        SET @result = 1;
    END

    RETURN @result;
END; -- end func
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateCreateStagingTableSQLWith_NO_IdentityProperty]
(
    @DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
    @StagingSchemaName NVARCHAR(128),
    @StagingTableName NVARCHAR(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
	declare @SQL nvarchar(max)

	/*Note: a simple way to create a new table empty
	table based on structure of an existing table would be:
	SET @SQL = N'SELECT * INTO ' + QUOTENAME(@StagingSchemaName) + '.' + QUOTENAME(@StagingTableName) + 
               N' FROM ' + QUOTENAME(@DomainSchemaName) + '.' + QUOTENAME(@DomainTableName) + 
               N' WHERE 1 = 0;';
	...BUT, the problem with that is that it transfer identity columns as such
	...and staging tables should *NOT* need identity properties, else
	...staging an update would require 'allow identity insert' on/off, etc.	*/

	-- Generate the column definitions excluding identity property
	SELECT @SQL = COALESCE(@SQL + ', ', '') +
		   COLUMN_NAME + ' ' +
		   DATA_TYPE +
		   CASE WHEN DATA_TYPE IN ('char', 'varchar', 'nchar', 'nvarchar') THEN '(' + 
				CASE WHEN CHARACTER_MAXIMUM_LENGTH = -1 THEN 'MAX'
					 ELSE CAST(CHARACTER_MAXIMUM_LENGTH AS NVARCHAR)
				END + ')' ELSE '' END + ' ' +
		   CASE WHEN IS_NULLABLE = 'NO' THEN 'NOT NULL' ELSE 'NULL' END
	FROM INFORMATION_SCHEMA.COLUMNS
	WHERE 
	TABLE_SCHEMA = @DomainSchemaName
	and TABLE_NAME = @DomainTableName
	ORDER BY ORDINAL_POSITION

	--Add RequestID column, too...
	set @SQL = @SQL + ',RequestID NVARCHAR(36) NOT NULL';

	-- Add CREATE TABLE statement
	SET @SQL = 
		'CREATE TABLE ' + @StagingSchemaName + '.' + @StagingTableName + 
		' (' + @SQL + ')';
    
    RETURN @SQL
END --end func
GO

CREATE OR ALTER FUNCTION [SqlXl].GenerateRandomString
(
    @MaxLength INT
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
	if @MaxLength is null or @MaxLength < 1
	begin
		return '';
	end 

    DECLARE @Chars NVARCHAR(MAX) = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    DECLARE @Result NVARCHAR(MAX) = '';
    DECLARE @i INT = 0;
    DECLARE @CharIndex INT;

    -- Use a seed that varies across executions (current time in milliseconds)
    DECLARE @Seed INT = ABS(CHECKSUM(CAST(GETDATE() AS VARBINARY))) % LEN(@Chars) + 1;

    WHILE @i < @MaxLength
    BEGIN
        -- Modify the seed with each iteration to create more variability
        SET @CharIndex = ABS(CHECKSUM(@i + @Seed)) % LEN(@Chars) + 1;
        SET @Result = @Result + SUBSTRING(@Chars, @CharIndex, 1);
        SET @i = @i + 1;
    END;

    RETURN @Result;
END;--end func 
GO

CREATE OR ALTER FUNCTION [SqlXl].[SqlToListAllRowsFromOnlyWithinZZTempThatDuplicate_A_ValueForColumn]
(
    @ColumnNameToCheckForDuplication NVARCHAR(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @SQL NVARCHAR(MAX)
    DECLARE @Message NVARCHAR(MAX)

	--Validation message to show...
	set @Message = 
		'''Row '' + CAST(t.ZZTemp_ID AS NVARCHAR) + '', within multiple rows of the input data, a duplicate value was found for ColumnNameToCheckForDuplication.  ColumnNameToCheckForDuplication must be unique in this context.''';
	
	/* List rows from #ZZTemp that duplicate 
		a value, for for the given column... */
	set @SQL = 
		'WITH DuplicateValues AS (
			SELECT ColumnNameToCheckForDuplication
			FROM #ZZTemp
			GROUP BY ColumnNameToCheckForDuplication
			HAVING COUNT(*) > 1
		)
		SELECT ' + @Message + ' AS Msg
		FROM #ZZTemp t
		JOIN DuplicateValues d
		ON t.ColumnNameToCheckForDuplication = d.ColumnNameToCheckForDuplication;'

	set @SQL = REPLACE(@SQL,'ColumnNameToCheckForDuplication', @ColumnNameToCheckForDuplication);	

	return @SQL;
END --end func 
GO

CREATE OR ALTER FUNCTION [SqlXl].GenerateDuplicateCheckSQL
(
    @DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
    @CommaDelimitedColumnNames NVARCHAR(MAX),
    @PrimaryKeyColumnName NVARCHAR(128) = NULL -- NULL for INSERT, populated for UPDATE
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @SQL NVARCHAR(MAX)
    DECLARE @Columns NVARCHAR(MAX)
    DECLARE @SelectColumns NVARCHAR(MAX)
    DECLARE @JoinCondition NVARCHAR(MAX)
    DECLARE @Message NVARCHAR(MAX)

    -- Prepare the columns for SELECT
    SET @SelectColumns = REPLACE(@CommaDelimitedColumnNames, ',', ', ')

    -- Prepare the JOIN condition
    DECLARE @Column NVARCHAR(128)
    DECLARE @Position INT
    DECLARE @NextPosition INT
    SET @JoinCondition = ''
    SET @Position = 1

    WHILE @Position <= LEN(@CommaDelimitedColumnNames)
    BEGIN
        SET @NextPosition = CHARINDEX(',', @CommaDelimitedColumnNames, @Position)
        IF @NextPosition = 0
            SET @NextPosition = LEN(@CommaDelimitedColumnNames) + 1

        SET @Column = SUBSTRING(@CommaDelimitedColumnNames, @Position, @NextPosition - @Position)
        IF @JoinCondition <> ''
            SET @JoinCondition = @JoinCondition + ' AND '
        SET @JoinCondition = @JoinCondition + 't.' + LTRIM(RTRIM(@Column)) + ' = dc.' + LTRIM(RTRIM(@Column))

        SET @Position = @NextPosition + 1
    END

    SET @Message = '''Row '' + CAST(t.ZZTemp_ID AS NVARCHAR) + '', row uniqueness violation, unique constraint column(s): ' + @CommaDelimitedColumnNames + '. Please note that this combines existing data from ' + @DomainSchemaName + '.' + @DomainTableName + '.'''

    -- For UPDATE scenarios, exclude rows being updated from the existing combinations check
    DECLARE @ExcludePKCondition NVARCHAR(MAX) = ''
    IF @PrimaryKeyColumnName IS NOT NULL
    BEGIN
        SET @ExcludePKCondition = '
        WHERE ' + QUOTENAME(@PrimaryKeyColumnName) + ' NOT IN (SELECT ' + QUOTENAME(@PrimaryKeyColumnName) + ' FROM #ZZTemp)'
    END

    SET @SQL = '
    WITH ExistingCombinations AS (
        SELECT ' + @SelectColumns + '
        FROM ' + @DomainSchemaName + '.' + @DomainTableName + @ExcludePKCondition + '
    ),
    AllCombinations AS (
        SELECT ' + @SelectColumns + '
        FROM #ZZTemp
        UNION ALL
        SELECT ' + @SelectColumns + '
        FROM ExistingCombinations
    ),
    DuplicateCombinations AS (
        SELECT ' + @SelectColumns + ', COUNT(*) AS cnt
        FROM AllCombinations
        GROUP BY ' + @SelectColumns + '
        HAVING COUNT(*) > 1
    )
    SELECT ' + @Message + ' AS Msg
    FROM #ZZTemp t
    JOIN DuplicateCombinations dc
    ON ' + @JoinCondition + ';'

    RETURN @SQL
END --end func
GO

CREATE OR ALTER FUNCTION [SqlXl].[CreateInvalidNonForeignKeySampleValue]
(
    @DataType NVARCHAR(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @Result NVARCHAR(MAX);

    -- Return an invalid sample value based on the data type
    SET @Result = CASE @DataType
        WHEN 'int' THEN 'InvalidInt' -- not an integer
        WHEN 'nvarchar' THEN '123' -- expected a non-numeric string
        WHEN 'varchar' THEN '123' -- expected a non-numeric string
        WHEN 'datetime' THEN 'InvalidDate' -- not a datetime
        WHEN 'datetime2' THEN 'InvalidDate' -- not a datetime2
        WHEN 'bit' THEN '2' -- bit should be 0 or 1
        WHEN 'decimal' THEN 'InvalidDecimal' -- not a decimal
        WHEN 'money' THEN 'InvalidMoney' -- not a money type
        WHEN 'float' THEN 'InvalidFloat' -- not a float
        WHEN 'date' THEN 'InvalidDate' -- not a date
        WHEN 'time' THEN 'InvalidTime' -- not a time
        WHEN 'bigint' THEN 'InvalidBigInt' -- not a bigint
        WHEN 'smallint' THEN 'InvalidSmallint' -- not a smallint
        WHEN 'tinyint' THEN 'InvalidTinyint' -- not a tinyint
        WHEN 'char' THEN 'InvalidChar' -- longer than expected for char(1)
        WHEN 'nchar' THEN 'InvalidNChar' -- longer than expected for nchar(1)
        WHEN 'binary' THEN 'InvalidBinary' -- not a binary
        WHEN 'varbinary' THEN 'InvalidVarbinary' -- not a varbinary
        WHEN 'image' THEN 'InvalidImage' -- not an image
        -- Add more data type cases as needed
        ELSE 'ERROR: Unsupported data type of ' + @DataType + '.'
    END

    RETURN @Result
END --end func
GO

CREATE OR ALTER FUNCTION [SqlXl].[CreateNonForeignKeySampleValue]
(
    @DataType NVARCHAR(128),
	@MaxLengthForString int
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @Result NVARCHAR(MAX);

    -- Return a sample value based on the data type
    SET @Result = CASE @DataType
        WHEN 'int' THEN '1'
        WHEN 'nvarchar' THEN '' --[SqlXl].GenerateRandomString(@MaxLengthForString)
        WHEN 'varchar' THEN '' --[SqlXl].GenerateRandomString(@MaxLengthForString)
        WHEN 'datetime' THEN '2023-01-01T00:00:00'
		when 'datetime2' then '2024-04-18 12:34:56'
        WHEN 'bit' THEN '1'
        WHEN 'decimal' THEN '1'
        WHEN 'money' THEN '1'
        WHEN 'float' THEN '1'
        WHEN 'date' THEN '2023-01-01'
        WHEN 'time' THEN '12:34:56.123' --'00:00:00'
        WHEN 'bigint' THEN '1'
        WHEN 'smallint' THEN '1'
        WHEN 'tinyint' THEN '1'
        WHEN 'char' THEN ''
        WHEN 'nchar' THEN ''
        --WHEN 'binary' THEN '0x01'
        --WHEN 'varbinary' THEN '0x010203'
        --WHEN 'image' THEN '0x01020304'
        -- Add more data type cases as needed
        ELSE 'ERROR: Unsupported data type of ' + @DataType + '.'
    END

    RETURN @Result
END --end func
go

CREATE OR ALTER FUNCTION SqlXl.GetPrimaryKeyColumnName (@SchemaName nvarchar(128), @TableName NVARCHAR(256))
RETURNS NVARCHAR(256)
AS
BEGIN
    DECLARE @PrimaryKeyColumnName NVARCHAR(256);

	-- Note here that the framework expects only ONE, single int primary key column!
    -- Find primary key column name for the specified table
    SELECT @PrimaryKeyColumnName = COL_NAME(ic.object_id, ic.column_id)
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    INNER JOIN sys.tables AS t ON i.object_id = t.object_id
    WHERE i.is_primary_key = 1
		and t.schema_id = schema_id(@SchemaName)
		AND t.name = @TableName;

    RETURN @PrimaryKeyColumnName;
END;--end func 
go

CREATE OR ALTER FUNCTION [SqlXl].[GenerateSqlToInsert_a_SingleValidSampleRowToStagingTable]
(
	@DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128),
	@StagingTableName nvarchar(128),
	@RequestID nvarchar(36)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN --begin func 
	DECLARE @SQL NVARCHAR(MAX),
			@ColumnName nvarchar(128) = '',
            @ColumnList NVARCHAR(MAX) = '',
            @ValueList NVARCHAR(MAX) = '',
            @DataType NVARCHAR(128),
            @IsNullable varchar;

    -- Cursor to select column details from the staging table
    DECLARE column_cursor CURSOR FOR 
    SELECT 
        c.COLUMN_NAME, 
        c.DATA_TYPE,
        c.IS_NULLABLE
    FROM 
        INFORMATION_SCHEMA.COLUMNS c
    WHERE 
        c.TABLE_NAME = @StagingTableName 
		AND c.TABLE_SCHEMA = @StagingSchemaName
		and c.COLUMN_NAME <> 'RequestID';

    OPEN column_cursor;

    FETCH NEXT FROM column_cursor INTO @ColumnName, @DataType, @IsNullable;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @ColumnList <> ''
        BEGIN
            SET @ColumnList += ', ';
            SET @ValueList += ', ';
        END

        SET @ColumnList += QUOTENAME(@ColumnName);

        -- Generate the sample value by looking it up in the Meta_Columns table...		
        SET @ValueList += 
			'(select ValidSampleValue from SqlXl.Meta_Columns ' + 
			' where SchemaName = ''' + @DomainSchemaName + ''' ' + 
			' and TableName = ''' + @DomainTableName + ''' ' + 
			' and ColumnName = ''' + @ColumnName + ''' )';
		
        FETCH NEXT FROM column_cursor INTO @ColumnName, @DataType, @IsNullable;
    END

    CLOSE column_cursor;
    DEALLOCATE column_cursor;

    -- Generate the full SQL statement to insert the row
    SET @SQL = 'INSERT INTO ' + QUOTENAME(@StagingSchemaName) + '.' + QUOTENAME(@StagingTableName) 
             + ' (' + @ColumnList + ', RequestID) VALUES (' + @ValueList + ', ''' + @RequestID + ''');';
    RETURN @SQL;
END; --end func
go

CREATE OR ALTER FUNCTION [SqlXl].[ProposeStagingTableNameForInsertFeature] (@SourceTableName NVARCHAR(128))
RETURNS NVARCHAR(128) 
AS
BEGIN
    --Should maybe check for name clashes, or other sophistication???
    RETURN 'Staging_' + @SourceTableName + '_ForInserts';
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[ProposeStagingTableNameForUpdateFeature] (@SourceTableName NVARCHAR(128))
RETURNS NVARCHAR(128) 
AS
BEGIN
    --Should maybe check for name clashes, or other sophistication???
    RETURN 'Staging_' + @SourceTableName + '_ForUpdates';
END;
GO

CREATE OR ALTER FUNCTION SqlXl.GetIdentityColumnName (@SchemaName nvarchar(128), @TableName NVARCHAR(256))
RETURNS NVARCHAR(256)
AS
BEGIN
    DECLARE @IdentityColumnName NVARCHAR(256);

    SELECT @IdentityColumnName = c.name
    FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    WHERE
		t.schema_id = SCHEMA_ID(@SchemaName)
		and t.name = @TableName
		AND c.is_identity = 1;

    RETURN @IdentityColumnName;
END;
GO

--Returns one or more columns for a given PK,UQ,FK constraint name
--....(more than one column in cases of composite keys)...
CREATE OR ALTER FUNCTION SqlXl.GetConstraintColumns (@ConstraintName NVARCHAR(256), @SchemaName NVARCHAR(256))
RETURNS TABLE
AS
RETURN
(
    SELECT 
        c.name AS ColumnName
    FROM 
        sys.tables t
        INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
        INNER JOIN sys.indexes i ON t.object_id = i.object_id
        INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        INNER JOIN sys.key_constraints kc ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id
    WHERE 
        kc.name = @ConstraintName
        AND s.name = @SchemaName
);
GO

CREATE OR ALTER FUNCTION SqlXl.GenerateUniqueConstraintSQL
(
	--Builds a series of Alter Table statements
	--that would create unique key constraints
	--on the staging table if the sql were executed.
    @DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128),
	@StagingTableName nvarchar(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN --begin func 
	DECLARE @SQL NVARCHAR(MAX) = '';

	--List all Unique Key constaints on the given 
	-- DomainTable, but omit any identity-autogenerated ID columns...
	WITH UniqueConstraints AS (
		SELECT
			tc.CONSTRAINT_NAME,
			c.name AS IndexName,
			ColumnNames = STUFF((
				SELECT ', ' + COLUMN_NAME
				FROM INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu2
				WHERE ccu2.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
				ORDER BY COLUMN_NAME
				FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
		FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
		JOIN sys.indexes c ON tc.CONSTRAINT_NAME = c.name
		WHERE 
			tc.TABLE_SCHEMA = @DomainSchemaName
			AND tc.TABLE_NAME = @DomainTableName 
			AND (tc.CONSTRAINT_TYPE = 'UNIQUE' or tc.CONSTRAINT_TYPE = 'PRIMARY KEY')
			AND NOT EXISTS (
				SELECT 1
				FROM 
					sys.columns sc
				JOIN 
					sys.tables st ON sc.object_id = st.object_id
				JOIN 
					sys.schemas ss ON st.schema_id = ss.schema_id
				WHERE 
					st.schema_id = SCHEMA_ID(@DomainSchemaName)
					AND st.name = @DomainTableName 
					AND sc.is_identity = 1 --IS *NOT* an identity-autogenerated column
				)--end not exists
		GROUP BY tc.CONSTRAINT_NAME, c.name
	)--end with
	SELECT @SQL = @SQL + 
		'ALTER TABLE ' + QUOTENAME(@StagingSchemaName) + '.' + QUOTENAME(@StagingTableName) + 
		' ADD CONSTRAINT [UQ_' + @StagingTableName + '_' + IndexName + '] UNIQUE (' + ColumnNames + ');' + CHAR(13) + CHAR(10)
	FROM UniqueConstraints;

	RETURN @SQL;
END --end func
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateSelectStatementToResultInOneValidRow] 
(
    @SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN 
	--Validate params...
	IF not EXISTS (SELECT * FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @SchemaName)
    BEGIN
        RETURN 'Error in func GenerateSelectStatementToResultInOneValidRow: the given SchemaName does not exist.';
    END --end if 

	IF not EXISTS (
		SELECT 1 FROM INFORMATION_SCHEMA.TABLES 
		WHERE 
			TABLE_SCHEMA = @SchemaName 
			and TABLE_NAME = @TableName)
    BEGIN
        RETURN 'Error in func GenerateSelectStatementToResultInOneValidRow: the given TableName does not exist.';
    END --end if 

	declare @PrimaryKeyColumn nvarchar(256) = 
		SqlXl.GetPrimaryKeyColumnName (@SchemaName, @TableName);

    DECLARE @Sql NVARCHAR(MAX) = '';
    DECLARE @ColumnList NVARCHAR(MAX) = '';
    DECLARE @ColumnName NVARCHAR(128);
    DECLARE @DataType NVARCHAR(128);
    DECLARE @DefaultValue NVARCHAR(100);
    
    -- Cursor to loop through all the columns of the target table
    DECLARE ColumnCursor CURSOR FOR 
    SELECT 
        COLUMN_NAME, 
        DATA_TYPE
    FROM INFORMATION_SCHEMA.COLUMNS 
    WHERE TABLE_SCHEMA = @SchemaName 
      AND TABLE_NAME = @TableName
	;--end select 

    OPEN ColumnCursor;
    FETCH NEXT FROM ColumnCursor INTO @ColumnName, @DataType;

    WHILE @@FETCH_STATUS = 0 
    BEGIN
        -- Skip primary key column...
        IF @PrimaryKeyColumn = @ColumnName
        BEGIN
            FETCH NEXT FROM ColumnCursor INTO @ColumnName, @DataType;
            CONTINUE;
        END

        -- Determine default value based on data type
        SET @DefaultValue =
            CASE
                WHEN @DataType IN ('nvarchar', 'varchar', 'nchar', 'char', 'text') THEN ''''''
                WHEN @DataType IN ('int', 'bigint', 'smallint', 'tinyint') THEN '1'
                WHEN @DataType IN ('decimal', 'numeric', 'money', 'smallmoney', 'float', 'real') THEN '1.0'
                WHEN @DataType IN ('bit') THEN '1'
                WHEN @DataType IN ('date', 'datetime', 'datetime2', 'smalldatetime', 'time') THEN '''2023-01-01'''
                ELSE 'NULL'
            END;

        -- Build the SELECT line: pipe encodes "DbColumn|ExcelDisplayName" (same both sides for regular columns).
        SET @ColumnList =
            @ColumnList +
            ', [' + @ColumnName + '|' + @ColumnName + '] = ' + @DefaultValue + CHAR(13) + CHAR(10);
        
        FETCH NEXT FROM ColumnCursor INTO @ColumnName, @DataType;
    END;

    CLOSE ColumnCursor;
    DEALLOCATE ColumnCursor;

    -- Remove the leading comma and newline
    IF LEN(@ColumnList) > 0
        SET @ColumnList = STUFF(@ColumnList, 1, 1, '');

    -- Assemble the final SQL statement
    SET @Sql = 
		'/******' + nchar(10) + 
		'Note: You will likely want to customize the column order' + nchar(10) + 
		' and relevant sample values below, per requirements.' + nchar(10) + 
		' Please note that foreign key columns were **NOT** ' + nchar(10) + 
		'considered when proposing these sample values.' + nchar(10) + 
		'********/' + nchar(10) + 
		'SELECT ' + nchar(10) + @ColumnList;

    RETURN @Sql;
END; 
GO

CREATE OR ALTER FUNCTION SqlXl.RemoveMultiLineComments (@SQLCode NVARCHAR(MAX)) 
RETURNS NVARCHAR(MAX)
AS
BEGIN
    -- Step 1: Remove all multi-line comments /* ... */
    WHILE PATINDEX('%/*%', @SQLCode) > 0 AND PATINDEX('%*/%', @SQLCode) > 0
    BEGIN
        -- Find start and end of the comment
        DECLARE @StartIndex INT = PATINDEX('%/*%', @SQLCode);
        DECLARE @EndIndex INT = PATINDEX('%*/%', @SQLCode) + 2; -- +2 to account for '*/'
        
        -- Remove everything between /* and */
        IF @EndIndex > @StartIndex
        BEGIN
            SET @SQLCode = STUFF(@SQLCode, @StartIndex, @EndIndex - @StartIndex + 1, '');
        END
        ELSE
        BEGIN
            BREAK; -- Avoid infinite loops
        END
    END

    -- Step 2: Trim leading and trailing whitespace
    SET @SQLCode = LTRIM(RTRIM(@SQLCode));

    -- Step 3: Replace multiple spaces, tabs, and newlines with a single space
    SET @SQLCode = REPLACE(@SQLCode, CHAR(13) + CHAR(10), ' '); -- Replace newlines with space
    SET @SQLCode = REPLACE(@SQLCode, CHAR(9), ' '); -- Replace tabs with space
    WHILE CHARINDEX('  ', @SQLCode) > 0
    BEGIN
        SET @SQLCode = REPLACE(@SQLCode, '  ', ' '); -- Remove double spaces
    END

    RETURN @SQLCode;
END 
GO

CREATE OR ALTER FUNCTION [SqlXl].[GetBestDisplayColumn]
(
    @SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
)
RETURNS NVARCHAR(128)
AS
BEGIN
    DECLARE @BestColumn NVARCHAR(128) = NULL;
    DECLARE @PrimaryKeyColumn NVARCHAR(128);
    
    -- Get the primary key column name
    SET @PrimaryKeyColumn = SqlXl.GetPrimaryKeyColumnName(@SchemaName, @TableName);
    
    -- Priority 1: Find first single-column unique constraint (non-PK)
    SELECT TOP 1 @BestColumn = ccu.COLUMN_NAME
    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
    INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu 
        ON tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
    WHERE tc.TABLE_SCHEMA = @SchemaName
      AND tc.TABLE_NAME = @TableName
      AND tc.CONSTRAINT_TYPE = 'UNIQUE'
      AND ccu.COLUMN_NAME != @PrimaryKeyColumn
      AND tc.CONSTRAINT_NAME IN (
          -- Only single-column unique constraints
          SELECT CONSTRAINT_NAME 
          FROM INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE 
          WHERE TABLE_SCHEMA = @SchemaName 
            AND TABLE_NAME = @TableName
          GROUP BY CONSTRAINT_NAME 
          HAVING COUNT(*) = 1
      )
    ORDER BY ccu.COLUMN_NAME;
    
    -- If no unique constraint found, Priority 2: First string column (non-PK)
    IF @BestColumn IS NULL
    BEGIN
        SELECT TOP 1 @BestColumn = COLUMN_NAME
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = @SchemaName
          AND TABLE_NAME = @TableName
          AND DATA_TYPE IN ('varchar', 'nvarchar', 'char', 'nchar', 'text', 'ntext')
          AND COLUMN_NAME != @PrimaryKeyColumn
        ORDER BY ORDINAL_POSITION;
    END
    
    -- Fallback: If still nothing, return PK (shouldn't happen in practice)
    IF @BestColumn IS NULL
        SET @BestColumn = @PrimaryKeyColumn;
    
    RETURN @BestColumn;
END;
Go

CREATE OR ALTER FUNCTION [SqlXl].[GenerateDropdownQuery]
(
    @SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @PkColName NVARCHAR(128);
    DECLARE @BestDisplayColName NVARCHAR(128);
    DECLARE @SQL NVARCHAR(MAX);
    
    -- Get the primary key column name
    SET @PkColName = SqlXl.GetPrimaryKeyColumnName(@SchemaName, @TableName);
    
    -- Get the best display column name  
    SET @BestDisplayColName = SqlXl.GetBestDisplayColumn(@SchemaName, @TableName);
    
    -- Build the dropdown query
    SET @SQL = 'select [Value] = ' + QUOTENAME(@PkColName) +
               ', [Text] = convert(nvarchar(max),' + QUOTENAME(@PkColName) + ') + '' - '' + convert(nvarchar(max),' + QUOTENAME(@BestDisplayColName) + ')' +
               ' from ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName) +
               ' where 1 = 1';--allowing for and...and...filters to be added
    
    RETURN @SQL;
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[ParseSchemaFromReferencedTable]
-- Helper function to parse "Schema.Table" format
(
    @ReferencedTable NVARCHAR(256)  -- Format: "SomeSchema.SomeTableName"
)
RETURNS NVARCHAR(128)
AS
BEGIN
    DECLARE @DotPosition INT = CHARINDEX('.', @ReferencedTable);
    
    IF @DotPosition > 0
	begin
        RETURN LEFT(@ReferencedTable, @DotPosition - 1);
	end 
    ELSE
		begin
        RETURN 'dbo'; 
		end 

	RETURN 'dbo'; 
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[ParseTableFromReferencedTable]
(
    @ReferencedTable NVARCHAR(256)  -- Format: "SomeSchema.SomeTableName"
)
RETURNS NVARCHAR(128)
AS
BEGIN
    DECLARE @DotPosition INT = CHARINDEX('.', @ReferencedTable);
    
    IF @DotPosition > 0
	begin
		RETURN SUBSTRING(@ReferencedTable, @DotPosition + 1, LEN(@ReferencedTable));
	end 
        
    ELSE
	begin
		RETURN @ReferencedTable;
	end 

	return @ReferencedTable;
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[GetBestDisplayColumnFromReferencedTable]
-- Overloaded function that accepts "Schema.Table" format
(
    @ReferencedTable NVARCHAR(256)  -- Format: "SomeSchema.SomeTableName"
)
RETURNS NVARCHAR(128)
AS
BEGIN
    DECLARE @SchemaName NVARCHAR(128) = SqlXl.ParseSchemaFromReferencedTable(@ReferencedTable);
    DECLARE @TableName NVARCHAR(128) = SqlXl.ParseTableFromReferencedTable(@ReferencedTable);
    
    RETURN SqlXl.GetBestDisplayColumn(@SchemaName, @TableName);
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateDropdownQueryFromReferencedTable]
-- Overloaded dropdown query generator
(
    @ReferencedTable NVARCHAR(256)  -- Format: "SomeSchema.SomeTableName"
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @SchemaName NVARCHAR(128) = SqlXl.ParseSchemaFromReferencedTable(@ReferencedTable);
    DECLARE @TableName NVARCHAR(128) = SqlXl.ParseTableFromReferencedTable(@ReferencedTable);
    
    RETURN SqlXl.GenerateDropdownQuery(@SchemaName, @TableName);
END;
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateDisplayViewSQL]
  (
      @SchemaName NVARCHAR(128),
      @TableName NVARCHAR(128)
  )
  RETURNS NVARCHAR(MAX)
  AS
  BEGIN
      DECLARE @SQL NVARCHAR(MAX) = '';
      DECLARE @ViewName NVARCHAR(128) = 'vw_' + @TableName;
      DECLARE @FullTableName NVARCHAR(256) = QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName);
      DECLARE @SelectClause NVARCHAR(MAX) = '';
      DECLARE @JoinClause NVARCHAR(MAX) = '';
      DECLARE @FKColumnsAtEnd NVARCHAR(MAX) = '';
      DECLARE @HasFKs BIT = 0;

      -- Check if table has any FKs using INFORMATION_SCHEMA
      IF EXISTS (
          SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk
              ON rc.constraint_name = fk.constraint_name
          WHERE fk.table_schema = @SchemaName AND fk.table_name = @TableName
      )
      BEGIN
          SET @HasFKs = 1;
      END

      -- If no FKs, return simple select statement
      IF @HasFKs = 0
      BEGIN
          RETURN '-- No foreign keys found. Use direct table query: select * from ' + @FullTableName;
      END

      -- Variables for cursor
      DECLARE @ColumnName NVARCHAR(128);
      DECLARE @OrdinalPosition INT;
      DECLARE @IsNullable VARCHAR(3);
      DECLARE @IsFK BIT;
      DECLARE @PKTable NVARCHAR(256);
      DECLARE @PKColumn NVARCHAR(128);
      DECLARE @ReferencedTableName NVARCHAR(128);
      DECLARE @BestDisplayColumn NVARCHAR(128);

      -- Cursor to process all columns in ordinal order
      DECLARE column_cursor CURSOR FOR
      SELECT
          col.column_name,
          col.ordinal_position,
          col.is_nullable,
          CASE WHEN fk_data.fk_column IS NOT NULL THEN 1 ELSE 0 END as is_fk,
          fk_data.pk_table,
          fk_data.pk_column,
          fk_data.referenced_table_name
      FROM INFORMATION_SCHEMA.COLUMNS col
      LEFT JOIN (
          SELECT
              fk_cols.column_name as fk_column,
              pk.table_schema + '.' + pk.table_name as pk_table,
              pk_cols.column_name as pk_column,
              pk.table_name as referenced_table_name
          FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk
              ON rc.constraint_name = fk.constraint_name
          INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk
              ON rc.unique_constraint_name = pk.constraint_name
          INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE fk_cols
              ON rc.constraint_name = fk_cols.constraint_name
          INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pk_cols
              ON rc.unique_constraint_name = pk_cols.constraint_name
              AND fk_cols.ordinal_position = pk_cols.ordinal_position
          WHERE fk.table_schema = @SchemaName AND fk.table_name = @TableName
      ) fk_data ON col.column_name = fk_data.fk_column
      WHERE col.table_schema = @SchemaName AND col.table_name = @TableName
      ORDER BY col.ordinal_position;

      OPEN column_cursor;
      FETCH NEXT FROM column_cursor INTO @ColumnName, @OrdinalPosition, @IsNullable, @IsFK, @PKTable, @PKColumn,
  @ReferencedTableName;

      WHILE @@FETCH_STATUS = 0
      BEGIN
          IF @IsFK = 1
          BEGIN
              -- Use GetBestDisplayColumn function to ensure consistency with dropdown logic
              DECLARE @PKTableSchema NVARCHAR(128);
              DECLARE @PKTableName NVARCHAR(128);

              SET @PKTableSchema = SqlXl.ParseSchemaFromReferencedTable(@PKTable);
              SET @PKTableName = SqlXl.ParseTableFromReferencedTable(@PKTable);

              SET @BestDisplayColumn = SqlXl.GetBestDisplayColumn(@PKTableSchema, @PKTableName);

              -- Add FK display column with clean alias: ReferencedTable_BestDisplayColumn
              SET @SelectClause = @SelectClause +
                  CASE WHEN @SelectClause = '' THEN '' ELSE ',' + CHAR(13) + CHAR(10) + '    ' END +
                  QUOTENAME(@ReferencedTableName + '_' + @BestDisplayColumn) + ' = ' + @PKTable + '.' +
  QUOTENAME(@BestDisplayColumn);

              -- Add FK raw key column at end, clean alias
              SET @FKColumnsAtEnd = @FKColumnsAtEnd +
                  CASE WHEN @FKColumnsAtEnd = '' THEN '' ELSE ',' + CHAR(13) + CHAR(10) + '    ' END +
                  QUOTENAME(@ColumnName) + ' = ' + @FullTableName + '.' + QUOTENAME(@ColumnName);

              -- Add JOIN clause
              SET @JoinClause = @JoinClause + CHAR(13) + CHAR(10) +
                  CASE WHEN @IsNullable = 'NO' THEN 'inner join ' ELSE 'left join ' END +
                  @PKTable + ' on ' + @FullTableName + '.' + QUOTENAME(@ColumnName) + ' = ' +
                  @PKTable + '.' + QUOTENAME(@PKColumn);
          END
          ELSE
          BEGIN
              -- Regular column: clean alias, no pipe syntax
              SET @SelectClause = @SelectClause +
                  CASE WHEN @SelectClause = '' THEN '' ELSE ',' + CHAR(13) + CHAR(10) + '    ' END +
                  QUOTENAME(@ColumnName) + ' = ' + @FullTableName + '.' + QUOTENAME(@ColumnName);
          END

          FETCH NEXT FROM column_cursor INTO @ColumnName, @OrdinalPosition, @IsNullable, @IsFK, @PKTable, @PKColumn,
   @ReferencedTableName;
      END

      CLOSE column_cursor;
      DEALLOCATE column_cursor;

      -- Add FK columns at the end
      IF @FKColumnsAtEnd != ''
      BEGIN
          SET @SelectClause = @SelectClause + ',' + CHAR(13) + CHAR(10) + '    ' + @FKColumnsAtEnd;
      END

      -- Build final CREATE VIEW statement
      SET @SQL = 'CREATE VIEW ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@ViewName) + ' AS' +
                 CHAR(13) + CHAR(10) + 'select' +
                 CHAR(13) + CHAR(10) + '    ' + @SelectClause +
                 CHAR(13) + CHAR(10) + 'from ' + @FullTableName +
                 @JoinClause;

      RETURN @SQL;
  END;
  go 
--*******end functions

/******************************
Table(s)...**/
IF OBJECT_ID('[SqlXl].[Meta_Columns]', 'U') IS NULL
BEGIN
CREATE TABLE [SqlXl].[Meta_Columns](
	/*Intended to hold all meta data needed 
	by BulkOpsHelper for a column in a domain table.*/

	ID INT IDENTITY(1,1) PRIMARY KEY,

	--Combination unique key columns...
	[SchemaName] [nvarchar](128) not NULL,
	[TableName] [nvarchar](128) not NULL,
	[ColumnName] [nvarchar](128) not NULL,

	--Note: Rest of columns here are 
	-- nullable so that record
	--can be created with only the 
	--minimum, key columns, and then updated 
	--with these other values AFTER
	--the initial insert of the record...
	[SqlDataType] [nvarchar](128) NULL,
	[IsNullable] [varchar](3) NULL,
	[MaxLengthForString] [int] NULL,
	[IsPrimaryKey] [varchar](3) NULL,
	[IsForeignKey] [varchar](3) NULL,
	[ReferencedTable] [nvarchar](128) NULL,
	[ReferencedColumn] [nvarchar](128) NULL,
	[ValidValueSelectStatement] [nvarchar](max) NULL,
	[InvalidValueSelectStatement] [nvarchar](max) NULL,
	[ValidSampleValue] [nvarchar](max) NULL,
	[InvalidSampleValue] [nvarchar](max) NULL,

	/*Combination of SchemaName, TableName, ColumnName
		MUST be unique...*/
	CONSTRAINT UQ_Columns_Schema_Tbl_Column 
		UNIQUE (SchemaName, TableName, ColumnName),

	-- CHECK constraint to ensure that the specified column exists within the specified table and schema
	CONSTRAINT chk_ColumnExists_Meta_Columns 
		CHECK ([SqlXl].ColumnExists(SchemaName, TableName, ColumnName) = 1)

	--Note: consider adding more check constraints on remaining columns, for further validation??
); --end create table
END
GO

IF OBJECT_ID('[SqlXl].[BulkOpFeatures]', 'U') IS NULL
BEGIN
CREATE TABLE [SqlXl].[BulkOpFeatures](
	[ID] [int] IDENTITY(1,1) NOT NULL,
	[UserFriendlyFeatureName] [nvarchar](255) NOT NULL,
	[InsertUpdateDeleteOrCustom] [nvarchar](6) NOT NULL,
	DomainSchemaName nvarchar(128) not null,
	DomainTableName nvarchar(128) not null,
	StagingSchemaName nvarchar(128) not null,
	StagingTableName [nvarchar](128) NOT NULL,
	GetRowsToChooseFrom_SelectStatement nvarchar(max) null,
	GetRowsToEdit_SelectStatement nvarchar(max) null,
	[SprocToProcessPerfectStagedData] [NVARCHAR](128) NOT NULL,
	[MenuDisplayRanking] [INT] NOT NULL,

	/*Next 12 cols are flags for whether or not the 
	given feature needs special request context vars
	in order to function. For example, if the 
	logged on user's ID is needed from the web application
	to process a data submition, then dev would manually 
	set RequiresCtxVar001 to 1 and also configure
	the necessary C# code to collect that var
	and submit it along with rest of submitted data...*/
	--RequiresCtxVar001 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar002 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar003 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar004 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar005 BIT NOT NULL DEFAULT 0,
	--RequiresCtxVar006 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar007 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar008 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar009 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar010 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar011 BIT NOT NULL DEFAULT 0,
 --   RequiresCtxVar012 BIT NOT NULL DEFAULT 0,

	--For InsertUpdateDeleteOrCustom column, allow these values only...
	CONSTRAINT chk_InsertUpdateDeleteOrCustom
		CHECK (InsertUpdateDeleteOrCustom IN ('Insert', 'Update', 'Delete', 'Custom')),

	-- For other columns, check that the specified db objects exist...
	CONSTRAINT chk_TableExists 
		CHECK ([SqlXl].TableExists(DomainSchemaName, DomainTableName) = 1),

	CONSTRAINT chk_TableExists002 
		CHECK ([SqlXl].TableExists(StagingSchemaName, StagingTableName) = 1),

	CONSTRAINT chk_SprocExists 
		CHECK ([SqlXl].SprocExists(DomainSchemaName, SprocToProcessPerfectStagedData) = 1)
) ON [PRIMARY]
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_BulkOpFeatures')
BEGIN
    ALTER TABLE [SqlXl].[BulkOpFeatures] ADD  CONSTRAINT [PK_BulkOpFeatures] PRIMARY KEY CLUSTERED
    (
        [ID] ASC
    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UK_BulkOpFeatures')
BEGIN
    ALTER TABLE [SqlXl].[BulkOpFeatures] ADD  CONSTRAINT [UK_BulkOpFeatures] UNIQUE NONCLUSTERED
    (
        [UserFriendlyFeatureName] ASC
    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DEFAULT_BulkOpFeatures_MenuDisplayRanking')
BEGIN
    ALTER TABLE [SqlXl].[BulkOpFeatures] ADD  CONSTRAINT [DEFAULT_BulkOpFeatures_MenuDisplayRanking]  DEFAULT ((0)) FOR [MenuDisplayRanking]
END
GO
--end BulkOpFeatures table def*********************

-- Create RequestContext table to store SPECIAL runtime context variables
-- ...(One-off vars, like logged-on userID, role, etc
--....NOT intended for common vars from QueryString or HtmlForm)
IF OBJECT_ID('[SqlXl].[RequestContext]', 'U') IS NULL
BEGIN
CREATE TABLE [SqlXl].[RequestContext] (
    RequestID NVARCHAR(36) NOT NULL PRIMARY KEY,
    CtxVar001 NVARCHAR(MAX) NULL,
    CtxVar002 NVARCHAR(MAX) NULL,
    CtxVar003 NVARCHAR(MAX) NULL,
    CtxVar004 NVARCHAR(MAX) NULL,
    CtxVar005 NVARCHAR(MAX) NULL,
    CtxVar006 NVARCHAR(MAX) NULL,
    CtxVar007 NVARCHAR(MAX) NULL,
    CtxVar008 NVARCHAR(MAX) NULL,
    CtxVar009 NVARCHAR(MAX) NULL,
    CtxVar010 NVARCHAR(MAX) NULL,
    CtxVar011 NVARCHAR(MAX) NULL,
    CtxVar012 NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);--end create table 
END
go 

IF OBJECT_ID('[SqlXl].[ColumnUIConfigurations]', 'U') IS NULL
BEGIN
CREATE TABLE [SqlXl].[ColumnUIConfigurations](
    [ID] [int] IDENTITY(1,1) NOT NULL,
    [SchemaName] [nvarchar](128) NOT NULL,
    [TableName] [nvarchar](128) NOT NULL,  
    [ColumnName] [nvarchar](128) NOT NULL,
    [DropdownSelectStatement] [nvarchar](max) NULL,
    [UIHint] [nvarchar](50) NULL, --text, select, select2_client, select2_server
 CONSTRAINT [PK_ColumnUIConfigurations] PRIMARY KEY CLUSTERED 
(
    [ID] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [UK_ColumnUIConfigurations] UNIQUE NONCLUSTERED 
(
    [SchemaName] ASC,
    [TableName] ASC,
    [ColumnName] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
END
GO

CREATE OR ALTER FUNCTION [SqlXl].[GenerateVarDeclarations]
(	/*Note: this func depends on table BulkOpFeatures, so it's AFTER it...*/
    @BulkOpFeaturesID INT
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @Result NVARCHAR(MAX) = '';
    DECLARE @Temp NVARCHAR(MAX);

    -- Fetch the row for the given ID
    SELECT 
        @Temp =             
            'DECLARE @InsertUpdateDeleteOrCustom NVARCHAR(6) = ''' + REPLACE(InsertUpdateDeleteOrCustom, '''', '''''') + ''';' + CHAR(13) + CHAR(10) +
            'DECLARE @DomainSchemaName NVARCHAR(128) = ''' + REPLACE(DomainSchemaName, '''', '''''') + ''';' + CHAR(13) + CHAR(10) +
            'DECLARE @DomainTableName NVARCHAR(128) = ''' + REPLACE(DomainTableName, '''', '''''') + ''';' + CHAR(13) + CHAR(10) +
            'DECLARE @StagingSchemaName NVARCHAR(128) = ''' + REPLACE(StagingSchemaName, '''', '''''') + ''';' + CHAR(13) + CHAR(10) +
            'DECLARE @StagingTableName NVARCHAR(128) = ''' + REPLACE(StagingTableName, '''', '''''') + ''';' + CHAR(13) + CHAR(10) +
            'DECLARE @SprocToProcessPerfectStagedData NVARCHAR(128) = ''' + REPLACE(SprocToProcessPerfectStagedData, '''', '''''') + ''';' + CHAR(13) + CHAR(10)
    FROM [SqlXl].[BulkOpFeatures]
    WHERE [ID] = @BulkOpFeaturesID;

    -- Assign the generated declarations to the result
    SET @Result = ISNULL(@Temp, '-- No record found for the given ID');

    RETURN @Result;
END;--end func 
GO

IF OBJECT_ID('[SqlXl].[SavedQueries]', 'U') IS NULL
BEGIN
CREATE TABLE [SqlXl].SavedQueries (
    ID INT IDENTITY(1,1) PRIMARY KEY,
    SavedQueryName NVARCHAR(255) NOT NULL,
    SavedQueryText NVARCHAR(MAX) NOT NULL,
    CreatedOnDate DATETIME2 DEFAULT SYSDATETIME(),
    LastModifiedOnDate DATETIME2 DEFAULT SYSDATETIME()
);
END
go 

--For debugging support...
IF OBJECT_ID('[SqlXl].[DebugLog]', 'U') IS NULL
BEGIN
CREATE TABLE SqlXl.DebugLog (
    LogID INT IDENTITY(1,1) PRIMARY KEY,
	LogTime datetime2 not null default sysutcdatetime(),
	RequestID nvarchar(36) null,
	InputParameters nvarchar(max) null,
	LogInfo nvarchar(max) not null
);
END
go 
--***********end Tables 

/******************************
View(s)...**/
--...(none)
--*******end Views

/******************************
Stored Procedure(s)...**/

--For debugging support...
CREATE OR ALTER PROCEDURE SqlXl.DebugLogInsert
    @RequestID NVARCHAR(36) = NULL,
    @InputParameters NVARCHAR(MAX) = NULL,
    @LogInfo NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON; -- Prevent extra result sets from being returned

    -- Insert a new row into the DebugLog table
    INSERT INTO SqlXl.DebugLog (
        RequestID,
        InputParameters,
        LogInfo
    )
    VALUES (
        @RequestID,
        @InputParameters,
        @LogInfo
    );
END;--end sproc 
GO

--region ErrorIf...validations
CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfSchemaDoesNotExist]
	@SchemaName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

	IF not EXISTS (SELECT * FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = @SchemaName)
    BEGIN
        RAISERROR('SchemaName does not exist.', 16, 1);
        RETURN -1;
    END
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfTableDoesNotExist]
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

	--Bypass check for this special temp table...
	if @TableName = '#ZZTemp'
	begin
		return 0;
	end 
	
    IF not EXISTS (
		SELECT 1 FROM INFORMATION_SCHEMA.TABLES 
		WHERE 
			TABLE_SCHEMA = @SchemaName 
			and TABLE_NAME = @TableName)
    BEGIN
        RAISERROR('Table does not exist.', 16, 1);
		--DECLARE @ErrorMessage NVARCHAR(500);
		--SET @ErrorMessage = 'Table [' + @SchemaName + '].[' + @TableName + '] does not exist.';
		--RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfColumnDoesNotExist]
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
	@ColumnName nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

	--Must check for existence of column for temp table DIFFERENTLY...
	IF @TableName = '#ZZTemp' and OBJECT_ID('tempdb..#ZZTemp') IS NOT NULL
	BEGIN
		-- Check for the column in a temporary table
		IF NOT EXISTS (
			SELECT 1
			FROM tempdb.sys.columns c
			INNER JOIN tempdb.sys.objects o
				ON c.object_id = o.object_id
			WHERE o.name LIKE '#ZZTemp%' -- Handles internal naming of temp tables
			  AND c.name = @ColumnName
		)
		BEGIN
			RAISERROR('The specified column does not exist in the temp table #ZZTemp.', 16, 1);
			RETURN -1;
		END
	END
	ELSE
	BEGIN
		-- Check for the column in a standard table
		IF NOT EXISTS (
			SELECT 1
			FROM INFORMATION_SCHEMA.COLUMNS
			WHERE TABLE_SCHEMA = @SchemaName
			  AND TABLE_NAME = @TableName
			  AND COLUMN_NAME = @ColumnName
		)
		BEGIN
			RAISERROR('The specified column does not exist in the given schema and table.', 16, 1);
			RETURN -1;
		END
	END

END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfSprocDoesNotExist]
    @SprocName NVARCHAR(128),
	@SchemaName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;
	IF not EXISTS (SELECT * FROM INFORMATION_SCHEMA.ROUTINES 
			   WHERE ROUTINE_TYPE = 'PROCEDURE' 
			   AND ROUTINE_SCHEMA = @SchemaName 
			   AND ROUTINE_NAME = @SprocName)
	BEGIN
		RAISERROR('Given SprocName does not exist in given SchemaName.', 16, 1);
        RETURN -1;
	END
end;--end sproc 
go 

CREATE OR ALTER PROCEDURE SqlXl.ErrorIfNoIntegerPrimaryKey
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
AS
BEGIN
	-- Validate that table exists
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;

    DECLARE @PrimaryKeyColumn NVARCHAR(128);
    DECLARE @PrimaryKeyDataType NVARCHAR(128);
    DECLARE @ErrorMessage NVARCHAR(256);
        
    -- Get the primary key column and data type
    SELECT @PrimaryKeyColumn = c.COLUMN_NAME,
           @PrimaryKeyDataType = c.DATA_TYPE
    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk
    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
        ON pk.TABLE_NAME = kcu.TABLE_NAME
        AND pk.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
    JOIN INFORMATION_SCHEMA.COLUMNS c
        ON c.TABLE_NAME = kcu.TABLE_NAME
        AND c.COLUMN_NAME = kcu.COLUMN_NAME
    WHERE pk.TABLE_SCHEMA = @SchemaName 
		AND	pk.TABLE_NAME = @TableName
		AND pk.CONSTRAINT_TYPE = 'PRIMARY KEY';

    -- Check if primary key is single column and integer
    IF @PrimaryKeyColumn IS NULL
    BEGIN
        SET @ErrorMessage = 'Table ' + @TableName + ' does not have a primary key.';
        RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END

    IF @PrimaryKeyDataType NOT IN ('int', 'bigint', 'smallint', 'tinyint')
    BEGIN
        SET @ErrorMessage = 'Table ' + @TableName + ' primary key is not an integer type.';
        RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END

    -- Check if the primary key is a single column
    IF (SELECT COUNT(*)
        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
        WHERE TABLE_NAME = @TableName
          AND CONSTRAINT_NAME = (SELECT CONSTRAINT_NAME
                                 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                                 WHERE 
									TABLE_SCHEMA = @SchemaName
									AND TABLE_NAME = @TableName
                                   AND CONSTRAINT_TYPE = 'PRIMARY KEY')) > 1
    BEGIN
        SET @ErrorMessage = 'Table ' + @TableName + ' primary key is not a single column.';
        RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END

    --PRINT 'Table ' + @TableName + ' has a single-column integer primary key.';
END --end sproc 
GO

CREATE OR ALTER PROCEDURE SqlXl.ErrorIfNoRequestIDColumn
	@SchemaName nvarchar(128),
	@TableName nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;

	DECLARE @ErrorMessage NVARCHAR(256);

	IF NOT EXISTS (SELECT 1
                       FROM INFORMATION_SCHEMA.COLUMNS
                       WHERE TABLE_NAME = @TableName
                         AND TABLE_SCHEMA = @SchemaName
                         AND COLUMN_NAME = 'RequestID')
    BEGIN
        SET @ErrorMessage = 'RequestID column does not exist in staging table: ' + @SchemaName + '.' + @TableName + '.';
        RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END

    --PRINT 'RequestID column was found in staging table: ' + @SchemaName + '.' + @TableName + '.';
END --end sproc 
GO 

CREATE OR ALTER PROCEDURE SqlXl.ErrorIfColumnMismatch
	@DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128),
	@StagingTableName [nvarchar](128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ErrorMessage NVARCHAR(256);

    -- Check for column mismatches
    DECLARE @ColumnName NVARCHAR(128);

    DECLARE column_cursor CURSOR FOR
    SELECT COLUMN_NAME
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = @StagingTableName
      AND TABLE_SCHEMA = @StagingSchemaName
	  and COLUMN_NAME != 'RequestID'; --RequestID is expected in ALL staging tables

    OPEN column_cursor;
    FETCH NEXT FROM column_cursor INTO @ColumnName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1
                       FROM INFORMATION_SCHEMA.COLUMNS
                       WHERE TABLE_NAME = @DomainTableName
                         AND TABLE_SCHEMA = @DomainSchemaName
                         AND COLUMN_NAME = @ColumnName)
        BEGIN
            SET @ErrorMessage = 'Column ' + @ColumnName + ' exists in staging table ' + @StagingTableName + ' but does not exist in production/domain table ' + @DomainTableName + '.';
            RAISERROR(@ErrorMessage, 16, 1);
            CLOSE column_cursor;
            DEALLOCATE column_cursor;
            RETURN -1;
        END

        FETCH NEXT FROM column_cursor INTO @ColumnName;
    END

    CLOSE column_cursor;
    DEALLOCATE column_cursor;

    --PRINT 'No column mismatches found between ' + @DomainTableName + ' and ' + @StagingTableName + '.';
END --end sproc 
GO 

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfInvalidGuid]
    @RequestID NVARCHAR(36)
AS
BEGIN
    SET NOCOUNT ON;
	IF TRY_CAST(@RequestID AS UNIQUEIDENTIFIER) IS NULL
    BEGIN
        RAISERROR ('Invalid requestId format', 16, 1);
        RETURN -1;
    END
end;--end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorIfNoUniqueDisplayColumn]
    @SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- Validate params...
    EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
    EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;
    EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @SchemaName, @TableName;

    -- Check if table has at least one unique constraint on a non-primary-key column
    DECLARE @HasUniqueDisplayColumn BIT = 0;
    DECLARE @PrimaryKeyColumn NVARCHAR(128);

    -- Get the primary key column name
    SET @PrimaryKeyColumn = SqlXl.GetPrimaryKeyColumnName(@SchemaName, @TableName);

    -- Check for unique constraints on non-PK columns
    IF EXISTS (
        SELECT 1
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu 
            ON tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
        WHERE tc.TABLE_SCHEMA = @SchemaName
          AND tc.TABLE_NAME = @TableName
          AND tc.CONSTRAINT_TYPE = 'UNIQUE'
          AND ccu.COLUMN_NAME != @PrimaryKeyColumn
    )
    BEGIN
        SET @HasUniqueDisplayColumn = 1;
    END

    -- Error if no unique display column found
    IF @HasUniqueDisplayColumn = 0
    BEGIN
        DECLARE @ErrorMessage NVARCHAR(500);
        SET @ErrorMessage = 'Table [' + @SchemaName + '].[' + @TableName + '] must have at least one UNIQUE constraint on a non-primary-key column to serve as a display value for foreign key lookups.';
        RAISERROR(@ErrorMessage, 16, 1);
        RETURN -1;
    END

    -- Success - table has appropriate unique display column(s)
    RETURN 0;
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorOnAnyBadFeatureParameter]
(
    @BulkOpFeatureID INT
)
AS
BEGIN 

	DECLARE @UserFriendlyFeatureName NVARCHAR(255);
	DECLARE @DomainSchemaName NVARCHAR(128);
	DECLARE @DomainTableName NVARCHAR(128);
	DECLARE @StagingSchemaName NVARCHAR(128);
	DECLARE @StagingTableName NVARCHAR(128);
	DECLARE @SprocToProcessPerfectStagedData NVARCHAR(128);
	declare @InsertUpdateDeleteOrCustom nvarchar(6);
	DECLARE @MenuDisplayRanking INT;

	--Select the feature values into variables...
	SELECT 
		@UserFriendlyFeatureName = [UserFriendlyFeatureName],
		@DomainSchemaName = DomainSchemaName,
        @DomainTableName = DomainTableName,
        @StagingSchemaName = StagingSchemaName,
        @StagingTableName = StagingTableName,
		@SprocToProcessPerfectStagedData = [SprocToProcessPerfectStagedData],
		@InsertUpdateDeleteOrCustom = InsertUpdateDeleteOrCustom,
		@MenuDisplayRanking = [MenuDisplayRanking]
	FROM [SqlXl].[BulkOpFeatures]
	where ID = @BulkOpFeatureID;

	--Ensure that this is Insert, Update, Delete or Custom...
	if @InsertUpdateDeleteOrCustom not in ('Insert','Update','Delete','Custom')
	begin
		RAISERROR('BulkOpFeatures.InsertUpdateDeleteOrCustom value must be ''Insert'', ''Update'', ''Delete'' or ''Custom''.', 16, 1);
        RETURN -1;
	end 

	--Sanity-check the values provided in the feature...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfSprocDoesNotExist @SprocToProcessPerfectStagedData, @DomainSchemaName;
	EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @DomainSchemaName, @DomainTableName;

	--Sanity-check staging table param too, if it's NOT a delete feature...
	IF @StagingTableName <> 'NotApplicableForBulkDelete'
	BEGIN
		EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
		exec SqlXl.ErrorIfNoRequestIDColumn @StagingSchemaName, @StagingTableName;
		EXEC SqlXl.ErrorIfColumnMismatch @DomainSchemaName, @DomainTableName, @StagingSchemaName, @StagingTableName;	
	END 
end; --end sproc 
go

CREATE OR ALTER PROCEDURE [SqlXl].[ErrorSelectedIdsNotAsExpected]
	@SelectedIds NVARCHAR(MAX) -- "1,2,3,4"
AS
BEGIN
    SET NOCOUNT ON;

	-- Validate @SelectedIds contains only numbers and commas
	IF @SelectedIds IS NULL OR @SelectedIds = '' OR @SelectedIds NOT LIKE '%[0-9]%'
	BEGIN
		RAISERROR('SelectedIds parameter is required and must contain numeric values.', 16, 1);
		RETURN;
	END

	-- Check for SQL injection patterns
	IF @SelectedIds LIKE '%[^0-9,]%'
	BEGIN
		RAISERROR('SelectedIds parameter contains invalid characters. Only numbers and commas allowed.', 16, 1);
		RETURN;
	END

	-- Prevent empty values like "1,,3"
	IF @SelectedIds LIKE '%,,%' OR @SelectedIds LIKE ',%' OR @SelectedIds LIKE '%,'
	BEGIN
		RAISERROR('SelectedIds parameter contains empty values or invalid format.', 16, 1);
		RETURN;
	END
END;--end sproc 
GO
--endregion

CREATE OR ALTER PROCEDURE [SqlXl].[PurgeStagingForRequestID]
    @StagingTableName NVARCHAR(128),
	@RequestID nvarchar(36)
AS
BEGIN
    SET NOCOUNT ON;

	--Validate params...
	exec SqlXl.ErrorIfTableDoesNotExist 'SqlXl', @StagingTableName;
	exec SqlXl.ErrorIfInvalidGuid @RequestID;

    DECLARE @SQLCommand NVARCHAR(MAX);
    SET @SQLCommand = 
		'delete from SqlXl.' + @StagingTableName + 
		' where RequestID = ''' + @RequestID + ''' ;';
    EXEC sp_executesql @SQLCommand;
END; --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ListUniqueKeyConstraintsForTable]
(
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
	@IncludePrimaryKeyColumnInResults bit = 0,
	@IncludeIdentityAutoNumberColumnInResults bit = 0,
	@IncludeMultiColumnUniqueKeyConstraints bit = 0
)
AS
BEGIN
	SET NOCOUNT ON;

	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;

	-- Declare a table variable to store constraint information
	DECLARE @ConstraintDetails TABLE (
		CONSTRAINT_NAME NVARCHAR(255),
		CONSTRAINT_TYPE NVARCHAR(50),
		ColumnNames NVARCHAR(MAX)
	);

	--Query INFORMATION_SCHEMA.TABLE_CONSTRAINTS
	--to populate the table variable...
	INSERT INTO @ConstraintDetails (CONSTRAINT_NAME, CONSTRAINT_TYPE, ColumnNames)
	SELECT
		tc.CONSTRAINT_NAME,
		tc.CONSTRAINT_TYPE,
		ColumnNames = STUFF((
			SELECT ', ' + COLUMN_NAME
			FROM INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu2
			WHERE ccu2.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
			ORDER BY COLUMN_NAME
			FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
	FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
	WHERE 
		tc.TABLE_SCHEMA = @SchemaName
		AND tc.TABLE_NAME = @TableName 
		AND (tc.CONSTRAINT_TYPE = 'UNIQUE' or tc.CONSTRAINT_TYPE = 'PRIMARY KEY');

	--If not including autonumber/identity column...
	if @IncludeIdentityAutoNumberColumnInResults = 0
	begin
		--Remove any identity columns from constraints list...
		DELETE FROM @ConstraintDetails 
		WHERE [@ConstraintDetails].CONSTRAINT_TYPE = 'PRIMARY KEY'
		AND [@ConstraintDetails].ColumnNames IN  
			(SELECT 
				sc.name AS IdentityColumnName
			FROM 
				sys.columns sc
			JOIN 
				sys.tables st ON sc.object_id = st.object_id
			JOIN 
				sys.schemas ss ON st.schema_id = ss.schema_id
			WHERE 
				ss.name = @SchemaName
				AND st.name = @TableName 
				AND sc.is_identity = 1
			);--end delete 
	end --end if 

	--If NOT including primary key column...
	if @IncludePrimaryKeyColumnInResults = 0
	begin
		DELETE FROM @ConstraintDetails 
		WHERE [@ConstraintDetails].CONSTRAINT_TYPE = 'PRIMARY KEY'; 
	end --end if 

	--If not including multi-column UQ constraints...
	if @IncludeMultiColumnUniqueKeyConstraints = 0
	begin
		delete from @ConstraintDetails
		WHERE ColumnNames LIKE '%,%';
	end --end if 

	SELECT ColumnNames
	FROM @ConstraintDetails;
end --end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[ValidateZZTemp_For_UPDATE_FEATURE_ForUniqueConstraintsReturnErrors]
(
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
    @StagingTableName NVARCHAR(128),
	@StopAfterThisManyErrors INT = 10,  -- Default value 
	@RequestID nvarchar(36)
)
AS
BEGIN
	SET NOCOUNT ON;

	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
	exec SqlXl.ErrorIfInvalidGuid @RequestID;
	
	declare @SQL nvarchar(max);
	declare @PrimaryKeyColumnName nvarchar(128);

	--Update logic needs the primary key column for domain table...
	set @PrimaryKeyColumnName = 
		SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName, @DomainTableName);

	-- Check for duplicates within zztemp of domain table's pk column...
	set @SQL = 
		SqlXl.SqlToListAllRowsFromOnlyWithinZZTempThatDuplicate_A_ValueForColumn(
			@PrimaryKeyColumnName);
	INSERT #Messages(Msg)
	EXEC sp_executesql @SQL; 

	--Stop and return if error max is reached...
	if (select count(*) from #Messages) >= @StopAfterThisManyErrors
	begin
		EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;
		
		RETURN 0;
	end --end if 
	
	-- Declare a table variable to store EACH 
	-- separate unique key constraint 
	-- ColumnNames value will one or many comma-delimited column name(s)...
	DECLARE @ConstraintDetails TABLE (
		ColumnNames NVARCHAR(MAX)
	);
	insert @ConstraintDetails (ColumnNames)
	exec [SqlXl].[ListUniqueKeyConstraintsForTable]
		@SchemaName = @DomainSchemaName,
		@TableName = @DomainTableName,
		@IncludePrimaryKeyColumnInResults = 0,
		@IncludeIdentityAutoNumberColumnInResults = 0,
		@IncludeMultiColumnUniqueKeyConstraints = 1
	;--end insert-sproc-results

	--Variable to hold one-to-many comma-delimited
	--column names for unique constraints...
	DECLARE @ColumnNames NVARCHAR(MAX);	
	
	DECLARE ConstraintCursor CURSOR FOR
	SELECT ColumnNames
	FROM @ConstraintDetails;

	OPEN ConstraintCursor;
	FETCH NEXT FROM ConstraintCursor INTO @ColumnNames;
	WHILE @@FETCH_STATUS = 0
	BEGIN
		--PRINT @ColumnNames;

		--Generate sql that makes an error listing for all uniqueness violations...
		set @SQL =
				SqlXl.GenerateDuplicateCheckSQL
					(@DomainSchemaName, @DomainTableName, @ColumnNames, @PrimaryKeyColumnName);
		
		-- Execute the dynamic SQL
		INSERT #Messages(Msg)
		EXEC sp_executesql @SQL; 
		
		--Stop and return if error max is reached...
		if (select count(*) from #Messages) >= @StopAfterThisManyErrors
		begin
			EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;
			
			-- Exit from both cursors cleanly
			CLOSE ConstraintCursor;
			DEALLOCATE ConstraintCursor;
			
			RETURN 0;
		end --end if 
		
		FETCH NEXT FROM ConstraintCursor INTO @ColumnNames;
	END --end cursor loop

	CLOSE ConstraintCursor;
	DEALLOCATE ConstraintCursor;

	--Clean staging table...
	EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;

	RETURN 0;
end --end sproc 
go 


CREATE OR ALTER PROCEDURE [SqlXl].[CreateUniqueKeyConstraint]
(
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
    @ColumnName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @SchemaName, @TableName, @ColumnName;

    DECLARE @SQL NVARCHAR(MAX)
	SET @SQL = N'ALTER TABLE ' + @SchemaName + '.' + @TableName + 
               N' ADD CONSTRAINT UQ_' + @TableName + '_' + @ColumnName + 
               N' UNIQUE (' + @ColumnName + ') ';
    EXEC sp_executesql @SQL
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[DropTableIfExists]
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @DynamicSQL NVARCHAR(MAX);
	--ToDo, this would fail for #ZZTemp!
    -- Check if the table exists and build the dynamic SQL to drop it
    IF EXISTS (
		SELECT * FROM INFORMATION_SCHEMA.TABLES 
		WHERE 
			TABLE_SCHEMA = @SchemaName 
			and TABLE_NAME = @TableName
		)
    BEGIN
        SET @DynamicSQL = N'DROP TABLE ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName) + ';';
        EXEC sp_executesql @DynamicSQL;
    END
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[AttemptToUpdateOneSingleColumnInTheDestinationTableFromTheSourceTableAndReturnMessage] 
/* This sproc is *NOT* designed to be performant or efficient
but INSTEAD is designed to perform the most granular
field-level validation possible.  It does this 
by attempting to update a column in a destination 
table, using the looked-up, single value 
from the source table.
For example, it would attempt to run an 
update statement similiar to the following:
--===
update DestinationPersons 
set DestinationPersons.LastName = 
	(select SourcePersons.LastName 
		from SourcePersons 
		where SourcePersons.ID = 1234);
--=========
The less rows that are in the destination table, 
the faster this should run.  So, for the 
purposes of this framework, only one single 
row of data is expected to be in the 
destination table prior to calling this.

The intended purpose is so that calling logic 
can enumerate all rows and all columns for a given source
table, in order to produce a resulting valid/invalid 
message for every single column value in the entire
table.

Here is an example of using this spoc to 
test only a single column value...
DECLARE @ResultMessage NVARCHAR(255);
DECLARE @ReturnStatus INT;
EXEC @ReturnStatus = 
    AttemptToUpdateOneSingleColumnInTheDestinationTableFromTheSourceTableAndReturnMessage 
        @SourceTableName = 'tblDemoSongs' ,
        @SourceTablePrimaryKeyColumnName = 'ID' , 
        @SourceTablePrimaryKeyValue = 2 ,
        @DestinationTableName = 'tbl_Staging_Songs' ,
        @ColumnNameToUpdate = 'YearReleased' ,
        @ResultMessage = @ResultMessage output  
;--end exec 
print @ResultMessage;
PRINT 'Return Status: ' + CAST(@ReturnStatus AS NVARCHAR(10));
 ***********end tester*****************************************************/
    --params...
    @SourceSchemaName NVARCHAR(128),
	@SourceTableName NVARCHAR(128),
    @SourceTablePrimaryKeyColumnName NVARCHAR(128),
    @SourceTablePrimaryKeyValue int,
	@DestinationSchemaName NVARCHAR(128),
    @DestinationTableName NVARCHAR(128),
    @ColumnNameToUpdate NVARCHAR(128),
    @ResultMessage NVARCHAR(255) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SourceSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SourceSchemaName, @SourceTableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @SourceSchemaName, @SourceTableName, @SourceTablePrimaryKeyColumnName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DestinationSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DestinationSchemaName, @DestinationTableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @DestinationSchemaName, @DestinationTableName, @ColumnNameToUpdate;
	--end param validation--

    declare @ValidationSQL nvarchar(max);
    DECLARE @DynamicSQL NVARCHAR(MAX);
    DECLARE @RowCount INT;

	--Fail fast, until try block...
	SET XACT_ABORT ON;

    -- Construct and execute the validation SQL to ensure one and only one row is returned...
    SET @ValidationSQL = N'SELECT @RowCountOUT = COUNT(*) ' + 
						' FROM ' + QUOTENAME(@SourceSchemaName) + '.' + QUOTENAME(@SourceTableName) + 
						' WHERE ' + QUOTENAME(@SourceTablePrimaryKeyColumnName) + ' = @PrimaryKeyValue';
    
    EXEC sp_executesql @ValidationSQL, N'@PrimaryKeyValue INT, @RowCountOUT INT OUTPUT', 
                        @SourceTablePrimaryKeyValue, @RowCount OUTPUT;

    -- Error if the row count is NOT exactly 1...
    IF @RowCount <> 1
    BEGIN
        RAISERROR('AttemptToUpdateOneSingleColumnInTheDestinationTableFromTheSourceTableAndReturnMessage says subquery did not return exactly one row, operation aborted.', 16, 1);
        RETURN;
    END

    BEGIN TRY
        -- Construct the dynamic SQL for updating the destination table
        SET @DynamicSQL = N'UPDATE ' + QUOTENAME(@DestinationSchemaName) + '.' + QUOTENAME(@DestinationTableName) + '
                          SET ' + QUOTENAME(@ColumnNameToUpdate) + ' = 
							(SELECT ' + QUOTENAME(@ColumnNameToUpdate) + '
                                FROM ' + QUOTENAME(@SourceSchemaName) + '.' + QUOTENAME(@SourceTableName) + '
                                WHERE ' + QUOTENAME(@SourceTablePrimaryKeyColumnName) + ' = @PrimaryKeyValue);';

        -- Execute the dynamic SQL
        EXEC sp_executesql @DynamicSQL, N'@PrimaryKeyValue INT', @SourceTablePrimaryKeyValue;

        -- Set success message
        SET @ResultMessage = 'Success';
    END TRY
    BEGIN CATCH
        -- Capture the error message
        SET @ResultMessage = ERROR_MESSAGE();
    END CATCH
END --end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[TransferData]
(
	@SourceSchemaName NVARCHAR(128),
	@SourceTableName NVARCHAR(128),
	@DestinationSchemaName NVARCHAR(128),
    @DestinationTableName NVARCHAR(128),
    @ColumnsToOmit NVARCHAR(MAX)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SourceSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SourceSchemaName, @SourceTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DestinationSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DestinationSchemaName, @DestinationTableName;
	--end param validation--

    DECLARE @Columns NVARCHAR(MAX);
    DECLARE @Query NVARCHAR(MAX);

    -- Get the columns of the destination table...
    SELECT @Columns = STRING_AGG(column_name, ', ')
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = @DestinationSchemaName
	AND TABLE_NAME = @DestinationTableName
    AND CHARINDEX(column_name, @ColumnsToOmit) = 0;

    -- Build and execute the dynamic sql...
    SET @Query = 'INSERT INTO ' + QUOTENAME(@DestinationSchemaName) + '.' + QUOTENAME(@DestinationTableName) + 
                 ' (' + @Columns + ') ' + 
                 'SELECT ' + @Columns + ' FROM ' + QUOTENAME(@SourceSchemaName) + '.' + QUOTENAME(@SourceTableName);
    
    EXEC sp_executesql @Query;
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[TransferDataForRequestID]
(
	@SourceSchemaName NVARCHAR(128),
	@SourceTableName NVARCHAR(128),
	@DestinationSchemaName NVARCHAR(128),
    @DestinationTableName NVARCHAR(128),
    @ColumnsToOmit NVARCHAR(MAX),
	@RequestID NVARCHAR(36)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SourceSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SourceSchemaName, @SourceTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DestinationSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DestinationSchemaName, @DestinationTableName;
	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation--

    DECLARE @Columns NVARCHAR(MAX);
    DECLARE @Query NVARCHAR(MAX);

    -- Get the columns of the destination table...
    SELECT @Columns = STRING_AGG(column_name, ', ')
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = @DestinationSchemaName
	AND TABLE_NAME = @DestinationTableName
    AND CHARINDEX(column_name, @ColumnsToOmit) = 0;

    -- Build and execute the dynamic sql...
    SET @Query = 'INSERT INTO ' + QUOTENAME(@DestinationSchemaName) + '.' + QUOTENAME(@DestinationTableName) + 
                 ' (' + @Columns + ') ' + 
                 'SELECT ' + @Columns + ' FROM ' + QUOTENAME(@SourceSchemaName) + '.' + QUOTENAME(@SourceTableName) + 
				 ' where RequestID = ''' + @RequestID + '''; ';
    
    EXEC sp_executesql @Query;
END;--end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ProcessRawDataFromZZTemp]
(
	@BulkOpsFeaturesID int,
	@RequestID nvarchar(36),
	@Debug BIT = 0 -- Default to no debugging
)
AS
BEGIN
	IF @Debug = 1
	BEGIN
		exec SqlXl.DebugLogInsert @RequestID, '','Started procedure: ProcessRawDataFromZZTemp...';
	END

	--validate params...
	-- Validate that exists for the given @BulkOpsFeaturesID
	IF NOT EXISTS (
		SELECT 1
		FROM [SqlXl].[BulkOpFeatures]
		WHERE [ID] = @BulkOpsFeaturesID
	)
	BEGIN
		RAISERROR('No matching row found in BulkOpFeatures for the given @BulkOpsFeaturesID.', 16, 1);
		RETURN -1; -- Halt execution
	END

	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation

	-- Declare the variables
	DECLARE @StagingSchemaName NVARCHAR(128);
	DECLARE @StagingTableName NVARCHAR(128);
	DECLARE @DomainSchemaName NVARCHAR(128);
	DECLARE @DomainSprocNameToProcessDataFromStagingTable NVARCHAR(128);

	-- Fetch values into variables
	SELECT 
		@StagingSchemaName = [StagingSchemaName],
		@StagingTableName = [StagingTableName],
		@DomainSchemaName = [DomainSchemaName],
		@DomainSprocNameToProcessDataFromStagingTable = [SprocToProcessPerfectStagedData]
	FROM [SqlXl].[BulkOpFeatures]
	WHERE [ID] = @BulkOpsFeaturesID;

	IF @Debug = 1 and @StagingTableName is not null 
	BEGIN
		exec SqlXl.DebugLogInsert @RequestID, '','Params validated successfully.';
	END

    DECLARE @SQLCommand NVARCHAR(255);

	-- Transfer data from #ZZTemp to StagingTable...
	SET XACT_ABORT ON;--this should halt execution
	--...and immediatly propogate the error UP
	--...to the caller if TransferData fails.
	EXEC SqlXl.TransferData @SourceSchemaName = @StagingSchemaName,      -- nvarchar(128)
	                                @SourceTableName = N'#ZZTemp',       -- nvarchar(128)
	                                @DestinationSchemaName = @StagingSchemaName, -- nvarchar(128)
	                                @DestinationTableName = @StagingTableName,  -- nvarchar(128)
	                                @ColumnsToOmit = N'ZZTemp_ID'          -- nvarchar(max)
	IF @Debug = 1
	BEGIN
		exec SqlXl.DebugLogInsert @RequestID, '','Moved data from #ZZTemp to staging table successfully.';
	END

	/* Next block is where the domain/production table(s)
	   are involved, so wrap this in a transaction
	   and try-catch to ensure that this happens all-or-nothing.
	   Note that the use of "begin try" OVERRIDES the 
	   "set xact_abort on".  After "begin try"
	   , on error, execution would flow into 'begin catch'.*/
	BEGIN TRANSACTION;
	BEGIN TRY
        -- Call sproc to process the data...
        SET @SQLCommand = 
			'EXEC ' + QUOTENAME(@DomainSchemaName) + '.' + QUOTENAME(@DomainSprocNameToProcessDataFromStagingTable) + 
				' @RequestID = ''' + @RequestID +  ''' ;';
        EXEC sp_executesql @SQLCommand;

		-- If all operations succeed, commit the transaction
		COMMIT TRANSACTION;

		IF @Debug = 1
		BEGIN
			exec SqlXl.DebugLogInsert @RequestID, '', 'Successfully ran @DomainSprocNameToProcessDataFromStagingTable.';
		END

		--Clean-up staging...
		EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;
	END TRY
	BEGIN CATCH
		-- If an error occurs, rollback the transaction and capture the error
		ROLLBACK TRANSACTION;

		--Clean-up staging...
		EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;

		IF @Debug = 1
		BEGIN
			exec SqlXl.DebugLogInsert @RequestID, '','@DomainSprocNameToProcessDataFromStagingTable failed, trans rolled back.';
		END
		
		-- Ensure an error exists before THROW
		IF ERROR_NUMBER() IS NOT NULL
			THROW; -- Re-raises the original error
	END CATCH

	IF @Debug = 1
	BEGIN
		exec SqlXl.DebugLogInsert @RequestID, '','Ended procedure: ProcessRawDataFromZZTemp.';
	END
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE SqlXl.DropColumnFromTable
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
    @ColumnName NVARCHAR(128)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @SchemaName, @TableName, @ColumnName;
	--end param validation--

    -- Construct the dynamic SQL command to drop the column
    DECLARE @SQL NVARCHAR(MAX);
    SET @SQL = N'ALTER TABLE '+ QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName) + 
               N' DROP COLUMN ' + QUOTENAME(@ColumnName) + ';';

    -- Execute the dynamic SQL command
    EXEC sp_executesql @SQL;
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[CreateNonAutoNumberPrimaryKey]
(
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
    @ColumnName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @SchemaName, @TableName, @ColumnName;
	--end param validation--

    DECLARE @SQL NVARCHAR(MAX)
	SET @SQL = N'ALTER TABLE ' + @SchemaName + '.' + @TableName + 
               N' ADD CONSTRAINT PK_' + @SchemaName + '_' + @TableName + 
               N' PRIMARY KEY (' + @ColumnName + ') ';
    EXEC sp_executesql @SQL
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[CreateForeignKey]
(
    @ForeignKeyConstraintName NVARCHAR(128),
	@MainTableSchemaName NVARCHAR(128),
    @MainTableName NVARCHAR(128),
    @MainTableForeignKeyColumnName NVARCHAR(128),
	@ReferencedTableSchemaName NVARCHAR(128),
    @ReferencedTableName NVARCHAR(128),
    @ReferencedColumnName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @MainTableSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @MainTableSchemaName, @MainTableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @MainTableSchemaName, @MainTableName, @MainTableForeignKeyColumnName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @ReferencedTableSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @ReferencedTableSchemaName, @ReferencedTableName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @ReferencedTableSchemaName, @ReferencedTableName, @ReferencedColumnName;
	--end param validation--

    DECLARE @SQL NVARCHAR(MAX)

    SET @SQL = N'ALTER TABLE ' + QUOTENAME(@MainTableSchemaName) + '.' + QUOTENAME(@MainTableName) + 
               N' ADD CONSTRAINT ' + QUOTENAME(@ForeignKeyConstraintName) + 
               N' FOREIGN KEY (' + QUOTENAME(@MainTableForeignKeyColumnName) + 
               N') REFERENCES ' + QUOTENAME(@ReferencedTableSchemaName) + '.'  + QUOTENAME(@ReferencedTableName) + 
               N'(' + QUOTENAME(@ReferencedColumnName) + N');'

    EXEC sp_executesql @SQL
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[CreateForeignKeysOnStagingTable]
(
	@DomainSchemaName NVARCHAR(128),
	@DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
    @StagingTableName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
	--end param validation--

    DECLARE @ForeignKey NVARCHAR(128),
        @ParentTable NVARCHAR(128),
        @ParentColumn NVARCHAR(128),
        @ReferencedTable NVARCHAR(128),
        @ReferencedColumn NVARCHAR(128)

    -- Cursor declaration
    DECLARE ForeignKeyCursor CURSOR FOR
    SELECT 
        fk.name AS ForeignKey,
        tp.name AS ParentTable,
        cp.name AS ParentColumn,
        tr.name AS ReferencedTable,
        cr.name AS ReferencedColumn
    FROM 
        sys.foreign_keys AS fk
    INNER JOIN 
        sys.tables AS tp ON fk.parent_object_id = tp.object_id
    INNER JOIN 
        sys.tables AS tr ON fk.referenced_object_id = tr.object_id
    INNER JOIN 
        sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
    INNER JOIN 
        sys.columns AS cp ON fkc.parent_column_id = cp.column_id AND fkc.parent_object_id = cp.object_id
    INNER JOIN 
        sys.columns AS cr ON fkc.referenced_column_id = cr.column_id AND fkc.referenced_object_id = cr.object_id
    WHERE 
		tp.schema_id = SCHEMA_ID(@DomainSchemaName)
        AND tp.name = @DomainTableName

-- Open the cursor
OPEN ForeignKeyCursor

-- Fetch the first row from the cursor
FETCH NEXT FROM ForeignKeyCursor INTO @ForeignKey, @ParentTable, @ParentColumn, @ReferencedTable, @ReferencedColumn

-- Loop through all rows
WHILE @@FETCH_STATUS = 0
BEGIN
    -- Print the values of the current row
    --PRINT 'ForeignKey: ' + @ForeignKey + ', ParentTable: ' + @ParentTable + ', ParentColumn: ' + @ParentColumn + ', ReferencedTable: ' + @ReferencedTable + ', ReferencedColumn: ' + @ReferencedColumn

	-- Ensure a display column exists in the referenced table (for dropdown listings, for example)...
	EXEC SqlXl.ErrorIfNoUniqueDisplayColumn @DomainSchemaName, @ReferencedTable;

    --Name FK constraint per staging table name...
    set @ForeignKey = 'FK_' + @StagingTableName + '_' + @ReferencedColumn + '_' + @ReferencedTable;
    
    EXEC [SqlXl].[CreateForeignKey] @ForeignKeyConstraintName = @ForeignKey, -- nvarchar(128)
                                            @MainTableSchemaName = @StagingSchemaName, -- nvarchar(128)
                                            @MainTableName = @StagingTableName, -- nvarchar(128)
                                            @MainTableForeignKeyColumnName = @ParentColumn, -- nvarchar(128)
                                            @ReferencedTableSchemaName = @DomainSchemaName, -- nvarchar(128)
                                            @ReferencedTableName = @ReferencedTable, -- nvarchar(128)
                                            @ReferencedColumnName = @ReferencedColumn -- nvarchar(128)
    
    -- Fetch the next row from the cursor
    FETCH NEXT FROM ForeignKeyCursor INTO @ForeignKey, @ParentTable, @ParentColumn, @ReferencedTable, @ReferencedColumn
END

-- Close and deallocate the cursor
CLOSE ForeignKeyCursor
DEALLOCATE ForeignKeyCursor

END;
GO

CREATE OR ALTER PROCEDURE SqlXl.AddCheckConstraint
	@SchemaName NVARCHAR(128),
    @TableName NVARCHAR(128),
    @ConstraintName NVARCHAR(128),
    @CheckClause NVARCHAR(MAX)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SchemaName, @TableName;
	--end param validation--

    DECLARE @SQL NVARCHAR(MAX);
    
    -- Building the dynamic SQL statement
    SET @SQL = N'ALTER TABLE ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName) + 
               N' ADD CONSTRAINT ' + QUOTENAME(@ConstraintName) + 
               N' CHECK (' + @CheckClause + N');';

    -- Executing the dynamic SQL statement
    EXEC sp_executesql @SQL;
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[CreateCheckConstraintsOnStagingTable]
(	
	@DomainSchemaName NVARCHAR(128),
	@DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
    @StagingTableName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
	--end param validation--

    -- Declaring variables to hold data for each row
    DECLARE @Constraint_Name NVARCHAR(256);
    DECLARE @Check_Clause NVARCHAR(MAX);
    DECLARE @Column_Name NVARCHAR(128);

    -- Cursor declaration
    DECLARE constraints_cursor CURSOR FOR
        SELECT
            cc.CONSTRAINT_NAME,
            cc.CHECK_CLAUSE,
            ccu.COLUMN_NAME
        FROM
            INFORMATION_SCHEMA.CHECK_CONSTRAINTS AS cc
            JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE AS ccu ON cc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
        WHERE 
			ccu.TABLE_SCHEMA = @DomainSchemaName
			AND ccu.TABLE_NAME = @DomainTableName;

    -- Opening cursor
    OPEN constraints_cursor;

    -- Fetching the first row
    FETCH NEXT FROM constraints_cursor INTO @Constraint_Name, @Check_Clause, @Column_Name;

    -- Looping through the rows
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- -- Printing the values
        -- PRINT 'Constraint Name: ' + @Constraint_Name;
        -- PRINT 'Check Clause: ' + @Check_Clause;
        -- PRINT 'Column Name: ' + @Column_Name;
        -- PRINT '-----------------------------------'; -- Separator for readability

        --Use convention to name the constraint...
        set @Constraint_Name = 'Check_' + @StagingTableName + '_' + @Column_Name;

        --Add check constraint to staging table...
        EXEC SqlXl.AddCheckConstraint @SchemaName = @StagingSchemaName,     -- nvarchar(128)
                                              @TableName = @StagingTableName,      -- nvarchar(128)
                                              @ConstraintName = @Constraint_Name, -- nvarchar(128)
                                              @CheckClause = @Check_Clause  -- nvarchar(max)
            
        -- Fetching the next row
        FETCH NEXT FROM constraints_cursor INTO @Constraint_Name, @Check_Clause, @Column_Name;
    END;

    -- Closing and deallocating the cursor
    CLOSE constraints_cursor;
    DEALLOCATE constraints_cursor;
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ReScaffoldAStagingTable]
(
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
	@InsertOrUpdate nvarchar(6)
	--insert update
)
AS
BEGIN
    SET NOCOUNT ON;

	--Lite sanity-checks/param validation...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @DomainSchemaName, @DomainTableName;
	                                                 
	-- Propose a staging table name based on whether 
	-- the staging table will hold rows for inserts or updates...
	DECLARE @StagingTableName NVARCHAR(128);
	IF @InsertOrUpdate <> 'insert' AND @InsertOrUpdate <> 'update'
	BEGIN
		RAISERROR('@InsertOrUpdate parameter must be either ''insert'' or ''update''.', 16, 1);
	END
	IF @InsertOrUpdate = 'insert'
	BEGIN
		SET @StagingTableName = SqlXl.ProposeStagingTableNameForInsertFeature(@DomainTableName);
	END 
	ELSE	
	BEGIN
		SET @StagingTableName = SqlXl.ProposeStagingTableNameForUpdateFeature(@DomainTableName);
	END 

	DECLARE @SQL NVARCHAR(MAX);

    EXEC SqlXl.DropTableIfExists @SchemaName = @StagingSchemaName, -- nvarchar(128)
                                         @TableName = @StagingTableName   -- nvarchar(128)
    
    -- Build dynamic SQL to create the staging table, 
	-- based on the structure of the domain table...
	set @SQL = [SqlXl].[GenerateCreateStagingTableSQLWith_NO_IdentityProperty]
					(@DomainSchemaName, @DomainTableName, 
						@StagingSchemaName, @StagingTableName);
    EXEC sp_executesql @SQL;

    --Create foreign keys on staging table...
	EXEC [SqlXl].[CreateForeignKeysOnStagingTable] @DomainSchemaName = @DomainSchemaName,  -- nvarchar(128)
	                                                       @DomainTableName = @DomainTableName,   -- nvarchar(128)
	                                                       @StagingSchemaName = @StagingSchemaName, -- nvarchar(128)
	                                                       @StagingTableName = @StagingTableName   -- nvarchar(128)
	

    --Create check constraints on staging table...
    exec [SqlXl].[CreateCheckConstraintsOnStagingTable] @DomainSchemaName = @DomainSchemaName,  -- nvarchar(128)
	                                                       @DomainTableName = @DomainTableName,   -- nvarchar(128)
	                                                       @StagingSchemaName = @StagingSchemaName, -- nvarchar(128)
	                                                       @StagingTableName = @StagingTableName   -- nvarchar(128)

	--Generate a sql script that will add
	--unique key constraints from Domain table to StagingTable...
	set @SQL = SqlXl.GenerateUniqueConstraintSQL(@DomainSchemaName,@DomainTableName,@StagingSchemaName,@StagingTableName);

	--print @SQL; --test/debug
	EXEC sp_executesql @SQL;

	if @InsertOrUpdate = 'Insert'
	begin
		--Drop any identity/autogenerated cols from this 
		-- newly created table, if present...
		declare @IdColName nvarchar(256) = SqlXl.GetIdentityColumnName(@DomainSchemaName, @DomainTableName);
		if @IdColName is not NULL
		BEGIN
			EXEC SqlXl.DropColumnFromTable @SchemaName = @StagingSchemaName, -- nvarchar(128)
													@TableName = @StagingTableName,  -- nvarchar(128)
													@ColumnName = @IdColName -- nvarchar(128)
		end;--end if 
	end --end if 

END;--end sproc 
go 

CREATE OR ALTER PROCEDURE SqlXl.UpdateDestinationTableFromSourceTableForRequestID
	@SourceSchemaName NVARCHAR(128),
    @SourceTableName NVARCHAR(128),
	@DestinationSchemaName NVARCHAR(128),
    @DestinationTableName NVARCHAR(128),
    @PrimaryKeyColumnName NVARCHAR(128),
    @CommaDelimitedColumnsToOmit NVARCHAR(MAX),
	@RequestID nvarchar(36)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @SourceSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @SourceSchemaName, @SourceTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DestinationSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DestinationSchemaName, @DestinationTableName;

	-- Note: Same PrimaryKeyColumnName is expected in *BOTH* source and destination tables...
	EXEC SqlXl.ErrorIfColumnDoesNotExist @SourceSchemaName, @SourceTableName, @PrimaryKeyColumnName;
	EXEC SqlXl.ErrorIfColumnDoesNotExist @DestinationSchemaName, @DestinationTableName, @PrimaryKeyColumnName;

	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation--

    SET NOCOUNT ON;

    DECLARE @SQL NVARCHAR(MAX);
    DECLARE @UpdateList NVARCHAR(MAX);

    -- Create the list of columns to update
    SELECT @UpdateList = STRING_AGG(CAST('Dest.' + QUOTENAME(c.name) + ' = Src.' + QUOTENAME(c.name) AS NVARCHAR(MAX)), ', ')
    FROM sys.columns c
    JOIN sys.tables t ON c.object_id = t.object_id
    WHERE
		t.schema_id = SCHEMA_ID(@DestinationSchemaName)
		AND t.name = @DestinationTableName 
		AND c.name <> @PrimaryKeyColumnName
		and c.name <> @RequestID
        AND CHARINDEX(',' + c.name + ',', ',' + @CommaDelimitedColumnsToOmit + ',') = 0;

    -- Build the dynamic SQL statement for updating the destination table from the source table
    SET @SQL = N'UPDATE Dest
                 SET ' + @UpdateList + '
                 FROM ' + QUOTENAME(@DestinationSchemaName) + '.' + QUOTENAME(@DestinationTableName) + ' AS Dest
                 INNER JOIN ' + QUOTENAME(@SourceSchemaName) + '.' + QUOTENAME(@SourceTableName) + ' AS Src
                 ON Dest.' + QUOTENAME(@PrimaryKeyColumnName) + ' = Src.' + QUOTENAME(@PrimaryKeyColumnName) + 
				 ' where Src.RequestID = ''' + @RequestID + ''';';

    -- Execute the dynamic SQL
    EXEC sp_executesql @SQL;
END; --end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[DropZZTempAndPurgeStaging]
    @PermanentStagingTableName NVARCHAR(128),
	@RequestID nvarchar(36)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfTableDoesNotExist 'SqlXl', @PermanentStagingTableName;
	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation

    SET NOCOUNT ON;

    DECLARE @SQLCommand NVARCHAR(MAX);

	--Drop table ZZTemp, and delete ALL records from staging table...
    IF  EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[SqlXl].[#ZZTemp]') AND type in (N'U'))
        DROP TABLE [SqlXl].[#ZZTemp];

	EXEC [SqlXl].[PurgeStagingForRequestID] @PermanentStagingTableName, @RequestID;
END; --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[RefreshMetaDataForTable]
(
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	--end param validation

    SET NOCOUNT ON;

	--Need this later...
	DECLARE @SQL NVARCHAR(MAX);

	--Drop if existence of #MetaColumns...
	IF OBJECT_ID('tempdb..#MetaColumns') IS NOT NULL
    DROP TABLE #MetaColumns;

	--Begin creating metadata for the given @DomainTableName...
	SELECT 
		c.COLUMN_NAME, 
		c.DATA_TYPE,
		c.IS_NULLABLE,
		c.CHARACTER_MAXIMUM_LENGTH,
		CASE 
			WHEN pk.COLUMN_NAME IS NOT NULL THEN 'YES'
			ELSE 'NO' 
		END AS IS_PRIMARY_KEY,
		CASE 
			WHEN fk.COLUMN_NAME IS NOT NULL THEN 'YES'
			ELSE 'NO' 
		END AS IS_FOREIGN_KEY

	into #MetaColumns

	FROM 
		INFORMATION_SCHEMA.COLUMNS c
		LEFT JOIN
		(   -- Subquery to find primary key columns
			SELECT kcu.COLUMN_NAME
			FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
			JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
				ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
				AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
				AND tc.TABLE_NAME = kcu.TABLE_NAME
			WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
				  AND tc.TABLE_NAME = @DomainTableName
				  AND tc.TABLE_SCHEMA = @DomainSchemaName
		) pk ON c.COLUMN_NAME = pk.COLUMN_NAME
		LEFT JOIN
		(   -- Subquery to find foreign key columns
			SELECT kcu.COLUMN_NAME
			FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
			JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
				ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
				AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
				AND tc.TABLE_NAME = kcu.TABLE_NAME
			WHERE tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
				  AND tc.TABLE_NAME = @DomainTableName
				  AND tc.TABLE_SCHEMA = @DomainSchemaName
		) fk ON c.COLUMN_NAME = fk.COLUMN_NAME

	WHERE 
		c.TABLE_NAME = @DomainTableName AND
		c.TABLE_SCHEMA = @DomainSchemaName;

	/*****
	Note:  If for example our SourceTable were called Projects
	then at this point, select * from #MetaColumns would give
	something like the following, just for example...
	COLUMN_NAME	DATA_TYPE	IS_NULLABLE	IS_PRIMARY_KEY	IS_FOREIGN_KEY  CHARACTER_MAXIMUM_LENGTH
	ProjectId	int	NO	YES	NO
	ProjectName	nvarchar	NO	NO	NO  100
	StartDate	datetime	NO	NO	NO
	EndDate	datetime2	YES	NO	NO
	DepartmentId	int	YES	NO	YES
	Budget	decimal	NO	NO	NO
	Cost	money	NO	NO	NO
	IsActive	bit	NO	NO	NO
	TeamSize	tinyint	NO	NO	NO
	***************************************/

	--Purge meta data for given table...
	delete from SqlXl.Meta_Columns 
		where SchemaName = @DomainSchemaName
			and TableName = @DomainTableName
	; --end delete 

	--Initialize meta data for given table...
	INSERT INTO [SqlXl].[Meta_Columns]
	([SchemaName]
	,[TableName]
	,[ColumnName]
	,[SqlDataType]
	,[IsNullable]
	,[MaxLengthForString]
	,[IsPrimaryKey]
	,[IsForeignKey]
	,[ReferencedTable]
	,[ReferencedColumn]
	,[ValidValueSelectStatement]
	,[InvalidValueSelectStatement]
	,[ValidSampleValue]
	,[InvalidSampleValue])
	select 
		[SchemaName] = @DomainSchemaName
		,[TableName] = @DomainTableName
		,[ColumnName] = #MetaColumns.COLUMN_NAME
		,[SqlDataType] = #MetaColumns.DATA_TYPE
		,[IsNullable] = #MetaColumns.IS_NULLABLE
		,[MaxLengthForString] = #MetaColumns.CHARACTER_MAXIMUM_LENGTH 
		,[IsPrimaryKey] = #MetaColumns.IS_PRIMARY_KEY
		,[IsForeignKey] = #MetaColumns.IS_FOREIGN_KEY
		,[ReferencedTable] = null
		,[ReferencedColumn] = null
		,[ValidValueSelectStatement] = null
		,[InvalidValueSelectStatement] = null
		,[ValidSampleValue] = null
		,[InvalidSampleValue] = null
	from #MetaColumns;
	--end insert-select 

	--Drop if existence of #TempForeignKeyDetails...
	IF OBJECT_ID('tempdb..#TempForeignKeyDetails') IS NOT NULL
    DROP TABLE #TempForeignKeyDetails;

	--Gather all foreign key details for given table...
	SELECT 
		fk.name AS ForeignKey,
		tp.name AS ParentTable,
		cp.name AS ParentColumn,
		ReferencedSchemaName = @DomainSchemaName,
		tr.name AS ReferencedTable,
		cr.name AS ReferencedColumn
	INTO #TempForeignKeyDetails
	FROM 
		sys.foreign_keys AS fk
	INNER JOIN 
		sys.tables AS tp ON fk.parent_object_id = tp.object_id
	INNER JOIN 
		sys.tables AS tr ON fk.referenced_object_id = tr.object_id
	INNER JOIN 
		sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
	INNER JOIN 
		sys.columns AS cp ON fkc.parent_column_id = cp.column_id AND fkc.parent_object_id = cp.object_id
	INNER JOIN 
		sys.columns AS cr ON fkc.referenced_column_id = cr.column_id AND fkc.referenced_object_id = cr.object_id
	WHERE 
		tp.schema_id = SCHEMA_ID(@DomainSchemaName)
		AND tp.name = @DomainTableName;

	--Update the meta table to set foreign key details that were just collected...
	update SqlXl.Meta_Columns
		set

		--Lookup foreign key columns in the #TempForeignKeyDetails table...
		ReferencedTable = 
			(select C1 = #TempForeignKeyDetails.ReferencedSchemaName + '.' + #TempForeignKeyDetails.ReferencedTable 
				from #TempForeignKeyDetails 
				where #TempForeignKeyDetails.ReferencedColumn = Meta_Columns.ColumnName),
		ReferencedColumn = 
			(select C1 = #TempForeignKeyDetails.ReferencedColumn
				from #TempForeignKeyDetails 
				where #TempForeignKeyDetails.ReferencedColumn = Meta_Columns.ColumnName)
	where 
		SchemaName = @DomainSchemaName
		and TableName = @DomainTableName
		and IsForeignKey = 'YES'
	;--end update 

	--Clean-up temp table...
	IF OBJECT_ID('tempdb..#TempForeignKeyDetails') IS NOT NULL
    DROP TABLE #TempForeignKeyDetails;

	--Make scalar select statements resulting in valid and invalid values...
	update SqlXl.Meta_Columns 
		set 
			ValidValueSelectStatement = 
							CASE  
								WHEN IsForeignKey = 'YES' THEN 'select top 1 ' + ReferencedColumn + ' from ' + ReferencedTable + ''
								WHEN SqlDataType like 'date%' THEN 'select getdate()' 
								ELSE 'select SqlXl.CreateNonForeignKeySampleValue(''' + SqlDataType + ''',' + convert(nvarchar, isnull(MaxLengthForString,0)) + ')'
							   END,

			InvalidValueSelectStatement = 
							CASE  
								WHEN IsForeignKey = 'YES' THEN 'select -1 '
								ELSE 'select SqlXl.CreateInvalidNonForeignKeySampleValue(''' + SqlDataType + ''')'
							   END
	where 
		SchemaName = @DomainSchemaName 
		and TableName = @DomainTableName
	;--end update 

END;--end sproc 
go

CREATE OR ALTER PROCEDURE SqlXl.ExecuteDynamicScalarSelect
    @SqlScalarSelect nvarchar(max),
    @Result nvarchar(max) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    -- Prepare the dynamic SQL statement. We will directly select into the output parameter
    DECLARE @DynamicSQL nvarchar(max) = N'SELECT @Result = (' + @SqlScalarSelect + ')';

    -- Execute the dynamic SQL
    EXEC sp_executesql 
        @stmt = @DynamicSQL, 
        @params = N'@Result nvarchar(max) OUTPUT', 
        @Result = @Result OUTPUT;

    -- No need to select @Result again since it's already in the output parameter
END
GO

CREATE OR ALTER PROCEDURE [SqlXl].[RefreshSampleValues]
(
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	--end param validation

    SET NOCOUNT ON;
	
	DECLARE @ValidValueSelectStatement NVARCHAR(MAX), 
        @InvalidValueSelectStatement NVARCHAR(MAX), 
        @ValidValueScalarSelectResult NVARCHAR(MAX), 
        @InvalidValueScalarSelectResult NVARCHAR(MAX)
	DECLARE @ColumnName NVARCHAR(128)
			
	--Enumerate records from Meta_Columns for given table...
	DECLARE column_cursor CURSOR FOR
	SELECT ColumnName, ValidValueSelectStatement, InvalidValueSelectStatement
	FROM [SqlXl].[Meta_Columns]
	where SchemaName = @DomainSchemaName
	and TableName = @DomainTableName

	OPEN column_cursor

	FETCH NEXT FROM column_cursor INTO @ColumnName, @ValidValueSelectStatement, @InvalidValueSelectStatement

	WHILE @@FETCH_STATUS = 0
	BEGIN
		--Use special sproc to run the sql select statement, assign the scalar result...
		EXEC SqlXl.ExecuteDynamicScalarSelect @ValidValueSelectStatement, @ValidValueScalarSelectResult OUTPUT;
		EXEC SqlXl.ExecuteDynamicScalarSelect @InvalidValueSelectStatement, @InvalidValueScalarSelectResult OUTPUT;

		--Update the 2 sample value columns...
		update SqlXl.Meta_Columns 
			set ValidSampleValue = @ValidValueScalarSelectResult
			,InvalidSampleValue = @InvalidValueScalarSelectResult
		where 
			SchemaName = @DomainSchemaName 
			and TableName = @DomainTableName
			and ColumnName = @ColumnName
		;--end update 

		FETCH NEXT FROM column_cursor INTO @ColumnName, @ValidValueSelectStatement, @InvalidValueSelectStatement
	END
	CLOSE column_cursor
	DEALLOCATE column_cursor
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[InsertSingleValidSampleRowToStagingGivenTheRealProdTableName]
(
	@DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128),
	@StagingTableName NVARCHAR(128),
	@RequestID nvarchar(36)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation

    SET NOCOUNT ON;
    DECLARE @SQL NVARCHAR(MAX);

	--Refresh sample values...
	--This especially matters for generating foreign key values
	--because they must be based on current values in related tables...
	EXEC SqlXl.RefreshSampleValues @DomainSchemaName, -- nvarchar(128)
	                                       @DomainTableName
	
	--Create and run an insert statement that writes 
	--a single valid row to the staging table.
	--Sample values are provided by the Meta_Columns table...
	SET @SQL = SqlXl.GenerateSqlToInsert_a_SingleValidSampleRowToStagingTable(
		@DomainSchemaName,@DomainTableName,@StagingSchemaName,@StagingTableName,@RequestID);
    EXEC sp_executesql @SQL;

	-- Check if exactly one row was inserted
    IF @@ROWCOUNT != 1
    BEGIN
        RAISERROR('[SqlXl].[InsertSingleValidSampleRowToStagingGivenTheRealProdTableName] failed to insert exactly one row.', 16, 1);
		RETURN -1;  -- Return -1 to indicate an error condition
    END

	RETURN 0;  -- Return 0 to indicate success
    
	/* Note: To provide your own custom insert statement 
	   to write a row of sample data to the staging table, you may wish
	   to modify this sproc to be something like...*** 
	IF @RealProdTableName = 'tblSomeTable001'
    BEGIN
		--Your custom insert statement...
        INSERT INTO Staging_Table001 (ColumnNames) VALUES ('Value1', 'Value2', ...);
    END
    ELSE IF @RealProdTableName = 'tblSomeTable002'
    BEGIN
		--Your custom insert statement...
        INSERT INTO Staging_Table002 (ColumnNames) VALUES ('Value1', 'Value2', ...);
    END
    ELSE IF @RealProdTableName = 'tblSomeTable003'
    BEGIN
		--Your custom insert statement...
        INSERT INTO Staging_Table003 (ColumnNames) VALUES ('Value1', 'Value2', ...);
    END
    ELSE
    BEGIN
        -- Default to using what is provided by the framework...
        SET @SQL = SqlXl.[GenerateSqlToInsert_a_SingleValidSampleRowToStagingTable](@DomainSchemaName,@DomainTableName,@StagingSchemaName,@StagingTableName,@RequestID);
        EXEC sp_executesql @SQL;
    END 
	*************/
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GenerateTestData]
(
	@DomainSchemaName NVARCHAR(128),
	@DomainTableName NVARCHAR(128),
	@StagingTableName NVARCHAR(128),
	@NumberOfRowsUpTo100 INT,
	@RequestID nvarchar(36)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist 'SqlXl';
	EXEC SqlXl.ErrorIfTableDoesNotExist 'SqlXl', @StagingTableName;
	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation

	-- Error if invalid number of rows desired...
    IF @NumberOfRowsUpTo100 < 1 or @NumberOfRowsUpTo100 > 100
    BEGIN
        RAISERROR('GenerateTestData requires number of rows to be between 1 and 100.', 16, 1);
        RETURN -1;--failure code
    END

	--Purge staging table...
	EXEC SqlXl.DropZZTempAndPurgeStaging @StagingTableName, @RequestID
	
	--Insert single seed row into staging table...
	EXEC SqlXl.InsertSingleValidSampleRowToStagingGivenTheRealProdTableName @DomainSchemaName = @DomainSchemaName, -- nvarchar(128)
	                                                                                @DomainTableName = @DomainTableName,  -- nvarchar(128)
	                                                                                @StagingSchemaName = N'SqlXl', -- nvarchar(128)
																					@StagingTableName = @StagingTableName,
																					@RequestID = @RequestID

	
	--Use a common table expression to make desired number of rows...
    DECLARE @Query NVARCHAR(MAX) = 
		'WITH RecursiveCTE AS (
			SELECT *
			FROM SqlXl.' + QUOTENAME(@StagingTableName) + '
			UNION ALL
			SELECT *
			FROM RecursiveCTE
		)
		SELECT top ' + cast(@NumberOfRowsUpTo100 as nvarchar) + ' *
		INTO #ZZTemp002
		FROM RecursiveCTE;'
	set @Query = @Query + 'select * from #ZZTemp002;';
    EXEC sp_executesql @Query;
	--return that last query result to caller.
	
	--Clean-up staging...
	exec SqlXl.DropZZTempAndPurgeStaging @StagingTableName, @RequestID;
END; --end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[PrintScriptToDropAllSqlXlObjects]
AS
BEGIN

DECLARE @SchemaName NVARCHAR(128) = 'SqlXl';

DECLARE @sql NVARCHAR(MAX) = '';

SET @sql = 
'DECLARE @ConfirmDelete NVARCHAR(6) = '''';--''delete'' to confirm

IF @ConfirmDelete <> ''delete''
BEGIN
	PRINT ''Action aborted. @ConfirmDelete did not equal ''''DELETE''''.''
	RETURN;
END
';

PRINT @sql;
SET @sql = '';

-- Drop all Foreign Keys
SELECT @sql += 'ALTER TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + 
               ' DROP CONSTRAINT ' + QUOTENAME(fk.name) + ';' + CHAR(13)
FROM sys.foreign_keys fk
JOIN sys.tables t ON fk.parent_object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @SchemaName;

PRINT @sql;
SET @sql = '';

-- Drop all Tables
SELECT @sql += 'DROP TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ';' + CHAR(13)
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @SchemaName;

PRINT @sql;
SET @sql = '';

-- Drop all Views
SELECT @sql += 'DROP VIEW ' + QUOTENAME(s.name) + '.' + QUOTENAME(v.name) + ';' + CHAR(13)
FROM sys.views v
JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE s.name = @SchemaName;

PRINT @sql;
SET @sql = '';

-- Drop all Functions
SELECT @sql += 'DROP FUNCTION ' + QUOTENAME(s.name) + '.' + QUOTENAME(f.name) + ';' + CHAR(13)
FROM sys.objects f
JOIN sys.schemas s ON f.schema_id = s.schema_id
WHERE s.name = @SchemaName AND f.type IN ('FN', 'TF', 'IF');

PRINT @sql;
SET @sql = '';

-- Drop all Procedures
SELECT @sql += 'DROP PROCEDURE ' + QUOTENAME(s.name) + '.' + QUOTENAME(p.name) + ';' + CHAR(13)
FROM sys.procedures p
JOIN sys.schemas s ON p.schema_id = s.schema_id
WHERE s.name = @SchemaName;

PRINT @sql;
SET @sql = '';

SET @sql += 'DROP SCHEMA ' + @SchemaName;

PRINT @sql;

END;--end sproc 
GO 

CREATE OR ALTER PROCEDURE [SqlXl].[ScaffoldAn_INSERT_Feature]
(
    @DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128) = 'SqlXl'
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @DomainSchemaName, @DomainTableName;
	--end param validation

	declare @SQL nvarchar(max) = '';
	declare @UserFriendlyFeatureName nvarchar(255) = '';
	declare @StagingTableName [nvarchar](128) = '';
	declare @SprocToProcessPerfectStagedData nvarchar(128) = '';
	DECLARE @ProdTablePrimaryKeyColumnName nvarchar(128) = '';
	declare @GetRowsToEdit_SelectStatement nvarchar(max) =
		SqlXl.GenerateSelectStatementToResultInOneValidRow(@DomainSchemaName, @DomainTableName);

	set @UserFriendlyFeatureName = 'Add ' + @DomainTableName + ' - Bulk Grid';

	-- Make a staging table name like 'Staging_MyTable001' for example...
	SET @StagingTableName = SqlXl.ProposeStagingTableNameForInsertFeature(@DomainTableName);
	EXEC SqlXl.ReScaffoldAStagingTable @DomainSchemaName,  -- nvarchar(128)
	                                           @DomainTableName,   -- nvarchar(128)
	                                           @StagingSchemaName, -- nvarchar(128)
	                                           @InsertOrUpdate = 'insert'     -- nvarchar(6)
	
	--Create/Refresh meta data and sample values for given table and staging table...
	EXEC SqlXl.RefreshMetaDataForTable @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.RefreshMetaDataForTable @StagingSchemaName, @StagingTableName;
	EXEC SqlXl.RefreshSampleValues @DomainSchemaName, @DomainTableName;
		
	--Lookup production table's primary key column name...
	set @ProdTablePrimaryKeyColumnName = SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName);
	
	--Create a sproc to insert from staging into production table...
	SET @SQL = 
	N'CREATE PROCEDURE ' + @DomainSchemaName + '.' + @DomainTableName + '_InsertFromStaging  
		(@RequestID nvarchar(36))
		AS
		BEGIN
			SET NOCOUNT ON;

			-- The following logic may likely be replaced with something like
			-- INSERT Persons(...) SELECT...FROM Staging_Persons;
			-- instead of using this generic TransferDataForRequestID sproc...
			EXEC [SqlXl].[TransferDataForRequestID]
				@SourceSchemaName = N''' + @StagingSchemaName + ''',
				@SourceTableName = N''' + @StagingTableName + ''',
				@DestinationSchemaName = N''' + @DomainSchemaName + ''',
				@DestinationTableName = N''' + @DomainTableName + ''',
				@ColumnsToOmit = N''ZZTemp_ID,' + @ProdTablePrimaryKeyColumnName + ''',
				--@ColumnsToOmit = N''PerhapsThePrimaryKeyColumnName,SecondColumnToOmit,ThirdColEtc''
				@RequestID = @RequestID 

			-- Capture the number of rows affected, 
			-- return success information datatable...
			SELECT IsSuccessful = ''true'', 
			RowsInserted = @@ROWCOUNT, 
			RowsUpdated = 0,
			RowsDeleted = 0;
         
			--Return empty errors listing, too...
			select Msg from #Messages;
		END';
	EXEC sp_executesql @SQL;
	
	--Note the name of sproc that was just created...
	set @SprocToProcessPerfectStagedData = @DomainTableName + '_InsertFromStaging';

	--Insert a record for this newly created feature...
	INSERT SqlXl.BulkOpFeatures
	(
	    UserFriendlyFeatureName,
		InsertUpdateDeleteOrCustom,
	    DomainSchemaName,
	    DomainTableName,
	    StagingSchemaName,
	    StagingTableName,
		GetRowsToEdit_SelectStatement,
	    SprocToProcessPerfectStagedData,
	    MenuDisplayRanking
	)
	VALUES
	(   @UserFriendlyFeatureName,    -- UserFriendlyFeatureName - nvarchar(255)
		'Insert', --InsertUpdateDeleteOrCustom nvarchar(6)
	    @DomainSchemaName,    -- DomainSchemaName - nvarchar(128)
	    @DomainTableName,    -- DomainTableName - nvarchar(128)
	    @StagingSchemaName,    -- StagingSchemaName - nvarchar(128)
	    @StagingTableName,    -- StagingTableName - nvarchar(128)
		@GetRowsToEdit_SelectStatement,
	    @SprocToProcessPerfectStagedData,    -- SprocToProcessPerfectStagedData - nvarchar(128)
	    10 -- MenuDisplayRanking - int
	);--end insert 

	--Insert UI support for any FK columns...
	INSERT INTO [SqlXl].[ColumnUIConfigurations]	
			([SchemaName]
			,[TableName]
			,[ColumnName]
			,[DropdownSelectStatement]
			,[UIHint])
	select
		[SchemaName] = @DomainSchemaName
		,[TableName] = @DomainTableName
		,[ColumnName] = Meta_Columns.ColumnName
		,[DropdownSelectStatement] = [SqlXl].[GenerateDropdownQueryFromReferencedTable](ReferencedTable)
		,[UIHint] = 'select2_client'
	from
		SqlXl.Meta_Columns
	where
		IsForeignKey = 'YES'
		and SchemaName = @DomainSchemaName
		and TableName = @DomainTableName
		AND NOT EXISTS ( --<<only insert if this column config does not already exist
			SELECT 1
			FROM [SqlXl].[ColumnUIConfigurations] existing
			WHERE existing.SchemaName = @DomainSchemaName
			AND existing.TableName = @DomainTableName
			AND existing.ColumnName = Meta_Columns.ColumnName
		)
	;--end insert-select

end; --end sproc 

GO

CREATE OR ALTER PROCEDURE [SqlXl].[ScaffoldAn_UPDATE_Feature]
(
    @DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128) = 'SqlXl'
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @DomainSchemaName, @DomainTableName;

	DECLARE @SQL NVARCHAR(MAX) = '';
	DECLARE @UserFriendlyFeatureName NVARCHAR(255) = '';
	DECLARE @StagingTableName [NVARCHAR](128) = '';
	DECLARE @SprocToProcessPerfectStagedData NVARCHAR(128) = '';
    
	SET @UserFriendlyFeatureName = 'Edit ' + @DomainTableName + ' - Find & Edit';

	-- Make a staging table name like 'Staging_MyTable001' for example...
	SET @StagingTableName = SqlXl.ProposeStagingTableNameForUpdateFeature(@DomainTableName);
	EXEC SqlXl.ReScaffoldAStagingTable @DomainSchemaName,  -- nvarchar(128)
	                                           @DomainTableName,   -- nvarchar(128)
	                                           @StagingSchemaName = N'SqlXl', -- nvarchar(128)
	                                           @InsertOrUpdate = N'update'     -- nvarchar(6)

	--Create/Refresh meta data and sample values for given table and staging table...
	EXEC SqlXl.RefreshMetaDataForTable @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.RefreshMetaDataForTable @StagingSchemaName, @StagingTableName;
	EXEC SqlXl.RefreshSampleValues @DomainSchemaName, @DomainTableName;
	
	--Create a sproc to update production table given perfect staging data...
	SET @SQL = 
	N'CREATE PROCEDURE ' + @DomainSchemaName + '.' + @DomainTableName + '_UpdateFromStaging  
		(@RequestID nvarchar(36))
		AS
		BEGIN
			SET NOCOUNT ON;

			EXEC [SqlXl].[UpdateDestinationTableFromSourceTableForRequestID]
				@SourceSchemaName = N''' + @StagingSchemaName + ''',
				@SourceTableName = N''' + @StagingTableName + ''',
				@DestinationSchemaName = N''' + @DomainSchemaName + ''',
				@DestinationTableName = N''' + @DomainTableName + ''',
				@PrimaryKeyColumnName = N''' + SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName) + ''',
				@CommaDelimitedColumnsToOmit = N''' + SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName) + ''',
				--@CommaDelimitedColumnsToOmit = N''FirstColumnToOmit,SecondColumnToOmit,ThirdColEtc''
				@RequestID = @RequestID  

			-- Capture the number of rows affected, 
			-- return success information datatable...
			SELECT IsSuccessful = ''true'', 
			RowsInserted = 0, 
			RowsUpdated = @@ROWCOUNT,
			RowsDeleted = 0;
         
			--Return empty errors listing, too...
			select Msg from #Messages;
		END';
	EXEC sp_executesql @SQL;

	--Note the name of sproc that was just created...
	set @SprocToProcessPerfectStagedData = @DomainTableName + '_UpdateFromStaging';

	--Generate a 'select * from TableName...' to get the rows to edit, 
	--...note that fk columns must be carefully handled here...
	DECLARE @GetRowsToEdit_SelectStatement NVARCHAR(MAX) =
		'/******' + nchar(10) +
		'Note: You will likely want to customize this query, per requirements.' + nchar(10) +
		'********/' + nchar(10) +
		SqlXl.[GenerateSelectStatementToSupportBulkEditingWithFKs](
			@DomainSchemaName, @DomainTableName); 
	
	--Insert a record for this newly created feature...
	INSERT SqlXl.BulkOpFeatures
	(
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
	VALUES
	(   @UserFriendlyFeatureName,    -- UserFriendlyFeatureName - nvarchar(255)
		'Update', --InsertUpdateDeleteOrCustom
	    @DomainSchemaName,    -- DomainSchemaName - nvarchar(128)
	    @DomainTableName,    -- DomainTableName - nvarchar(128)
	    @StagingSchemaName,    -- StagingSchemaName - nvarchar(128)
	    @StagingTableName,    -- StagingTableName - nvarchar(128)

		--For now, insert SAME VALUE for BOTH of these...
		--(Later, we'll check for FKs and overwrite GetRowsToChooseFrom_SelectStatement as needed)
		@GetRowsToEdit_SelectStatement, --GetRowsToChooseFrom_SelectStatement
		@GetRowsToEdit_SelectStatement, --GetRowsToEdit_SelectStatement

	    @SprocToProcessPerfectStagedData,    -- SprocToProcessPerfectStagedData - nvarchar(128)
	    20 -- MenuDisplayRanking - int
	);--end insert

	INSERT INTO [SqlXl].[ColumnUIConfigurations]
           ([SchemaName]
           ,[TableName]
           ,[ColumnName]
           ,[DropdownSelectStatement]
           ,[UIHint])
	select
		[SchemaName] = @DomainSchemaName
        ,[TableName] = @DomainTableName
        ,[ColumnName] = Meta_Columns.ColumnName
        ,[DropdownSelectStatement] = [SqlXl].[GenerateDropdownQueryFromReferencedTable](ReferencedTable)
        ,[UIHint] = 'select2_client'
	from
		SqlXl.Meta_Columns
	where
		IsForeignKey = 'YES'
		and SchemaName = @DomainSchemaName
		and TableName = @DomainTableName
		AND NOT EXISTS ( --<<only insert if this column config does not already exist
			SELECT 1
			FROM [SqlXl].[ColumnUIConfigurations] existing
			WHERE existing.SchemaName = @DomainSchemaName
			AND existing.TableName = @DomainTableName
			AND existing.ColumnName = Meta_Columns.ColumnName
		)
	;--end insert-select

	-- Create display view if table has foreign keys
	DECLARE @HasForeignKeys BIT = 0;
	DECLARE @ViewSQL NVARCHAR(MAX) = '';
	DECLARE @ViewName NVARCHAR(255) = '';

	-- Check if table has foreign keys
	IF EXISTS (
		SELECT 1 FROM SqlXl.Meta_Columns
		WHERE IsForeignKey = 'YES'
		AND SchemaName = @DomainSchemaName
		AND TableName = @DomainTableName
	)
	BEGIN
		SET @HasForeignKeys = 1;
	END

	-- If table has FKs, create the display view (only if it doesn't exist)
	IF @HasForeignKeys = 1
	BEGIN
		-- Generate view SQL using the existing function
		SET @ViewSQL = [SqlXl].[GenerateDisplayViewSQL](@DomainSchemaName, @DomainTableName);
		SET @ViewName = 'vw_' + @DomainTableName;

		-- Only create view if it doesn't already exist
		IF OBJECT_ID(@DomainSchemaName + '.' + @ViewName, 'V') IS NULL
		BEGIN
			-- Create the new view
			EXEC sp_executesql @ViewSQL;
		END

		-- OVERWRITE the GetRowsToChooseFrom_SelectStatement with
		-- "select * from vw_TableName" since we need the related FK data...
		UPDATE SqlXl.BulkOpFeatures
		SET GetRowsToChooseFrom_SelectStatement =
			'/******' + nchar(10) +
			'Note: You will likely want to customize this query, per requirements.' + nchar(10) +
			'********/' + nchar(10) +
			'select * from ' + @DomainSchemaName + '.' + @ViewName
		WHERE DomainSchemaName = @DomainSchemaName
		AND DomainTableName = @DomainTableName
		AND InsertUpdateDeleteOrCustom = 'Update';
	END

end; --end sproc 
GO

--CREATE PROCEDURE [SqlXl].[ScaffoldA_DELETE_Feature]
--(
--    @DomainSchemaName nvarchar(128),
--	@DomainTableName nvarchar(128)
--)
--AS
--BEGIN
--	--Validate params...
--	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
--	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
--	EXEC SqlXl.ErrorIfNoIntegerPrimaryKey @DomainSchemaName, @DomainTableName;

--	DECLARE @SQL NVARCHAR(MAX) = '';
--	DECLARE @UserFriendlyFeatureName NVARCHAR(255) = '';	
--	DECLARE @SprocToProcessPerfectStagedData NVARCHAR(128) = '';
    
--	--Auto-create a bulk delete feature...
--	set @UserFriendlyFeatureName = 'Remove ' + @DomainTableName + ' - Find & Remove';
	
--	--Create a sproc to delete row(s) from production table given primary key values in ZZTemp...
--	SET @SQL = 
--	N'CREATE PROCEDURE ' + @DomainSchemaName + '.' + @DomainTableName + '_DeleteRowsPerZZTemp
--		AS
--		BEGIN
--			SET NOCOUNT ON;
			
--			--********************
--			-- Note: 
--			-- Be sure to handle deletes for related table(s) HERE FIRST, 
--			-- prior to deleting from "parent" table.
--			--**************************
--			--Determine expected number of deletions...
--			declare @RequestedDeletions int = 
--			(select count(distinct ' + SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName) + ' ) from #ZZTemp);

--			-- Delete row(s) from ProductionTable based on PrimaryKeyColumn expected in #ZZTemp...
--			delete from  ' + @DomainSchemaName + '.' + @DomainTableName + '  
--			where ' + SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName) + ' in (select distinct ' + SqlXl.GetPrimaryKeyColumnName(@DomainSchemaName,@DomainTableName) + '  from #ZZTemp);

--			--Determine ACTUAL number of deletions...
--			declare @ActualDeletions int = @@ROWCOUNT;	

--			--If deletions happened as expected...
--			if @RequestedDeletions > 0 
--			and @RequestedDeletions = @ActualDeletions 
--			begin
--				-- return success information datatable...
--				SELECT IsSuccessful = ''true'', 
--				RowsInserted = 0, 
--				RowsUpdated = 0,
--				RowsDeleted = @ActualDeletions
         
--				--Return empty errors listing, too...
--				select Msg from #Messages;
--			end --end if 

--			--Else return failure...
--			else
--			begin
--				--Load an error message...
--				insert #Messages (
--				Msg)
--				select Msg = 
--				''An unexpected number of deletion(s) occurred. '' +   
--				''Requested distinct deletions: '' + convert(nvarchar, @RequestedDeletions) + 
--				'' , Actual deletions: '' + convert(nvarchar, @ActualDeletions)
--				;--end insert-select 

--				SELECT IsSuccessful = ''false'', 
--				RowsInserted = 0, 
--				RowsUpdated = 0,
--				RowsDeleted = @ActualDeletions
         
--				select Msg from #Messages;
--			end --end else 

--		END --end sproc ';
--	EXEC sp_executesql @SQL;
	
--	--Note the name of sproc that was just created...
--	set @SprocToProcessPerfectStagedData = @DomainTableName + '_DeleteRowsPerZZTemp';

--	--Insert a record for this newly created feature...
--	INSERT SqlXl.BulkOpFeatures
--	(
--	    UserFriendlyFeatureName,
--		InsertUpdateDeleteOrCustom,
--	    DomainSchemaName,
--	    DomainTableName,
--	    StagingSchemaName,
--	    StagingTableName,
--	    SprocToProcessPerfectStagedData,
--	    MenuDisplayRanking
--	)
--	VALUES
--	(   @UserFriendlyFeatureName,    -- UserFriendlyFeatureName - nvarchar(255)
--		'Delete',--InsertUpdateDeleteOrCustom
--	    @DomainSchemaName,    -- DomainSchemaName - nvarchar(128)
--	    @DomainTableName,    -- DomainTableName - nvarchar(128)
--	    'SqlXl',    -- StagingSchemaName - nvarchar(128)
--	    'NotApplicableForBulkDelete',    -- StagingTableName - nvarchar(128)
--	    @SprocToProcessPerfectStagedData,    -- SprocToProcessPerfectStagedData - nvarchar(128)
--	    30 -- MenuDisplayRanking - int
--	);--end insert 
--end; --end sproc 

--GO

CREATE OR ALTER PROCEDURE [SqlXl].[Scaffold_INSERT_UPDATE_AND_DELETE_Features]
(
    @DomainSchemaName nvarchar(128),
	@DomainTableName nvarchar(128),
	@StagingSchemaName nvarchar(128) = 'SqlXl'
)
AS
BEGIN
	--(Assume that param validation happens in the following sproc calls, 
	--so no param validation here...)

	EXEC SqlXl.ScaffoldAn_INSERT_Feature @DomainSchemaName, @DomainTableName, @StagingSchemaName;

	EXEC SqlXl.ScaffoldAn_UPDATE_Feature @DomainSchemaName, @DomainTableName, @StagingSchemaName;
	
	--EXEC SqlXl.ScaffoldA_DELETE_Feature @DomainSchemaName, @DomainTableName;
	
END; --end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[Scaffold_ALL_TABLES_InsertUpdateAndDeleteFeatures]
(
    @DomainSchemaName nvarchar(128),
	@StagingSchemaName nvarchar(128) = 'SqlXl'
)
AS
BEGIN

--Validate params...
EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;

DECLARE @DomainTableName nvarchar(128);

-- Declare a table variable to hold table names
DECLARE @TableNames TABLE (
    TableName NVARCHAR(128) NOT NULL
);

-- Insert the names of all user tables in the current database into the table variable
INSERT INTO @TableNames (TableName)
SELECT name 
FROM sys.tables 
WHERE type = 'U' -- U = User table
and schema_id = SCHEMA_ID(@DomainSchemaName) 
--Optionally filter by table names...
--and name in ('MyTable001','MyTable002','etc')
;--end insert-select 

-- Define the cursor
DECLARE table_cursor CURSOR FOR 
SELECT TableName FROM @TableNames;

-- Open the cursor
OPEN table_cursor;

-- Fetch the first row from the cursor
FETCH NEXT FROM table_cursor INTO @DomainTableName;

-- Loop through the rows
WHILE @@FETCH_STATUS = 0
BEGIN
	EXEC SqlXl.Scaffold_INSERT_UPDATE_AND_DELETE_Features @DomainSchemaName, @DomainTableName, @StagingSchemaName;
	
    -- Fetch the next row
    FETCH NEXT FROM table_cursor INTO @DomainTableName;
END

-- Close the cursor
CLOSE table_cursor;

-- Deallocate the cursor
DEALLOCATE table_cursor;
	
END; --end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ListColumNamesToAttemptToUpdateOnSingleValidStagingRow]
(	/*When attempting a series of statements like: 
	update Staging_Persons_ForUpdates
		set SomeColumn = (select SomeValueFromZZTemp from #ZZTemp where ZZTemp_ID = @ID);
	...this sproc lists the columns to 
	try for the 'SomeColumn' above...****/
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
	@InsertUpdateDeleteOrCustom nvarchar(6)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	--Ensure that this is Insert, Update, Delete or Custom...
	if @InsertUpdateDeleteOrCustom not in ('Insert','Update','Delete','Custom')
	begin
		RAISERROR('BulkOpFeatures.InsertUpdateDeleteOrCustom value must be ''Insert'', ''Update'', ''Delete'' or ''Custom''.', 16, 1);
        RETURN -1;
	end 
	--end param validation

	SET NOCOUNT ON;

	--Get pk col name for domain table...
	declare @DomainTblPrimaryKeyColumnName nvarchar(128) = 
		SqlXl.GetPrimaryKeyColumnName (@DomainSchemaName, @DomainTableName);

	--If operation type is Update, then
	--OMIT the DomainTable.PrimaryKey column...
	--(DomainTable.PrimaryKey should NOT exist
	-- in the staging table for an update operation!
	-- Logic below would error if this column
	-- were not omitted!)
	if @InsertUpdateDeleteOrCustom = 'Update'
	begin
		SELECT c.name AS ColumnName
		FROM tempdb.sys.columns c
		INNER JOIN tempdb.sys.objects o
			ON c.object_id = o.object_id
		WHERE c.object_id = OBJECT_ID('tempdb..#ZZTemp') -- Session-specific temp table only
		  AND o.type = 'U'          -- 'U' ensures it is a user table
		  AND c.name <> 'ZZTemp_ID' -- Exclude specific column
		  and c.name <> @DomainTblPrimaryKeyColumnName
		;--end select
	end --end if

	--else return all columns except ZZTemp_ID...
	else
	begin
		SELECT c.name AS ColumnName
		FROM tempdb.sys.columns c
		INNER JOIN tempdb.sys.objects o
			ON c.object_id = o.object_id
		WHERE c.object_id = OBJECT_ID('tempdb..#ZZTemp') -- Session-specific temp table only
		  AND o.type = 'U'          -- 'U' ensures it is a user table
		  AND c.name <> 'ZZTemp_ID' -- Exclude specific column
	end --end else 
end --end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[ValidateZZTemp_For_INSERT_FEATURE_ForUniqueConstraintsReturnErrors]
(
	@DomainSchemaName NVARCHAR(128),
    @DomainTableName NVARCHAR(128),
	@StagingSchemaName NVARCHAR(128),
    @StagingTableName NVARCHAR(128),
	@StopAfterThisManyErrors INT = 10,  -- Default value 
	@RequestID nvarchar(36)
)
AS
BEGIN
	--Validate params...
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;
	EXEC SqlXl.ErrorIfSchemaDoesNotExist @StagingSchemaName;
	EXEC SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;
	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;
	--end param validation

	SET NOCOUNT ON;

	declare @SQL nvarchar(max);
	declare @PrimaryKeyColumnName nvarchar(128);

	-- Declare a table variable to store EACH 
	-- separate unique key constraint 
	-- ColumnNames value will one or many comma-delimited column name(s)...
	DECLARE @ConstraintDetails TABLE (
		ColumnNames NVARCHAR(MAX)
	);
	insert @ConstraintDetails (ColumnNames)
	exec [SqlXl].[ListUniqueKeyConstraintsForTable]
		@SchemaName = @DomainSchemaName,
		@TableName = @DomainTableName,
		@IncludePrimaryKeyColumnInResults = 0,
		@IncludeIdentityAutoNumberColumnInResults = 0,
		@IncludeMultiColumnUniqueKeyConstraints = 1
	;--end insert-sproc-results

	--Variable to hold one-to-many comma-delimited
	--column names for unique constraints...
	DECLARE @ColumnNames NVARCHAR(MAX);	
	
	DECLARE ConstraintCursor CURSOR FOR
	SELECT ColumnNames
	FROM @ConstraintDetails;

	OPEN ConstraintCursor;
	FETCH NEXT FROM ConstraintCursor INTO @ColumnNames;
	WHILE @@FETCH_STATUS = 0
	BEGIN
		--PRINT @ColumnNames;

		--Generate sql that makes an error listing for all uniqueness violations...
		set @SQL =
			SqlXl.GenerateDuplicateCheckSQL(
				@DomainSchemaName,
				@DomainTableName,
				@ColumnNames,
				NULL); -- NULL for INSERT - don't exclude any rows

		-- Execute the dynamic SQL
		INSERT #Messages(Msg)
		EXEC sp_executesql @SQL; 
	
		--Stop and return if error max is reached...
		if (select count(*) from #Messages) >= @StopAfterThisManyErrors
		begin
			--Clean staging table...
			EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;

			-- Exit cursor cleanly
			CLOSE ConstraintCursor;
			DEALLOCATE ConstraintCursor;
			
			RETURN 0;
		end --end if 
		
		FETCH NEXT FROM ConstraintCursor INTO @ColumnNames;
	END --end cursor loop

	CLOSE ConstraintCursor;
	DEALLOCATE ConstraintCursor;

	--Ensure an empty staging table...
	SET @SQL = 'delete from ' + @StagingSchemaName + '.' + @StagingTableName + ' ;';
	EXEC sp_executesql @SQL;

	RETURN 0;
end --end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[PurgeStagingValidateZZTempAndReturnErrors]
(
	@BulkOpFeaturesID int,
	@RequestID nvarchar(36),
	@StopAfterThisManyErrors INT,
	@Debug BIT = 0 -- Default to no debugging
)
AS
BEGIN
	--debug-related: exec SqlXl.DebugLogInsert @RequestID, '','Sproc started...';

	--validate params...
	-- Validate that exists for the given @BulkOpsFeaturesID
	IF NOT EXISTS (
		SELECT 1
		FROM [SqlXl].[BulkOpFeatures]
		WHERE [ID] = @BulkOpFeaturesID
	)
	BEGIN
		RAISERROR('No matching row found in BulkOpFeatures for the given @BulkOpsFeaturesID.', 16, 1);
		RETURN -1; -- Halt execution
	END

	EXEC SqlXl.ErrorIfInvalidGuid @RequestID;

	--Avoid runaway processing...
	if @StopAfterThisManyErrors > 1000
	begin
		set @StopAfterThisManyErrors = 1000
	end 
	--end param validation

	-- Declare the variables
	DECLARE @StagingSchemaName NVARCHAR(128);
	DECLARE @StagingTableName NVARCHAR(128);
	DECLARE @DomainSchemaName NVARCHAR(128);
	DECLARE @DomainTableName NVARCHAR(128);
	DECLARE @DomainSprocNameToProcessDataFromStagingTable NVARCHAR(128);
	declare @InsertUpdateDeleteOrCustom nvarchar(6);

	-- Fetch values into variables
	SELECT 
		@StagingSchemaName = [StagingSchemaName],
		@StagingTableName = [StagingTableName],
		@DomainSchemaName = [DomainSchemaName],
		@DomainTableName = [DomainTableName],
		@InsertUpdateDeleteOrCustom = [InsertUpdateDeleteOrCustom],
		@DomainSprocNameToProcessDataFromStagingTable = [SprocToProcessPerfectStagedData]
	FROM [SqlXl].[BulkOpFeatures]
	WHERE [ID] = @BulkOpFeaturesID;

	SET NOCOUNT ON;
		
	IF @Debug = 1
    BEGIN
		-- Compile the T-SQL statement for debugging
		DECLARE @DebugSQL NVARCHAR(MAX);

		SET @DebugSQL = N'EXEC [SqlXl].[PurgeStagingValidateZZTempAndReturnErrors] ' +
			N'@DomainSchemaName = N''' + REPLACE(@DomainSchemaName, '''', '''''') + N''', ' +
			N'@DomainTableName = N''' + REPLACE(@DomainTableName, '''', '''''') + N''', ' +
			N'@StagingSchemaName = N''' + REPLACE(@StagingSchemaName, '''', '''''') + N''', ' +
			N'@StagingTableName = N''' + REPLACE(@StagingTableName, '''', '''''') + N''', ' +
			N'@InsertUpdateDeleteOrCustom = N''' + REPLACE(@InsertUpdateDeleteOrCustom, '''', '''''') + N''', ' +
			N'@StopAfterThisManyErrors = ' + CAST(@StopAfterThisManyErrors AS NVARCHAR) + N', ' +
			N'@RequestID = N''' + REPLACE(@RequestID, '''', '''''') + N''', ' +
			N'@Debug = ' + CAST(@Debug AS NVARCHAR) + N';';

		-- Insert debug information into DebugLog...
		exec SqlXl.DebugLogInsert @RequestID, @DebugSQL, 
			'Sproc: [SqlXl].[PurgeStagingValidateZZTempAndReturnErrors] started...';
    END --end debug
	
    DECLARE @SQL NVARCHAR(MAX);

	--Delete all staging rows for this RequestID...
	EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;

	--debug-related: exec SqlXl.DebugLogInsert @RequestID, '','about to insert single valid row to staging...';
    
	/*// Load a single valid row of sample data to the staging table.
	// This row is what enables row-by-row, column-by-column
	// data validation later...*/
	exec SqlXl.InsertSingleValidSampleRowToStagingGivenTheRealProdTableName 
		@DomainSchemaName, @DomainTableName, @StagingSchemaName, @StagingTableName, @RequestID ;

	 -- Check if exactly one row was inserted
    IF @@ROWCOUNT != 1
    BEGIN
        RAISERROR('Sproc InsertSingleValidSampleRowToStagingGivenTheRealProdTableName failed to insert exactly one row.', 16, 1);
        RETURN -1;  -- Return -1 to indicate an error condition
    END

	--debug-related: exec SqlXl.DebugLogInsert @RequestID, '','after verifying one single row was inserted';

	DECLARE @ResultMessage NVARCHAR(255);
    DECLARE @ReturnStatus INT;

    -- Declare variables for the row cursor
    DECLARE @ZZTempID int

    -- Declare the row cursor
    DECLARE ZZTempCursor CURSOR FOR
    SELECT ZZTemp_ID FROM #ZZTemp

    -- Open the row cursor
    OPEN ZZTempCursor

    -- Fetch the first row
    FETCH NEXT FROM ZZTempCursor INTO @ZZTempID

    -- Loop through all rows
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Print the current row's ZZTemp_ID value
        --PRINT 'Row ZZTemp_ID: ' + CAST(@ZZTempID AS NVARCHAR(10))
		--debug-related: exec SqlXl.DebugLogInsert @RequestID, '','Entered ZZTempCursor...';

		-- Get a list of all columns in #ZZTemp
		-- for this data validation...
		declare @TblColumnNames table
			(ColumnName nvarchar(128));

		-- Clear table variable from previous iterations (table variables persist across loop iterations!)
		DELETE FROM @TblColumnNames;

		Insert @TblColumnNames (ColumnName)
		exec [SqlXl].[ListColumNamesToAttemptToUpdateOnSingleValidStagingRow]
				@DomainSchemaName, @DomainTableName, @StagingSchemaName, @InsertUpdateDeleteOrCustom;

        -- Declare variables for the column cursor
        DECLARE @ColumnName NVARCHAR(255)

        -- Declare the column cursor
        DECLARE ColumnCursor CURSOR FOR
        SELECT ColumnName
        FROM @TblColumnNames
        
        -- Open the column cursor
        OPEN ColumnCursor

        -- Fetch the first column name
        FETCH NEXT FROM ColumnCursor INTO @ColumnName

        -- Loop through all column names for the current row
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- Print the current column name
            --PRINT 'Column Name: ' + @ColumnName
			--debug-related: exec SqlXl.DebugLogInsert @RequestID, '','Entered column cursor...';
			--debug-related: exec SqlXl.DebugLogInsert @RequestID,'Validating column...',@ColumnName;

            EXEC @ReturnStatus = 
                SqlXl.AttemptToUpdateOneSingleColumnInTheDestinationTableFromTheSourceTableAndReturnMessage 
					@SourceSchemaName = @StagingSchemaName,
                    @SourceTableName = '#ZZTemp' ,
                    @SourceTablePrimaryKeyColumnName = 'ZZTemp_ID' , 
                    @SourceTablePrimaryKeyValue = @ZZTempID ,
					@DestinationSchemaName = @StagingSchemaName,
                    @DestinationTableName = @StagingTableName ,
                    @ColumnNameToUpdate = @ColumnName ,
                    @ResultMessage = @ResultMessage output  
            ;--end exec 

			--debug-related: exec SqlXl.DebugLogInsert @RequestID,'@ResultMessage:',@ResultMessage;

            --If update NOT successful...
            if @ResultMessage <> 'Success'
            BEGIN
                --Append error to #Messages table...
                insert #Messages(Msg)
                VALUES (
                    'Row ' +  CAST(@ZZTempID AS NVARCHAR(10)) + 
                    ', ' + @ColumnName + ': ' + @ResultMessage )
                ;--end insert 

				--debug-related: exec SqlXl.DebugLogInsert @RequestID, 'error message inserted for:', @ColumnName;
				
				--Check error count threshold...
				if (select count(*) from #Messages) >= @StopAfterThisManyErrors
				begin
					--Delete all staging rows for this RequestID...
					EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;
					
					-- Exit from both cursors cleanly
					CLOSE ColumnCursor;
					DEALLOCATE ColumnCursor;
					CLOSE ZZTempCursor;
					DEALLOCATE ZZTempCursor;

					-- Return the table variable and halt further processing
					select Msg from #Messages;
					RETURN 0;
				end --end if 
            end --end if 

            --print @ResultMessage;
            --PRINT 'Return Status: ' + CAST(@ReturnStatus AS NVARCHAR(10));

            -- Fetch the next column name
            FETCH NEXT FROM ColumnCursor INTO @ColumnName
        END --end ColumnCursor

        -- Close and deallocate the column cursor
        CLOSE ColumnCursor
        DEALLOCATE ColumnCursor

        -- Fetch the next row
        FETCH NEXT FROM ZZTempCursor INTO @ZZTempID
    END --end ZZTempCursor

    -- Close and deallocate the row cursor
    CLOSE ZZTempCursor
    DEALLOCATE ZZTempCursor

	--If not an insert or update, then exit now...
	if @InsertUpdateDeleteOrCustom <> 'Insert'
		and @InsertUpdateDeleteOrCustom <> 'Update'
	begin
		--Delete all staging rows for this RequestID...
		EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;
		
		-- Return the table variable and halt further processing
		select Msg from #Messages;
		RETURN 0;
	end --end if 

	--Check for uniqueness differently, based on inserts vs updates...
	if @InsertUpdateDeleteOrCustom = 'Insert'
	begin
		exec [SqlXl].ValidateZZTemp_For_INSERT_FEATURE_ForUniqueConstraintsReturnErrors 
			@DomainSchemaName, @DomainTableName, @StagingSchemaName, @StagingTableName,	
			@StopAfterThisManyErrors, @RequestID ;
	end --end if 

	if @InsertUpdateDeleteOrCustom = 'Update'
	begin
		exec [SqlXl].[ValidateZZTemp_For_UPDATE_FEATURE_ForUniqueConstraintsReturnErrors]
			@DomainSchemaName, @DomainTableName, @StagingSchemaName, 
			@StagingTableName, @StopAfterThisManyErrors, @RequestID ;
	end --end if

	--Delete all staging rows for this RequestID...
	EXEC [SqlXl].[PurgeStagingForRequestID] @StagingTableName, @RequestID;

    --Return all error messages
    --(empty table if no errors).

    select Msg from #Messages;
END; --end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].SavedQueryInsert
    @SavedQueryName NVARCHAR(255),
    @SavedQueryText NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [SqlXl].SavedQueries (SavedQueryName, SavedQueryText)
    VALUES (@SavedQueryName, @SavedQueryText);
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].SavedQueryUpdate
    @ID INT,
    @SavedQueryText NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [SqlXl].SavedQueries
    SET SavedQueryText = @SavedQueryText,
        LastModifiedOnDate = SYSDATETIME()
    WHERE ID = @ID;
END;
GO

CREATE OR ALTER PROCEDURE [SqlXl].[SavedQueries_CreateSamples_ALL_TABLES]
(
@DomainSchemaName nvarchar(128)
)
AS
BEGIN

--Validate params...
EXEC SqlXl.ErrorIfSchemaDoesNotExist @DomainSchemaName;

DECLARE @DomainTableName nvarchar(128);

-- Declare a table variable to hold table names
DECLARE @TableNames TABLE (
TableName NVARCHAR(128) NOT NULL
);

-- Insert the names of all user tables in the current database into the table variable
INSERT INTO @TableNames (TableName)
SELECT name
FROM sys.tables
WHERE type = 'U' -- U = User table
and schema_id = SCHEMA_ID(@DomainSchemaName)
--Optionally filter by table names...
--and name in ('MyTable001','MyTable002','etc')
;--end insert-select

DECLARE @SavedQueryName NVARCHAR(255);
DECLARE @SavedQueryText NVARCHAR(MAX);

-- Define the cursor
DECLARE table_cursor CURSOR FOR
SELECT TableName FROM @TableNames;

-- Open the cursor
OPEN table_cursor;

-- Fetch the first row from the cursor
FETCH NEXT FROM table_cursor INTO @DomainTableName;

-- Loop through the rows
WHILE @@FETCH_STATUS = 0
BEGIN
	-- Build the concatenated values first
	SET @SavedQueryName = @DomainTableName + ' - list top 1000 rows';
	SET @SavedQueryText = 'SELECT TOP 1000 * FROM [' + @DomainSchemaName + '].[' + @DomainTableName + ']';

	-- Execute the stored procedure
	EXEC [SqlXl].SavedQueryInsert 
		@SavedQueryName = @SavedQueryName,
		@SavedQueryText = @SavedQueryText;
	
-- Fetch the next row
FETCH NEXT FROM table_cursor INTO @DomainTableName;
END

-- Close the cursor
CLOSE table_cursor;

-- Deallocate the cursor
DEALLOCATE table_cursor;

END; --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ValidateThenRunSelectQueryReturnJsonMetadataAndData]
    @Query NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON; -- Suppress "rows affected" messages

	/*********
	IMPORTANT: This spoc appears crazy and exotic
	but it's actually very rare, clever engineering.
	This sproc manages to only run the given 
	query one single time, even though it does 
	return duplicate data.  It returns a regular 
	result as DataTable and then an 
	additional result that gives dynamically generated
	Metadata right alongside the data as json.
	This one is brilliant but unless you need all 
	of this returned in one single db call, then 
	consider using a simpler alternative, like 
	SqlXl.ValidateThenRunSelectQuery.
	*************************************************/

	-- Normalize the input
    SET @Query = REPLACE(REPLACE(LTRIM(RTRIM(@Query)), CHAR(13), ''), CHAR(10), '');

	--strip-out multi-line comments...
	Set @Query = SqlXl.RemoveMultiLineComments(@Query);

    -- Validate that the query starts with SELECT
    IF NOT EXISTS (SELECT 1 WHERE @Query LIKE 'SELECT%' AND NOT @Query LIKE '%--%' AND NOT @Query LIKE '%;%')
    BEGIN
        RAISERROR('Only SELECT statements are allowed.', 16, 1);
        RETURN;
    END

	-- Check for forbidden keywords with PATINDEX
	-- Must allow for a column named "RecordUpdatedOn" for example,
	-- so we can't just blindly look for the keywords only...
	IF PATINDEX('%[^a-zA-Z0-9]INSERT[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]UPDATE[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]DELETE[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]DROP[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]ALTER[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]EXEC[^a-zA-Z0-9]%', @Query) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]MERGE[^a-zA-Z0-9]%', @Query) > 0
	BEGIN
		RAISERROR('Query contains forbidden keywords.', 16, 1);
		RETURN;
	END

    -- Syntax validation using SET PARSEONLY
	declare @SyntaxCheckOnly as nvarchar(max) 
		= 'SET NOCOUNT ON;' + 
			@Query + ' OPTION (FAST 1)'; -- Lightweight execution plan
    BEGIN TRY
		-- Attempt to parse the query (no results returned)
		EXEC sp_executesql @SyntaxCheckOnly 
	END TRY
	BEGIN CATCH
		RAISERROR('Query syntax is invalid: %s', 16, 1);
		RETURN;
	END CATCH

    -- Validate schema using sys.dm_exec_describe_first_result_set
    BEGIN TRY
        IF NOT EXISTS (
            SELECT 1
            FROM sys.dm_exec_describe_first_result_set(@Query, NULL, 1)
            WHERE name IS NOT NULL
        )
        BEGIN
            RAISERROR('Query references invalid or nonexistent objects.', 16, 1);
            RETURN;
        END
    END TRY
    BEGIN CATCH
        RAISERROR('Schema validation failed: %s', 16, 1);
        RETURN;
    END CATCH

    -- Metadata extraction
    DECLARE @Metadata NVARCHAR(MAX);
    BEGIN TRY
        -- sys.dm_exec_describe_first_result_set has a quirk where it returns duplicate
        -- column metadata for bracket-delimited aliases with pipe syntax.
        -- Example: [DepartmentId|Department Id] returns BOTH "DepartmentId|Department Id" AND "DepartmentId"
        -- We deduplicate by keeping only the first occurrence (which is the aliased version).
        SET @Metadata = (
            SELECT
                name AS ColumnName,
                system_type_name AS DataType,
                is_nullable AS IsNullable
            FROM (
                SELECT
                    name,
                    system_type_name,
                    is_nullable,
                    column_ordinal,
                    -- Extract base column name (part before pipe, or full name if no pipe)
                    CASE
                        WHEN CHARINDEX('|', name) > 0
                        THEN LEFT(name, CHARINDEX('|', name) - 1)
                        ELSE name
                    END AS BaseColumnName,
                    -- Prefer rows with pipe syntax (longer names come first)
                    ROW_NUMBER() OVER (
                        PARTITION BY
                            CASE
                                WHEN CHARINDEX('|', name) > 0
                                THEN LEFT(name, CHARINDEX('|', name) - 1)
                                ELSE name
                            END
                        ORDER BY LEN(name) DESC
                    ) AS RowNum
                FROM sys.dm_exec_describe_first_result_set(@Query, NULL, 1)
                WHERE name IS NOT NULL
            ) AS Deduped
            WHERE RowNum = 1
            ORDER BY column_ordinal  -- Preserve original SQL column order
            FOR JSON AUTO
        );
    END TRY
    BEGIN CATCH
        RAISERROR('Failed to retrieve metadata: %s', 16, 1);
        RETURN;
    END CATCH

	-- Execute the query and retrieve data
    DECLARE @Data NVARCHAR(MAX);
    BEGIN TRY
        -- Execute the query with FOR JSON AUTO and store the result in @Data
		-- Temporary table to capture JSON result
		CREATE TABLE #JsonResult (JsonData NVARCHAR(MAX));

		-- Execute the query and store the result in the temporary table
		-- Wrap the dynamic query to suppress direct output
        DECLARE @DynamicQuery NVARCHAR(MAX) = 
            'INSERT INTO #JsonResult (JsonData) ' +
            'SELECT JsonOutput FROM '+ 
			'(SELECT * FROM (' + @Query + ') AS InnerQuery FOR JSON AUTO) AS JsonSubquery(JsonOutput);';
		exec sp_executesql @DynamicQuery;

		-- Retrieve the JSON result from the temporary table
        SELECT @Data = JsonData FROM #JsonResult;

		-- Clean up
		DROP TABLE #JsonResult;
    END TRY
    BEGIN CATCH
        RAISERROR('Execution failed: %s', 16, 1);
        RETURN;
    END CATCH

    -- Combine Metadata and Data into a single JSON object
    DECLARE @FinalResult NVARCHAR(MAX);
    SET @FinalResult = CONCAT('{"Metadata":', @Metadata, ',"Data":', @Data, '}');

    -- Return the combined JSON object
    SELECT @FinalResult as JsonResults;
END --end sproc 
go 

-- GetMenuItems sproc removed: was a SlappFramework web UI navigation helper
-- (ControllerName/ActionName/QueryString) with no relevance to the SqlXL CLI.

CREATE OR ALTER PROCEDURE [SqlXl].[GetDropdownOptionsForFeature]
    @FeatureID INT,
    @ReturnAsJson BIT = 0  --0 = DataTable, 1 = JSON
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Get staging table info from BulkOpFeatures
    DECLARE @StagingSchemaName NVARCHAR(128), 
            @StagingTableName NVARCHAR(128);
            
    SELECT 
        @StagingSchemaName = StagingSchemaName,
        @StagingTableName = StagingTableName
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;
    
    -- Validate that we found the feature
    IF @StagingSchemaName IS NULL OR @StagingTableName IS NULL
    BEGIN
        RAISERROR('Feature ID %d not found in BulkOpFeatures', 16, 1, @FeatureID);
        RETURN;
    END;
    
    -- Build combined dropdown query using cursor
    DECLARE @ColumnName NVARCHAR(128);
    DECLARE @DropdownSQL NVARCHAR(MAX);
    DECLARE @UnionSQL NVARCHAR(MAX) = '';
    
    -- Cursor through FK columns that have dropdown configurations
    DECLARE fk_cursor CURSOR FOR
        SELECT mc.ColumnName, cuc.DropdownSelectStatement
        FROM [SqlXl].[Meta_Columns] mc
        INNER JOIN [SqlXl].[ColumnUIConfigurations] cuc 
            ON mc.ColumnName = cuc.ColumnName 
        WHERE mc.SchemaName = @StagingSchemaName 
          AND mc.TableName = @StagingTableName
          AND mc.IsForeignKey = 'YES'
          AND cuc.DropdownSelectStatement IS NOT NULL
          AND LTRIM(RTRIM(cuc.DropdownSelectStatement)) != '';
    
    OPEN fk_cursor;
    FETCH NEXT FROM fk_cursor INTO @ColumnName, @DropdownSQL;
    
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Add ForColumn identifier to each dropdown query
        IF @UnionSQL != ''
            SET @UnionSQL = @UnionSQL + ' UNION ';
        
        SET @UnionSQL = @UnionSQL + 
            'SELECT ''' + @ColumnName + ''' as ForColumn, CONVERT(NVARCHAR(MAX), [Value]) as [Value], CONVERT(NVARCHAR(MAX), [Text]) as [Text] FROM (' +
            @DropdownSQL + ') as options';
        
        FETCH NEXT FROM fk_cursor INTO @ColumnName, @DropdownSQL;
    END;
    
    CLOSE fk_cursor;
    DEALLOCATE fk_cursor;
    
    -- Execute the combined query (if any FK columns exist)
    IF @UnionSQL != ''
    BEGIN
        -- Append JSON formatting if requested
        IF @ReturnAsJson = 1
            SET @UnionSQL = @UnionSQL + ' FOR JSON AUTO';
        
        EXEC sp_executesql @UnionSQL;
    END
    ELSE
    BEGIN
        -- Return empty result set with correct structure when no FK columns
        IF @ReturnAsJson = 1
        BEGIN
            SELECT '[]' AS JsonResult; -- Empty JSON array
        END
        ELSE
        BEGIN
            SELECT 
                CAST(NULL AS NVARCHAR(128)) as ForColumn, 
                CAST(NULL AS NVARCHAR(MAX)) as [Value], 
                CAST(NULL AS NVARCHAR(MAX)) as [Text] 
            WHERE 1=0;
        END
    END;
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GetRowsToChooseFrom]
	@FeatureID int 
AS
BEGIN
    SET NOCOUNT ON;
	
	--Lookup select statement...
	declare @Sql nvarchar(max) =
		(select GetRowsToChooseFrom_SelectStatement
			from SqlXl.BulkOpFeatures
			where ID = @FeatureID);

	--Remove any comments the sql code may have...
	set @Sql = SqlXl.RemoveMultiLineComments(@Sql);

	--Run query and return the metadata along with results...
	exec SqlXl.ValidateThenRunSelectQueryReturnJsonMetadataAndData @Sql;

	-- Integrate dropdown options for FKs here...
	exec SqlXl.GetDropdownOptionsForFeature @FeatureID, 1;--1=return as json

END --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GetMeta_ColumnsForTableAsJson]
	@FeatureID int 
AS
BEGIN
    -- Declare variables to store DomainSchemaName and DomainTableName
    DECLARE @DomainSchemaName NVARCHAR(128), 
            @DomainTableName NVARCHAR(128);

    -- Fetch the schema and table name for the given FeatureID
    SELECT 
        @DomainSchemaName = DomainSchemaName,
        @DomainTableName = DomainTableName
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

	exec SqlXl.ErrorIfTableDoesNotExist @DomainSchemaName, @DomainTableName;

    -- Return the corresponding columns as JSON
    SELECT 
        SchemaName,
        TableName,
        ColumnName,
        SqlDataType,
        IsNullable,
        MaxLengthForString,
        IsPrimaryKey,
        IsForeignKey,
        ReferencedTable,
        ReferencedColumn,

		--Omit these next 2 cols for now...
        --ValidValueSelectStatement,
        --InvalidValueSelectStatement,

        ValidSampleValue,
        InvalidSampleValue
    FROM [SqlXl].[Meta_Columns]
    WHERE SchemaName = @DomainSchemaName AND TableName = @DomainTableName
    FOR JSON AUTO, INCLUDE_NULL_VALUES;
END --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[ValidateThenRunSelectStatement]
    @SelectStatement NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON; -- Suppress "rows affected" messages

	-- Normalize the input
    SET @SelectStatement = REPLACE(REPLACE(LTRIM(RTRIM(@SelectStatement)), CHAR(13), ''), CHAR(10), '');

	--strip-out multi-line comments...
	Set @SelectStatement = SqlXl.RemoveMultiLineComments(@SelectStatement);

    -- Validate that the query starts with SELECT
    IF NOT EXISTS (SELECT 1 WHERE @SelectStatement LIKE 'SELECT%' AND NOT @SelectStatement LIKE '%--%' AND NOT @SelectStatement LIKE '%;%')
    BEGIN
        RAISERROR('Only SELECT statements are allowed.', 16, 1);
        RETURN;
    END

	-- Check for forbidden keywords with PATINDEX
	IF PATINDEX('%[^a-zA-Z0-9]INSERT[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]UPDATE[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]DELETE[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]DROP[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]ALTER[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]EXEC[^a-zA-Z0-9]%', @SelectStatement) > 0 OR
	   PATINDEX('%[^a-zA-Z0-9]MERGE[^a-zA-Z0-9]%', @SelectStatement) > 0
	BEGIN
		RAISERROR('Query contains forbidden keywords.', 16, 1);
		RETURN;
	END

    -- Syntax validation using SET PARSEONLY
    BEGIN TRY
        EXEC ('SET PARSEONLY ON; ' + @SelectStatement + '; SET PARSEONLY OFF;');
    END TRY
    BEGIN CATCH
        RAISERROR('Query syntax is invalid.', 16, 1);
        RETURN;
    END CATCH

    -- Validate schema using sys.dm_exec_describe_first_result_set
    BEGIN TRY
        IF NOT EXISTS (
            SELECT 1
            FROM sys.dm_exec_describe_first_result_set(@SelectStatement, NULL, 1)
            WHERE name IS NOT NULL
        )
        BEGIN
            RAISERROR('Query references invalid or nonexistent objects.', 16, 1);
            RETURN;
        END
    END TRY
    BEGIN CATCH
        RAISERROR('Schema validation failed.', 16, 1);
        RETURN;
    END CATCH

	--IMPORTANT: following was NOT needed to get a result returned...
    -- Execute the query and retrieve data
    --EXEC sp_executesql @SelectStatement;
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GetFormStarterData]
	@FeatureID int 
AS
BEGIN
    SET NOCOUNT ON;

	--Fetch from BulkOpFeatures, given FeatureID...
    DECLARE @StagingSchemaName NVARCHAR(128), 
            @StagingTableName NVARCHAR(128);
	declare @GetRowsToEdit_SelectStatement nvarchar(max);
    SELECT
		@StagingSchemaName = StagingSchemaName,
		@StagingTableName = StagingTableName,
		@GetRowsToEdit_SelectStatement = GetRowsToEdit_SelectStatement
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

	--Sanity-check result...
	exec SqlXl.ErrorIfTableDoesNotExist @StagingSchemaName, @StagingTableName;

    -- First result to return is from Meta_Columns...
    SELECT 
        ColumnName,
        SqlDataType,
        IsNullable,
        MaxLengthForString,
        IsPrimaryKey,
        IsForeignKey,
        ReferencedTable,
        ReferencedColumn,

		--Omit these next 2 cols for now...
        --ValidValueSelectStatement,
        --InvalidValueSelectStatement,

        ValidSampleValue,
        InvalidSampleValue
    FROM [SqlXl].[Meta_Columns]
    WHERE SchemaName = @StagingSchemaName 
			AND TableName = @StagingTableName
	;--end select 

	--The second result set is what results from the
	--"GetRowsToEdit_SelectStatement" - which is intended to
	--hold default values and optional label overrides...

	--Remove any comments the sql code may have...
	set @GetRowsToEdit_SelectStatement = SqlXl.RemoveMultiLineComments(@GetRowsToEdit_SelectStatement);

	--Run and return results from GetRowsToEdit_SelectStatement statement...
	exec SqlXl.ValidateThenRunSelectStatement @GetRowsToEdit_SelectStatement;

	-- After existing logic (metadata and default values)
	-- Add third result set:
	EXEC [SqlXl].[GetDropdownOptionsForFeature] @FeatureID;

	/* Note: end result should end up like the following...
	Table[0]: Metadata 
	Table[1]: Default values 
	Table[2]: All dropdown options 
	sql-- Table[2] structure EXAMPLE...
	ForColumn     | Value | Text
	DepartmentID  | 1     | 1 Engineering
	DepartmentID  | 2     | 2 Marketing  
	DepartmentID  | 3     | 3 Sales
	LocationID    | 101   | 101 New York Office
	LocationID    | 102   | 102 Chicago Office
	ManagerID     | 1001  | 1001 John Smith
	ManagerID     | 1002  | 1002 Jane Doe
	...
	C#...
	var metadata = dataSet.Tables[0];
	var defaultValues = dataSet.Tables[1]; 
	var allDropdownOptions = dataSet.Tables[2];

	// Get options for a specific FK column
	var deptOptions = allDropdownOptions.AsEnumerable()
		.Where(row => (string)row["ForColumn"] == "DepartmentID")
		.Select(row => new { 
			Value = row["Value"], 
			Text = (string)row["Text"] 
    });
	*****************************************/
END --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[PrintDebugScript]
	@BulkOpsFeatureID int,
	@SchemaName NVARCHAR(128),
    @ProcedureName NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ObjectID INT;
    DECLARE @ParamDefinition NVARCHAR(MAX);
    DECLARE @ProcedureCode NVARCHAR(MAX);
    DECLARE @DebugScript NVARCHAR(MAX);

    -- Get Object ID of the procedure
    SET @ObjectID = OBJECT_ID(QUOTENAME(@SchemaName) + '.' + QUOTENAME(@ProcedureName));

    IF @ObjectID IS NULL
    BEGIN
        PRINT 'Procedure not found.';
        RETURN;
    END

	print '-- Generated Debug Script for ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@ProcedureName) + CHAR(13) + CHAR(10);
	print '';
	print 'Note: will have to manually remove any return statements in the debug script.';
	print '';

	--Generate code to initialize temp tables...
	print [SqlXl].[GenerateDebugStarter]();

    -- Get parameter declarations
    SELECT @ParamDefinition = STRING_AGG(
        '--DECLARE ' + p.name + ' ' +
        TYPE_NAME(p.system_type_id) +
        CASE
            WHEN p.max_length > 0 AND p.system_type_id IN (167, 231) THEN '(' + CAST(p.max_length AS NVARCHAR) + ')'
            WHEN p.precision > 0 THEN '(' + CAST(p.precision AS NVARCHAR) + ',' + CAST(p.scale AS NVARCHAR) + ')'
            ELSE ''
        END +
        ' = NULL; -- Set your value here'
    , CHAR(13) + CHAR(10))
    FROM sys.parameters p
    WHERE p.object_id = @ObjectID;

    -- Get procedure definition
    SELECT @ProcedureCode = definition
    FROM sys.sql_modules
    WHERE object_id = @ObjectID;

    -- Construct the debug script
    SET @DebugScript = 
        '-- Declare and set input parameters' + CHAR(13) + CHAR(10) +
        ISNULL(@ParamDefinition, '-- No parameters') + CHAR(13) + CHAR(10) +
		'-- Parameter values looked-up from BulkOpFeaturesTable...' + CHAR(13) + CHAR(10) +
		'-- (use these instead of other param declarations as needed)' + CHAR(13) + CHAR(10) +
		[SqlXl].[GenerateVarDeclarations](@BulkOpsFeatureID) + 
        '-- Procedure Body (you will need to delete beginning and end of sproc code below)' + CHAR(13) + CHAR(10) +
        @ProcedureCode;

    -- Note: Next line (commented-out) fails due to truncation...
    --PRINT @DebugScript; -- For immediate display in SSMS

	DECLARE @CurrentIndex INT = 1;
	DECLARE @ChunkSize INT = 2000; -- Adjust as needed (must be <= 2,047)
	DECLARE @Chunk NVARCHAR(MAX);

	WHILE @CurrentIndex <= LEN(@DebugScript)
	BEGIN --for printing only...(else long sprocs are truncated)...
		Set @Chunk = '';
		SET @Chunk = SUBSTRING(@DebugScript, @CurrentIndex, @ChunkSize);
		RAISERROR('%s', 0, 1, @Chunk) WITH NOWAIT; -- Print chunk immediately
		SET @CurrentIndex = @CurrentIndex + @ChunkSize;
	END;--end while 
END;--end sproc 
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GetExcelTemplateData]
    @FeatureID INT,
	@SelectedIds NVARCHAR(MAX) = NULL -- Optional parameter
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Validate that the feature exists
    IF NOT EXISTS (SELECT 1 FROM [SqlXl].[BulkOpFeatures] WHERE ID = @FeatureID)
    BEGIN
        RAISERROR('FeatureID %d does not exist in BulkOpFeatures table.', 16, 1, @FeatureID);
        RETURN;
    END

	--If provided, validate selectedIds param...
	if @SelectedIds is not null 
	begin
		exec SqlXl.ErrorSelectedIdsNotAsExpected @SelectedIds;
	end 
    
	-- Get the feature details
    DECLARE @GetRowsToEdit_SelectStatement NVARCHAR(MAX);
	declare @DomainSchemaName nvarchar(128);
	declare @DomainTableName nvarchar(128);
    SELECT 
		@DomainSchemaName = BulkOpFeatures.DomainSchemaName,
		@DomainTableName = BulkOpFeatures.DomainTableName,
		@GetRowsToEdit_SelectStatement = BulkOpFeatures.GetRowsToEdit_SelectStatement
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

	-- Start with the configured select statement...
	DECLARE @Sql NVARCHAR(MAX) =
		@GetRowsToEdit_SelectStatement;

	-- Remove any comments the sql code may have...
	SET @Sql = SqlXl.RemoveMultiLineComments(@Sql);

	-- Append where clause if SelectedIDs were provided...
	if @SelectedIds is not null 
	begin
		-- Get primary key column name
		DECLARE @PKColumn NVARCHAR(128) =
			[SqlXl].[GetPrimaryKeyColumnName](@DomainSchemaName, @DomainTableName);

		-- Add WHERE clause for selected rows
		SET @Sql = @Sql + ' WHERE ' + QUOTENAME(@PKColumn) + ' IN (' + @SelectedIds + ')';
	end --end if 

    -- Result Set 1: Starting point data for Sheet1 (editable data rows)
    EXEC SqlXl.ValidateThenRunSelectStatement @Sql;

    -- Result Set 2: Dropdown options for Sheet2 (ForColumn, OptionText format)
    -- Call the existing sproc to get dropdown options
    EXEC [SqlXl].[GetDropDownOptionsForFeature] @FeatureID;

    -- Result Set 3: Column metadata from Meta_Columns (for metadata sheet)
    -- Get staging table info for SqlDataType, but domain table info for IsPrimaryKey
    DECLARE @StagingSchemaName NVARCHAR(128),
            @StagingTableName NVARCHAR(128);
    SELECT
        @StagingSchemaName = StagingSchemaName,
        @StagingTableName = StagingTableName
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

    SELECT
        s.ColumnName,
        s.SqlDataType,
        ISNULL(d.IsPrimaryKey, 'NO') AS IsPrimaryKey  -- Get PK info from domain table
    FROM [SqlXl].[Meta_Columns] s
    LEFT JOIN [SqlXl].[Meta_Columns] d
        ON s.ColumnName = d.ColumnName
        AND d.SchemaName = @DomainSchemaName
        AND d.TableName = @DomainTableName
    WHERE s.SchemaName = @StagingSchemaName
        AND s.TableName = @StagingTableName
        AND s.ColumnName != 'RequestID'  -- Exclude RequestID from metadata
    ORDER BY s.ColumnName;
END;--end sproc 
go 

CREATE OR ALTER PROCEDURE [SqlXl].[GetStartingPointData]
	@FeatureID INT
AS
BEGIN
    SET NOCOUNT ON;

	-- Get feature details
	DECLARE @GetRowsToEdit_SelectStatement nvarchar(max);
    SELECT
		@GetRowsToEdit_SelectStatement = GetRowsToEdit_SelectStatement
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

	-- Use the configured select statement with sample/default values
	DECLARE @Sql NVARCHAR(MAX) = @GetRowsToEdit_SelectStatement;

	-- Remove any comments the sql code may have...
	SET @Sql = SqlXl.RemoveMultiLineComments(@Sql);

	-- Execute the query and return JSON metadata and data
	EXEC SqlXl.ValidateThenRunSelectQueryReturnJsonMetadataAndData @Sql;

	-- Include dropdown options for FKs
	EXEC SqlXl.GetDropdownOptionsForFeature @FeatureID, 1;--1=return as json
END --end sproc
GO

CREATE OR ALTER PROCEDURE [SqlXl].[GetRowsToEdit]
	@FeatureID INT,
	@SelectedIds NVARCHAR(MAX) -- "1,2,3,4"
AS
BEGIN
    SET NOCOUNT ON;

	--validate selectedIds param...
	exec SqlXl.ErrorSelectedIdsNotAsExpected @SelectedIds;

	-- Get feature details
	DECLARE @DomainSchemaName NVARCHAR(128),
            @DomainTableName NVARCHAR(128),
			@GetRowsToEdit_SelectStatement nvarchar(max);
    SELECT
		@DomainSchemaName = DomainSchemaName,
		@DomainTableName = DomainTableName,
		@GetRowsToEdit_SelectStatement = GetRowsToEdit_SelectStatement
    FROM [SqlXl].[BulkOpFeatures]
    WHERE ID = @FeatureID;

	-- Start with "select * from TableName"...
	DECLARE @Sql NVARCHAR(MAX) =
		@GetRowsToEdit_SelectStatement;

	-- Get primary key column name
	DECLARE @PKColumn NVARCHAR(128) =
		[SqlXl].[GetPrimaryKeyColumnName](@DomainSchemaName, @DomainTableName);

	-- Add WHERE clause for selected rows
	SET @Sql = @Sql + ' WHERE ' + QUOTENAME(@PKColumn) + ' IN (' + @SelectedIds + ')';

	-- Remove any comments the sql code may have...
	SET @Sql = SqlXl.RemoveMultiLineComments(@Sql);

	-- Execute the query and return JSON metadata and data
	EXEC SqlXl.ValidateThenRunSelectQueryReturnJsonMetadataAndData @Sql;

	-- Include dropdown options for FKs
	EXEC SqlXl.GetDropdownOptionsForFeature @FeatureID, 1;--1=return as json

	-- Return the PK column name so client knows which field to make readonly
	SELECT @PKColumn AS PrimaryKeyColumnName FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
END --end sproc
GO

/*ToDo CREATE PROCEDURE [SqlXl].[ScaffoldA_CUSTOM_Feature]
...Demonstrate creating a custom feature that has a manually created
staging table.  It writes to more than one domain table, like 
users-and-roles, for example; something where it's possible 
to ask end-users for only a single csv file, but then 
the t-sql writes appropriately to more than 
one normalized table, in order to "abstract away" the 
normalization from the end-user/uploader.  The point here
would be to demonstrate the power and flexibility of 
the framework - that it's capable of far more than 
only plain vanilla inserts and updates to a single table.
***************************************************/

--*******end sprocs
--end file 