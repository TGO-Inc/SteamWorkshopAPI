using Newtonsoft.Json;
using SteamWorkshop.WebAPI.Internal;
using SteamWorkshop.WebAPI.Managers;
using System.Text;

namespace SteamWorkshop.WebAPI.IPublishedFileService;

public class PublishedFileService(char[] key, ConsoleManager? Logger = null) : SteamHTTP(key)
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
        queryString.Append(base.RequestKey());

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
        Logger?.WriteLine($"[{this.GetType().FullName}]: Downloading challenge mode steam_ids...");

        List<PublishedFileDetailsQuery.PublishedFileDetails> list = [];
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

        Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded: {publishedFiles.Count} / {total}");

        var results = new ISteamRemoteStorage.PublishedFileDetailsQuery(1, 0, []);

        if (publishedFiles.Count > 0)
        {
            StringBuilder itemDetailsQuery = new();
            itemDetailsQuery.Append(UGetPublishedFileDetails);
            itemDetailsQuery.Append(base.RequestKey());

            List<KeyValuePair<string, string>> formContent = new()
            {
                { "itemcount", publishedFiles.Count }
            };

            publishedFiles.ForEach((item, index) => formContent.Add($"publishedfileids[{index}]", item.PublishedFileId!));

            Logger?.WriteLine($"[{this.GetType().FullName}]: Downloading {publishedFiles.Count} file details...");
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
        
        File.WriteAllText(Path.Combine("challenges",".steam.ids"), JsonConvert.SerializeObject(output.ToArray(), Formatting.Indented));
            
        if (File.Exists(Path.Combine("challenges",".challenge.data")))
        {
            string jsonString = File.ReadAllText(Path.Combine("challenges",".challenge.data"));
            var oldResults = JsonConvert.DeserializeObject<ISteamRemoteStorage.PublishedFileDetailsQuery>(jsonString);
            results = new ISteamRemoteStorage.PublishedFileDetailsQuery(results.Result, results.ResultCount + oldResults!.ResultCount,
                [.. oldResults._PublishedFileDetails, .. results._PublishedFileDetails]);
        }

        File.WriteAllText(Path.Combine("challenges",".challenge.data"), JsonConvert.SerializeObject(results, Formatting.Indented));
        if (publishedFiles.Count < total && publishedFiles.Count > 0) {
            Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded/Collected SOME...? file details");
            // return (results, true);
        }

        Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded/Collected all file details");
        // return (results, false);
    }
}