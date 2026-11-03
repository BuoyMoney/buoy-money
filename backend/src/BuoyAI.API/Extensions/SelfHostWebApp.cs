using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace BuoyAI.API.Extensions;

/// <summary>
/// Serves the Flutter web build from Kestrel, so a self-hosted install is one process with no
/// nginx. Hosted does not use this: there nginx serves the app and the marketing site.
///
/// The app lives under <c>/app/</c> (the same base href the hosted build uses, so email links and
/// deep links match), and <c>/</c> redirects there. Unknown paths under <c>/app/</c> return the app
/// shell so client-side routes survive a refresh, exactly as nginx's try_files did.
///
/// Files are revalidated on every load (<c>no-cache</c> plus the ETag the static file middleware
/// sends), so an upgrade is picked up at once and a 304 costs almost nothing on a LAN. A build may
/// ship <c>file.gz</c> next to <c>file</c>; it is served as-is to clients that accept gzip, so a
/// Raspberry Pi never compresses the 3 MB <c>main.dart.js</c> per request.
/// </summary>
public static class SelfHostWebApp
{
  private const string BasePath = "/app";

  // Same intent as nginx-csp-production.conf with every third-party host removed: the self-hosted
  // page loads nothing from outside its own origin except the Roboto fallback font from Google Fonts.
  // 'unsafe-eval' is required by dart2js output; 'wasm-unsafe-eval' by CanvasKit.
  internal const string ContentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'; " +
    "style-src 'self' 'unsafe-inline'; " +
    "font-src 'self' https://fonts.gstatic.com data:; " +
    "img-src 'self' data: blob:; " +
    "connect-src 'self' https://fonts.gstatic.com blob:; " +
    "worker-src 'self' blob:; " +
    "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self';";

  /// <summary>The folder holding the Flutter build, or null when there is none to serve.</summary>
  public static string? ResolveWebRoot(IConfiguration configuration)
  {
    var configured = configuration["SelfHost:WebRoot"];
    var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "web") : configured;
    return File.Exists(Path.Combine(root, "index.html")) ? root : null;
  }

  /// <summary>
  /// Middleware: headers, precompressed files, static files. Call before authentication. It also
  /// adds routing right after the static files: the static file middleware stands down for any
  /// request that routing has already matched, and the implicit routing step would otherwise run
  /// first and match the app-shell fallback for every file.
  /// </summary>
  public static IApplicationBuilder UseSelfHostWebApp(this IApplicationBuilder app, string webRoot)
  {
    var files = new PhysicalFileProvider(webRoot);
    var contentTypes = new PrecompressedContentTypeProvider();

    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(BasePath), branch =>
    {
      // /app -> /app/ so the base href resolves.
      branch.Use(async (ctx, next) =>
      {
        if (ctx.Request.Path == BasePath)
        {
          ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
          ctx.Response.Headers.Location = BasePath + "/";
          return;
        }
        await next();
      });

      branch.Use(async (ctx, next) =>
      {
        ctx.Response.OnStarting(() =>
        {
          var h = ctx.Response.Headers;
          h["X-Content-Type-Options"] = "nosniff";
          h["X-Frame-Options"] = "SAMEORIGIN";
          h["Referrer-Policy"] = "strict-origin-when-cross-origin";
          h["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
          h["Content-Security-Policy"] = ContentSecurityPolicy;
          h["X-Robots-Tag"] = "noindex, nofollow";
          // Only over HTTPS (directly, or as reported by a trusted proxy). Over plain HTTP the
          // browser ignores it, and a LAN install must keep working on http://.
          if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
          return Task.CompletedTask;
        });
        await next();
      });

      // file.js -> file.js.gz when the client takes gzip and the build shipped one.
      branch.Use(async (ctx, next) =>
      {
        if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            && AcceptsGzip(ctx.Request)
            && ctx.Request.Path.Value is { } path
            && path.Length > BasePath.Length + 1
            && !path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            && files.GetFileInfo(path[BasePath.Length..] + ".gz") is { Exists: true })
        {
          ctx.Request.Path = new PathString(path + ".gz");
          ctx.Response.Headers[HeaderNames.ContentEncoding] = "gzip";
          ctx.Response.Headers.Append(HeaderNames.Vary, "Accept-Encoding");
        }
        await next();
      });

      branch.UseStaticFiles(new StaticFileOptions
      {
        FileProvider = files,
        RequestPath = BasePath,
        ContentTypeProvider = contentTypes,
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream",
        OnPrepareResponse = ctx => ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
      });
    });

    return app.UseRouting();
  }

  /// <summary>True when the client lists gzip (or *) with a quality above zero.</summary>
  internal static bool AcceptsGzip(HttpRequest request)
  {
    var values = request.GetTypedHeaders().AcceptEncoding;
    foreach (var v in values)
    {
      var isGzip = v.Value.Equals("gzip", StringComparison.OrdinalIgnoreCase);
      if (isGzip && (v.Quality ?? 1) > 0) return true;
    }
    return false;
  }

  /// <summary>Endpoints: <c>/</c> redirects to the app, everything else under <c>/app/</c> is the app shell.</summary>
  public static void MapSelfHostWebApp(this IEndpointRouteBuilder endpoints, string webRoot)
  {
    endpoints.MapGet("/", () => Results.Redirect(BasePath + "/")).AllowAnonymous();
    endpoints.MapFallbackToFile(BasePath + "/{**path}", "index.html", new StaticFileOptions
    {
      FileProvider = new PhysicalFileProvider(webRoot),
      OnPrepareResponse = ctx => ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
    }).AllowAnonymous();
  }

  /// <summary>Looks a ".gz" file up by the type of the file it compresses.</summary>
  private sealed class PrecompressedContentTypeProvider : IContentTypeProvider
  {
    private readonly FileExtensionContentTypeProvider _inner = new();

    public bool TryGetContentType(string subpath, out string contentType)
    {
      if (subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) subpath = subpath[..^3];
      return _inner.TryGetContentType(subpath, out contentType!);
    }
  }
}
