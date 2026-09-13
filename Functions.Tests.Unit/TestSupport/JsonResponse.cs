namespace Functions.Tests.Unit.TestSupport;

using System.Net;
using System.Net.Mime;
using System.Text;

internal static class JsonResponse
{
    internal const string EmptyArray = "[]";
    internal const string EmptyObject = "{}";

    internal static HttpResponseMessage Ok(string body) => WithStatus(HttpStatusCode.OK, body);

    internal static HttpResponseMessage OkEmptyArray() => Ok(EmptyArray);

    internal static HttpResponseMessage OkEmptyObject() => Ok(EmptyObject);

    internal static HttpResponseMessage WithStatus(HttpStatusCode status, string body) =>
        new(status) { Content = Content(body) };

    internal static StringContent Content(string body) =>
        new(body, Encoding.UTF8, MediaTypeNames.Application.Json);
}