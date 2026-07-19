using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OllaMonitor
{
    public static class Nvml
    {
        private static bool _isAvailable = false;
        private static IntPtr _deviceHandle = IntPtr.Zero;
        private static string _gpuName = "Unknown GPU";

        public static bool IsAvailable => _isAvailable;
        public static string GpuName => _gpuName;

        [StructLayout(LayoutKind.Sequential)]
        public struct nvmlMemory_t
        {
            public ulong total;
            public ulong free;
            public ulong used;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct nvmlUtilization_t
        {
            public uint gpu;
            public uint memory;
        }

        private static class NativeMethods
        {
            [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlInit();

            [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlShutdown();

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out nvmlUtilization_t utilization);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.StdCall)]
            public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        }

        public static bool Initialize()
        {
            try
            {
                // Verify DLL is loadable using NativeLibrary first to avoid uncatchable DLL loader errors on non-NVIDIA machines
                if (!NativeLibrary.TryLoad("nvml.dll", out _))
                {
                    _isAvailable = false;
                    return false;
                }

                int ret = NativeMethods.nvmlInit();
                if (ret != 0)
                {
                    _isAvailable = false;
                    return false;
                }

                ret = NativeMethods.nvmlDeviceGetHandleByIndex(0, out _deviceHandle);
                if (ret != 0)
                {
                    NativeMethods.nvmlShutdown();
                    _isAvailable = false;
                    return false;
                }

                // Retrieve GPU Name
                var sb = new StringBuilder(64);
                if (NativeMethods.nvmlDeviceGetName(_deviceHandle, sb, (uint)sb.Capacity) == 0)
                {
                    _gpuName = sb.ToString();
                }
                else
                {
                    _gpuName = "NVIDIA GPU";
                }

                _isAvailable = true;
                return true;
            }
            catch
            {
                _isAvailable = false;
                return false;
            }
        }

        public static void Shutdown()
        {
            if (_isAvailable)
            {
                try
                {
                    NativeMethods.nvmlShutdown();
                }
                catch
                {
                    // Ignore
                }
                _isAvailable = false;
                _deviceHandle = IntPtr.Zero;
            }
        }

        public static bool GetGpuMetrics(out uint gpuUtilizationPercent, out ulong totalVramBytes, out ulong usedVramBytes)
        {
            gpuUtilizationPercent = 0;
            totalVramBytes = 0;
            usedVramBytes = 0;

            if (!_isAvailable || _deviceHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int ret = NativeMethods.nvmlDeviceGetUtilizationRates(_deviceHandle, out nvmlUtilization_t util);
                if (ret == 0)
                {
                    gpuUtilizationPercent = util.gpu;
                }

                ret = NativeMethods.nvmlDeviceGetMemoryInfo(_deviceHandle, out nvmlMemory_t mem);
                if (ret == 0)
                {
                    totalVramBytes = mem.total;
                    usedVramBytes = mem.used;
                }

                return ret == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
