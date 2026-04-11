using ClosedXML.Excel;
using System.Data;

namespace SqlXl.Core;

public class ExcelTemplateGenerator
{
    public byte[] GenerateExcelTemplate(DataSet templateData)
    {
        using var workbook = new XLWorkbook();

        var dataSheet     = workbook.Worksheets.Add("Data");
        var dropdownSheet = workbook.Worksheets.Add("DropdownOptions");
        var metadataSheet = workbook.Worksheets.Add("Metadata");

        string pkColumn = FindPrimaryKeyColumn(templateData.Tables[2]);

        CreateDataSheet(dataSheet, templateData.Tables[0], pkColumn);
        CreateDropdownOptionsSheet(dropdownSheet, templateData.Tables[1]);
        CreateMetadataSheet(metadataSheet, templateData.Tables[0], templateData.Tables[2]);
        ApplyDropdownValidation(dataSheet, dropdownSheet, metadataSheet, templateData.Tables[1]);

        // Reference sheets are read-only — data sheet is intentionally unprotected
        // so users can freely add/edit rows. Staging table validation is the safety net.
        dropdownSheet.Protect();
        metadataSheet.Protect();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // Generates a plain single-sheet Excel from any DataTable — no template structure,
    // no DropdownOptions, no Metadata. Used by the export command for raw query dumps.
    public byte[] GenerateSimpleExcel(DataTable data)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Data");
        CreateDataSheet(ws, data, pkColumn: null);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private string FindPrimaryKeyColumn(DataTable metaColumnsTable)
    {
        if (metaColumnsTable == null) return null;
        var pkRow = metaColumnsTable.AsEnumerable()
            .FirstOrDefault(r => r.Field<string>("IsPrimaryKey") == "YES");
        return pkRow?.Field<string>("ColumnName");
    }

    private void CreateDataSheet(IXLWorksheet ws, DataTable data, string pkColumn)
    {
        // Headers — bold with a light blue-gray background
        for (int col = 0; col < data.Columns.Count; col++)
        {
            string displayName = ExtractDisplayName(data.Columns[col].ColumnName);
            var cell = ws.Cell(1, col + 1);
            cell.Value = displayName;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");
        }

        // Data rows
        for (int row = 0; row < data.Rows.Count; row++)
        {
            for (int col = 0; col < data.Columns.Count; col++)
            {
                var cellValue  = data.Rows[row][col];
                var cell       = ws.Cell(row + 2, col + 1);
                var colType    = data.Columns[col].DataType;
                string dbCol   = ExtractRealColumnName(data.Columns[col].ColumnName);
                bool isPk      = !string.IsNullOrEmpty(pkColumn) &&
                                  dbCol.Equals(pkColumn, StringComparison.OrdinalIgnoreCase);

                if (cellValue == DBNull.Value || cellValue == null)
                {
                    // leave blank
                }
                else if (colType == typeof(DateTime))
                {
                    cell.Value = (DateTime)cellValue;
                    cell.Style.NumberFormat.Format = "mm/dd/yyyy";
                }
                else if (colType == typeof(decimal) || colType == typeof(double) || colType == typeof(float))
                {
                    cell.Value = Convert.ToDouble(cellValue);
                    cell.Style.NumberFormat.Format = "#,##0.00";
                }
                else if (colType == typeof(int) || colType == typeof(long) ||
                         colType == typeof(short) || colType == typeof(byte))
                {
                    cell.Value = Convert.ToDouble(cellValue);
                    cell.Style.NumberFormat.Format = "0";
                }
                else if (colType == typeof(bool))
                {
                    cell.Value = (bool)cellValue;
                }
                else
                {
                    cell.Value = cellValue.ToString();
                }

                // PK cells get a light gray background as a visual hint (not locked)
                if (isPk)
                    cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            }
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private void CreateDropdownOptionsSheet(IXLWorksheet ws, DataTable dropdownData)
    {
        ws.Cell(1, 1).Value = "ForColumn";
        ws.Cell(1, 2).Value = "OptionText";
        ws.Row(1).Style.Font.Bold = true;

        for (int row = 0; row < dropdownData.Rows.Count; row++)
        {
            ws.Cell(row + 2, 1).Value = dropdownData.Rows[row]["ForColumn"]?.ToString() ?? "";
            ws.Cell(row + 2, 2).Value = dropdownData.Rows[row]["Text"]?.ToString() ?? "";
        }

        ws.Columns().AdjustToContents();
    }

    private void CreateMetadataSheet(IXLWorksheet ws, DataTable dataTable, DataTable metaColumnsTable)
    {
        ws.Cell(1, 1).Value = "DbColumnName";
        ws.Cell(1, 2).Value = "ExcelColumnName";
        ws.Cell(1, 3).Value = "SqlDataType";
        ws.Cell(1, 4).Value = "IsPrimaryKey";
        ws.Row(1).Style.Font.Bold = true;

        int row = 2;
        foreach (DataColumn column in dataTable.Columns)
        {
            string dbColName    = ExtractRealColumnName(column.ColumnName);
            string excelColName = ExtractDisplayName(column.ColumnName);
            string sqlDataType  = "";
            string isPrimaryKey = "";

            if (metaColumnsTable != null)
            {
                var metaRow = metaColumnsTable.AsEnumerable()
                    .FirstOrDefault(r => r.Field<string>("ColumnName")
                        .Equals(dbColName, StringComparison.OrdinalIgnoreCase));
                if (metaRow != null)
                {
                    sqlDataType  = metaRow.Field<string>("SqlDataType") ?? "";
                    isPrimaryKey = metaRow.Field<string>("IsPrimaryKey") ?? "";
                }
            }

            ws.Cell(row, 1).Value = dbColName;
            ws.Cell(row, 2).Value = excelColName;
            ws.Cell(row, 3).Value = sqlDataType;
            ws.Cell(row, 4).Value = isPrimaryKey;
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    private void ApplyDropdownValidation(IXLWorksheet dataSheet, IXLWorksheet dropdownSheet,
                                         IXLWorksheet metadataSheet, DataTable dropdownTable)
    {
        if (dropdownTable.Rows.Count == 0) return;

        // Build ExcelColumnName → DbColumnName from the Metadata sheet
        var excelToDb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int lastMetaRow = metadataSheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastMetaRow; r++)
        {
            var dbCol    = metadataSheet.Cell(r, 1).GetValue<string>();
            var excelCol = metadataSheet.Cell(r, 2).GetValue<string>();
            if (!string.IsNullOrEmpty(dbCol) && !string.IsNullOrEmpty(excelCol))
                excelToDb[excelCol] = dbCol;
        }

        // Build header → column index from the Data sheet
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int lastDataCol = dataSheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (int c = 1; c <= lastDataCol; c++)
        {
            var header = dataSheet.Cell(1, c).GetValue<string>();
            if (!string.IsNullOrEmpty(header))
                headers[header] = c;
        }

        var fkColumns      = dropdownTable.AsEnumerable()
                                          .Select(r => r.Field<string>("ForColumn"))
                                          .Distinct().ToList();
        int lastDropRow    = dropdownSheet.LastRowUsed()?.RowNumber() ?? 1;
        int maxDataRows    = Math.Max(1000, dataSheet.LastRowUsed()?.RowNumber() ?? 100);

        foreach (var fkColumn in fkColumns)
        {
            var excelColName = excelToDb.FirstOrDefault(x => x.Value == fkColumn).Key;
            if (string.IsNullOrEmpty(excelColName) || !headers.ContainsKey(excelColName))
                continue;

            int colIdx = headers[excelColName];

            // Find the contiguous rows for this FK in the DropdownOptions sheet (column 2)
            var optionRows = new List<int>();
            for (int r = 2; r <= lastDropRow; r++)
            {
                if (dropdownSheet.Cell(r, 1).GetValue<string>() == fkColumn)
                    optionRows.Add(r);
            }

            if (!optionRows.Any()) continue;

            var optionsRange    = dropdownSheet.Range(optionRows.Min(), 2, optionRows.Max(), 2);
            var validationRange = dataSheet.Range(2, colIdx, maxDataRows, colIdx);
            var validation      = validationRange.CreateDataValidation();
            validation.List(optionsRange);
            validation.IgnoreBlanks     = true;
            validation.ShowErrorMessage = true;
            validation.ErrorTitle       = "Invalid Selection";
            validation.ErrorMessage     = $"Please select a valid option for {excelColName}";
        }
    }

    private string ExtractRealColumnName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return columnName;
        return columnName.Split('|', 2)[0].Trim();
    }

    private string ExtractDisplayName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return columnName;
        var parts = columnName.Split('|', 2);
        return parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
    }
}
