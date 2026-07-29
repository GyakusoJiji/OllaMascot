using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OllaMascot
{
    public static class HardwareMonitor
    {
        // Kernel32 definitions for RAM
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // Kernel32 definitions for CPU
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        private static FILETIME _prevIdleTime;
        private static FILETIME _prevKernelTime;
        private static FILETIME _prevUserTime;

        static HardwareMonitor()
        {
            // Seed the CPU history
            GetSystemTimes(out _prevIdleTime, out _prevKernelTime, out _prevUserTime);
        }

        private static ulong FileTimeToUInt64(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        public static float GetCpuUtilization()
        {
            if (!GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
            {
                return 0f;
            }

            ulong idle = FileTimeToUInt64(idleTime);
            ulong kernel = FileTimeToUInt64(kernelTime);
            ulong user = FileTimeToUInt64(userTime);

            ulong prevIdle = FileTimeToUInt64(_prevIdleTime);
            ulong prevKernel = FileTimeToUInt64(_prevKernelTime);
            ulong prevUser = FileTimeToUInt64(_prevUserTime);

            _prevIdleTime = idleTime;
            _prevKernelTime = kernelTime;
            _prevUserTime = userTime;

            ulong idleDiff = idle - prevIdle;
            ulong kernelDiff = kernel - prevKernel;
            ulong userDiff = user - prevUser;

            ulong totalDiff = kernelDiff + userDiff;

            if (totalDiff == 0)
            {
                return 0f;
            }

            // Kernel time includes idle time, so active CPU time is totalDiff - idleDiff
            if (totalDiff >= idleDiff)
            {
                ulong activeDiff = totalDiff - idleDiff;
                float pct = (float)activeDiff * 100f / totalDiff;
                // Clamp to 0-100 to handle potential floating point precision errors
                return Math.Clamp(pct, 0f, 100f);
            }

            return 0f;
        }

        public static bool GetRamMetrics(out ulong totalRamBytes, out ulong usedRamBytes)
        {
            totalRamBytes = 0;
            usedRamBytes = 0;
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                totalRamBytes = memStatus.ullTotalPhys;
                usedRamBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
                return true;
            }
            return false;
        }
    }
}
