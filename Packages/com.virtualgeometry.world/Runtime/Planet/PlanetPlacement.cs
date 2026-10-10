using UnityEngine;
using VirtualizedGeometry;

namespace VirtualGeometry.World{
    /// <summary>camera ごとに、惑星をどの空間に置いて選択・cull・描画するかの値。</summary>
    public readonly struct PlanetPlacementFrame{
        /// <summary>惑星のローカル座標でのカメラの位置(Cell の選択に使う)。</summary>
        public readonly Vector3 CameraLocal;
        /// <summary>惑星のローカル座標から cull 空間への行列。</summary>
        public readonly Matrix4x4 LocalToCull;
        public readonly float LocalToCullScale;
        public readonly VirtualGeometryClusterCullSpace CullSpace;

        public PlanetPlacementFrame(Vector3 cameraLocal,Matrix4x4 localToCull,float localToCullScale,VirtualGeometryClusterCullSpace cullSpace){
            this.CameraLocal=cameraLocal;this.LocalToCull=localToCull;this.LocalToCullScale=localToCullScale;this.CullSpace=cullSpace;
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
        /// 原点 cellOrigin(惑星のローカル座標)の Cell の draw の値。頂点は cellOrigin からの相対位置で書かれている。
        /// properties は UsesProperties が true のときだけ渡る。値を作れないときは false(この Cell を描かない)。
        /// </summary>
        bool Describe(in PlanetPlacementFrame frame,Vector3 cellOrigin,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties);
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
            frame=new PlanetPlacementFrame(planet.InverseTransformPoint(view.CameraPosition),this.localToWorld,maximum,space);
            return true;
        }

        public bool Describe(in PlanetPlacementFrame frame,Vector3 cellOrigin,out Matrix4x4 objectToWorld,out Matrix4x4 objectToCull,MaterialPropertyBlock properties){
            objectToWorld=this.localToWorld*Matrix4x4.Translate(cellOrigin);
            objectToCull=objectToWorld;
            return true;
        }
    }
}
