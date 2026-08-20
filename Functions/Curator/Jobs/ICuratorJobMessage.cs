namespace Functions.Curator.Jobs;

public interface ICuratorJobMessage
{
    string RunId { get; }

    int Seq { get; }
}
