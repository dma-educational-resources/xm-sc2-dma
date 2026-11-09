// MonitorHelper.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SC2Radar.Common.Misc;

namespace SC2Radar.UI.Misc
{
    public class MonitorHelper
    {
        public class MonitorInfo
        {
            public (int X, int Y, int Width, int Height) Bounds { get; set; }
            public bool IsPrimary { get; set; }
        }

        public static List<MonitorInfo> GetAllMonitors()
        {
            var result = new List<MonitorInfo>();
            try
            {
                // Use Win32 directly -no WinForms or WPF needed
                foreach (var mi in SC2Radar.UI.Misc.MonitorInfo.GetAllMonitors())
                {
                    result.Add(new MonitorInfo
                    {
                        Bounds    = (mi.Left, mi.Top, mi.Width, mi.Height),
                        IsPrimary = mi.IsPrimary
                    });
                    XMLogging.WriteLine($"[MonitorHelper] Monitor: {mi.Width}x{mi.Height}, Primary: {mi.IsPrimary}");
                }
            }
            catch (Exception ex)
            {
                XMLogging.WriteLine($"[MonitorHelper] Failed to fetch monitors: {ex}");
            }
            return result;
        }
    }
}
