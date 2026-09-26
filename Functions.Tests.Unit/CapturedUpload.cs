namespace Functions.Tests.Unit;

internal sealed record CapturedUpload(string BlobName, string? ContentType, byte[] Bytes);
