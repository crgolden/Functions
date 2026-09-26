namespace Functions.Churches.Moderation;

internal sealed class DuplicateClusters
{
    private readonly int[] _parents;
    private readonly int[] _ranks;

    internal DuplicateClusters(int count)
    {
        _parents = new int[count];
        _ranks = new int[count];
        for (var index = 0; index < count; index++)
        {
            _parents[index] = index;
        }
    }

    internal bool TryJoin(int first, int second)
    {
        var firstRoot = RootOf(first);
        var secondRoot = RootOf(second);
        if (firstRoot == secondRoot)
        {
            return false;
        }

        if (_ranks[firstRoot] < _ranks[secondRoot])
        {
            (firstRoot, secondRoot) = (secondRoot, firstRoot);
        }

        _parents[secondRoot] = firstRoot;
        if (_ranks[firstRoot] == _ranks[secondRoot])
        {
            _ranks[firstRoot]++;
        }

        return true;
    }

    private int RootOf(int index)
    {
        var root = index;
        while (_parents[root] != root)
        {
            root = _parents[root];
        }

        var walker = index;
        while (_parents[walker] != root)
        {
            var next = _parents[walker];
            _parents[walker] = root;
            walker = next;
        }

        return root;
    }
}
