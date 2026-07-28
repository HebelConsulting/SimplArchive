namespace SimplArchive.Api.Pagination;

/// <summary>
/// Default/max page size shared by every list endpoint — see ADR "Pagination for list endpoints".
/// </summary>
public static class PageSize
{
    public const int Default = 50;

    public const int Max = 200;

    public static int Resolve(int? requested)
    {
        return Math.Clamp(requested ?? Default, 1, Max);
    }
}
