namespace SteamWorkshop.WebAPI;

public class SteamHTTP(char[] key)
{
    internal string RequestKey() => $"?key={new string(key)}";
}