namespace Functions.Tests.Unit.TestSupport;

using System.Collections.Specialized;
using System.Net;
using System.Security.Claims;
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

    public override string Method { get; } = "POST";

    public override Uri Url { get; } = new("https://localhost/api/bulk-import");

    public override NameValueCollection Query => _query;

    public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
}

internal sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext context)
        : base(context)
    {
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeadersCollection Headers { get; set; } = new();

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies { get; } = new Mock<HttpCookies>(MockBehavior.Loose).Object;
}
