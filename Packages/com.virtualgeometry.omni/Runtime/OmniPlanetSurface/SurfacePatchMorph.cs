using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>月面LODの親子形状差分を段階生成し、GPU頂点補間へ渡します。</summary>
    public sealed class SurfacePatchMorph{
        private readonly SurfacePatchBuilder parent,child;
        private Vector3[] endpointPositions,endpointNormals;
        private int cursor;
        private Bounds bounds;
        public Vector4[] PositionDelta{get;}
        public Vector4[] NormalDelta{get;}
        public bool IsComplete=>this.cursor==this.PositionDelta.Length;
        public int CompletedVertices=>this.cursor;
        public long CpuBytes=>this.PositionDelta.Length*32L;
        public Bounds Bounds=>this.bounds;

        /// <summary>同じ面にある完成済みの直接の親と子を渡します。</summary>
        public SurfacePatchMorph(SurfacePatchBuilder parent,SurfacePatchBuilder child):
            this(parent,child,CreateDeltaBuffer(child),CreateDeltaBuffer(child)){}
        internal SurfacePatchMorph(SurfacePatchBuilder parent,SurfacePatchBuilder child,
            Vector4[] positionDelta,Vector4[] normalDelta){
            if(parent==null||child==null||!parent.IsComplete||!child.IsComplete||
                parent.Key.Face!=child.Key.Face||child.Key.Level!=parent.Key.Level+1||
                child.Key.X/2!=parent.Key.X||child.Key.Y/2!=parent.Key.Y||parent.Resolution!=child.Resolution||
                positionDelta==null||normalDelta==null||positionDelta.Length!=child.Vertices.Length||
                normalDelta.Length!=child.Vertices.Length){
                throw new ArgumentException("完成した同一解像度の親子パッチが必要です。");
            }
            this.parent=parent;this.child=child;
            this.PositionDelta=positionDelta;this.NormalDelta=normalDelta;
        }

        /// <summary>同じパッチの変更前スナップショットを端点にします。配列は完成まで変更しないでください。</summary>
        public SurfacePatchMorph(SurfacePatchBuilder target,Vector3[] endpointPositions,Vector3[] endpointNormals):
            this(target,endpointPositions,endpointNormals,CreateDeltaBuffer(target),CreateDeltaBuffer(target)){}
        internal SurfacePatchMorph(SurfacePatchBuilder target,Vector3[] endpointPositions,Vector3[] endpointNormals,
            Vector4[] positionDelta,Vector4[] normalDelta){
            if(target==null||!target.IsComplete||endpointPositions==null||endpointNormals==null||
                endpointPositions.Length!=target.Vertices.Length||endpointNormals.Length!=target.Vertices.Length||
                positionDelta==null||normalDelta==null||positionDelta.Length!=target.Vertices.Length||
                normalDelta.Length!=target.Vertices.Length){
                throw new ArgumentException("完成したパッチと同じ頂点数のスナップショットが必要です。");
            }
            this.child=target;this.endpointPositions=endpointPositions;this.endpointNormals=endpointNormals;
            this.PositionDelta=positionDelta;this.NormalDelta=normalDelta;
        }
        private static Vector4[] CreateDeltaBuffer(SurfacePatchBuilder target){
            return new Vector4[target?.Vertices?.Length??0];
        }
        /// <summary>1頂点だけ進め、完成済みならfalseを返します。</summary>
        public bool Step(){
            if(this.IsComplete){return false;}
            int index=this.cursor,resolution=this.child.Resolution,surfaceCount=(resolution+1)*(resolution+1);
            Vector3 delta,normalDelta;
            if(this.parent==null){
                delta=this.endpointPositions[index]-this.child.Vertices[index];
                normalDelta=this.endpointNormals[index]-this.child.Normals[index];
            }else if(index<surfaceCount){
                double span=2.0/(1<<this.child.Key.Level);
                SurfaceVector direction=SurfaceGeometry.CubeDirection(this.child.Key.Face,
                    -1+span*(this.child.Key.X+(index%(resolution+1))/(double)resolution),
                    -1+span*(this.child.Key.Y+(index/(resolution+1))/(double)resolution));
                SurfacePatchSampler.SampleSurface(this.parent,direction,out SurfaceVector position,out Vector3 normal);
                // 天体中心の絶対座標をfloatへ変換せず、子原点との差分だけを渡します。
                SurfaceVector relative=position-this.child.Origin;
                delta=new Vector3((float)relative.X,(float)relative.Y,(float)relative.Z)-this.child.Vertices[index];
                normalDelta=normal-this.child.Normals[index];
            }else{
                int slot=index-surfaceCount;
                int source=SurfacePatchBuilder.EdgeVertex(slot/(resolution+1),slot%(resolution+1),resolution);
                delta=this.PositionDelta[source];normalDelta=this.NormalDelta[source];
            }
            this.PositionDelta[index]=new Vector4(delta.x,delta.y,delta.z,0);
            this.NormalDelta[index]=new Vector4(normalDelta.x,normalDelta.y,normalDelta.z,0);
            if(index==0){this.bounds=new Bounds(this.child.Vertices[index],Vector3.zero);}
            this.bounds.Encapsulate(this.child.Vertices[index]);
            this.bounds.Encapsulate(this.child.Vertices[index]+delta);
            this.cursor++;
            // Boundsのcenter/extents再計算による丸めで端点が外へ出ない最小限の余裕です。
            if(this.IsComplete){
                this.bounds.Expand(Mathf.Max(0.00001f,this.bounds.size.magnitude*0.000001f));
                this.endpointPositions=null;this.endpointNormals=null;
            }
            return true;
        }
        /// <summary>正の秒数は分割、負の秒数は統合。時刻はシェーダーの_Time.yと共通です。</summary>
        public void Upload(Mesh mesh,float startTime,float signedDuration){
            if(!this.IsComplete){throw new InvalidOperationException("形状補間の準備が完了していません。");}
            if(mesh==null||mesh.vertexCount!=this.PositionDelta.Length){throw new ArgumentException("対応する子メッシュが必要です。");}
            if(!float.IsFinite(startTime)||!float.IsFinite(signedDuration)){
                throw new ArgumentOutOfRangeException(nameof(startTime));
            }
            for(int i=0;i<this.PositionDelta.Length;i++){
                this.PositionDelta[i].w=startTime;this.NormalDelta[i].w=signedDuration;
            }
            // UV2は生成法線タイル用に空け、UV3/4へ形状差分と時間契約を格納します。
            mesh.SetUVs(3,this.PositionDelta);mesh.SetUVs(4,this.NormalDelta);mesh.bounds=this.bounds;
        }
        /// <summary>GPUと共通の親形状の重みです。期間0は通常形状を示します。</summary>
        public static float ParentWeight(float time,float startTime,float signedDuration){
            if(signedDuration==0){return 0;}
            float t=Mathf.Clamp01((time-startTime)/Mathf.Abs(signedDuration));
            float smooth=t*t*(3-2*t);
            if(signedDuration<0){return smooth;}
            return 1-smooth;
        }
    }
}
