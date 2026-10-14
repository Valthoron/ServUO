using System.Collections.Generic;
using System.Net;

using Server.Network;

namespace Server.Misc
{
    public static class IPLimiter
    {
        public static bool Enabled { get; set; } = true;

        public static bool SocketBlock { get; set; } = true; // true to block at connection, false to block at login request

        public static int MaxAddresses { get; set; } = 100;

        public static IPAddress[] Exemptions { get; set; } = // For hosting services where there are cases where IPs can be proxied
        {
            IPAddress.Loopback,
        };

        public static bool IsExempt(IPAddress ip)
        {
            for (int i = 0; i < Exemptions.Length; i++)
            {
                if (ip.Equals(Exemptions[i]))
                    return true;
            }

            return false;
        }

        public static bool Verify(IPAddress ourAddress)
        {
            if (!Enabled || IsExempt(ourAddress))
                return true;

            IList<IPAddress> netStates = NetState.Addresses;

            int count = 0;

            for (int i = 0; i < netStates.Count; ++i)
            {
                IPAddress compState = netStates[i];

                if (ourAddress.Equals(compState))
                {
                    ++count;

                    if (count >= MaxAddresses)
                        return false;
                }
            }

            return true;
        }
    }
}
