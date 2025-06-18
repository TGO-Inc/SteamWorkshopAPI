namespace SteamWorkshop.WebAPI.Internal;

public static class Extensions
{
    public static void ForEach<T>(this IEnumerable<T> ie, Action<T, int> action)
    {
        var i = 0;
        foreach (T e in ie)
            action(e, i++);
    }
    public static void Add<T, TG>(this List<KeyValuePair<T, TG>> list, T itemA, TG itemB)
    {
        list.Add(new KeyValuePair<T, TG>(itemA, itemB));
    }
    public static void Add<T, TG>(this List<KeyValuePair<T, TG>> list, object itemA, object itemB)
    {
        T a = typeof(T) == typeof(string) ? (T)(object)itemA.ToString()! : (T)itemA!;
        TG b = typeof(TG) == typeof(string) ? (TG)(object)itemB.ToString()! : (TG)itemB!;
        list.Add(new KeyValuePair<T, TG>(a, b));
    }
}