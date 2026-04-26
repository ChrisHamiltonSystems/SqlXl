using ClosedXML.Excel;

namespace SqlXl.Core.SchemaInference;

public class SheetInfo
{
    public string Name { get; set; }
    public bool IsHidden { get; set; }
}

public class TabularData
{
    public List<string> Headers { get; set; } = new List<string>();
    public List<object[]> Rows { get; set; } = new List<object[]>();
    public int TotalDataRowsScanned { get; set; }
}

public class ExcelTabularReader : IDisposable
{
    private readonly XLWorkbook _workbook;

    public ExcelTabularReader(string path)
    {
        _workbook = new XLWorkbook(path);
    }

    public List<SheetInfo> ListSheets()
    {
        return _workbook.Worksheets
            .Select(ws => new SheetInfo
            {
                Name = ws.Name,
                IsHidden = ws.Visibility != XLWorksheetVisibility.Visible
            })
            .ToList();
    }

    public bool SheetExists(string name)
    {
        return _workbook.Worksheets.Any(ws =>
            ws.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public TabularData Read(string sheetName, int maxRows)
    {
        var ws = _workbook.Worksheets.FirstOrDefault(w =>
            w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
        if (ws == null)
            throw new ArgumentException($"Sheet '{sheetName}' not found");

        int lastCol = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        if (lastCol == 0)
            throw new InvalidOperationException(
                $"Sheet '{sheetName}' has no header row (row 1 is empty)");

        var data = new TabularData();

        for (int c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetValue<string>()?.Trim() ?? "";
            data.Headers.Add(h);
        }

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        int kept = 0;
        for (int r = 2; r <= lastRow; r++)
        {
            data.TotalDataRowsScanned++;

            var rowValues = new object[lastCol];
            bool hasAny = false;
            for (int c = 1; c <= lastCol; c++)
            {
                var v = GetCellObject(ws.Cell(r, c));
                rowValues[c - 1] = v;
                if (v != null) hasAny = true;
            }

            if (!hasAny) continue;

            data.Rows.Add(rowValues);
            kept++;
            if (kept >= maxRows) break;
        }

        return data;
    }

    private static object GetCellObject(IXLCell cell)
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
            _ => (object)cell.GetString()
        };
    }

    public void Dispose() => _workbook?.Dispose();
}
