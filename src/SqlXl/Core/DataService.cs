using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;
using SqlXl.Models;

namespace SqlXl.Core;

public class DataService
{
    private readonly string _connectionString;
    private readonly IMemoryCache _cache;

    public DataService(string connectionString, IMemoryCache cache)
    {
        _connectionString = connectionString;
        _cache = cache;
    }//end method

    public void SaveGetRowsToEditSelectStatement(int featureID, string selectStatement)
    {//ToDo, change to sproc that validates given selectQuery...
        using(var connection = new SqlConnection(_connectionString))
        {
            // Open the connection
            connection.Open();

            // Define the SQL query
            const string sql = @"
            UPDATE [ZZ_SlappFramework].[BulkOpFeatures]
            SET GetRowsToEdit_SelectStatement = @SelectStatement
            WHERE ID = @FeatureID";

            // Execute the query
            connection.Execute(sql, new
            {
                FeatureID = featureID,
                SelectStatement = selectStatement
            });//end exec
        }//end using
    }//end method

    public BulkOpFeature GetBulkOpFeature(int featureID)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            const string query = @"
                    SELECT
                        ID,
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
                    FROM
                        ZZ_SlappFramework.BulkOpFeatures
                    WHERE
                        ID = @Id";
            return connection.QuerySingle<BulkOpFeature>(query, new { Id = featureID });
        }//end using 
    }//end method

    public string CallValidateThenRunSelectQuerySproc(string query)
    {
        string jsonResult = null;

        // Create and open a SQL connection
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            connection.Open();

            // Create the command for the stored procedure
            using (SqlCommand command = new SqlCommand("[ZZ_SlappFramework].ValidateThenRunSelectQueryReturnJsonMetadataAndData", connection))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Add the input parameter
                command.Parameters.Add(new SqlParameter("@Query", SqlDbType.NVarChar)
                {
                    Value = query
                });

                // Execute the stored procedure
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    // Skip the first result set
                    if (reader.NextResult())
                    {
                        // Read the second result set
                        if (reader.Read())
                        {
                            // The stored procedure returns the scalar JSON result in the second result set
                            jsonResult = reader.GetString(0);
                        }
                    }
                }//end using
            }//end using 
        }//end using 

        return jsonResult;
    }//end method

    public DataTable ExecuteStoredProcedure(string storedProcedureName)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();

            // Execute the stored procedure using Dapper and get an IDataReader
            using (var reader = connection.ExecuteReader(storedProcedureName, commandType: CommandType.StoredProcedure))
            {
                var dataTable = new DataTable();
                dataTable.Load(reader); // Populate the DataTable
                return dataTable;
            }//end using 
        }//end using 
    }//end method

    public string CallGetStartingPointDataSproc(int featureID, string sprocName, string selectedIds = null)
    {
        // Call existing sproc that returns 2 result sets
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            using (SqlCommand command = new SqlCommand($"[ZZ_SlappFramework].[{sprocName}]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add(new SqlParameter("@FeatureID", SqlDbType.Int) { Value = featureID });

                // Add optional selectedIds parameter if provided
                if (selectedIds != null)
                {
                    command.Parameters.Add(new SqlParameter("@SelectedIds", SqlDbType.NVarChar, -1) { Value = selectedIds });
                }

                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    DataSet dataSet = new DataSet();
                    adapter.Fill(dataSet);

                    // Extract JSON strings from both result sets
                    // (Notice first resultset is skipped entirely,
                    //  as those values are also in json in second result)...
                    string metaAndDataJson = dataSet.Tables[1].Rows[0][0]?.ToString() ?? "{}";
                    string dropdownJson = dataSet.Tables[2].Rows[0][0]?.ToString() ?? "[]";

                    // Simple string replacement to combine them
                    string combinedJson = metaAndDataJson.TrimEnd('}') +
                        ",\"DropdownOptions\":" + dropdownJson + "}";

                    return combinedJson;
                }//end using
            }//end using
        }//end using
    }//end method

    public string CallGetMeta_ColumnsForTableAsJsonSproc(int featureID)
    {
        string jsonResult = null;

        // Create and open a SQL connection
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            connection.Open();

            // Create the command for the stored procedure
            using (SqlCommand command = new SqlCommand("ZZ_SlappFramework.GetMeta_ColumnsForTableAsJson", connection))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Add the input parameter
                command.Parameters.Add(new SqlParameter("@FeatureID", SqlDbType.Int)
                {
                    Value = featureID
                });

                // Execute the stored procedure
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.HasRows && reader.Read())
                    {
                        // The stored procedure returns the scalar JSON result in the second result set
                        jsonResult = reader.GetString(0);
                    }//end if 
                }//end using
            }//end using 
        }//end using 

        return jsonResult;
    }//end method

    public string CallGetRowsToEditSproc(int featureID, string selectedIds)
    {
        // Call sproc that returns edit data for specific rows using base table query
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            using (SqlCommand command = new SqlCommand("[ZZ_SlappFramework].[GetRowsToEdit]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add(new SqlParameter("@FeatureID", SqlDbType.Int) { Value = featureID });
                command.Parameters.Add(new SqlParameter("@SelectedIds", SqlDbType.NVarChar, -1) { Value = selectedIds });

                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    DataSet dataSet = new DataSet();
                    adapter.Fill(dataSet);

                    // Extract JSON strings from result sets
                    // First resultset is skipped, second has the JSON data
                    string metaAndDataJson = dataSet.Tables[1].Rows[0][0]?.ToString() ?? "{}";
                    string dropdownJson = dataSet.Tables[2].Rows[0][0]?.ToString() ?? "[]";
                    string pkInfoJson = dataSet.Tables[3].Rows[0][0]?.ToString() ?? "{}";

                    // Combine JSON results
                    string combinedJson = metaAndDataJson.TrimEnd('}') +
                        ",\"DropdownOptions\":" + dropdownJson +
                        ",\"PrimaryKeyInfo\":" + pkInfoJson + "}";

                    return combinedJson;
                }//end using
            }//end using
        }//end using
    }//end method

    public DataSet CallGetFormStarterDataSproc(int featureID)
    {
        // Initialize the DataSet to hold the result sets
        DataSet dataSet = new DataSet();

        // Connection to the SQL Server database
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            // Open the database connection
            connection.Open();

            // Create the SQL command to execute the stored procedure
            using (SqlCommand command = new SqlCommand("[ZZ_SlappFramework].[GetFormStarterData]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Add the input parameter for FeatureID
                command.Parameters.Add(new SqlParameter("@FeatureID", SqlDbType.Int)
                {
                    Value = featureID
                });

                // Use SqlDataAdapter to fill the DataSet with the results
                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    // Fill the DataSet with the two result sets
                    adapter.Fill(dataSet);
                }//end using
            }//end using
        }//end using 

        return dataSet; // Return the populated DataSet
    }//end method

    public DataSet CallBulkOpsHelperToExecuteFeature(int featureID, DataTable dataTable)
    {
        // Initialize settings...
        var bulkOpsSettings = new BulkOpsSettings();
        bulkOpsSettings.ConnectionString = _connectionString;
        bulkOpsSettings.StopAfterThisManyErrors = 1;//ToDo??

        // Initialize a BulkOpsHelper...
        var bulkOpsHelper = new BulkOpsHelper(bulkOpsSettings);
        //bulkOpsHelper.WriteToZZDebug(dataTable);
        DataSet results = bulkOpsHelper.ExecuteFeature(featureID, dataTable, debug: true);

        return results;
    }//end method 

    public DataSet CallGetExcelTemplateDataSproc(int featureID, string selectedIds = null)
    {
        // Initialize the DataSet to hold the result sets
        DataSet dataSet = new DataSet();

        // Connection to the SQL Server database
        using (SqlConnection connection = new SqlConnection(_connectionString))
        {
            // Open the database connection
            connection.Open();

            // Create the SQL command to execute the stored procedure
            using (SqlCommand command = new SqlCommand("[ZZ_SlappFramework].[GetExcelTemplateData]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;

                // Add the input parameter for FeatureID
                command.Parameters.Add(new SqlParameter("@FeatureID", SqlDbType.Int)
                {
                    Value = featureID
                });

                // Add the optional SelectedIds parameter
                command.Parameters.Add(new SqlParameter("@SelectedIds", SqlDbType.NVarChar, -1)
                {
                    Value = selectedIds ?? (object)DBNull.Value
                });

                // Use SqlDataAdapter to fill the DataSet with the results
                using (SqlDataAdapter adapter = new SqlDataAdapter(command))
                {
                    // Fill the DataSet with the two result sets
                    adapter.Fill(dataSet);
                }//end using
            }//end using
        }//end using

        return dataSet; // Return the populated DataSet
    }//end method

    public DataTable GetDropdownOptionsForFeature(int featureID)
    {
        using (var connection = new SqlConnection(_connectionString))
        {
            connection.Open();
            using (var command = new SqlCommand("[ZZ_SlappFramework].[GetDropDownOptionsForFeature]", connection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("@FeatureID", featureID);

                using (var adapter = new SqlDataAdapter(command))
                {
                    var dataTable = new DataTable();
                    adapter.Fill(dataTable);
                    return dataTable;
                }//end using
            }//end using
        }//end using
    }//end method
}//end class
