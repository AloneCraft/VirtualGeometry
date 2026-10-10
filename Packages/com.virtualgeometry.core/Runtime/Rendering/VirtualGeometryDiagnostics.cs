using UnityEngine;
using UnityEngine.Rendering;
namespace VirtualizedGeometry{
    // serialized値を保つため、追加modeは末尾へ置く。
    public enum VirtualGeometryDebugView{None,Clusters,LodLevel,ScreenError,ResidentPages,Loading,Culling,Triangles}
    /// <summary>明示的に有効化した直前frameのmarker値。未取得のGPU時間はNaNとする。</summary>
    public static class VirtualGeometryDiagnostics{
        private static ProfilingSampler hzb,select,draw;private static bool enabled;
        internal static ProfilingSampler Hzb=>hzb??=Create("VG HZB");
        internal static ProfilingSampler Select=>select??=Create("VG Select");
        internal static ProfilingSampler Draw=>draw??=Create("VG Draw");
        private static ProfilingSampler Create(string name){var sampler=new ProfilingSampler(name);sampler.enableRecording=enabled;return sampler;}
        /// <summary>直近のcameraで出した選択・描画・feedbackの項目数。部品×material slotごとの描画が何件になっているかを見る診断値。</summary>
        public static int LastSelectItems,LastDrawItems,LastFeedbackItems;
        public static bool Enabled{get=>enabled;set{enabled=value;if(hzb!=null){hzb.enableRecording=value;}if(select!=null){select.enableRecording=value;}if(draw!=null){draw.enableRecording=value;}}}
        /// <summary>HZB生成・選択・描画のpassごとのGPU時間(ms)。未取得はNaN。passが複数ある描画では直近の値。</summary>
        public static double HzbGpuMilliseconds=>PassGpu(hzb);
        public static double SelectGpuMilliseconds=>PassGpu(select);
        public static double DrawGpuMilliseconds=>PassGpu(draw);
        private static double PassGpu(ProfilingSampler sampler){
            if(!enabled||sampler==null||sampler.gpuElapsedTime<=0){return double.NaN;}
            return sampler.gpuElapsedTime;
        }
        public static double CpuMilliseconds=>enabled?Hzb.cpuElapsedTime+Select.cpuElapsedTime+Draw.cpuElapsedTime:double.NaN;
        public static double GpuMilliseconds=>enabled&&Hzb.gpuElapsedTime+Select.gpuElapsedTime+Draw.gpuElapsedTime>0?Hzb.gpuElapsedTime+Select.gpuElapsedTime+Draw.gpuElapsedTime:double.NaN;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Reset(){enabled=false;hzb?.Dispose();select?.Dispose();draw?.Dispose();hzb=null;select=null;draw=null;}
    }
}