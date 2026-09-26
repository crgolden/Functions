namespace Functions.Tests.Unit.TestSupport;

using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

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
