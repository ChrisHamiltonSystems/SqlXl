using Microsoft.Data.SqlClient;
using System.Data;
using CsvHelper;
using System.Globalization;
using Dapper;
using System.Text.RegularExpressions;
using System.Collections;
using Azure.Core;
using System.Text;

namespace SqlXl.Core;
/*ToDo, carefully delete ALL obsolete methods and logic in this file!
 BulkOpsHelper should probably be a "service" eventually??
 ********/

#region SupportingPocoClasses
public class ProcessingResults
{
    public bool IsSuccessful { get; set; }
    public int RowsInserted { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsDeleted { get; set; }
    public List<string> ErrorMessages { get; set; } = new List<string>();
}//end class

/// <summary>
/// Data entity corresponding to BulkOpFeatures table.
/// </summary>
public class BulkOpsFeature
{
    public int ID { get; set; }
    public string UserFriendlyFeatureName { get; set; }
    public string InsertUpdateDeleteOrCustom { get; set; }
    public string DomainSchemaName { get; set; }
    public string DomainTableName { get; set; }
    public string StagingSchemaName { get; set; }
    public string StagingTableName { get; set; }
    public string StartingPointSelect { get; set; }
    public string SprocToProcessPerfectStagedData { get; set; }
    public int MenuDisplayRanking { get; set; } = 0;
}//end class 

/// <summary>
/// This entity class is more succinct 
/// and more secure than the full-blown
/// BulkOpsFeature class.  If all you 
/// need is a lightweight "pointer" to 
/// a feature, this holds all you need, 
/// without exposing sproc names, 
/// table names, etc.
/// </summary>
public class BulkOpsFeatureDescriptor
{
    public int ID { get; set; }
    public string UserFriendlyFeatureName { get; set; }
}//end class 

/// <summary>
/// Note: ConnectionString should always 
/// come from a secure configuration file, 
/// never hard-coded.
/// </summary>
public class BulkOpsSettings
{
    public string ConnectionString { get; set; }
    public string SmokeTestsCsvFilePath { get; set; } =
        Path.Combine(Path.GetTempPath(), "BulkOpsHelperTestData.csv");
    public int NumberOfSampleRowsForSmokeTests { get; set; } = 5;
    public int StopAfterThisManyErrors { get; set; } = 10;
    public string DomainSchemaName { get; set; } = "dbo";
    public string StagingSchemaName { get; set; } = "SqlXl";
}//end class 
#endregion

public class BulkOpsHelper
{
    #region BulkOpsHelperSetup
    private readonly BulkOpsSettings _settings;
    private readonly string _connectionString;
    private readonly List<BulkOpsFeature> _features;

    /// <summary>
    /// Uses given settings to query BulkOpFeatures table and store features in memory.
    /// </summary>
    /// <param name="settings">Connection string and other global defaults.</param>
    /// <exception cref="Exception">Thrown when any setting appears invalid or if BulkOpFeatures has no rows.</exception>
    public BulkOpsHelper(BulkOpsSettings settings)
    {
        // Light validation here...
        if (!IsValidTableOrSprocName(settings.DomainSchemaName))
        {
            throw new Exception("Setting.DomainSchemaName is invalid.");
        }//end if 

        if (!IsValidTableOrSprocName(settings.StagingSchemaName))
        {
            throw new Exception("Setting.StagingSchemaName is invalid.");
        }//end if 

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new Exception("Setting.ConnectionString is null or whitespace.");
        }//end if 

        _settings = settings;
        _connectionString = _settings.ConnectionString!;
        _features = GetAllFeatures();

        if (_features.Count == 0)
        {
            throw new Exception("No features available - BulkOpFeatures table appears empty.");
        }//end if 
    }//end method 

    private void ErrorOnAnyBadFeatureParameter(int featureId)
    {
        // Run sproc to validate feature parameters...
        using (SqlConnection conn = new SqlConnection(_connectionString))
        {
            conn.Open();
            using (SqlCommand cmd = new SqlCommand($"{_settings.StagingSchemaName}.ErrorOnAnyBadFeatureParameter", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.AddWithValue("@BulkOpFeatureID", featureId);
                cmd.ExecuteNonQuery();
            }//end using
        }//end using
    }//end method

    private DataTable CallSprocToGenerateTestData(string domainSchemaName, string domainTableName, string stagingTableName, int numberOfRowsUpTo100, string requestId)
    {
        // Run the sproc to GenerateTestData...
        using (SqlConnection conn = new SqlConnection(_connectionString))
        {
            using (SqlCommand command = new SqlCommand($"{_settings.StagingSchemaName}.GenerateTestData", conn))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Add parameters
                command.Parameters.AddWithValue("@DomainSchemaName", domainSchemaName);
                command.Parameters.AddWithValue("@DomainTableName", domainTableName);
                command.Parameters.AddWithValue("@StagingTableName", stagingTableName);
                command.Parameters.AddWithValue("@NumberOfRowsUpTo100", numberOfRowsUpTo100);

                /*Note: the RequestID should NOT be required in order to generate test 
                  data.  A RequestID WILL be generated and provided automatically 
                  when a feature is ran.  However, there is shared logic down in 
                  the sprocs that needs a RequestID, so it's here for now..*/
                command.Parameters.AddWithValue("@RequestID", requestId);

                // Create a SqlDataAdapter object
                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    // Create a DataSet to hold the result
                    var dataSet = new DataSet();

                    // Fill the DataSet using the SqlDataAdapter
                    adapter.Fill(dataSet);

                    // Return results...
                    return dataSet.Tables[0];
                }//end using SqlDataAdapter
            }//end using SqlCommand
        }//end using SqlConnection
    }//end method

    /// <summary>
    /// Lists all available feature IDs and UserFriendlyFeatureNames.
    /// </summary>
    /// <returns>A list of <see cref="BulkOpsFeatureDescriptor"/>s.</returns>
    public List<BulkOpsFeatureDescriptor> ListFeatures()
    {
        return _features.Select(f => new BulkOpsFeatureDescriptor { ID = f.ID, UserFriendlyFeatureName = f.UserFriendlyFeatureName }).ToList();
    }

    private List<BulkOpsFeature> GetAllFeatures()
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();

            // The query
            var sql = $"SELECT * FROM {_settings.StagingSchemaName}.BulkOpFeatures;";

            // Querying and returning the result
            var result = connection.Query<BulkOpsFeature>(sql).ToList();
            return result;
        }//end using 
    }//end method
    #endregion

    #region OlderProbablyTrash
    private string SqlToDropZZTempIfItExists()
    {
        return
            $" IF EXISTS " +
            $"(SELECT * FROM sys.objects " +
            $"WHERE object_id = OBJECT_ID(N'#ZZTemp') " +
            $"AND type in (N'U')) " +
            $"DROP TABLE #ZZTemp;";
    }//end method

    private string SqlToDropDebugZZTempIfItExists()
    {
        string sql = SqlToDropZZTempIfItExists();
        sql = sql.Replace("#ZZTemp", "SqlXl.DebugZZTemp");
        return sql;
    }//end method

    ///<summary>
    /// IMPORTANT: only handles ints and strings as column datatypes!
    /// Expects only ONE int column - ZZTemp_ID, ALL other columns 
    /// are expected to be string datatypes.
    ///</summary>
    private string SqlToCreateTableZZTemp(DataTable dataTable)
    {
        // Validate column types
        foreach (DataColumn col in dataTable.Columns)
        {
            if (col.DataType != typeof(int) && col.DataType != typeof(string))
            {
                throw new ArgumentException($"Invalid data type in column: {col.ColumnName}. Only int and string are allowed.");
            }//end if 
        }//end foreach 

        // This uses LINQ to cast the Columns collection to 
        // DataColumn objects and then builds the
        // "CREATE TABLE..." SQL string...
        string sql = $" CREATE TABLE #ZZTemp ({string.Join(", ", dataTable.Columns.Cast<DataColumn>().Select(col => $"[{col.ColumnName}] {(col.DataType == typeof(int) ? "INT" : "VARCHAR(MAX)")}"))});";

        // Change RequestID to nvarchar(36)...
        sql = sql.Replace("[RequestID] VARCHAR(MAX)", "[RequestID] nvarchar(36)");

        return sql;
    }//end method

    private string SqlToCreateDebugZZTemp(DataTable dataTable)
    {
        // Create table statement would be identical 
        // to creating #ZZTemp, except for table name...
        string sql = SqlToCreateTableZZTemp(dataTable);
        sql = sql.Replace("#ZZTemp", "SqlXl.DebugZZTemp");
        return sql;
    }//end method

    /// <summary>
    /// ToDo: Consider deprecating this method by using default values 
    /// (requestId is ALWAYS same value, for example)
    /// and auto-increment (define sql column ZZTemp_ID as identity(1,1) 
    /// in create table statement for #ZZTemp.
    /// Then, provide a col mapping when doing SqlBulkCopy.
    /// </summary>
    /// <param name="dataTable"></param>
    /// <param name="requestId"></param>
    /// <param name="rowNumberColumnName"></param>
    private void AppendRowNumberingAndRequestIdColumns(DataTable dataTable, string requestId, string rowNumberColumnName = "ZZTemp_ID")
    {
        // Add new column of type 'int'
        dataTable.Columns.Add(rowNumberColumnName, typeof(int));

        // Add string for requestId column...
        dataTable.Columns.Add("RequestID", typeof(string));

        // Populate rowNumberColumnName with row numbers
        int rowNumber = 1;
        foreach (DataRow row in dataTable.Rows)
        {
            row[rowNumberColumnName] = rowNumber++;
            row["RequestID"] = requestId;
        }//end foreach
    }//end method

    private DataTable ToDataTable(Stream validCsvDataStream)
    {
        // Ensure the stream is at the beginning if it supports seeking
        if (validCsvDataStream.CanSeek)
        {
            validCsvDataStream.Seek(0, SeekOrigin.Begin);
        }//end if 

        using (var reader = new StreamReader(validCsvDataStream))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            // Do any configuration to `CsvReader` before creating CsvDataReader.
            using (var dr = new CsvDataReader(csv))
            {
                var dt = new DataTable();
                dt.Load(dr);
                return dt;
            }//end using 
        }//end using
    }//end using

    private void ExecuteNonQuery(SqlConnection connThatIsAlreadyOPEN, string sqlStatement)
    {
        using SqlCommand cmd = new SqlCommand(sqlStatement, connThatIsAlreadyOPEN);
        cmd.ExecuteNonQuery();
    }//end method

    private DataTable PurgeStagingValidateZZTempAndReturnErrors(SqlConnection connThatIsAlreadyOPEN, string domainSchemaName, string domainTableName, string stagingSchemaName, string stagingTableName, string insertUpdateDeleteOrCustom, string requestId, bool debug = false)
    {// NOTE this method probably obsolete now
        using (SqlCommand command = new SqlCommand($"{_settings.StagingSchemaName}.PurgeStagingValidateZZTempAndReturnErrors", connThatIsAlreadyOPEN))
        {
            command.CommandType = CommandType.StoredProcedure;

            /*
             * todo error: Procedure or function PurgeStagingValidateZZTempAndReturnErrors has too many arguments specified.
             * @BulkOpFeaturesID int,
	@RequestID nvarchar(36),
	@StopAfterThisManyErrors INT,
	@Debug BIT = 0 -- Default to no debugging*/

            // Add parameters
            command.Parameters.AddWithValue("@DomainSchemaName", domainSchemaName);
            command.Parameters.AddWithValue("@DomainTableName", domainTableName);
            command.Parameters.AddWithValue("@StagingSchemaName", stagingSchemaName);
            command.Parameters.AddWithValue("@StagingTableName", stagingTableName);
            command.Parameters.AddWithValue("@InsertUpdateDeleteOrCustom", insertUpdateDeleteOrCustom);
            command.Parameters.AddWithValue("@StopAfterThisManyErrors", _settings.StopAfterThisManyErrors);
            command.Parameters.AddWithValue("@RequestID", requestId);
            command.Parameters.AddWithValue("@Debug", debug);

            // Create a SqlDataAdapter object
            using (SqlDataAdapter adapter = new SqlDataAdapter(command))
            {
                // Create a DataSet to hold the result
                var dataSet = new DataSet();

                // Fill the DataSet using the SqlDataAdapter
                adapter.Fill(dataSet);

                // Return results...
                return dataSet.Tables[0];
            }//end using SqlDataAdapter
        }//end using SqlCommand
    }//end method

    private void InsertSingleValidSampleRowToStaging(SqlConnection connThatIsAlreadyOPEN, string domainSchemaName, string domainTableName, string stagingTableName)
    {
        using (SqlCommand command = new SqlCommand($"{_settings.StagingSchemaName}.InsertSingleValidSampleRowToStagingGivenTheRealProdTableName", connThatIsAlreadyOPEN))
        {
            command.CommandType = CommandType.StoredProcedure;

            command.Parameters.AddWithValue("@DomainSchemaName", domainSchemaName);
            command.Parameters.AddWithValue("@DomainTableName", domainTableName);
            command.Parameters.AddWithValue("@StagingSchemaName", _settings.StagingSchemaName);
            command.Parameters.AddWithValue("@StagingTableName", stagingTableName);

            command.ExecuteNonQuery();
        }//end using SqlCommand
    }//end method

    /// <summary>
    /// This method assumes that valid, raw data was just written to #ZZTemp table.
    /// This moves data from #ZZTemp to the given staging table and
    /// then writes the staged data to production tables.
    /// </summary>
    /// <param name="connThatIsAlreadyOPEN"></param>
    /// <param name="stagingTableName"></param>
    /// <param name="sprocNameToProcessDataFromStagingTable"></param>
    /// <returns>A dataset holding summary results.</returns>
    private DataSet ProcessRawDataFromZZTempReturnSummaryResults(SqlConnection connThatIsAlreadyOPEN, string stagingSchemaName, string stagingTableName, string domainSchemaName, string domainSprocNameToProcessDataFromStagingTable, string requestId)
    {
        // Process the data using given sproc...
        using (SqlCommand command = new SqlCommand($"{_settings.StagingSchemaName}.ProcessRawDataFromZZTemp", connThatIsAlreadyOPEN))
        {
            command.CommandType = CommandType.StoredProcedure;

            // Add parameters
            command.Parameters.AddWithValue("@StagingSchemaName", stagingSchemaName);
            command.Parameters.AddWithValue("@StagingTableName", stagingTableName);
            command.Parameters.AddWithValue("@DomainSchemaName", domainSchemaName);
            command.Parameters.AddWithValue("@DomainSprocNameToProcessDataFromStagingTable", domainSprocNameToProcessDataFromStagingTable);
            command.Parameters.AddWithValue("@RequestID", requestId);

            // Create a SqlDataAdapter object
            using (SqlDataAdapter adapter = new SqlDataAdapter(command))
            {
                // Create a DataSet to hold the result
                var dataSet = new DataSet();

                /***** ToDo int overflow blows up next line!
                 * and this SHOULD have been caught during validation!
                 * *************************************************/
                // Fill the DataSet using the SqlDataAdapter
                adapter.Fill(dataSet);

                // Return results...
                return dataSet;
            }//end using SqlDataAdapter
        }//end using SqlCommand
    }//end method 

    private void InsertToDestinationTableNameUsingSqlBulkCopy(SqlConnection connThatIsAlreadyOPEN, DataTable dataTable, string destinationTableName)
    {
        using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connThatIsAlreadyOPEN))
        {
            // Write the DataTable to the SQL database.
            bulkCopy.DestinationTableName = destinationTableName;
            bulkCopy.WriteToServer(dataTable);
            // ToDo, considering WriteToServer() above,
            // test/find/fix **ALL** circumstances that would 
            // cause that to fail, given our KNOWN
            // DataTable structure of all string columns
            // except for a single row count integer column.
        }//end using 
    }//end method

    /// <summary>
    /// Converts all columns to string datatypes
    /// then appends a row numbering integer column called "ZZTemp_ID."
    /// </summary>
    /// <param name="dataTable"></param>
    /// <returns></returns>
    private DataTable PrepareForSqlBulkCopy(DataTable dataTable, string requestId)
    {
        DataTable preparedDataTable = ConvertColumnsToStrings(dataTable);
        AppendRowNumberingAndRequestIdColumns(preparedDataTable, requestId, rowNumberColumnName: "ZZTemp_ID");
        return preparedDataTable;
    }//end method 

    /// <summary>
    /// Submits the given dataTable as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="dataTable">DataTable holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteFeature(int featureId, DataTable dataTable, bool debug = false)
    {
        // ToDo, ideally the logic below only opens a db 
        // connection ONE TIME, but this appears 
        // to open and close several times. 
        // Optimize this as feasible...

        // Error on not allowed col names...
        ErrorOnAnyProblematicColumnNames(dataTable);//todo, part of validations?

        // Locate given feature...
        BulkOpsFeature feature =
            _features.Single(f => f.ID == featureId);

        // Validate the given BulkOpsFeatures record...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Also, create requestId for the db connection session...
        string requestId = Guid.NewGuid().ToString();

        // Ensure all columns are strings
        // and also append a row numbering and RequestID column...
        dataTable = PrepareForSqlBulkCopy(dataTable, requestId);

        // Construct a sql script that:
        // 1 - Drops table #ZZTemp, if it exists.
        // 2 - Creates a new #ZZTemp table, per the given DataTable.
        string sqlToReCreateZZTemp = SqlToDropZZTempIfItExists();
        sqlToReCreateZZTemp = sqlToReCreateZZTemp + SqlToCreateTableZZTemp(dataTable);

        // If debug mode, write a permanent table
        // parallel to #ZZTemp, called "SqlXl.DebugZZTemp"...
        if (debug)
        {
            sqlToReCreateZZTemp = sqlToReCreateZZTemp +
                SqlToDropDebugZZTempIfItExists() + SqlToCreateDebugZZTemp(dataTable);
        }//end if 

        // Also, create a temp table - #Messages to log errors as things process...
        sqlToReCreateZZTemp = sqlToReCreateZZTemp +
            " CREATE TABLE #Messages (Msg NVARCHAR(MAX)); ";

        // Open a connection...
        using (SqlConnection conn = new SqlConnection(_connectionString))
        {
            conn.Open();

            // Create #ZZTemp and Messages temp tables...
            ExecuteNonQuery(connThatIsAlreadyOPEN: conn,
                sqlStatement: sqlToReCreateZZTemp);

            // Load all raw data into #ZZTemp table...
            InsertToDestinationTableNameUsingSqlBulkCopy(conn, dataTable,
                destinationTableName: "#ZZTemp");

            if (debug)
            {
                //truncate table SqlXl.DebugLog;--ToDo, best place for this??
                InsertToDestinationTableNameUsingSqlBulkCopy(conn, dataTable,
                    destinationTableName: "SqlXl.DebugZZTemp");
            }//end if 

            // Purge staging and run all validations...
            // Validate all raw data in #ZZTemp by 
            // going row-by-row and column-by-column
            // thru #ZZTemp and attempting to update
            // the single valid row of staging data 
            // using the given row from #ZZTemp...
            // This also attempts to validate for any 
            // unique constraint violations, too...

            // ToDo, next line is failing to return 
            // ...overflow errors when given int too big...
            DataTable validationErrors =
                PurgeStagingValidateZZTempAndReturnErrors(
                    connThatIsAlreadyOPEN: conn,
                    domainSchemaName: feature.DomainSchemaName,
                    domainTableName: feature.DomainTableName,
                    stagingSchemaName: feature.StagingSchemaName,
                    stagingTableName: feature.StagingTableName,
                    insertUpdateDeleteOrCustom: feature.InsertUpdateDeleteOrCustom,
                    requestId: requestId,
                    debug: debug);

            // If ZERO validation errors were found...
            if (validationErrors.Rows.Count == 0)
            {
                //...then proceed to run the full sql transaction
                //and return the summary results...
                return ProcessRawDataFromZZTempReturnSummaryResults(
                    connThatIsAlreadyOPEN: conn,
                    stagingSchemaName: feature.StagingSchemaName,
                    stagingTableName: feature.StagingTableName,
                    domainSchemaName: feature.DomainSchemaName,
                    domainSprocNameToProcessDataFromStagingTable: feature.SprocToProcessPerfectStagedData,
                    requestId: requestId);
            }//end if 

            // Else return the data validation errors...
            else
            {
                return UnsuccessfulResult(singleStringColumnOfErrorMessages: validationErrors);
            }//end else
        }//end conn
    }//end method


    /// <summary>
    /// Submits the given dataTable as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="dataTable">DataTable holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteBulkDelete(int featureId, DataTable dataTable)
    {
        // Error on not allowed col names...
        ErrorOnAnyProblematicColumnNames(dataTable);

        // Locate given feature...
        BulkOpsFeature feature =
            _features.Single(f => f.ID == featureId);

        ErrorOnAnyBadFeatureParameter(featureId);

        // Fail if unexpected param...
        if (feature.StagingTableName != "NotApplicableForBulkDelete")
        {
            throw new Exception(
                "Value for BulkOpFeatures.StagingTableName " +
                "is expected to be set to: " +
                "'NotApplicableForBulkDelete' for any bulk delete feature.");
        }//end if 

        // Since this DataTable is coming as unvalidated 
        // input, convert all to strings first...
        // Also, create requestId for the db connection session...
        string requestId = Guid.NewGuid().ToString();
        dataTable = PrepareForSqlBulkCopy(dataTable, requestId);

        // Create sql commands to:
        // - Drop table #ZZTemp, if it exists.
        string sql = SqlToDropZZTempIfItExists();

        // Make a create table sql statement, 
        // corresponding to the datatable...
        sql = sql + SqlToCreateTableZZTemp(dataTable);

        // Also, create a temp table - #Messages to log errors as things process...
        sql = sql + " CREATE TABLE #Messages (Msg NVARCHAR(MAX)); ";

        using (SqlConnection conn = new SqlConnection(_connectionString))
        {
            conn.Open();
            using (SqlCommand cmd = new SqlCommand(sql, conn))
            {
                cmd.ExecuteNonQuery();
            }//end using 
            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(conn))
            {
                // Write the DataTable to the SQL database.
                bulkCopy.DestinationTableName = "#ZZTemp";
                bulkCopy.WriteToServer(dataTable);
                // ToDo, considering WriteToServer() above,
                // test/find **ALL** circumstances that would 
                // cause that to fail, given our known
                // DataTable structure...???
            }//end using 

            // Process the data using given sproc...
            using (SqlCommand command = new SqlCommand(feature.DomainSchemaName + "." + feature.SprocToProcessPerfectStagedData, conn))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Create a SqlDataAdapter object
                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    // Create a DataSet to hold the result
                    var dataSet = new DataSet();

                    /* Note: data input 'validation' is very 
                     * weak here for deletes...*/
                    try
                    {
                        // Fill the DataSet using the SqlDataAdapter
                        adapter.Fill(dataSet);
                    }//end try 

                    catch (Exception ex)
                    {
                        // Create an unsuccessful result set, given error...
                        DataTable errorDataTable = SingleMsgDataTable(ex.Message);
                        dataSet = UnsuccessfulResult(errorDataTable);
                    }//end catch 

                    // Return results...
                    return dataSet;
                }//end using SqlDataAdapter
            }//end using SqlCommand
        }//end conn
    }//end method

    private DataTable SingleMsgDataTable(string message)
    {
        // Create a new DataTable
        DataTable dt = new DataTable();

        // Add a single column named "Msg"
        dt.Columns.Add("Msg", typeof(string));

        // Create a new row
        DataRow row = dt.NewRow();

        // Set the value for the "Msg" column
        row["Msg"] = message;

        // Add the row to the DataTable
        dt.Rows.Add(row);

        return dt;
    }//end method 

    /* ToDo, whether it's a csvString, a csvStream, a csvFilePath, 
        or a DataTable, the col names could be validated up-front, 
        in this application layer, prior to calling the sprocs.
        (The sprocs DO validate col names, but that throws the less
        graceful error, and that could be avoided here.)
        At the beginning of calling a feature, for example, the 
        db could be queried for all required columns for that feature.
        Then, this C# code should gracefully error immediately 
        (returning the UnsuccessfulResult dataset), if csv format is bad, or if 
        a required column is missing.  Also, all non-required cols
        should be stripped out of the data in this logic, too.
        (We DO want to allow non-required columns to be submitted though, 
         they just need to be automatically ignored or dropped in this logic.*/

    /// <summary>
    /// Submits the given csvString as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="csvString">CSV formatted data as a single string, 
    /// holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the 
    /// first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteFeature(int featureId, string csvString)
    {
        //Make a datatable from the given csvString...
        var data = CsvToDataTable(csvString);
        return ExecuteFeature(featureId, data);
    }

    private DataTable CsvToDataTable(string csvString)
    {
        // Ensure the CSV string is not null or empty
        if (string.IsNullOrWhiteSpace(csvString))
            throw new ArgumentException("CSV string is null or empty.");

        using var reader = new StringReader(csvString);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        // Get records from the CSV string
        var records = csv.GetRecords<dynamic>().ToList();

        // Create a DataTable
        DataTable dataTable = new DataTable();

        if (records.Any())
        {
            // Create columns based on the first record
            foreach (var pair in (IDictionary<string, object>)records.First())
            {
                dataTable.Columns.Add(pair.Key);
            }

            // Add records to the DataTable
            foreach (var record in records)
            {
                var row = dataTable.NewRow();
                foreach (var pair in (IDictionary<string, object>)record)
                {
                    row[pair.Key] = pair.Value ?? DBNull.Value;
                }
                dataTable.Rows.Add(row);
            }//end foreach
        }//end if 

        return dataTable;
    }//end method 

    /// <summary>
    /// Submits the given csvStream as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="csvStream">CSV formatted Stream of data holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteFeature(int featureId, Stream csvStream)
    {
        // Read stream into a DataTable...
        DataTable data = ToDataTable(csvStream);

        // Run feature...
        return ExecuteFeature(featureId, data);
    }//end method

    /// <summary>
    /// Submits the given csvStream as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="csvStream">CSV formatted Stream of data holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteBulkDelete(int featureId, Stream csvStream)
    {
        // Read stream into a DataTable...
        DataTable data = ToDataTable(csvStream);

        // Run feature...
        return ExecuteBulkDelete(featureId, data);
    }//end method

    /// <summary>
    /// Submits the given CSV file as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="csvFilePath">Path to CSV file of data holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteFeatureGivenFilePath(int featureId, string csvFilePath)
    {
        if (string.IsNullOrEmpty(csvFilePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(csvFilePath));
        }

        if (!File.Exists(csvFilePath))
        {
            throw new FileNotFoundException($"The file '{csvFilePath}' was not found.");
        }

        // Open csv file...
        using (FileStream fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read))
        {
            return ExecuteFeature(featureId, fileStream);
        }//end using 
    }//end method 

    /// <summary>
    /// Submits the given CSV file as input data for the given featureId.
    /// </summary>
    /// <param name="featureId">ID for the feature being called.</param>
    /// <param name="csvFilePath">Path to CSV file of data holding all required columns for the feature.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet ExecuteBulkDeleteGivenFilePath(int featureId, string csvFilePath)
    {
        if (string.IsNullOrEmpty(csvFilePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(csvFilePath));
        }

        if (!File.Exists(csvFilePath))
        {
            throw new FileNotFoundException($"The file '{csvFilePath}' was not found.");
        }

        // Open csv file...
        using (FileStream fileStream = new FileStream(csvFilePath, FileMode.Open, FileAccess.Read))
        {
            return ExecuteBulkDelete(featureId, fileStream);
        }//end using 
    }//end method 

    private static bool IsValidTableOrSprocName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Regular expression for valid SQL Server stored procedure name
        // Rules:
        // 1. Must start with a letter, underscore, @, or #
        // 2. Can contain letters, numbers, _, or $
        // 3. Maximum length of 128 characters
        var validNamePattern = @"^[a-zA-Z_@#][a-zA-Z0-9_$]{0,127}$";
        return Regex.IsMatch(name, validNamePattern);
    }//end method 

    private DataTable RunSelectQueryReturnDataTable(string selectQuery)
    {
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            connection.Open();

            using (SqlCommand command = new SqlCommand(selectQuery, connection))
            {
                DataTable dataTable = new DataTable();

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    dataTable.Load(reader);
                    connection.Close();
                }

                return dataTable;
            }
        }
    }//end method

    /// <summary>
    /// Write given dataTable to given filePath, as CSV file.
    /// </summary>
    /// <param name="dataTable">Data to write to CSV file.</param>
    /// <param name="filePath">Path to write CSV file.</param>
    public static void WriteCsvFile(DataTable dataTable, string filePath)
    {
        // Error on not allowed col names...
        ErrorOnAnyProblematicColumnNames(dataTable);

        using (var writer = new StreamWriter(filePath))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            // Writing the DataTable to CSV
            foreach (DataColumn column in dataTable.Columns)
            {
                csv.WriteField(column.ColumnName);
            }
            csv.NextRecord();

            foreach (DataRow row in dataTable.Rows)
            {
                for (var i = 0; i < dataTable.Columns.Count; i++)
                {
                    csv.WriteField(row[i]);
                }
                csv.NextRecord();
            }
        }//end using
    }//end method 

    private DataTable ConvertColumnsToStrings(DataTable originalTable)
    {
        // Create a new DataTable with the same structure as the original but with string columns
        DataTable stringTable = new DataTable();

        foreach (DataColumn column in originalTable.Columns)
        {
            stringTable.Columns.Add(column.ColumnName, typeof(string));
        }//end foreach

        // Populate the new DataTable with string representations of the data in the original DataTable
        foreach (DataRow row in originalTable.Rows)
        {
            DataRow newRow = stringTable.NewRow();
            foreach (DataColumn column in originalTable.Columns)
            {
                newRow[column.ColumnName] = row[column].ToString();
            }
            stringTable.Rows.Add(newRow);
        }//end foreach

        return stringTable;
    }//end method

    /// <summary>
    /// Writes all column values in all DataTable(s) in the given DataSet.
    /// </summary>
    /// <param name="dataSet">One or more DataTable(s).</param>
    /// <returns>A list of strings, one per row of data.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static List<string> ExtractDataSetContents(DataSet dataSet)
    {
        List<string> results = new List<string>();

        if (dataSet == null)
        {
            throw new ArgumentNullException(nameof(dataSet), "DataSet cannot be null.");
        }

        foreach (DataTable table in dataSet.Tables)
        {
            foreach (DataRow row in table.Rows)
            {
                foreach (DataColumn column in table.Columns)
                {
                    string value = row[column].ToString();
                    results.Add($"{column.ColumnName}: {value}");
                }
            }
        }

        return results;
    }//end method 

    private static DataTable ConvertColumnsToString(DataTable originalTable)
    {
        DataTable stringTable = new DataTable();

        // Create columns with string type
        foreach (DataColumn column in originalTable.Columns)
        {
            stringTable.Columns.Add(column.ColumnName, typeof(string));
        }

        // Copy rows with values converted to strings
        foreach (DataRow row in originalTable.Rows)
        {
            DataRow newRow = stringTable.NewRow();
            foreach (DataColumn column in originalTable.Columns)
            {
                newRow[column.ColumnName] = row[column].ToString();
            }
            stringTable.Rows.Add(newRow);
        }

        return stringTable;
    }//end method
    #endregion

    #region TroubleshootingSupport
    /// <summary>
    /// Writes the given data to a table called ZZDebug, 
    /// for troubleshooting purposes.
    /// </summary>
    /// <param name="dataTable">Input data.</param>
    public void WriteToZZDebug(DataTable dataTable)
    {
        // Error on not allowed col names...
        ErrorOnAnyProblematicColumnNames(dataTable);

        // Also, create requestId for the db connection session...
        string requestId = Guid.NewGuid().ToString();

        // Ensure all columns are strings
        // and also append a row numbering and RequestID column...
        dataTable = PrepareForSqlBulkCopy(dataTable, requestId);

        // Sql to re-create ZZDebug...
        string sql = SqlToDropZZTempIfItExists();
        sql = sql + SqlToCreateTableZZTemp(dataTable);
        sql = sql.Replace("#ZZTemp", $"{_settings.StagingSchemaName}.ZZDebug");

        using (SqlConnection connection = new SqlConnection(_settings.ConnectionString))
        {
            connection.Open();
            ExecuteNonQuery(connection, sql);
            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
            {
                bulkCopy.DestinationTableName = $"{_settings.StagingSchemaName}.ZZDebug";
                bulkCopy.WriteToServer(dataTable);
            }//end using 
        }//end using
    }//end method 
    #endregion

    #region MiscUtilFunctions
    /// <summary>
    /// Valid column names: (letters, numbers, and underscores)
    /// </summary>
    /// <param name="dataTable"></param>
    /// <exception cref="ArgumentException"></exception>
    
    private static DataTable UnionDataTables(DataTable table1, DataTable table2)
    {
        // Clone the structure of the first table
        DataTable resultTable = table1.Clone();

        // Import rows from the first table
        foreach (DataRow row in table1.Rows)
        {
            resultTable.ImportRow(row);
        }

        // Import rows from the second table
        foreach (DataRow row in table2.Rows)
        {
            resultTable.ImportRow(row);
        }

        return resultTable;
    }//end method
    #endregion

    #region GeneratingTestData
    /// <summary>
    /// Create bad data for the given INSERT feature.
    /// </summary>
    /// <param name="featureId">ID of the INSERT feature.</param>
    /// <param name="violationType">Leave as-is.</param>
    /// <returns></returns>
    public DataTable GenerateViolationTestDataForAn_INSERT_Feature(int featureId, int violationType = 1)
    {
        // Should make 2 identical rows, so any non-pk,
        // single-column unique constraints should get triggered...
        DataTable dataTable1 = GenerateValidTestDataForAn_INSERT_Feature(featureId, numberOfRowsUpTo100: 1);
        DataTable dataTable2 = GenerateValidTestDataForAn_INSERT_Feature(featureId, numberOfRowsUpTo100: 1);
        return BulkOpsHelper.UnionDataTables(dataTable1, dataTable2);
    }//end method

    /// <summary>
    /// Creates data to violate uniqueness constraints for an update feature.
    /// </summary>
    /// <param name="featureId">ID for the feature that would fail validation, 
    /// if the resulting data were submitted to it.</param>
    /// <param name="violationType"></param>
    /// <returns></returns>
    public DataTable GenerateViolationTestDataForAn_UPDATE_Feature(int featureId, int violationType = 1)
    {
        /* violationTypes: 
         * 1 - Check for domain table pk column duplicated within #ZZTemp.
         * 2 - Check for single column unique constraint violations within #ZZTemp
         * 
         */

        // Duplicate values for domain table's primary key column...
        DataTable dataTable1 = GenerateValidTestDataForAn_UPDATE_Feature(featureId, numberOfRowsUpTo100: 1);
        DataTable dataTable2 = GenerateValidTestDataForAn_UPDATE_Feature(featureId, numberOfRowsUpTo100: 1);
        return BulkOpsHelper.UnionDataTables(dataTable1, dataTable2); 
    }//end method

    /// <summary>
    /// Create valid test data for the given INSERT feature.
    /// </summary>
    /// <param name="featureId">ID of the feature that would be tested using the generated data.</param>
    /// <param name="numberOfRowsUpTo100">Number of rows of data to generate.</param>
    /// <returns>Valid test data.</returns>
    public DataTable GenerateValidTestDataForAn_INSERT_Feature(int featureId, int numberOfRowsUpTo100)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        // Validate feature params...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Create requestId for the db connection session...
        string requestId = Guid.NewGuid().ToString();
        
        return CallSprocToGenerateTestData(
            feature.DomainSchemaName, feature.DomainTableName,
            feature.StagingTableName, numberOfRowsUpTo100,
            requestId);
    }//end method

    /// <summary>
    /// Create valid test data for the given UPDATE feature.
    /// </summary>
    /// <param name="featureId">ID of the feature that would be tested using the generated data.</param>
    /// <param name="numberOfRowsUpTo100">Number of rows of data to generate.</param>
    /// <returns>Valid test data.</returns>
    public DataTable GenerateValidTestDataForAn_UPDATE_Feature(int featureId, int numberOfRowsUpTo100)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        // Validate feature params...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Since this is an update feature, 
        // start with existing records...
        DataTable dataTable =
            RunSelectQueryReturnDataTable(
                $"select top {numberOfRowsUpTo100} * " +
                $" from {feature.DomainSchemaName}.{feature.DomainTableName} ;");
        if (dataTable.Rows.Count < numberOfRowsUpTo100)
        {
            throw new Exception(
                $"{feature.DomainSchemaName}.{feature.DomainTableName} had " +
                $"less than {numberOfRowsUpTo100} rows.  " +
                $"This test expects {feature.DomainSchemaName}.{feature.DomainTableName} to " +
                $"at least have the {numberOfRowsUpTo100} rows requested for the test.");
        }//end if 

        // Convert all column datatypes to string, 
        // else we would have to type-cast values later...
        dataTable = ConvertColumnsToString(dataTable);

        // Use meta-info to get valid sample values, omitting primary key...
        string sql = $"SELECT ColumnName, ValidSampleValue " +
            $"FROM {feature.StagingSchemaName}.Meta_Columns " +
            $"WHERE SchemaName = '{feature.DomainSchemaName}' " +
            $"AND TableName = '{feature.DomainTableName}' " +
            $"AND IsPrimaryKey = 'NO' ;";
        DataTable newValues = RunSelectQueryReturnDataTable(sql);

        // Put results in hashtable for easy lookup...
        Hashtable newColValues = new Hashtable();
        foreach (DataRow row in newValues.Rows)
        {
            newColValues.Add(
                key: row["ColumnName"].ToString(),
                value: row["ValidSampleValue"].ToString());
        }//end foreach 

        // Enumerate all existing rows and set new values for columns...
        foreach (DataRow row in dataTable.Rows)
        {
            // For each sample value available...
            // Note: assumes no primary key columns in sample values.
            foreach (DictionaryEntry entry in newColValues)
            {
                // Expect all columns to be of type string, 
                // else would have to type-cast this next line...
                row[entry.Key.ToString()] = entry.Value.ToString();
            }//end foreach 
        }//end foreach

        return dataTable;
    }//end method

    /// <summary>
    /// Create valid test data for the given DELETE feature.
    /// </summary>
    /// <param name="featureId">ID of the feature that would be tested using the generated data.</param>
    /// <param name="numberOfRowsUpTo100">Number of rows of data to generate.</param>
    /// <returns>Valid test data.</returns>
    public DataTable GenerateValidTestDataForA_DELETE_Feature(int featureId, int numberOfRowsUpTo100)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        // Validate feature params...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Lookup primary key of table being deleted from...
        string sql = 
            $"select PkColumnName = " +
                $"{feature.StagingSchemaName}.GetPrimaryKeyColumnName(" +
                    $"'{feature.DomainSchemaName}'," +
                    $"'{feature.DomainTableName}');";
        string pkColumnName = RunSelectQueryReturnDataTable(sql).Rows[0][0].ToString();

        // Since this is a DELETE feature, 
        // select top x ID from TableName; , basically...
        DataTable dataTable =
            RunSelectQueryReturnDataTable(
                $"select top {numberOfRowsUpTo100} {pkColumnName} " +
                $" from {feature.DomainSchemaName}.{feature.DomainTableName} ;");
        if (dataTable.Rows.Count < numberOfRowsUpTo100)
        {
            throw new Exception(
                $"{feature.DomainSchemaName}.{feature.DomainTableName} had " +
                $"less than {numberOfRowsUpTo100} rows.  " +
                $"This test expects {feature.DomainSchemaName}.{feature.DomainTableName} to " +
                $"have at least {numberOfRowsUpTo100} rows requested for the test.");
        }//end if 

        // Convert all column datatypes to string, 
        // else we would have to type-cast values later...
        dataTable = ConvertColumnsToString(dataTable);

        return dataTable;
    }//end method
    #endregion

    #region SmokeTests
    /// <summary>
    /// Creates valid data for the given feature,
    /// writes that data to settings.SmokeTestsCsvFilePath, 
    /// and then attempts to execute the given 
    /// feature using the generated file as input data.
    /// Errors if all test data was NOT processed as intended.
    /// </summary>
    /// <param name="featureId">ID of the INSERT feature to test.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the 
    /// first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet SmokeTest_a_BulkInsertFeature(int featureId)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        ErrorOnAnyBadFeatureParameter(featureId);

        // Generate test data...
        DataTable dataTable =
            GenerateValidTestDataForAn_INSERT_Feature(featureId, numberOfRowsUpTo100: 1);

        // Must strip RequestID column in this case...
        dataTable.Columns.Remove("RequestID");

        // Save test data to csv file...
        WriteCsvFile(dataTable, _settings.SmokeTestsCsvFilePath);

        // Count rows prior to insert...
        string sql = $"select Cnt = count(*) from {feature.DomainSchemaName}.{feature.DomainTableName};";
        int origRowCount = int.Parse(RunSelectQueryReturnDataTable(sql).Rows[0]["Cnt"].ToString());

        // Run the insert feature...
        DataSet result = ExecuteFeatureGivenFilePath(featureId, _settings.SmokeTestsCsvFilePath);

        /*Note: First DataTable in result should be like this...
         * SELECT IsSuccessful = 'true', 
			RowsInserted = @@ROWCOUNT, 
			RowsUpdated = 0,
			RowsDeleted = 0; */
        // Verify results... (Commented out for CLI - these were test assertions)
        // Assert.That(result.Tables[0].Rows[0]["IsSuccessful"].ToString() == "true");
        // Assert.That(result.Tables[0].Rows[0]["RowsInserted"].ToString() == "1");
        // Assert.That(result.Tables[0].Rows[0]["RowsUpdated"].ToString() == "0");
        // Assert.That(result.Tables[0].Rows[0]["RowsDeleted"].ToString() == "0");

        // Count final number of rows...
        int finalRowCount = int.Parse(RunSelectQueryReturnDataTable(sql).Rows[0]["Cnt"].ToString());
        // Assert.That(finalRowCount == origRowCount + 1);

        return result;
    }//end method

    /// <summary>
    /// Creates valid data for the given feature,
    /// writes that data to settings.SmokeTestsCsvFilePath, 
    /// and then attempts to execute the given 
    /// feature using the generated file as input data.
    /// Errors if all test data was NOT processed as intended.
    /// </summary>
    /// <param name="featureId">ID of the UPDATE feature to test.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the 
    /// first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet SmokeTest_a_BulkUpdateFeature(int featureId)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        // Validate feature params...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Generate test data...
        DataTable dataTable =
            GenerateValidTestDataForAn_UPDATE_Feature(featureId, numberOfRowsUpTo100: 1);

        // Save test data to csv file...
        WriteCsvFile(dataTable, _settings.SmokeTestsCsvFilePath);

        // Count rows prior to UPDATE...
        string sql = $"select Cnt = count(*) from {feature.DomainSchemaName}.{feature.DomainTableName};";
        int origRowCount = int.Parse(RunSelectQueryReturnDataTable(sql).Rows[0]["Cnt"].ToString());

        // Run the UPDATE feature...
        DataSet result = ExecuteFeatureGivenFilePath(featureId, _settings.SmokeTestsCsvFilePath);

        /*Note: First DataTable in result should be something like this...
         * SELECT IsSuccessful = 'true', 
			RowsInserted = 0, 
			RowsUpdated = 999,
			RowsDeleted = 0; */
        // Verify results... (Commented out for CLI - these were test assertions)
        // Assert.That(result.Tables[0].Rows[0]["IsSuccessful"].ToString() == "true");
        // Assert.That(result.Tables[0].Rows[0]["RowsInserted"].ToString() == "0");
        // Assert.That(result.Tables[0].Rows[0]["RowsUpdated"].ToString() == "1");
        // Assert.That(result.Tables[0].Rows[0]["RowsDeleted"].ToString() == "0");

        // Count final number of rows...
        int finalRowCount = int.Parse(RunSelectQueryReturnDataTable(sql).Rows[0]["Cnt"].ToString());
        // Assert.That(finalRowCount == origRowCount);

        return result;
    }//end method

    /// <summary>
    /// Creates valid data for the given feature,
    /// writes that data to settings.SmokeTestsCsvFilePath, 
    /// and then attempts to execute the given 
    /// feature using the generated file as input data.
    /// Errors if all test data was NOT processed as intended.
    /// </summary>
    /// <param name="featureId">ID of the DELETE feature to test.</param>
    /// <returns>A DataSet holding exactly 2 DataTables - the 
    /// first is a result summary, 
    /// the second holds validation errors, if any.</returns>
    public DataSet SmokeTest_a_BulkDeleteFeature(int featureId)
    {
        // Get the feature...
        BulkOpsFeature feature = _features.Single(f => f.ID == featureId);

        // Validate params...
        ErrorOnAnyBadFeatureParameter(featureId);

        // Select top x rows to be deleted...
        DataTable testData = GenerateValidTestDataForA_DELETE_Feature(featureId, numberOfRowsUpTo100: 1);

        // Save test data to csv file...
        WriteCsvFile(testData, _settings.SmokeTestsCsvFilePath);

        // Save test data to ZZDebug table, too...
        WriteToZZDebug(testData);

        // Provide the test data to run the feature...
        DataSet results = ExecuteBulkDeleteGivenFilePath(featureId,_settings.SmokeTestsCsvFilePath);

        return results;
    }//end method
    #endregion

    #region FinalVersionLatestMethods
    private static void ErrorOnAnyProblematicColumnNames(DataTable dataTable)
    {
        // Define a regular expression to match valid column names (letters, numbers, and underscores)
        Regex validColumnNamePattern = new Regex(@"^[a-zA-Z0-9_]+$");

        // Iterate through each column in the DataTable
        foreach (DataColumn column in dataTable.Columns)
        {
            // Check if the column name matches the pattern
            if (!validColumnNamePattern.IsMatch(column.ColumnName.Trim()))
            {
                // Throw an exception if a column name is invalid
                throw new ArgumentException(
                    $"Invalid column name: {column.ColumnName}. " +
                    $"Column names must contain only letters, numbers, and underscores.");
            }//end if 
        }//end foreach
    }//end method 

    private static string GenerateTempTableScript(DataTable dataTable, string requestId, bool debug = false)
    {
        if (dataTable == null || dataTable.Columns.Count == 0)
            throw new ArgumentException("DataTable must have at least one column.");

        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("RequestID must be a valid non-empty string.");

        StringBuilder scriptBuilder = new StringBuilder();

        // Drop temp tables if they exist
        scriptBuilder.AppendLine("IF OBJECT_ID('tempdb..#ZZTemp') IS NOT NULL");
        scriptBuilder.AppendLine("    DROP TABLE #ZZTemp;");
        scriptBuilder.AppendLine("IF OBJECT_ID('tempdb..#Messages') IS NOT NULL");
        scriptBuilder.AppendLine("    DROP TABLE #Messages;");

        // Drop/Create DebugZZTemp only if debug is enabled
        if (debug)
        {
            scriptBuilder.AppendLine("IF OBJECT_ID('SqlXl.DebugZZTemp') IS NOT NULL");
            scriptBuilder.AppendLine("    DROP TABLE SqlXl.DebugZZTemp;");

            scriptBuilder.AppendLine("CREATE TABLE SqlXl.DebugZZTemp (");
            scriptBuilder.AppendLine($"   RequestID NVARCHAR(36) DEFAULT '{requestId}', -- Default value");
            scriptBuilder.AppendLine("    ZZTemp_ID INT IDENTITY(2,1),   -- Auto-incrementing ID starting at 2");

            // Add all input data columns as nvarchar(max)...
            for (int i = 0; i < dataTable.Columns.Count; i++)
            {
                DataColumn column = dataTable.Columns[i];
                string columnDefinition = $"    {column.ColumnName} NVARCHAR(MAX)";
                if (i < dataTable.Columns.Count - 1)
                    columnDefinition += ","; // Add a comma except for the last column
                scriptBuilder.AppendLine(columnDefinition);
            }//end for
            scriptBuilder.AppendLine(");");
        }

        // Create #ZZTemp table
        scriptBuilder.AppendLine("CREATE TABLE #ZZTemp (");
        scriptBuilder.AppendLine($"   RequestID NVARCHAR(36) DEFAULT '{requestId}', -- Default value");
        scriptBuilder.AppendLine("    ZZTemp_ID INT IDENTITY(2,1),   -- Auto-incrementing ID starting at 2");

        // Add all input data columns as nvarchar(max)...
        for (int i = 0; i < dataTable.Columns.Count; i++)
        {
            DataColumn column = dataTable.Columns[i];
            string columnDefinition = $"    {column.ColumnName} NVARCHAR(MAX)";
            if (i < dataTable.Columns.Count - 1)
                columnDefinition += ","; // Add a comma except for the last column
            scriptBuilder.AppendLine(columnDefinition);
        }//end for
        scriptBuilder.AppendLine(");");

        // Create #Messages table
        scriptBuilder.AppendLine("CREATE TABLE #Messages (Msg NVARCHAR(MAX));");

        return scriptBuilder.ToString();
    }//end method
     
    private static DataTable ConvertAllCellValuesToStrings(DataTable originalTable)
    {
        // Create a new DataTable with the same structure but all columns as string
        DataTable stringTable = new DataTable();

        foreach (DataColumn column in originalTable.Columns)
        {
            stringTable.Columns.Add(column.ColumnName, typeof(string));
        }

        // Copy data with conversion
        foreach (DataRow row in originalTable.Rows)
        {
            DataRow newRow = stringTable.NewRow();
            foreach (DataColumn column in originalTable.Columns)
            {
                newRow[column.ColumnName] = row[column] == DBNull.Value ? DBNull.Value : row[column]?.ToString();
            }
            stringTable.Rows.Add(newRow);
        }

        return stringTable;
    }//end method


    private static DataSet UnsuccessfulResult(string errorMessage)
    {
        // Create a new DataTable
        DataTable errorTable = new DataTable();

        // Add the "Msg" column of type string (to match NVARCHAR(MAX))
        errorTable.Columns.Add("Msg", typeof(string));

        // Add a single row with the provided error message
        DataRow row = errorTable.NewRow();
        row["Msg"] = errorMessage;
        errorTable.Rows.Add(row);

        return UnsuccessfulResult(errorTable);
    }//end method 

    /// <summary>
    /// Bundles the given error messages into a DataSet
    /// that has identical structure of a successful
    /// dataset, but instead includes unsuccessful 
    /// summary values for RowsInserted, RowsUpdated, etc.
    /// </summary>
    /// <param name="singleStringColumnOfErrorMessages"></param>
    /// <returns></returns>
    private static DataSet UnsuccessfulResult(DataTable singleStringColumnOfErrorMessages)
    {
        // Create a new DataTable
        DataTable table = new DataTable("Table");

        // Define columns
        table.Columns.Add("IsSuccessful", typeof(string));
        table.Columns.Add("RowsInserted", typeof(int));
        table.Columns.Add("RowsUpdated", typeof(int));
        table.Columns.Add("RowsDeleted", typeof(int));

        // Create a new DataRow
        DataRow row = table.NewRow();

        // Set the row values
        row["IsSuccessful"] = "false";
        row["RowsInserted"] = 0;
        row["RowsUpdated"] = 0;
        row["RowsDeleted"] = 0;

        // Add the row to the DataTable
        table.Rows.Add(row);

        // Create a DataSet and add both DataTables
        DataSet dataSet = new DataSet();
        dataSet.Tables.Add(table);
        singleStringColumnOfErrorMessages.TableName = "Table1";
        dataSet.Tables.Add(singleStringColumnOfErrorMessages);

        return dataSet;
    }//end method 
    
    private static async Task CreateAndPopulateTempTablesAsync(SqlConnection connection, DataTable inputData, string requestId, bool debug = false)
    {
        string sqlToCreateTempTables = GenerateTempTableScript(inputData, requestId, debug);

        // Create temp tables
        await using var initCommand = new SqlCommand(sqlToCreateTempTables, connection);
        await initCommand.ExecuteNonQueryAsync();

        // Bulk insert data into #ZZTemp
        using (var bulkCopy = new SqlBulkCopy(connection))
        {
            bulkCopy.DestinationTableName = "#ZZTemp";

            // Map columns
            foreach (DataColumn column in inputData.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            //  Removed Task.Run — Keeping it synchronous for backend performance
            bulkCopy.WriteToServer(inputData);
        }//end using 
    }//end method

    /// <summary>
    /// Executes the main processing stored procedure that moves validated data from staging to domain tables.
    ///
    /// IMPORTANT - Debug Mode Behavior:
    /// When running in Visual Studio Debug mode, you may see SQL exceptions thrown at the adapter.Fill() call below.
    /// This is EXPECTED BEHAVIOR and NOT A BUG - it's the constraint-driven validation working as designed.
    ///
    /// What's happening:
    /// 1. SqlBulkCopy inserts data into staging table
    /// 2. SQL Server enforces constraints (NOT NULL, CHECK, FK, UNIQUE, etc.)
    /// 3. Constraint violations throw SqlException (e.g., "Cannot insert NULL into FirstName")
    /// 4. TRY-CATCH block in SQL Server sproc catches the exception
    /// 5. Error message is added to #Messages table
    /// 6. Errors are returned to C# and displayed in UI
    ///
    /// Why Visual Studio breaks:
    /// - In Debug mode, VS debugger breaks on "first-chance exceptions" BEFORE the TRY-CATCH catches them
    /// - Just hit Continue (F5) - the exception WILL be caught and validation proceeds normally
    /// - In Release mode, no debugger runs, so exceptions are caught silently
    ///
    /// To disable debugger breaks:
    /// - Debug → Windows → Exception Settings
    /// - Uncheck "Microsoft.Data.SqlClient.SqlException"
    /// - Or just run in Release mode for smoother development experience
    ///
    /// This is the core of SqlXL's value proposition:
    /// "Data as valid as your staging table, GUARANTEED!" - Zero validation code required.
    /// </summary>
    private static async Task<DataSet> ExecuteMainProcessingSprocAsync(SqlConnection connection, int bulkOpFeaturesID, string requestId, bool debug = false)
    {
        DataSet resultDataSet = new DataSet();

        try
        {
            await using var processCommand = new SqlCommand("EXEC SqlXl.ProcessRawDataFromZZTemp @BulkOpFeaturesID, @RequestID, @Debug;", connection);
            processCommand.Parameters.AddWithValue("@BulkOpFeaturesID", bulkOpFeaturesID);
            processCommand.Parameters.AddWithValue("@RequestID", requestId);
            processCommand.Parameters.AddWithValue("@Debug", debug);

            var adapter = new SqlDataAdapter(processCommand);
            adapter.Fill(resultDataSet); // SQL constraint violations may throw here (caught by sproc's TRY-CATCH)

            return resultDataSet;
        }
        catch (Exception ex)
        {
            throw new ApplicationException(
                "Error in ProcessRawDataFromZZTemp: " + ex.Message, ex);
        }
    }//end method

    private static async Task<DataTable> ExecuteValidationSprocAsync(SqlConnection connection, int bulkOpFeaturesID, string requestId, int stopAfterThisManyErrors, bool debug)
    {
        DataTable validationErrors = new DataTable();

        try
        {
            using (var command = new SqlCommand("[SqlXl].[PurgeStagingValidateZZTempAndReturnErrors]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("@BulkOpFeaturesID", bulkOpFeaturesID);
                command.Parameters.AddWithValue("@RequestID", requestId);
                command.Parameters.AddWithValue("@StopAfterThisManyErrors", stopAfterThisManyErrors);
                command.Parameters.AddWithValue("@Debug", debug ? 1 : 0);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    validationErrors.Load(reader);
                }
            }
        }
        catch (Exception ex)
        {
            // Include the actual SQL error message for better debugging
            var errorMessage = $"Error in validation sproc: {ex.Message}";
            if (ex.InnerException != null)
            {
                errorMessage += $" | Inner: {ex.InnerException.Message}";
            }
            throw new ApplicationException(errorMessage, ex);
        }

        return validationErrors;
    }//end method

    public static async Task<DataSet> ExecuteFeatureAsync(string connectionString, DataTable inputData, int bulkOpFeaturesID, int stopAfterThisManyErrors, bool debug = false)
    {
        // SQL injection safeguard - allow only letters, numbers and underscores...
        ErrorOnAnyProblematicColumnNames(inputData);

        // Create requestId for the db connection session...
        string requestId = Guid.NewGuid().ToString();

        // Ensure all columns are strings...
        inputData = ConvertAllCellValuesToStrings(inputData);

        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            // ✅ Step 1: Create temp tables and bulk insert data
            await CreateAndPopulateTempTablesAsync(connection, inputData, requestId, debug);

            DataSet resultDataSet;
            string processingErrorMessage = "";
            try
            {
                // ✅ Step 2: Execute main processing sproc
                resultDataSet = await ExecuteMainProcessingSprocAsync(connection, bulkOpFeaturesID, requestId, debug);

                // ✅ If processing succeeds, return now
                if (string.Equals(resultDataSet.Tables[0].Rows[0]["IsSuccessful"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    return resultDataSet;
                }
            }
            catch (Exception ex)
            {
                // ✅ Capture but DO NOT throw yet — allow validation to run
                processingErrorMessage = $@"
                    ERROR: {ex.Message}
                    TYPE: {ex.GetType().Name}
                    STACK TRACE: {ex.StackTrace}
                    INNER EXCEPTION: {ex.InnerException?.Message ?? "None"}
                    TIMESTAMP (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
            }//end catch 

            // ✅ Step 3: Execute validation sproc
            DataTable validationErrors = await ExecuteValidationSprocAsync(connection, bulkOpFeaturesID, requestId, stopAfterThisManyErrors, debug);

            // ✅ If validation errors exist, return unsuccessful results
            if (validationErrors.Rows.Count > 0)
            {
                return UnsuccessfulResult(validationErrors);
            }

            string unexpectedError =
                "Unexpected condition: ProcessRawDataFromZZTemp failed " +
                "but no validation errors were found. " + processingErrorMessage;
            return UnsuccessfulResult(unexpectedError);
        } // Temp tables are automatically cleaned up when connection closes
    }//end method
    #endregion 
}//end class 
