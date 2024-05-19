using Newtonsoft.Json;
using static SteamWorkshop.WebAPI.ISteamRemoteStorage.PublishedFileDetailsQuery;

namespace SteamWorkshop.WebAPI.ISteamRemoteStorage
{
    public record PublishedFileDetailsQuery(
            [property: JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] int Result,
            [property: JsonProperty("resultcount", NullValueHandling = NullValueHandling.Ignore)] int ResultCount,
            [property: JsonProperty("publishedfiledetails", NullValueHandling = NullValueHandling.Ignore)] PublishedFileDetails[] _PublishedFileDetails
        )
    {
        public record PublishedFileDetails(
            [property: JsonProperty("publishedfileid", NullValueHandling = NullValueHandling.Ignore)] string Publishedfileid,
            [property: JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] int Result,
            [property: JsonProperty("creator", NullValueHandling = NullValueHandling.Ignore)] string Creator,
            [property: JsonProperty("creator_app_id", NullValueHandling = NullValueHandling.Ignore)] int CreatorAppId,
            [property: JsonProperty("consumer_app_id", NullValueHandling = NullValueHandling.Ignore)] int ConsumerAppId,
            [property: JsonProperty("filename", NullValueHandling = NullValueHandling.Ignore)] string Filename,
            [property: JsonProperty("file_size", NullValueHandling = NullValueHandling.Ignore)] int FileSize,
            [property: JsonProperty("file_url", NullValueHandling = NullValueHandling.Ignore)] string FileUrl,
            [property: JsonProperty("hcontent_file", NullValueHandling = NullValueHandling.Ignore)] string HcontentFile,
            [property: JsonProperty("preview_url", NullValueHandling = NullValueHandling.Ignore)] string PreviewUrl,
            [property: JsonProperty("hcontent_preview", NullValueHandling = NullValueHandling.Ignore)] string HcontentPreview,
            [property: JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] string Title,
            [property: JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)] string Description,
            [property: JsonProperty("time_created", NullValueHandling = NullValueHandling.Ignore)] int TimeCreated,
            [property: JsonProperty("time_updated", NullValueHandling = NullValueHandling.Ignore)] int TimeUpdated,
            [property: JsonProperty("visibility", NullValueHandling = NullValueHandling.Ignore)] int Visibility,
            [property: JsonProperty("banned", NullValueHandling = NullValueHandling.Ignore)] int Banned,
            [property: JsonProperty("ban_reason", NullValueHandling = NullValueHandling.Ignore)] string BanReason,
            [property: JsonProperty("subscriptions", NullValueHandling = NullValueHandling.Ignore)] int Subscriptions,
            [property: JsonProperty("favorited", NullValueHandling = NullValueHandling.Ignore)] int Favorited,
            [property: JsonProperty("lifetime_subscriptions", NullValueHandling = NullValueHandling.Ignore)] int LifetimeSubscriptions,
            [property: JsonProperty("lifetime_favorited", NullValueHandling = NullValueHandling.Ignore)] int LifetimeFavorited,
            [property: JsonProperty("views", NullValueHandling = NullValueHandling.Ignore)] int Views,
            [property: JsonProperty("tags", NullValueHandling = NullValueHandling.Ignore)] Tag[] Tags
        );

        public record Tag(
            [property: JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)] string Tag_
        );
    }
}