namespace SteamWorkshop.WebAPI;

public class SteamHTTP(char[] key)
{
    internal string Key => $"?key={new string(key)}";
}