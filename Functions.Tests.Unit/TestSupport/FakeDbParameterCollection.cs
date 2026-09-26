namespace Functions.Tests.Unit.TestSupport;

using System.Collections;
using System.Data.Common;

internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _params = [];

    public override int Count => _params.Count;

    public override object SyncRoot => _params;

    public override bool IsFixedSize => false;

    public override bool IsReadOnly => false;

    public override bool IsSynchronized => false;

    public override int Add(object value)
    {
        _params.Add((DbParameter)value);
        return _params.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values)
        {
            _params.Add((DbParameter)v);
        }
    }

    public override void Clear() => _params.Clear();

    public override bool Contains(object value) => _params.Contains((DbParameter)value);

    public override bool Contains(string value) => _params.Any(p => p.ParameterName == value);

    public override void CopyTo(Array array, int index) =>
        ((ICollection)_params).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _params.GetEnumerator();

    public override int IndexOf(object value) => _params.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _params.FindIndex(p => p.ParameterName == parameterName);

    public override void Insert(int index, object value) => _params.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _params.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _params.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _params.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _params[index];

    protected override DbParameter GetParameter(string parameterName) =>
        _params.First(p => p.ParameterName == parameterName);

    protected override void SetParameter(int index, DbParameter value) => _params[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _params[IndexOf(parameterName)] = value;
}
