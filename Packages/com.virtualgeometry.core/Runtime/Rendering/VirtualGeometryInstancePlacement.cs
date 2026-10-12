using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// <see cref="VirtualGeometryRenderer"/> を、Transform の行列とは別の配置で描くための差し込み口(Omni の Logical 座標と対数描画など)。
    /// 同じ GameObject の component が実装する。無ければ、これまでどおり Transform の行列で world 空間に描く。
    /// RenderFeature が camera ごと・frame ごとに、記録中(RenderGraph の記録、main thread)に呼ぶ。
    /// 返した姿勢で、LOD の選択と cull(<see cref="VirtualGeometryInstancePose.CullSpace"/> の空間)と描画(描画行列と MaterialPropertyBlock)を行う。
    /// </summary>
    public interface IVirtualGeometryInstancePlacement{
        /// <summary>
        /// この camera での姿勢を求め、draw 専用の値(物体ごとの値など)を properties に書く。
        /// properties は renderer×camera ごとに 1 つで、前の frame の値が残っている。描けないときは false を返し、この camera では描かない。
        /// </summary>
        bool TryDescribe(VirtualGeometryRenderer renderer,in VirtualGeometryClusterView view,MaterialPropertyBlock properties,out VirtualGeometryInstancePose pose);
    }

    /// <summary>差し込み口(<see cref="IVirtualGeometryInstancePlacement"/>)が返す、1 つの camera での姿勢。</summary>
    public readonly struct VirtualGeometryInstancePose{
        /// <summary>描画の行列(unity_ObjectToWorld。照明などに使う)。</summary>
        public readonly Matrix4x4 ObjectToWorld;
        /// <summary>物体の局所から cull 空間への行列(LOD の画面上の誤差、視錐台と HZB の判定に使う)。</summary>
        public readonly Matrix4x4 ObjectToCull;
        /// <summary>cull を行う空間(camera の位置、視錐台、near、HZB を使ってよいか)。</summary>
        public readonly VirtualGeometryClusterCullSpace CullSpace;

        public VirtualGeometryInstancePose(Matrix4x4 objectToWorld,Matrix4x4 objectToCull,VirtualGeometryClusterCullSpace cullSpace){
            this.ObjectToWorld=objectToWorld;this.ObjectToCull=objectToCull;this.CullSpace=cullSpace;
        }
    }
}
