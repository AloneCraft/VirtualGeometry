using System.Collections.Generic;

namespace VirtualizedGeometry{
    /// <summary>診断readback由来のinstance計測と共有resource容量を区別する。</summary>
    public struct VirtualGeometryInstanceMetrics{
        public string GeometryKey;public string ResourceKey;public long InputTriangles;public long UniqueTriangles;public long InstanceTriangles;
        public long GpuSelectionBytes;public long DrawnTriangles;public long SelectedClusters;public long GpuGeometryBytes;public long GpuMetadataBytes;public long CpuCacheBytes;public long DrawCalls;
    }
    /// <summary>VG Statsで同じassetを複数配置してもunique bytesを二重に数えない。</summary>
    public static class VirtualGeometryDebugMetrics{
        public static VirtualGeometryInstanceMetrics Aggregate(IEnumerable<VirtualGeometryInstanceMetrics> instances){
            var result=new VirtualGeometryInstanceMetrics();var resources=new HashSet<string>();var geometries=new HashSet<string>();
            foreach(var item in instances){
                result.GpuSelectionBytes+=item.GpuSelectionBytes;result.InstanceTriangles+=item.UniqueTriangles;result.DrawnTriangles+=item.DrawnTriangles;result.SelectedClusters+=item.SelectedClusters;result.DrawCalls+=item.DrawCalls;
                if(geometries.Add(item.GeometryKey??item.ResourceKey??string.Empty)){result.InputTriangles+=item.InputTriangles;result.UniqueTriangles+=item.UniqueTriangles;}
                if(resources.Add(item.ResourceKey??string.Empty)){result.GpuGeometryBytes+=item.GpuGeometryBytes;
                    result.GpuMetadataBytes+=item.GpuMetadataBytes;result.CpuCacheBytes+=item.CpuCacheBytes;
                }
            }
            return result;
        }
    }
}