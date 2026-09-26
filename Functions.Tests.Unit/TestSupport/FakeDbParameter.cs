namespace Functions.Tests.Unit.TestSupport;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

internal sealed class FakeDbParameter : DbParameter
{
    private string? _parameterName;
    private string? _sourceColumn;

    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName
            ?? throw new InvalidOperationException($"No {nameof(ParameterName)} was set on this fake.");
        set => _parameterName = value;
    }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn
            ?? throw new InvalidOperationException($"No {nameof(SourceColumn)} was set on this fake.");
        set => _sourceColumn = value;
    }

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}
