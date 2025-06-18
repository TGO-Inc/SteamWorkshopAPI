namespace SteamWorkshop.WebAPI.IPublishedFileService;

public class QueryResponse
{
    public required string[] SteamIDs;
    public required PublishedFileDetailsQuery.PublishedFileDetails[] PublishedFileDetails;
    public required ISteamRemoteStorage.PublishedFileDetailsQuery Results;
    public required int Total;
}