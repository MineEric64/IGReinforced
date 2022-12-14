using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IGReinforced.Recording.Types
{
    public class BitrateInfo
    {
        public int Mbps { get; set; }
        public int Bps => GetBitrateFromMbps(Mbps);

        public BitrateInfo(int mbps)
        {
            Mbps = mbps;
        }

        public static int GetBitrateFromMbps(int mbps)
        {
            return mbps * 1000000;
        }
    }
}
