using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWorkshop.WebAPI
{
    public class SteamHTTP(char[] key)
    {
        internal string RequestKey()
        {
            return $"?key={new string(key)}";
        }
    }
}
