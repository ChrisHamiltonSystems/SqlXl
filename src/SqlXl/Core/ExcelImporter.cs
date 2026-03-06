using OfficeOpenXml;
using System.Data;
using System.Text.RegularExpressions;

namespace SqlXl.Core;

public class ExcelImporter
{
    public class ImportResult
    {
        public bool IsSuccessful { get; set; }
        public DataTable ImportedData { get; set; } = new DataTable();
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public int RowsProcessed { get; set; }
        public int EmptyRowsSkipped { get; set; }
    }

    // ============================================================================
    // IMPORTANT: EPPlus License Configuration Required!
    // ============================================================================
    // EPPlus 8.x requires you to set a license before use.
    //
    // FOR PERSONAL NON-COMMERCIAL USE (free):
    //     ExcelPackage.License.SetNonCommercialPersonal("Your Name");
    //
    // FOR NON-COMMERCIAL ORGANIZATION (free):
    //     ExcelPackage.License.SetNonCommercialOrganization("Organization Name");
    //
    // FOR COMMERCIAL USE (requires paid license from https://epplussoftware.com):
    //     ExcelPackage.License.SetCommercial("<Your License Key>");
    //
    // Recommended: Set this once in your application startup (Program.cs or Startup.cs)
    // instead of in this static constructor.
    // See: https://github.com/EPPlusSoftware/EPPlus/wiki/LicenseException
    // ============================================================================

    static ExcelImporter()
    {
        // TODO: REQUIRED - Uncomment the appropriate line below based on your usage:
        // ExcelPackage.License.SetNonCommercialPersonal("Your Name");              // Free - Personal use
        // ExcelPackage.License.SetNonCommercialOrganization("Organization Name");  // Free - Organization
        // ExcelPackage.License.SetCommercial("<Your License Key>");                // Paid - Commercial
    }

    public ImportResult ImportFromExcel(byte[] excelFileBytes, DataTable expectedStructure, DataTable dropdownOptions = null)
    {
        var result = new ImportResult();

        try
        {
            using (var package = new ExcelPackage(new MemoryStream(excelFileBytes)))
            {
                // Get the main data worksheet
                var dataWorksheet = package.Workbook.Worksheets["Data"];
                if (dataWorksheet == null)
                {
                    result.ValidationErrors.Add("Excel file must contain a 'Data' worksheet");
                    return result;
                }

                // Get the metadata worksheet for column mapping
                var metadataWorksheet = package.Workbook.Worksheets["Metadata"];
                Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping = null;

                if (metadataWorksheet != null)
                {
                    columnMapping = ReadMetadataMapping(metadataWorksheet);
                }
                else
                {
                    result.ValidationErrors.Add("Warning: Metadata sheet not found in Excel file");
                }

                // Validate and import the data
                result = ProcessDataWorksheet(dataWorksheet, expectedStructure, dropdownOptions, columnMapping);
            }
        }
        catch (Exception ex)
        {
            result.ValidationErrors.Add($"Error reading Excel file: {ex.Message}");
            result.IsSuccessful = false;
        }

        return result;
    }

    private Dictionary<string, (string DbColumnName, string SqlDataType)> ReadMetadataMapping(ExcelWorksheet metadataWorksheet)
    {
        // Returns a mapping: ExcelColumnName -> (DbColumnName, SqlDataType)
        var mapping = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        if (metadataWorksheet == null || metadataWorksheet.Dimension == null)
            return mapping;

        // Read from row 2 onwards (row 1 is headers: DbColumnName, ExcelColumnName, SqlDataType)
        for (int row = 2; row <= metadataWorksheet.Dimension.Rows; row++)
        {
            var dbColumnName = metadataWorksheet.Cells[row, 1].Value?.ToString();
            var excelColumnName = metadataWorksheet.Cells[row, 2].Value?.ToString();
            var sqlDataType = metadataWorksheet.Cells[row, 3].Value?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(dbColumnName) && !string.IsNullOrWhiteSpace(excelColumnName))
            {
                mapping[excelColumnName] = (dbColumnName, sqlDataType);
            }
        }

        return mapping;
    }

    private ImportResult ProcessDataWorksheet(ExcelWorksheet worksheet, DataTable expectedStructure, DataTable dropdownOptions, Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping = null)
    {
        var result = new ImportResult();

        // Initialize the result DataTable with expected structure
        result.ImportedData = CreateEmptyDataTableFromStructure(expectedStructure);

        if (worksheet.Dimension == null)
        {
            result.ValidationErrors.Add("Data worksheet is empty");
            return result;
        }

        try
        {
            // Read and validate headers
            var headerMapping = ValidateAndMapHeaders(worksheet, expectedStructure, result, columnMapping);
            if (!result.IsSuccessful)
            {
                return result;
            }

            // Process data rows
            ProcessDataRows(worksheet, headerMapping, result, dropdownOptions);

            result.IsSuccessful = result.ValidationErrors.Count == 0;
        }
        catch (Exception ex)
        {
            result.ValidationErrors.Add($"Error processing worksheet: {ex.Message}");
            result.IsSuccessful = false;
        }

        return result;
    }

    private Dictionary<int, (string DbColumnName, string SqlDataType)> ValidateAndMapHeaders(ExcelWorksheet worksheet, DataTable expectedStructure, ImportResult result, Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping = null)
    {
        var headerMapping = new Dictionary<int, (string DbColumnName, string SqlDataType)>(); // Excel column index -> (DbColumnName, SqlDataType)
        var foundHeaders = new HashSet<string>();

        // Read headers from row 1
        for (int col = 1; col <= worksheet.Dimension.Columns; col++)
        {
            var headerValue = worksheet.Cells[1, col].Value?.ToString()?.Trim();

            if (!string.IsNullOrEmpty(headerValue))
            {
                // First, try to map using metadata (Excel column name -> (DB column name, SQL type))
                (string DbColumnName, string SqlDataType) metadata;
                if (columnMapping != null && columnMapping.TryGetValue(headerValue, out metadata))
                {
                    // Found in metadata mapping - use the DB column name and SQL type
                    var matchingColumn = expectedStructure.Columns.Cast<DataColumn>()
                        .FirstOrDefault(c => c.ColumnName.Equals(metadata.DbColumnName, StringComparison.OrdinalIgnoreCase));

                    if (matchingColumn != null)
                    {
                        if (foundHeaders.Contains(matchingColumn.ColumnName))
                        {
                            result.ValidationErrors.Add($"Duplicate column found: {headerValue}");
                            continue;
                        }

                        headerMapping[col] = (matchingColumn.ColumnName, metadata.SqlDataType);
                        foundHeaders.Add(matchingColumn.ColumnName);
                        continue;
                    }
                }

                // Fallback: Find matching column directly (for backwards compatibility, no SQL type)
                var matchingColumn2 = FindMatchingColumn(headerValue, expectedStructure);

                if (matchingColumn2 != null)
                {
                    if (foundHeaders.Contains(matchingColumn2.ColumnName))
                    {
                        result.ValidationErrors.Add($"Duplicate column found: {headerValue}");
                        continue;
                    }

                    headerMapping[col] = (matchingColumn2.ColumnName, ""); // No SQL type for fallback
                    foundHeaders.Add(matchingColumn2.ColumnName);
                }
                else
                {
                    result.ValidationErrors.Add($"Unknown column in Excel file: {headerValue}");
                }
            }
        }

        // Check for missing required columns
        foreach (DataColumn expectedColumn in expectedStructure.Columns)
        {
            if (!foundHeaders.Contains(expectedColumn.ColumnName))
            {
                result.ValidationErrors.Add($"Required column missing from Excel file: {expectedColumn.ColumnName}");
            }
        }

        result.IsSuccessful = result.ValidationErrors.Count == 0;
        return headerMapping;
    }

    private DataColumn FindMatchingColumn(string excelHeader, DataTable expectedStructure)
    {
        // Direct match first
        foreach (DataColumn col in expectedStructure.Columns)
        {
            if (string.Equals(col.ColumnName, excelHeader, StringComparison.OrdinalIgnoreCase))
            {
                return col;
            }
        }

        // Handle pipe syntax matching (ColumnName|Display Name)
        foreach (DataColumn col in expectedStructure.Columns)
        {
            var realColumnName = ExtractRealColumnName(col.ColumnName);
            if (string.Equals(realColumnName, excelHeader, StringComparison.OrdinalIgnoreCase))
            {
                return col;
            }
        }

        return null;
    }

    private void ProcessDataRows(ExcelWorksheet worksheet, Dictionary<int, (string DbColumnName, string SqlDataType)> headerMapping, ImportResult result, DataTable dropdownOptions)
    {
        // Start from row 2 (skip headers)
        for (int row = 2; row <= worksheet.Dimension.Rows; row++)
        {
            // Check if row is empty
            if (IsRowEmpty(worksheet, row, headerMapping.Keys))
            {
                result.EmptyRowsSkipped++;
                continue;
            }

            try
            {
                var dataRow = result.ImportedData.NewRow();
                bool hasData = false;

                // Process each mapped column
                foreach (var mapping in headerMapping)
                {
                    int excelCol = mapping.Key;
                    string dataTableCol = mapping.Value.DbColumnName;
                    string sqlDataType = mapping.Value.SqlDataType;

                    var cellValue = worksheet.Cells[row, excelCol].Value;

                    if (cellValue != null)
                    {
                        var targetColumn = result.ImportedData.Columns[dataTableCol];
                        dataRow[dataTableCol] = ConvertCellValue(cellValue, targetColumn, dropdownOptions, sqlDataType);
                        hasData = true;
                    }
                }

                if (hasData)
                {
                    result.ImportedData.Rows.Add(dataRow);
                    result.RowsProcessed++;
                }
                else
                {
                    result.EmptyRowsSkipped++;
                }
            }
            catch (Exception ex)
            {
                result.ValidationErrors.Add($"Error processing row {row}: {ex.Message}");
            }
        }
    }

    private bool IsRowEmpty(ExcelWorksheet worksheet, int row, IEnumerable<int> columnsToCheck)
    {
        foreach (int col in columnsToCheck)
        {
            var value = worksheet.Cells[row, col].Value;
            if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return false;
            }
        }
        return true;
    }

    private object ConvertCellValue(object cellValue, DataColumn targetColumn, DataTable dropdownOptions, string sqlDataType = "")
    {
        if (cellValue == null)
            return DBNull.Value;

        // Use SQL data type to determine proper conversion
        sqlDataType = sqlDataType?.ToLower() ?? "";

        // Handle date/time types based on SQL type
        if (sqlDataType.Contains("date") || sqlDataType.Contains("time"))
        {
            if (cellValue is DateTime dateTimeValue)
            {
                // Format as ISO 8601 - SQL Server universal format
                return dateTimeValue.ToString("yyyy-MM-ddTHH:mm:ss.fff");
            }
            else if (cellValue is double doubleValue)
            {
                try
                {
                    // Excel stores dates as OLE Automation date
                    var excelDate = DateTime.FromOADate(doubleValue);
                    return excelDate.ToString("yyyy-MM-ddTHH:mm:ss.fff");
                }
                catch
                {
                    // Not a valid date
                    return cellValue.ToString().Trim();
                }
            }
        }

        // Handle numeric types - strip any formatting (commas, etc)
        if (sqlDataType.Contains("int") || sqlDataType.Contains("decimal") ||
            sqlDataType.Contains("numeric") || sqlDataType.Contains("money") ||
            sqlDataType.Contains("float") || sqlDataType.Contains("real"))
        {
            // For numeric types, EPPlus returns native types - convert to plain string
            if (cellValue is int || cellValue is long || cellValue is short || cellValue is byte ||
                cellValue is decimal || cellValue is double || cellValue is float)
            {
                // Return as string without any formatting
                return cellValue.ToString();
            }
        }

        var stringValue = cellValue.ToString().Trim();

        if (string.IsNullOrEmpty(stringValue))
            return DBNull.Value;

        // Check if this column has dropdown options (is a foreign key)
        var fkOptions = dropdownOptions?.AsEnumerable()
            .Where(row => row.Field<string>("ForColumn") == targetColumn.ColumnName)
            .ToList();

        if (fkOptions != null && fkOptions.Any())
        {
            // This is a foreign key column with dropdown options
            // Try to extract the actual FK value from the display text
            string extractedValue = ExtractForeignKeyValue(stringValue, fkOptions);
            return extractedValue ?? stringValue; // Fall back to original if parsing fails
        }

        // For non-FK columns, return as-is
        return stringValue;
    }

    private string ExtractForeignKeyValue(string displayValue, List<DataRow> fkOptions)
    {
        // First, try exact match with a dropdown option
        var exactMatch = fkOptions.FirstOrDefault(row =>
            string.Equals(row.Field<string>("Text"), displayValue, StringComparison.OrdinalIgnoreCase));

        if (exactMatch != null)
        {
            return exactMatch.Field<string>("Value");
        }

        // If no exact match, try to parse "ID - Description" format
        // Common patterns: "1 - HR Department", "1 HR Department", "1-HR Department"
        var match = System.Text.RegularExpressions.Regex.Match(displayValue, @"^(\d+)\s*[-\s]\s*(.+)$");
        if (match.Success)
        {
            string potentialId = match.Groups[1].Value;

            // Verify this ID exists in the dropdown options
            var validOption = fkOptions.FirstOrDefault(row =>
                row.Field<string>("Value") == potentialId);

            if (validOption != null)
            {
                return potentialId;
            }
        }

        // Try just the beginning numbers (in case format is "1 HR Department")
        var numberMatch = System.Text.RegularExpressions.Regex.Match(displayValue, @"^(\d+)");
        if (numberMatch.Success)
        {
            string potentialId = numberMatch.Groups[1].Value;

            var validOption = fkOptions.FirstOrDefault(row =>
                row.Field<string>("Value") == potentialId);

            if (validOption != null)
            {
                return potentialId;
            }
        }

        // If all parsing fails, return null so caller can handle appropriately
        return null;
    }

    private DataTable CreateEmptyDataTableFromStructure(DataTable structure)
    {
        var result = new DataTable();

        foreach (DataColumn sourceColumn in structure.Columns)
        {
            // Create new column with same name but as string type
            // This matches how SlappFramework processes data
            result.Columns.Add(sourceColumn.ColumnName, typeof(string));
        }

        return result;
    }

    private string ExtractRealColumnName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return columnName;

        // Split on pipe and take the first part (real column name)
        var parts = columnName.Split('|', 2);
        return parts[0].Trim();
    }

    public static bool IsValidExcelFile(byte[] fileBytes)
    {
        try
        {
            using (var package = new ExcelPackage(new MemoryStream(fileBytes)))
            {
                return package.Workbook.Worksheets.Count > 0;
            }
        }
        catch
        {
            return false;
        }
    }

    public static List<string> GetWorksheetNames(byte[] fileBytes)
    {
        var worksheetNames = new List<string>();

        try
        {
            using (var package = new ExcelPackage(new MemoryStream(fileBytes)))
            {
                worksheetNames.AddRange(package.Workbook.Worksheets.Select(ws => ws.Name));
            }
        }
        catch
        {
            // Return empty list if file can't be read
        }

        return worksheetNames;
    }
}