using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video.WGC;

namespace IGReinforced.Recording.Video
{
    public class RescreenSettings
    {
        public int Fps { get; set; }
        public EncodingType Encoding { get; set; }
        public int Bitrate { get; set; }

        public CaptureVideoType VideoType { get; set; }

        public static MonitorInfo PrimaryMonitor => MonitorEnumerationHelper.GetMonitors().Where((m) => m.IsPrimary).FirstOrDefault();
        public MonitorInfo SelectedMonitor { get; set; }

        public static RescreenSettings Default => new RescreenSettings()
        {
            SelectedMonitor = PrimaryMonitor,
            Fps = 60,
            Encoding = EncodingType.Nvenc,
            Bitrate = BitrateInfo.GetBitrateFromMbps(20),
            VideoType = CaptureVideoType.WGC//CaptureVideoType.DD
        };
    }
}
