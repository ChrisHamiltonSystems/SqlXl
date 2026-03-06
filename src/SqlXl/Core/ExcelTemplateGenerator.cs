using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Data;
using System.Drawing;

namespace SqlXl.Core;

public class ExcelTemplateGenerator
{
    /*ToDo...
     // Color-coding columns - BUILT-IN feature
dataSheet.Cells[1, col, lastRow, col].Style.Fill.BackgroundColor.SetColor(Color.LightGray); // Read-only
dataSheet.Cells[1, col, lastRow, col].Style.Locked = true; // Prevent editing

    handsontable: 
    // Read-only columns - CORE feature since day 1
columns: [
    { data: 'Name', readOnly: false, className: 'editable' },
    { data: 'HireDate', readOnly: true, className: 'readonly' }
]
     **********/

    // Define your dark theme colors
    private static readonly Color DarkBackground = ColorTranslator.FromHtml("#010e1b");     // Your body background
    private static readonly Color LightText = ColorTranslator.FromHtml("#c7fcfc");         // Your light text
    private static readonly Color HeaderBackground = ColorTranslator.FromHtml("#213149");   // Your component background
    private static readonly Color HeaderText = ColorTranslator.FromHtml("#8495a7");        // Your muted text
    private static readonly Color AccentBlue = ColorTranslator.FromHtml("#1a73e8");        // Your primary blue
    private static readonly Color BorderColor = ColorTranslator.FromHtml("#213149");       // Your border color
    private static readonly Color ReadOnlyBackground = ColorTranslator.FromHtml("#2a2a2a"); // Slightly lighter gray for read-only PK cells

    public byte[] GenerateExcelTemplate(DataSet templateData)
    {
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
        // Uncomment ONE of the lines above based on your usage.
        // Recommended: Set this once in your application startup (Program.cs or Startup.cs)
        // See: https://github.com/EPPlusSoftware/EPPlus/wiki/LicenseException
        // ============================================================================

        // TODO: REQUIRED - Uncomment the appropriate line below based on your usage:
        // ExcelPackage.License.SetNonCommercialPersonal("Your Name");              // Free - Personal use
        // ExcelPackage.License.SetNonCommercialOrganization("Organization Name");  // Free - Organization
        // ExcelPackage.License.SetCommercial("<Your License Key>");                // Paid - Commercial

        using (var package = new ExcelPackage())
        {
            // Create Sheet1 - Editable data
            var sheet1 = package.Workbook.Worksheets.Add("Data");
            CreateDataSheet(sheet1, templateData.Tables[0], templateData.Tables[2]);

            // Create Sheet2 - Dropdown options (reference)
            var sheet2 = package.Workbook.Worksheets.Add("DropdownOptions");
            CreateDropdownOptionsSheet(sheet2, templateData.Tables[1]);

            // Create Sheet3 - Metadata (column mapping)
            var sheet3 = package.Workbook.Worksheets.Add("Metadata");
            CreateMetadataSheet(sheet3, templateData.Tables[0], templateData.Tables[2]);

            // Apply dropdown validation to foreign key columns in Sheet1
            ApplyDropdownValidation(sheet1, sheet2, sheet3, templateData.Tables[1]);

            // Set sheet protection on Data sheet (allows editing unlocked cells, blocks editing locked PK cells)
            sheet1.Protection.IsProtected = true;
            sheet1.Protection.AllowSelectLockedCells = true;
            sheet1.Protection.AllowSelectUnlockedCells = true;

            // Set sheet protection (make reference sheets completely read-only)
            sheet2.Protection.IsProtected = true;
            sheet2.Protection.AllowSelectLockedCells = true;
            sheet2.Protection.AllowSelectUnlockedCells = false;

            sheet3.Protection.IsProtected = true;
            sheet3.Protection.AllowSelectLockedCells = true;
            sheet3.Protection.AllowSelectUnlockedCells = false;

            // Return the Excel file as byte array
            return package.GetAsByteArray();
        }//end using
    }//end method

    private void CreateDataSheet(ExcelWorksheet worksheet, DataTable dataTable, DataTable metaColumnsTable)
    {
        // Set worksheet background
        ApplyDarkThemeToWorksheet(worksheet);

        // Find THE primary key column (singular - framework assumes one PK per table)
        string primaryKeyColumn = null;
        if (metaColumnsTable != null)
        {
            var pkRow = metaColumnsTable.AsEnumerable()
                .FirstOrDefault(r => r.Field<string>("IsPrimaryKey") == "YES");
            if (pkRow != null)
            {
                primaryKeyColumn = pkRow.Field<string>("ColumnName");
            }
        }

        // Add headers - extract display names (after the pipe, or column name if no pipe)
        for (int col = 0; col < dataTable.Columns.Count; col++)
        {
            string fullColumnName = dataTable.Columns[col].ColumnName;
            string displayColumnName = ExtractDisplayName(fullColumnName);

            var headerCell = worksheet.Cells[1, col + 1];
            headerCell.Value = displayColumnName;

            // Style headers with dark theme
            StyleHeaderCell(headerCell);
        }//end for

        // Add data rows
        for (int row = 0; row < dataTable.Rows.Count; row++)
        {
            for (int col = 0; col < dataTable.Columns.Count; col++)
            {
                var cellValue = dataTable.Rows[row][col];
                var dataCell = worksheet.Cells[row + 2, col + 1];
                var columnType = dataTable.Columns[col].DataType;
                string fullColumnName = dataTable.Columns[col].ColumnName;
                string dbColumnName = ExtractRealColumnName(fullColumnName);

                // Set cell value and apply appropriate formatting based on data type
                if (cellValue == DBNull.Value)
                {
                    dataCell.Value = null;
                }
                else if (columnType == typeof(DateTime))
                {
                    dataCell.Value = cellValue;
                    dataCell.Style.Numberformat.Format = "mm/dd/yyyy";
                }
                else if (columnType == typeof(decimal) || columnType == typeof(double) || columnType == typeof(float))
                {
                    dataCell.Value = cellValue;
                    dataCell.Style.Numberformat.Format = "#,##0.00";
                }
                else if (columnType == typeof(int) || columnType == typeof(long) || columnType == typeof(short) || columnType == typeof(byte))
                {
                    dataCell.Value = cellValue;
                    dataCell.Style.Numberformat.Format = "#,##0";
                }
                else if (columnType == typeof(bool))
                {
                    dataCell.Value = cellValue;
                    // Excel will display True/False by default for boolean values
                }
                else
                {
                    // Default for strings and other types
                    dataCell.Value = cellValue;
                }

                // Check if this column is the primary key
                bool isPrimaryKey = !string.IsNullOrEmpty(primaryKeyColumn) &&
                                    dbColumnName.Equals(primaryKeyColumn, StringComparison.OrdinalIgnoreCase);

                if (isPrimaryKey)
                {
                    // Lock and style PK cells as read-only
                    dataCell.Style.Locked = true;
                    StyleReadOnlyDataCell(dataCell);
                }
                else
                {
                    // Unlock and style non-PK cells as editable
                    dataCell.Style.Locked = false;
                    StyleDataCell(dataCell);
                }
            }//end for
        }//end for

        // Apply borders to the data range
        if (dataTable.Rows.Count > 0)
        {
            var dataRange = worksheet.Cells[1, 1, dataTable.Rows.Count + 1, dataTable.Columns.Count];
            ApplyDarkBorders(dataRange);
        }

        // Auto-fit columns
        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

        // Freeze the top row (header row)
        worksheet.View.FreezePanes(2, 1); // Freeze everything above row 2
    }//end method

    private void CreateDropdownOptionsSheet(ExcelWorksheet worksheet, DataTable dropdownTable)
    {
        // Set worksheet background
        ApplyDarkThemeToWorksheet(worksheet);

        // Add headers
        var headerCell1 = worksheet.Cells[1, 1];
        var headerCell2 = worksheet.Cells[1, 2];
        headerCell1.Value = "ForColumn";
        headerCell2.Value = "OptionText";

        // Style headers
        StyleHeaderCell(headerCell1);
        StyleHeaderCell(headerCell2);

        // Add dropdown data
        for (int row = 0; row < dropdownTable.Rows.Count; row++)
        {
            var forColumnCell = worksheet.Cells[row + 2, 1];
            var optionTextCell = worksheet.Cells[row + 2, 2];

            forColumnCell.Value = dropdownTable.Rows[row]["ForColumn"];
            optionTextCell.Value = dropdownTable.Rows[row]["Text"];

            // Style data cells
            StyleReferenceDataCell(forColumnCell);
            StyleReferenceDataCell(optionTextCell);
        }//end for

        // Apply borders
        if (dropdownTable.Rows.Count > 0)
        {
            var dataRange = worksheet.Cells[1, 1, dropdownTable.Rows.Count + 1, 2];
            ApplyDarkBorders(dataRange);
        }
        else
        {
            var headerRange = worksheet.Cells[1, 1, 1, 2];
            ApplyDarkBorders(headerRange);
        }

        // Auto-fit columns
        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
    }//end method

    private void CreateMetadataSheet(ExcelWorksheet worksheet, DataTable dataTable, DataTable metaColumnsTable)
    {
        // Set worksheet background
        ApplyDarkThemeToWorksheet(worksheet);

        // Add headers
        var headerCell1 = worksheet.Cells[1, 1];
        var headerCell2 = worksheet.Cells[1, 2];
        var headerCell3 = worksheet.Cells[1, 3];
        var headerCell4 = worksheet.Cells[1, 4];
        headerCell1.Value = "DbColumnName";
        headerCell2.Value = "ExcelColumnName";
        headerCell3.Value = "SqlDataType";
        headerCell4.Value = "IsPrimaryKey";

        // Style headers
        StyleHeaderCell(headerCell1);
        StyleHeaderCell(headerCell2);
        StyleHeaderCell(headerCell3);
        StyleHeaderCell(headerCell4);

        // Add metadata data by parsing column names and looking up SQL data types
        int row = 2;
        foreach (DataColumn column in dataTable.Columns)
        {
            string fullColumnName = column.ColumnName;
            string dbColumnName = ExtractRealColumnName(fullColumnName);
            string excelColumnName = ExtractDisplayName(fullColumnName);

            // Look up SQL data type and PK status from metaColumnsTable
            string sqlDataType = "";
            string isPrimaryKey = "";
            if (metaColumnsTable != null)
            {
                var metaRow = metaColumnsTable.AsEnumerable()
                    .FirstOrDefault(r => r.Field<string>("ColumnName").Equals(dbColumnName, StringComparison.OrdinalIgnoreCase));
                if (metaRow != null)
                {
                    sqlDataType = metaRow.Field<string>("SqlDataType") ?? "";
                    isPrimaryKey = metaRow.Field<string>("IsPrimaryKey") ?? "";
                }
            }

            var dbCell = worksheet.Cells[row, 1];
            var excelCell = worksheet.Cells[row, 2];
            var typeCell = worksheet.Cells[row, 3];
            var pkCell = worksheet.Cells[row, 4];

            dbCell.Value = dbColumnName;
            excelCell.Value = excelColumnName;
            typeCell.Value = sqlDataType;
            pkCell.Value = isPrimaryKey;

            // Style data cells
            StyleReferenceDataCell(dbCell);
            StyleReferenceDataCell(excelCell);
            StyleReferenceDataCell(typeCell);
            StyleReferenceDataCell(pkCell);

            row++;
        }//end foreach

        // Apply borders
        if (dataTable.Columns.Count > 0)
        {
            var dataRange = worksheet.Cells[1, 1, row - 1, 4];
            ApplyDarkBorders(dataRange);
        }

        // Auto-fit columns
        worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
    }//end method

    private void ApplyDropdownValidation(ExcelWorksheet dataSheet, ExcelWorksheet optionsSheet, ExcelWorksheet metadataSheet, DataTable dropdownTable)
    {
        if (dropdownTable.Rows.Count == 0) return; // No foreign keys to validate

        // Get unique foreign key columns
        var foreignKeyColumns = dropdownTable.AsEnumerable()
            .Select(row => row.Field<string>("ForColumn"))
            .Distinct()
            .ToList();

        // Build metadata mapping from Excel column names to DB column names
        var excelToDbMapping = new Dictionary<string, string>();
        if (metadataSheet.Dimension != null)
        {
            for (int row = 2; row <= metadataSheet.Dimension.Rows; row++)
            {
                var dbColumnName = metadataSheet.Cells[row, 1].Value?.ToString();
                var excelColumnName = metadataSheet.Cells[row, 2].Value?.ToString();
                if (!string.IsNullOrEmpty(dbColumnName) && !string.IsNullOrEmpty(excelColumnName))
                {
                    excelToDbMapping[excelColumnName] = dbColumnName;
                }
            }
        }

        // Get the header row to find column positions
        var headers = new Dictionary<string, int>();
        for (int col = 1; col <= dataSheet.Dimension.Columns; col++)
        {
            var headerValue = dataSheet.Cells[1, col].Value?.ToString();
            if (!string.IsNullOrEmpty(headerValue))
            {
                headers[headerValue] = col;
            }
        }//end for

        // Apply validation to each foreign key column
        foreach (var fkColumn in foreignKeyColumns)
        {
            // Find the Excel column name that maps to this DB column
            // fkColumn is the DB column name, we need to find the Excel display name
            var excelColumnName = excelToDbMapping.FirstOrDefault(x => x.Value == fkColumn).Key;

            if (!string.IsNullOrEmpty(excelColumnName) && headers.ContainsKey(excelColumnName))
            {
                int columnIndex = headers[excelColumnName];

                // Get the options for this foreign key column
                var optionsForColumn = dropdownTable.AsEnumerable()
                    .Where(row => row.Field<string>("ForColumn") == fkColumn)
                    .Select(row => row.Field<string>("Text"))
                    .ToList();

                if (optionsForColumn.Any())
                {
                    // Create validation for a reasonable range (1000 rows should be plenty)
                    var lastRow = Math.Max(100, dataSheet.Dimension?.Rows ?? 100);
                    var validationRange = dataSheet.Cells[2, columnIndex, lastRow + 1000, columnIndex];

                    var validation = validationRange.DataValidation.AddListDataValidation();

                    // Add each option to the validation list
                    foreach (var option in optionsForColumn)
                    {
                        if (!string.IsNullOrWhiteSpace(option))
                        {
                            validation.Formula.Values.Add(option);
                        }
                    }

                    validation.ShowErrorMessage = true;
                    validation.ErrorTitle = "Invalid Selection";
                    validation.Error = $"Please select a valid option for {excelColumnName}";
                    validation.AllowBlank = true; // Allow empty cells
                }//end if
            }//end if
        }//end foreach
    }//end method

    private void ApplyDarkThemeToWorksheet(ExcelWorksheet worksheet)
    {
        // Set the tab color
        worksheet.TabColor = AccentBlue;

        // You can't set worksheet background directly, but we'll style all visible cells
        // This gets handled by the individual cell styling methods
    }//end method

    private void StyleHeaderCell(ExcelRange cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.Color.SetColor(HeaderText);
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(HeaderBackground);

        // Header borders
        cell.Style.Border.Top.Style = ExcelBorderStyle.Thin;
        cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        cell.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        cell.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        cell.Style.Border.Top.Color.SetColor(BorderColor);
        cell.Style.Border.Bottom.Color.SetColor(BorderColor);
        cell.Style.Border.Left.Color.SetColor(BorderColor);
        cell.Style.Border.Right.Color.SetColor(BorderColor);
    }//end method

    private void StyleDataCell(ExcelRange cell)
    {
        cell.Style.Font.Color.SetColor(LightText);
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(DarkBackground);
    }//end method

    private void StyleReadOnlyDataCell(ExcelRange cell)
    {
        // Read-only cells (PK columns) get a distinct gray background
        cell.Style.Font.Color.SetColor(HeaderText); // Slightly muted text color
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(ReadOnlyBackground);
    }//end method

    private void StyleReferenceDataCell(ExcelRange cell)
    {
        // Reference data (Sheet2) gets slightly different styling
        cell.Style.Font.Color.SetColor(HeaderText);
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(DarkBackground);
    }//end method

    private void ApplyDarkBorders(ExcelRange range)
    {
        range.Style.Border.Top.Style = ExcelBorderStyle.Thin;
        range.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        range.Style.Border.Left.Style = ExcelBorderStyle.Thin;
        range.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        range.Style.Border.Top.Color.SetColor(BorderColor);
        range.Style.Border.Bottom.Color.SetColor(BorderColor);
        range.Style.Border.Left.Color.SetColor(BorderColor);
        range.Style.Border.Right.Color.SetColor(BorderColor);
    }//end method

    private string ExtractRealColumnName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return columnName;

        // Split on pipe and take the first part (real column name)
        var parts = columnName.Split('|', 2);
        return parts[0].Trim();
    }//end method

    private string ExtractDisplayName(string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return columnName;

        // Split on pipe and take the second part (display name), or first if no pipe
        var parts = columnName.Split('|', 2);
        return parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
    }//end method
}//end class