using System;

namespace PrinterIpFix
{
    internal static class ProbeMain
    {
        public static int Main(string[] args)
        {
            return MatchProbe.Run(args);
        }
    }
}
