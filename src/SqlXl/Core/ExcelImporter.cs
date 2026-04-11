using ClosedXML.Excel;
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

    public ImportResult ImportFromExcel(byte[] excelFileBytes, DataTable expectedStructure, DataTable dropdownOptions = null)
    {
        var result = new ImportResult();

        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(excelFileBytes));

            var dataWorksheet = workbook.Worksheets.FirstOrDefault(ws => ws.Name == "Data");
            if (dataWorksheet == null)
            {
                result.ValidationErrors.Add("Excel file must contain a 'Data' worksheet");
                return result;
            }

            Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping = null;
            var metadataWorksheet = workbook.Worksheets.FirstOrDefault(ws => ws.Name == "Metadata");
            if (metadataWorksheet != null)
                columnMapping = ReadMetadataMapping(metadataWorksheet);
            else
                result.ValidationErrors.Add("Warning: Metadata sheet not found in Excel file");

            result = ProcessDataWorksheet(dataWorksheet, expectedStructure, dropdownOptions, columnMapping);
        }
        catch (Exception ex)
        {
            result.ValidationErrors.Add($"Error reading Excel file: {ex.Message}");
            result.IsSuccessful = false;
        }

        return result;
    }

    private Dictionary<string, (string DbColumnName, string SqlDataType)> ReadMetadataMapping(IXLWorksheet ws)
    {
        // Returns: ExcelColumnName -> (DbColumnName, SqlDataType)
        var mapping = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            var dbColumnName   = ws.Cell(row, 1).GetValue<string>();
            var excelColumnName = ws.Cell(row, 2).GetValue<string>();
            var sqlDataType    = ws.Cell(row, 3).GetValue<string>() ?? "";

            if (!string.IsNullOrWhiteSpace(dbColumnName) && !string.IsNullOrWhiteSpace(excelColumnName))
                mapping[excelColumnName] = (dbColumnName, sqlDataType);
        }

        return mapping;
    }

    private ImportResult ProcessDataWorksheet(IXLWorksheet ws, DataTable expectedStructure,
                                              DataTable dropdownOptions,
                                              Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping)
    {
        var result = new ImportResult();
        result.ImportedData = CreateEmptyDataTableFromStructure(expectedStructure);

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastRow == 0)
        {
            result.ValidationErrors.Add("Data worksheet is empty");
            return result;
        }

        try
        {
            var headerMapping = ValidateAndMapHeaders(ws, expectedStructure, result, columnMapping);
            if (!result.IsSuccessful)
                return result;

            ProcessDataRows(ws, lastRow, headerMapping, result, dropdownOptions);
            result.IsSuccessful = result.ValidationErrors.Count == 0;
        }
        catch (Exception ex)
        {
            result.ValidationErrors.Add($"Error processing worksheet: {ex.Message}");
            result.IsSuccessful = false;
        }

        return result;
    }

    private Dictionary<int, (string DbColumnName, string SqlDataType)> ValidateAndMapHeaders(
        IXLWorksheet ws, DataTable expectedStructure, ImportResult result,
        Dictionary<string, (string DbColumnName, string SqlDataType)> columnMapping)
    {
        var headerMapping = new Dictionary<int, (string DbColumnName, string SqlDataType)>();
        var foundHeaders  = new HashSet<string>();

        int lastCol = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int col = 1; col <= lastCol; col++)
        {
            var headerValue = ws.Cell(1, col).GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(headerValue)) continue;

            // Try metadata mapping first (ExcelColumnName → DbColumnName + SqlDataType)
            if (columnMapping != null && columnMapping.TryGetValue(headerValue, out var metadata))
            {
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

            // Fallback: direct column name match
            var fallback = FindMatchingColumn(headerValue, expectedStructure);
            if (fallback != null)
            {
                if (foundHeaders.Contains(fallback.ColumnName))
                {
                    result.ValidationErrors.Add($"Duplicate column found: {headerValue}");
                    continue;
                }
                headerMapping[col] = (fallback.ColumnName, "");
                foundHeaders.Add(fallback.ColumnName);
            }
            else
            {
                result.ValidationErrors.Add($"Unknown column in Excel file: {headerValue}");
            }
        }

        foreach (DataColumn expectedColumn in expectedStructure.Columns)
        {
            if (!foundHeaders.Contains(expectedColumn.ColumnName))
                result.ValidationErrors.Add($"Required column missing from Excel file: {expectedColumn.ColumnName}");
        }

        result.IsSuccessful = result.ValidationErrors.Count == 0;
        return headerMapping;
    }

    private DataColumn FindMatchingColumn(string excelHeader, DataTable expectedStructure)
    {
        foreach (DataColumn col in expectedStructure.Columns)
            if (string.Equals(col.ColumnName, excelHeader, StringComparison.OrdinalIgnoreCase))
                return col;

        // Pipe-syntax fallback
        foreach (DataColumn col in expectedStructure.Columns)
            if (string.Equals(ExtractRealColumnName(col.ColumnName), excelHeader, StringComparison.OrdinalIgnoreCase))
                return col;

        return null;
    }

    private void ProcessDataRows(IXLWorksheet ws, int lastRow,
                                 Dictionary<int, (string DbColumnName, string SqlDataType)> headerMapping,
                                 ImportResult result, DataTable dropdownOptions)
    {
        for (int row = 2; row <= lastRow; row++)
        {
            if (IsRowEmpty(ws, row, headerMapping.Keys))
            {
                result.EmptyRowsSkipped++;
                continue;
            }

            try
            {
                var dataRow = result.ImportedData.NewRow();
                bool hasData = false;

                foreach (var mapping in headerMapping)
                {
                    int    excelCol    = mapping.Key;
                    string dbCol       = mapping.Value.DbColumnName;
                    string sqlDataType = mapping.Value.SqlDataType;

                    var cellValue = GetCellObject(ws.Cell(row, excelCol));
                    if (cellValue != null)
                    {
                        var targetColumn = result.ImportedData.Columns[dbCol];
                        dataRow[dbCol] = ConvertCellValue(cellValue, targetColumn, dropdownOptions, sqlDataType);
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

    // Converts an XLCellValue to a plain CLR object so ConvertCellValue can work type-safely.
    private object GetCellObject(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        return cell.DataType switch
        {
            XLDataType.Blank    => null,
            XLDataType.Boolean  => (object)cell.GetValue<bool>(),
            XLDataType.Number   => (object)cell.GetValue<double>(),
            XLDataType.Text     => (object)cell.GetValue<string>(),
            XLDataType.DateTime => (object)cell.GetValue<DateTime>(),
            XLDataType.TimeSpan => (object)cell.GetValue<TimeSpan>().ToString(),
            _                   => (object)cell.GetString()
        };
    }

    private bool IsRowEmpty(IXLWorksheet ws, int row, IEnumerable<int> columnsToCheck)
    {
        foreach (int col in columnsToCheck)
        {
            var cell = ws.Cell(row, col);
            if (!cell.IsEmpty() && !string.IsNullOrWhiteSpace(cell.GetString()))
                return false;
        }
        return true;
    }

    private object ConvertCellValue(object cellValue, DataColumn targetColumn, DataTable dropdownOptions, string sqlDataType = "")
    {
        if (cellValue == null)
            return DBNull.Value;

        sqlDataType = sqlDataType?.ToLower() ?? "";

        // Date/time columns
        if (sqlDataType.Contains("date") || sqlDataType.Contains("time"))
        {
            if (cellValue is DateTime dt)
                return dt.ToString("yyyy-MM-ddTHH:mm:ss.fff");

            if (cellValue is double d)
            {
                try { return DateTime.FromOADate(d).ToString("yyyy-MM-ddTHH:mm:ss.fff"); }
                catch { return cellValue.ToString().Trim(); }
            }
        }

        // Numeric columns — if ClosedXML gave us a native double, convert to string
        if (sqlDataType.Contains("int") || sqlDataType.Contains("decimal") ||
            sqlDataType.Contains("numeric") || sqlDataType.Contains("money") ||
            sqlDataType.Contains("float") || sqlDataType.Contains("real"))
        {
            if (cellValue is double || cellValue is int || cellValue is long ||
                cellValue is short || cellValue is byte || cellValue is decimal || cellValue is float)
                return cellValue.ToString();
        }

        var stringValue = cellValue.ToString().Trim();
        if (string.IsNullOrEmpty(stringValue))
            return DBNull.Value;

        // FK columns — resolve display value back to the raw key
        var fkOptions = dropdownOptions?.AsEnumerable()
            .Where(r => r.Field<string>("ForColumn") == targetColumn.ColumnName)
            .ToList();

        if (fkOptions != null && fkOptions.Any())
        {
            string extracted = ExtractForeignKeyValue(stringValue, fkOptions);
            return extracted ?? stringValue;
        }

        return stringValue;
    }

    private string ExtractForeignKeyValue(string displayValue, List<DataRow> fkOptions)
    {
        // Exact match against dropdown Text
        var exact = fkOptions.FirstOrDefault(r =>
            string.Equals(r.Field<string>("Text"), displayValue, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Field<string>("Value");

        // "1 - Electronics" pattern
        var m = Regex.Match(displayValue, @"^(\d+)\s*[-\s]\s*(.+)$");
        if (m.Success)
        {
            string id = m.Groups[1].Value;
            if (fkOptions.Any(r => r.Field<string>("Value") == id)) return id;
        }

        // Leading digits fallback
        var n = Regex.Match(displayValue, @"^(\d+)");
        if (n.Success)
        {
            string id = n.Groups[1].Value;
            if (fkOptions.Any(r => r.Field<string>("Value") == id)) return id;
        }

        return null;
    }

    private DataTable CreateEmptyDataTableFromStructure(DataTable structure)
    {
        var result = new DataTable();
        foreach (DataColumn col in structure.Columns)
            result.Columns.Add(col.ColumnName, typeof(string));
        return result;
    }

    private string ExtractRealColumnName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return columnName;
        return columnName.Split('|', 2)[0].Trim();
    }

    public static bool IsValidExcelFile(byte[] fileBytes)
    {
        try
        {
            using var wb = new XLWorkbook(new MemoryStream(fileBytes));
            return wb.Worksheets.Count > 0;
        }
        catch { return false; }
    }

    public static List<string> GetWorksheetNames(byte[] fileBytes)
    {
        try
        {
            using var wb = new XLWorkbook(new MemoryStream(fileBytes));
            return wb.Worksheets.Select(ws => ws.Name).ToList();
        }
        catch { return new List<string>(); }
    }
}
