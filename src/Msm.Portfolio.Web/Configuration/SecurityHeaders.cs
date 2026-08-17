namespace Msm.Portfolio.Web.Configuration;

/// <summary>
/// Adds the protective response headers (specification section 43).
/// </summary>
/// <remarks>
/// Applied to every response rather than selected pages: a header that is only
/// sometimes present protects only sometimes, and the pages most worth protecting are
/// the ones carrying a client's private media.
/// </remarks>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseMsmSecurityHeaders(this IApplicationBuilder app, bool isDevelopment)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Stops a browser second-guessing a declared content type. Without it, an
            // uploaded file served as an image could be sniffed as something executable.
            headers["X-Content-Type-Options"] = "nosniff";

            // A portfolio framed inside another site could be used to trick a signed-in
            // member of staff into clicking something they cannot see.
            headers["X-Frame-Options"] = "DENY";

            // Referrer is trimmed to the origin, so a portfolio slug — which identifies
            // a real person — is not handed to every site a visitor clicks through to.
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // Nothing here needs a camera, microphone or location.
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            headers["Content-Security-Policy"] = BuildContentSecurityPolicy(isDevelopment);

            await next();
        });
    }

    /// <summary>
    /// The Content Security Policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is same-origin. All scripts and styles are served from wwwroot, and
    /// media goes through the application's own authorised endpoint, so no external
    /// origin needs allowing. That makes a restrictive policy practical rather than
    /// aspirational.
    /// </para>
    /// <para>
    /// 'unsafe-inline' is permitted for styles only, because the gallery sets each
    /// image's aspect ratio inline to stop the page jumping as thumbnails load. Scripts
    /// carry no such exception.
    /// </para>
    /// </remarks>
    internal static string BuildContentSecurityPolicy(bool isDevelopment)
    {
        var directives = new List<string>
        {
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "media-src 'self'",
            "font-src 'self'",
            "connect-src 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            "object-src 'none'"
        };

        if (!isDevelopment)
        {
            // Left off in development because the developer exception page and hot
            // reload use plain HTTP resources locally.
            directives.Add("upgrade-insecure-requests");
        }

        return string.Join("; ", directives);
    }
}
