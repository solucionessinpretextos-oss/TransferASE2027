namespace FileXfer.Server.Middleware;

/// <summary>
/// El Dashboard se publica con base /admin/. La redirección protege también
/// clientes que aún conservan un bundle con ese basename literal en caché.
/// </summary>
public sealed class AdminDashboardCanonicalPathMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            context.Request.Path.Equals(new PathString("/admin"), StringComparison.OrdinalIgnoreCase))
        {
            var location = context.Request.PathBase.Add(new PathString("/admin/")).ToUriComponent() +
                context.Request.QueryString.ToUriComponent();
            context.Response.Redirect(location, permanent: true, preserveMethod: true);
            return Task.CompletedTask;
        }

        return next(context);
    }
}
