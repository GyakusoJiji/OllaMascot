using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OllaMonitor
{
    public static class SystemBackdropHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);

        // Attributes for DWM
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // Backdrop Types
        private const int DWMSBT_AUTO = 1;
        private const int DWMSBT_NONE = 2;
        private const int DWMSBT_MICA = 3;
        private const int DWMSBT_ACRYLIC = 4;

        public static void ApplyBackdrop(Window window, bool useAcrylic = true)
        {
            try
            {
                var helper = new WindowInteropHelper(window);
                IntPtr hwnd = helper.EnsureHandle();

                // Enable immersive dark mode attribute
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // Apply Backdrop (Windows 11 build 22000+)
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int backdropType = useAcrylic ? DWMSBT_ACRYLIC : DWMSBT_MICA;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                }
            }
            catch
            {
                // Fallback silently if DWM APIs fail
            }
        }
    }
}
