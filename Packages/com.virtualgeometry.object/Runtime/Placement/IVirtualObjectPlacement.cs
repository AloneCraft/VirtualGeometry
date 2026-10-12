using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 仮想オブジェクトを置く空間。ルート(<see cref="VirtualObjectSystem"/> の GameObject)の局所の Logical 座標と、Unity の描画の間をつなぐ。
    /// 標準は Unity の world(<see cref="StandardObjectPlacement"/>)、Omni の Logical 座標は VirtualGeometry.Omni の OmniObjectPlacement。
    /// </summary>
    public interface IVirtualObjectPlacement{
        /// <summary>camera のルートの局所の位置(Logical)と向き、投影の倍率(画面の高さの px ÷ (2·tan(画角/2)))。</summary>
        bool TryGetViewer(Transform root,Camera camera,out LogicalVector3 rootLocalPosition,out Quaternion rootLocalRotation,out float projectionScale);
        /// <summary>GameObject を、ルートの局所の原点・向き・拡大率に置く。</summary>
        void Place(Transform root,GameObject instance,in LogicalVector3 rootLocalOrigin,Quaternion rootLocalRotation,float scale);
        /// <summary>見せる直前(描画への登録など)。</summary>
        void OnShown(GameObject instance);
        /// <summary>隠した直後(描画からの登録の解除など)。</summary>
        void OnHidden(GameObject instance);
        /// <summary>隠した GameObject を破棄するまで待つ frame 数(描画の仕組みが同じ frame の破棄を許さないとき)。</summary>
        int RetireFrames{get;}
        /// <summary>見せてから実際に描かれるまでの frame 数(描画への登録が次の更新で効く仕組みでは 2 以上)。粗い表現を外すのは、この frame 数の後。</summary>
        int VisibleAfterFrames{get;}
    }

    /// <summary>
    /// Unity の world に置く(float)。ルートの Transform の局所の位置を、ルートの行列で world にする。Omni の無いプロジェクトと EditMode のテスト用。
    /// 位置は float になるので、ルートから遠いと精度が落ちる(大きな世界では Omni の配置を使う)。
    /// </summary>
    public sealed class StandardObjectPlacement:IVirtualObjectPlacement{
        public int RetireFrames=>0;
        public int VisibleAfterFrames=>1;

        public bool TryGetViewer(Transform root,Camera camera,out LogicalVector3 rootLocalPosition,out Quaternion rootLocalRotation,out float projectionScale){
            rootLocalPosition=default;rootLocalRotation=Quaternion.identity;projectionScale=0f;
            if(camera==null||root==null){return false;}
            Vector3 local=root.InverseTransformPoint(camera.transform.position);
            if(!LogicalVector3.TryFromFloat3((float3)local,out rootLocalPosition)){return false;}
            rootLocalRotation=Quaternion.Inverse(root.rotation)*camera.transform.rotation;
            projectionScale=ProjectionScale(camera);
            return projectionScale>0f;
        }

        public void Place(Transform root,GameObject instance,in LogicalVector3 rootLocalOrigin,Quaternion rootLocalRotation,float scale){
            if(!rootLocalOrigin.TryToFloat3(out float3 local)){return;}
            Transform transform=instance.transform;
            transform.SetParent(null,false);
            transform.SetPositionAndRotation(root.TransformPoint((Vector3)local),root.rotation*rootLocalRotation);
            transform.localScale=Vector3.one*scale;
        }

        public void OnShown(GameObject instance){
        }

        public void OnHidden(GameObject instance){
        }

        /// <summary>画面の高さの px ÷ (2·tan(画角/2))。正投影は未対応(0 を返す)。</summary>
        public static float ProjectionScale(Camera camera){
            if(camera.orthographic){return 0f;}
            float height=camera.pixelHeight;
            if(camera.targetTexture!=null){height=camera.targetTexture.height;}
            if(!(height>0f)){height=Screen.height;}
            return height/(2f*Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad));
        }
    }

    /// <summary>
    /// 地面。ルートの局所の位置で地面の高さを求め、地形が十分に細かく出ているかを返す(粗い地形の上に置くと、谷では浮いて見えるため)。
    /// 惑星では <see cref="PlanetObjectGround"/>。
    /// </summary>
    public interface IVirtualObjectGround{
        /// <summary>
        /// position(ルートの局所)の真下(または真上)の地面の点と、上向き。地面が無ければ false。
        /// </summary>
        bool TryProject(in LogicalVector3 rootLocalPosition,out LogicalVector3 groundPoint,out Vector3 up);
        /// <summary>position の周り radius の地形が、表示に十分な細かさで出ているか(出ていなければ要求する)。</summary>
        bool IsReady(in LogicalVector3 rootLocalPosition,float radius);
    }
}
