using Newtonsoft.Json;
using SteamWorkshop.WebAPI.Internal;
using SteamWorkshop.WebAPI.Managers;
using System.Text;

namespace SteamWorkshop.WebAPI.IPublishedFileService;

public class PublishedFileService(char[] key, ConsoleManager? logger = null) : SteamHTTP(key)
{
    private const string UGetPublishedFileDetails = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const string UQueryFiles = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/";

    private static T Request<T>(string query)
    {
        //try
        //{
        HttpResponseMessage results = new HttpClient().GetAsync(query).GetAwaiter().GetResult();
        string json = results.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonConvert.DeserializeObject<T>(json[12..^1])!;
        /*}
        catch (AggregateException e)
        {
            Console.WriteLine($"[{DateTime.Now}] {e}");
            return default!;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{DateTime.Now}] {e}");
            return default!;
        }*/
    }

    private static T Post<T>(string query, FormUrlEncodedContent content)
    {
        HttpResponseMessage results = new HttpClient().PostAsync(query, content).GetAwaiter().GetResult();
        string json = results.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonConvert.DeserializeObject<T>(json[12..^1])!;
    }

    public QueryResponse SendQuery(PublishedFileServiceQuery query)
    {
        StringBuilder queryString = new();

        queryString.Append(UQueryFiles);
        queryString.Append(this.Key);

        queryString.Append("&query_type=");
        queryString.Append(query.QueryType);
        queryString.Append("&numperpage=");
        queryString.Append(query.ResultsPerPage);
        queryString.Append("&appid=");
        queryString.Append(query.AppId);
        queryString.Append("&requiredtags[0]=");
        queryString.Append(query.RequiredTags);
        queryString.Append("&page=");

        Directory.CreateDirectory("challenges");
        logger?.WriteLine($"[{this.GetType().FullName}]: Downloading challenge mode steam_ids...");

        List<string>? old = null;
            
        if (File.Exists(Path.Combine("challenges",".steam.ids")))
            old = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(Path.Combine("challenges",".steam.ids")))!;

        int total = Request<PublishedFileDetailsQuery>(queryString.ToString().Replace($"rpage={query.ResultsPerPage}", "rpage=1")).Total;
        double loop = total / (double)query.ResultsPerPage;
        if (loop > Math.Floor(loop))
            loop = Math.Floor(loop) + 1;

        ManagedArray<PublishedFileDetailsQuery.PublishedFileDetails> publishedFiles = new(total, true);

        Parallel.For(1, (int)loop +1, (x, _) =>
        {
            var response = Request<PublishedFileDetailsQuery>($"{queryString}{x}");
            if (response._PublishedFileDetails is null) return;

            if (old is not null)
                publishedFiles.Add(response._PublishedFileDetails.Where(details => !old.Contains(details.PublishedFileId!)));
            else
                publishedFiles.Add(response._PublishedFileDetails);
        });

        logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded: {publishedFiles.Count} / {total}");

        var results = new ISteamRemoteStorage.PublishedFileDetailsQuery(1, 0, []);

        if (publishedFiles.Count > 0)
        {
            StringBuilder itemDetailsQuery = new();
            itemDetailsQuery.Append(UGetPublishedFileDetails);
            itemDetailsQuery.Append(this.Key);

            List<KeyValuePair<string, string>> formContent = new()
            {
                { "itemcount", publishedFiles.Count }
            };

            publishedFiles.ForEach((item, index) => formContent.Add($"publishedfileids[{index}]", item.PublishedFileId!));

            logger?.WriteLine($"[{this.GetType().FullName}]: Downloading {publishedFiles.Count} file details...");
            results = Post<ISteamRemoteStorage.PublishedFileDetailsQuery>(
                itemDetailsQuery.ToString(), new FormUrlEncodedContent(formContent));
        }
        string[] @new = old?.ToArray() ?? [];

        ManagedArray<string> output = new(publishedFiles.Count + @new.Length)
        {
            publishedFiles.Select(i => i.PublishedFileId!).ToArray(),
            @new
        };

        return new QueryResponse { SteamIDs = output.ToArray(), Results = results, PublishedFileDetails = publishedFiles.ToArray(), Total = total };
    }
}