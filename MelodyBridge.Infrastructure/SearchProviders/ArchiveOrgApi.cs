namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>The archive.org endpoints and request shape shared by the search provider and the availability probe.</summary>
internal static class ArchiveOrgApi
{
    public static readonly Uri SearchBase = new("https://archive.org/advancedsearch.php");
    public static string MetadataUrl(string identifier) => $"https://archive.org/metadata/{identifier}";
}
