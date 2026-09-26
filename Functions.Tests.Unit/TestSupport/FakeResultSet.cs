namespace Functions.Tests.Unit.TestSupport;

using System.Data;

internal static class FakeResultSet
{
    internal static DataTable WithColumns(params Type[] columnTypes)
    {
        var table = new DataTable();
        foreach (var columnType in columnTypes)
        {
            var columnName = Guid.NewGuid().ToString();
            table.Columns.Add(columnName, columnType);
        }

        return table;
    }
}
