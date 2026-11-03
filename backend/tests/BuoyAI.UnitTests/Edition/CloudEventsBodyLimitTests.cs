using System.Reflection;
using BuoyAI.API.Controllers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class CloudEventsBodyLimitTests
{
    [Fact]
    public void Receive_IsCappedAt64KbByRequestSizeLimit()
    {
        var method = typeof(CloudEventsController).GetMethod(nameof(CloudEventsController.Receive))!;
        var attr = method.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(attr);
        var filter = Assert.IsAssignableFrom<IFilterFactory>(attr);
        var policy = filter.CreateInstance(new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider());
        var feature = new TestMaxBodyFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var ctx = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
        ((IAuthorizationFilter)policy).OnAuthorization(ctx);
        Assert.Equal(64 * 1024, feature.MaxRequestBodySize);
    }
}

internal sealed class TestMaxBodyFeature : IHttpMaxRequestBodySizeFeature
{
    public bool IsReadOnly => false;
    public long? MaxRequestBodySize { get; set; }
}
