using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SteamWorkshop.WebAPI.Internal
{
    public static class Extensions
    {
        public static void ForEach<T>(this IEnumerable<T> ie, Action<T, int> action)
        {
            var i = 0;
            foreach (var e in ie)
                action(e, i++);
        }
        public static void Add<T, G>(this List<KeyValuePair<T, G>> list, T itemA, G itemB)
        {
            list.Add(new KeyValuePair<T, G>(itemA, itemB));
        }
        public static void Add<T, G>(this List<KeyValuePair<T, G>> list, object itemA, object itemB)
        {
            T a = typeof(T) == typeof(string) ? (T)(object)itemA.ToString()! : (T)itemA!;
            G b = typeof(G) == typeof(string) ? (G)(object)itemB.ToString()! : (G)itemB!;
            list.Add(new KeyValuePair<T, G>(a, b));
        }
    }
}
