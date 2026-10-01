using System;
using System.Runtime.InteropServices;

namespace VirtualizedGeometry{
    /// <summary>Windows実processのworking setを取得する。Unity MonoのProcess.WorkingSet64が0を返す環境の計測用。</summary>
    public static class VirtualGeometryProcessMemory{
        [StructLayout(LayoutKind.Sequential)] private struct Counters{
            public uint Size,PageFaults;public UIntPtr PeakWorkingSet,WorkingSet,PeakPagedPool,PagedPool,PeakNonPagedPool,NonPagedPool,PageFile,PeakPageFile,PrivateBytes;
        }
        [DllImport("kernel32.dll",ExactSpelling=true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll",ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryPerformanceCounter(out long value);
        public static long Timestamp{get{if(!QueryPerformanceCounter(out long value)){throw new InvalidOperationException("QueryPerformanceCounter failed.");}return value;}}
        [DllImport("psapi.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(IntPtr process,ref Counters counters,uint size);
        public static long WorkingSetBytes{
            get{
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                var counters=new Counters{Size=(uint)Marshal.SizeOf<Counters>()};if(!GetProcessMemoryInfo(GetCurrentProcess(),ref counters,counters.Size)){throw new InvalidOperationException("GetProcessMemoryInfo failed: "+Marshal.GetLastWin32Error());}
                return checked((long)counters.WorkingSet.ToUInt64());
#else
                throw new PlatformNotSupportedException("Process memory validation currently targets Windows.");
#endif
            }
        }
    }
}