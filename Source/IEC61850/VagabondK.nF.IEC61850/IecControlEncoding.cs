using System;
using System.Runtime.CompilerServices;
using VagabondK.nF.MMS;
[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Server")]
[assembly: InternalsVisibleTo("VagabondK.nF.IEC61850.Client")]
namespace VagabondK.nF.IEC61850
{
    internal static class IecControlEncoding
    {
        internal static void ControlItem(string item)
        {
            MmsEncoding.Identifier(item);
            int p = item.IndexOf("$CO$");
            if (p < 1 || p + 4 >= item.Length || item.IndexOf('$') != p ||
                item.Substring(p + 4).IndexOf('$') >= 0 || item.Length > 55)
                throw new ArgumentException("Expected LN$CO$object of at most 55 characters.");
        }
        internal static string ConfigurationItem(string item)
        {
            int p = item.IndexOf("$CO$");
            return item.Substring(0, p) + "$CF$" + item.Substring(p + 4) + "$ctlModel";
        }
    }
}
