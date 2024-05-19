using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamWorkshop.WebAPI.Internal;
using SteamWorkshop.WebAPI.ISteamRemoteStorage;
using SteamWorkshop.WebAPI.Managers;
using System.Linq;
using System.Text;

namespace SteamWorkshop.WebAPI.IPublishedFileService
{
    public class PublishedFileService(char[] key, ConsoleManager? Logger = null) : SteamHTTP(key)
    {
        internal readonly static string uGET_PUBLISHED_FILE_DETAILS
            = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
        internal readonly static string uQUERY_FILES
            = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/";

        private static T Request<T>(string query)
        {
            //try
            //{
            var Results = new HttpClient().GetAsync(query).GetAwaiter().GetResult();
            var Json = Results.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonConvert.DeserializeObject<T>(Json[12..^1])!;
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
            var Results = new HttpClient().PostAsync(query, content).GetAwaiter().GetResult();
            var Json = Results.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonConvert.DeserializeObject<T>(Json[12..^1])!;
        }

        public (ISteamRemoteStorage.PublishedFileDetailsQuery, bool) SendQuery(IPublishedFileServiceQuery query)
        {
            StringBuilder QueryString = new();

            QueryString.Append(uQUERY_FILES);
            QueryString.Append(base.RequestKey());

            QueryString.Append("&query_type=");
            QueryString.Append(query.QueryType);
            QueryString.Append("&numperpage=");
            QueryString.Append(query.ResultsPerPage);
            QueryString.Append("&appid=");
            QueryString.Append(query.AppId);
            QueryString.Append("&requiredtags[0]=");
            QueryString.Append(query.RequiredTags);
            QueryString.Append("&page=");

            Directory.CreateDirectory("challenges");
            Logger?.WriteLine($"[{this.GetType().FullName}]: Downloading challenge mode steam_ids...");

            List<PublishedFileDetailsQuery.PublishedFileDetails> list = [];
            List<string>? old = null;
            
            if (File.Exists(Path.Combine("challenges",".steam.ids")))
                old = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(Path.Combine("challenges",".steam.ids")))!;

            int total = Request<PublishedFileDetailsQuery>(QueryString.ToString().Replace($"rpage={query.ResultsPerPage}", "rpage=1")).Total;
            double loop = total / (double)query.ResultsPerPage;
            if (loop > Math.Floor(loop))
                loop = Math.Floor(loop) + 1;

            ManagedArray<PublishedFileDetailsQuery.PublishedFileDetails> ChallengePackIds = new(total, true);

            Parallel.For(1, (int)loop +1, (x, g) =>
            {
                var Response = Request<PublishedFileDetailsQuery>($"{QueryString}{x}");
                if (Response._PublishedFileDetails is null) return;

                if (old is not null)
                    ChallengePackIds.Add(
                        Response._PublishedFileDetails
                        .Where(i => !old.Contains(i.PublishedFileId!)));
                else
                    ChallengePackIds.Add(Response._PublishedFileDetails);
            });

            Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded: {ChallengePackIds.Count} / {total}");

            var Results
                = new ISteamRemoteStorage.PublishedFileDetailsQuery(
                    1, 0, []);

            if (ChallengePackIds.Count > 0)
            {
                StringBuilder ItemDetailsQuery = new();
                ItemDetailsQuery.Append(uGET_PUBLISHED_FILE_DETAILS);
                ItemDetailsQuery.Append(base.RequestKey());

                List<KeyValuePair<string, string>> FormContent = new()
                {
                    { "itemcount", ChallengePackIds.Count }
                };

                ChallengePackIds.ForEach((item, index) =>
                {
                    FormContent.Add($"publishedfileids[{index}]", item.PublishedFileId!);
                });

                Logger?.WriteLine($"[{this.GetType().FullName}]: Downloading {ChallengePackIds.Count} file details...");
                Results = Post<ISteamRemoteStorage.PublishedFileDetailsQuery>(
                    ItemDetailsQuery.ToString(), new FormUrlEncodedContent(FormContent));
            }
            string[] @new = old?.ToArray() ?? [];

            ManagedArray<string> output = new(ChallengePackIds.Count + @new.Length)
            {
                ChallengePackIds.Select(i => i.PublishedFileId!).ToArray(),
                @new
            };

            File.WriteAllText(Path.Combine("challenges",".steam.ids"), JsonConvert.SerializeObject(output.ToArray(), Formatting.Indented));
            
            if (File.Exists(Path.Combine("challenges",".challenge.data")))
            {
                var jsonstring = File.ReadAllText(Path.Combine("challenges",".challenge.data"));
                var old_results = JsonConvert.DeserializeObject<ISteamRemoteStorage.PublishedFileDetailsQuery>(jsonstring);
                Results = new(Results.Result, Results.ResultCount + old_results!.ResultCount,
                    [.. old_results._PublishedFileDetails, .. Results._PublishedFileDetails]);
            }

            File.WriteAllText(Path.Combine("challenges",".challenge.data"), JsonConvert.SerializeObject(Results, Formatting.Indented));
            if (ChallengePackIds.Count < total && ChallengePackIds.Count > 0) {
                Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded/Collected SOME...? file details");
                return (Results, true);
            }

            Logger?.WriteLine($"[{this.GetType().FullName}]: Downloaded/Collected all file details");
            return (Results, false);
        }
    }
}