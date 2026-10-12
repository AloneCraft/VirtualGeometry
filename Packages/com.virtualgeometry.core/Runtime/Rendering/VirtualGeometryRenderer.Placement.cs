using System.Collections.Generic;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// 配置の差し込み口(<see cref="IVirtualGeometryInstancePlacement"/>)と、VG で描いた frame の記録。
    /// 差し込み口は同じ GameObject の component で、RenderFeature が camera ごとに引く(後から足した component も次の描画から効く)。
    /// </summary>
    public sealed partial class VirtualGeometryRenderer{
        private readonly Dictionary<Camera,MaterialPropertyBlock> placementProperties=new Dictionary<Camera,MaterialPropertyBlock>();
        private int lastVirtualDrawFrame=-1;
        private bool proxyFallback=true;

        /// <summary>同じ GameObject の配置の差し込み口。無ければ null(Transform の行列で描く)。</summary>
        public IVirtualGeometryInstancePlacement Placement{
            get{
                if(this.TryGetComponent(out IVirtualGeometryInstancePlacement placement)){return placement;}
                return null;
            }
        }

        /// <summary>
        /// VG が描けない camera で、代替 Mesh(通常の MeshRenderer)を描くか。既定は true。
        /// false にすると、VG が描けない間は何も描かない(通常の MeshRenderer が正しい位置に描けない配置、例えば Omni の対数描画で使う)。
        /// </summary>
        public bool ProxyFallback{
            get=>this.proxyFallback;
            set{
                if(this.proxyFallback==value){return;}
                this.proxyFallback=value;
                this.ApplyDecision();
            }
        }

        /// <summary>VG で描く項目を最後に出した frame(Time.frameCount、どの camera でも)。まだなら −1。表示の切り替えで、実際に描かれたかを知るのに使う。</summary>
        public int LastVirtualDrawFrame=>this.lastVirtualDrawFrame;

        internal void MarkVirtualDrawn(){
            this.lastVirtualDrawFrame=Time.frameCount;
        }

        /// <summary>camera ごとの、差し込み口が書く draw 専用の値。</summary>
        internal MaterialPropertyBlock GetPlacementProperties(Camera camera){
            if(!this.placementProperties.TryGetValue(camera,out MaterialPropertyBlock properties)){
                properties=new MaterialPropertyBlock();
                this.placementProperties.Add(camera,properties);
            }
            return properties;
        }

        /// <summary>変換データの局所の bounds を、matrix で写した軸平行の bounds。</summary>
        internal Bounds BoundsIn(Matrix4x4 matrix){
            if(this.meshAsset==null){return new Bounds(matrix.GetColumn(3),Vector3.zero);}
            Bounds local=this.meshAsset.Bounds;
            Vector3 center=matrix.MultiplyPoint3x4(local.center);
            Vector3 extents=local.extents;
            Vector3 worldExtents=new Vector3(
                Mathf.Abs(matrix.m00)*extents.x+Mathf.Abs(matrix.m01)*extents.y+Mathf.Abs(matrix.m02)*extents.z,
                Mathf.Abs(matrix.m10)*extents.x+Mathf.Abs(matrix.m11)*extents.y+Mathf.Abs(matrix.m12)*extents.z,
                Mathf.Abs(matrix.m20)*extents.x+Mathf.Abs(matrix.m21)*extents.y+Mathf.Abs(matrix.m22)*extents.z);
            return new Bounds(center,worldExtents*2);
        }

        // 破棄された camera の値を捨てる。
        private void ReleaseDestroyedPlacementCameras(){
            if(this.placementProperties.Count==0){return;}
            List<Camera> expired=null;
            foreach(Camera camera in this.placementProperties.Keys){
                if(camera==null){
                    if(expired==null){expired=new List<Camera>();}
                    expired.Add(camera);
                }
            }
            if(expired==null){return;}
            foreach(Camera camera in expired){this.placementProperties.Remove(camera);}
        }
    }
}
