namespace Functions.Curator.Jobs;

public interface ICuratorJobMessage
{
    Guid RunId { get; }

    int Seq { get; }
}
