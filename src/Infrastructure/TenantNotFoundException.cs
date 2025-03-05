namespace Meshmakers.Octo.Services.Infrastructure;

public class TenantNotFoundException : Exception
{
    public TenantNotFoundException()
    {
    }

    public TenantNotFoundException(string message) : base(message)
    {
    }

    public TenantNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }


    public static Exception TenantIdNotFound()
    {
        return new TenantNotFoundException("The current request does not contain a tenant id");
    }
}
