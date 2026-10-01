using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// cameraごとのcluster選択状態(描画三角形数、Level分布、cut変化頻度、culling、page常駐)を集計する。
    /// 値はAsyncGPUReadbackで非同期に取得し、通常描画へ同期readbackを混ぜない。表示はEditorのMonitor windowが担う。
    /// </summary>
    public sealed class VirtualGeometrySelectionMonitor{
        private sealed class Row{
            public bool Pending;public bool HasSample;
            public long DrawnTriangles;public int SelectedNodes;public int[] LevelCounts=new int[0];public uint[] Culling=new uint[4];
            public ulong LastCutHash;public bool HasCut;public readonly Queue<float> CutChangeTimes=new Queue<float>();
            public readonly Queue<KeyValuePair<float,long>> EvictionHistory=new Queue<KeyValuePair<float,long>>();
        }
        private readonly Dictionary<VirtualGeometryRenderer,Row> rows=new Dictionary<VirtualGeometryRenderer,Row>();
        private readonly StringBuilder text=new StringBuilder();
        private Camera lastCamera;

        /// <summary>完了済みでないinstanceごとに1件ずつ非同期readbackを要求する。cameraを切り替えると集計をやり直す。</summary>
        public void Update(Camera camera){
            if(camera!=this.lastCamera){this.rows.Clear();this.lastCamera=camera;}
            if(camera==null){return;}
            foreach(VirtualGeometryRenderer renderer in VirtualGeometryRenderRegistry.Renderers){
                if(renderer==null||!renderer.isActiveAndEnabled){continue;}
                if(!this.rows.TryGetValue(renderer,out Row row)){row=new Row();this.rows.Add(renderer,row);}
                if(row.Pending){continue;}
                uint[] levels=renderer.NodeLevels;
                row.Pending=renderer.RequestSelectionReadback(camera,snapshot=>{row.Pending=false;if(snapshot!=null&&levels!=null){Apply(row,snapshot,levels);}});
            }
            List<VirtualGeometryRenderer> removed=null;
            foreach(var pair in this.rows){if(pair.Key==null){removed??=new List<VirtualGeometryRenderer>();removed.Add(pair.Key);}}
            if(removed!=null){foreach(VirtualGeometryRenderer renderer in removed){this.rows.Remove(renderer);}}
        }

        public string BuildReport(Camera camera){
            this.text.Clear();
            if(camera==null){return "対象cameraがありません。";}
            float now=Time.realtimeSinceStartup;
            this.text.Append("camera: ").Append(camera.name).Append("  (Level 0 = 最詳細)\n");
            foreach(var pair in this.rows){
                VirtualGeometryRenderer renderer=pair.Key;
                if(renderer==null||!renderer.isActiveAndEnabled){continue;}
                this.AppendRow(renderer,pair.Value,camera,now);
            }
            if(this.rows.Count==0){this.text.Append("\nVirtual Geometry Rendererがありません。");}
            return this.text.ToString();
        }

        private static void Apply(Row row,VirtualGeometryGpuSelectionSnapshot snapshot,uint[] levels){
            uint[] flags=snapshot.SelectionFlags;int maximumLevel=0;
            for(int i=0;i<levels.Length;i++){maximumLevel=Mathf.Max(maximumLevel,(int)levels[i]);}
            int[] counts=new int[maximumLevel+1];int selected=0;ulong hash=14695981039346656037UL;
            for(int node=0;node<flags.Length&&node<levels.Length;node++){
                if(flags[node]==0){continue;}
                selected++;counts[levels[node]]++;
                hash=(hash^(ulong)node)*1099511628211UL;
            }
            long drawn=0;for(int i=0;i<snapshot.Arguments.Length;i+=4){drawn+=snapshot.Arguments[i]/3;}
            float now=Time.realtimeSinceStartup;
            if(row.HasCut&&row.LastCutHash!=hash){row.CutChangeTimes.Enqueue(now);}
            row.LastCutHash=hash;row.HasCut=true;
            row.DrawnTriangles=drawn;row.SelectedNodes=selected;row.LevelCounts=counts;row.Culling=snapshot.CullingStatistics;row.HasSample=true;
        }

        private void AppendRow(VirtualGeometryRenderer renderer,Row row,Camera camera,float now){
            this.text.Append('\n').Append(renderer.name);
            if(renderer.TryGetCameraDecision(camera,out VirtualGeometryRenderDecision decision)&&decision.UseProxy){
                this.text.Append("  [Proxy] ").Append(decision.Reason).Append('\n');return;
            }
            this.text.Append("  [専用描画]\n");
            if(!row.HasSample){this.text.Append("  選択結果を取得中(対象cameraの描画待ち)\n");return;}
            long leaf=0;if(renderer.MeshAsset!=null){leaf=renderer.MeshAsset.LeafTriangleCount;}
            this.text.Append("  triangles ").Append(row.DrawnTriangles.ToString("N0",CultureInfo.InvariantCulture)).Append(" / leaf ").Append(leaf.ToString("N0",CultureInfo.InvariantCulture));
            if(leaf>0){this.text.Append(" (").Append((100.0*row.DrawnTriangles/leaf).ToString("F0",CultureInfo.InvariantCulture)).Append("%)");}
            this.text.Append("   selected nodes ").Append(row.SelectedNodes).Append('\n');
            this.text.Append("  Level");
            for(int level=0;level<row.LevelCounts.Length;level++){this.text.Append("  L").Append(level).Append(':').Append(row.LevelCounts[level]);}
            this.text.Append('\n');
            while(row.CutChangeTimes.Count>0&&now-row.CutChangeTimes.Peek()>1f){row.CutChangeTimes.Dequeue();}
            this.text.Append("  cut変化 ").Append(row.CutChangeTimes.Count).Append("/s   clusters considered ").Append(row.Culling[0]).Append(" frustum ").Append(row.Culling[1])
                .Append(" HZB ").Append(row.Culling[2]).Append(" visible ").Append(row.Culling[3]).Append('\n');
            if(row.DrawnTriangles==0&&row.Culling[3]>0){this.text.Append("  警告: 可視clusterがあるのに描画0 (出力容量超過の可能性)\n");}
            VirtualGeometryStreamingStatistics streaming=renderer.StreamingStatistics;
            if(streaming==null){this.text.Append("  streaming なし(全常駐)\n");return;}
            row.EvictionHistory.Enqueue(new KeyValuePair<float,long>(now,streaming.Evictions));
            while(row.EvictionHistory.Count>1&&now-row.EvictionHistory.Peek().Key>1f){row.EvictionHistory.Dequeue();}
            long evictionsPerSecond=streaming.Evictions-row.EvictionHistory.Peek().Value;
            this.text.Append("  pages resident ").Append(streaming.Resident).Append(" queued/loading/uploading ").Append(streaming.Queued).Append('/').Append(streaming.Loading).Append('/').Append(streaming.Uploading)
                .Append("   eviction ").Append(evictionsPerSecond).Append("/s   quality target miss ").Append(streaming.QualityTargetMisses).Append('\n');
        }
    }
}
