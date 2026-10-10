using UnityEngine;
using VirtualizedGeometry;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>camera ごとに、惑星をどの空間に置いて選択・cull・描画するかの値。</summary>
    public readonly struct PlanetPlacementFrame{
        /// <summary>惑星のローカル座標でのカメラの位置(Logical。Cell の選択に使う)。</summary>
        public readonly LogicalVector3 CameraLocal;
        /// <summary>惑星のローカル座標の向きを cull 空間へ写す行列(線形部分だけを使う)。</summary>
        public readonly Matrix4x4 LocalToCull;
        /// <summary>cull 空間でのカメラの位置。</summary>
        public readonly Vector3 CameraCull;
        public readonly float LocalToCullScale;
        public readonly VirtualGeometryClusterCullSpace CullSpace;

        public PlanetPlacementFrame(in LogicalVector3 cameraLocal,Matrix4x4 localToCull,Vector3 cameraCull,float localToCullScale,VirtualGeometryClusterCullSpace cullSpace){
            this.CameraLocal=cameraLocal;this.LocalToCull=localToCull;this.CameraCull=cameraCull;this.LocalToCullScale=localToCullScale;this.CullSpace=cullSpace;
        }
    }

    /// <summary>
    /// 惑星を置く座標系。既定(<see cref="StandardPlanetPlacement"/>)は Unity の world 空間で、惑星の GameObject の行列を使う。
    /// Omni の対数描画では、惑星の位置を Logical で持ち、カメラ相対の物理空間で cull し、Cell ごとに Omni の物体の値を書く実装を使う
    /// (VirtualGeometry.Omni の OmniPlanetPlacement。惑星と同じ GameObject に付けると <see cref="PlanetTerrainSource"/> が使う)。
    /// 呼び出しは camera ごとに TryBeginFrame → Describe(Cell の数だけ)の順で、同じ camera の中で入れ子にならない。
    /// </summary>
    public interface IPlanetPlacement{
        /// <summary>この camera で描くための値を作る。描けないときは false(この camera では惑星を描かない)。</summary>
        bool TryBeginFrame(in VirtualGeometryClusterView view,Transform planet,out PlanetPlacementFrame frame);

        /// <summary>Cell ごとの MaterialPropertyBlock を使うか。</summary>
        bool UsesProperties{get;}

        /// <summary>
        /// Cell の draw の値。頂点は Cell の原点(cell.Origin、惑星のローカル座標、Logical 精度)からの相対位置で書かれている。
        /// properties は UsesProperties が true のときだけ渡る。値を作れないときは false(この Cell を描かない)。
        /// </summary>
        bool Describe(in PlanetPlacementFrame frame,in PlanetCellFrame cell,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties);
    }

    /// <summary>Unity の world 空間に惑星を置く(float の精度で足りる大きさに限る)。</summary>
    public sealed class StandardPlanetPlacement:IPlanetPlacement{
        private Matrix4x4 localToWorld;

        public bool UsesProperties=>false;

        public bool TryBeginFrame(in VirtualGeometryClusterView view,Transform planet,out PlanetPlacementFrame frame){
            this.localToWorld=planet.localToWorldMatrix;
            Vector3 scale=planet.lossyScale;
            float maximum=Mathf.Max(Mathf.Abs(scale.x),Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
            var space=new VirtualGeometryClusterCullSpace(view.CameraPosition,view.FrustumPlanes,view.ViewProjection,view.NearPlane,true);
            // Unity の world 空間は float なので、カメラの位置も float から作る(大きな惑星では Omni の配置を使う)。
            frame=default;
            if(!LogicalVector3.TryFromFloat3(planet.InverseTransformPoint(view.CameraPosition),out LogicalVector3 cameraLocal)){return false;}
            frame=new PlanetPlacementFrame(in cameraLocal,this.localToWorld,view.CameraPosition,maximum,space);
            return true;
        }

        public bool Describe(in PlanetPlacementFrame frame,in PlanetCellFrame cell,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties){
            objectToWorld=this.localToWorld*Matrix4x4.Translate(cell.ApproximateOrigin);
            objectToCull=objectToWorld;
            return true;
        }
    }
}
