namespace Functions.Tests.Unit.TestSupport;

using System.Collections.Specialized;
using System.Security.Claims;
using Functions.Churches.Import;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

internal sealed class FakeHttpRequestData : HttpRequestData
{
    private readonly NameValueCollection _query;

    public FakeHttpRequestData(NameValueCollection query)
        : base(new Mock<FunctionContext>(MockBehavior.Loose).Object)
    {
        _query = query;
    }

    public override HttpHeadersCollection Headers { get; } = new();

    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];

    public override Stream Body { get; } = Stream.Null;

    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];

    public override string Method { get; } = HttpMethod.Post.Method;

    public override Uri Url { get; } =
        new UriBuilder(Uri.UriSchemeHttps, Generated.NewHostLabel())
        {
            Path = $"{AzureFunctionsHostFixtureConstants.DefaultHttpRoutePrefix}/{BulkImportJob.Route}",
        }.Uri;

    public override NameValueCollection Query => _query;

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
}
