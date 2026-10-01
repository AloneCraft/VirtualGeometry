using System;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// space_elevator.fbx の座標規約を1か所にまとめる。手続きシェーダー(SpaceElevator/Procedural)は Blender のオブジェクト座標(m、Z上)で模様を計算するため、
    /// Unity Mesh の座標を Blender 座標へ戻す倍率と軸をここで定め、マテリアル生成(SpaceElevatorMaterialBuilder)の _PatternScale と、
    /// シーン生成(SpaceElevatorSceneBuilder)の太陽・カメラの変換に使う。
    /// 使用先: SpaceElevator.unity の FBX prefab(原点・無回転で配置)、Import.unity の同じ FBX。
    /// </summary>
    public static class SpaceElevatorPatternSpace{
        /// <summary>対象モデル。Blender 5.2.1 の FBX 既定設定(UnitScaleFactor 1 = cm、各オブジェクトに Lcl Rotation(-90,0,0)・Lcl Scaling 100)で書き出されている。</summary>
        public const string ModelPath="Assets/VirtualizedGeometrySample/space_elevator.fbx";

        /// <summary>Unity Mesh の1単位が Blender の何 m か。ModelImporter の useFileScale が cm を 0.01 倍で頂点へ焼き込み、Transform に 100 倍が残る。</summary>
        public const float MeshToBlenderScale=100f;

        /// <summary>
        /// Unity Mesh 座標を Blender のオブジェクト座標(m)へ戻す。FBX の右手系から Unity の左手系へは X が反転し、軸の入れ替え(Z上→Y上)は Transform 側に残る。
        /// </summary>
        /// <param name="meshPosition">Mesh の頂点座標(Unity Mesh 空間)。</param>
        public static Vector3 MeshToBlender(Vector3 meshPosition){
            return new Vector3(-meshPosition.x,meshPosition.y,meshPosition.z)*MeshToBlenderScale;
        }

        /// <summary>
        /// Blender のワールド座標(m、Z上)を、prefab を原点・無回転に置いたときの Unity ワールド座標(m、Y上)へ変換する。
        /// 方向ベクトルにもそのまま使える(倍率1・回転と反転だけ)。太陽の向きとカメラの位置合わせに使う。
        /// </summary>
        /// <param name="blender">Blender のワールド座標または方向。</param>
        public static Vector3 BlenderToWorld(Vector3 blender){
            return new Vector3(-blender.x,blender.z,-blender.y);
        }

        /// <summary>
        /// FBX の元 Mesh(VG 変換前のサブアセット)を名前で探す。MeshFilter には VG の代替 Mesh が入るため、座標規約の確認と元 Mesh との比較にはこちらを使う。
        /// </summary>
        /// <param name="meshName">Blender のメッシュ名(例: "SE_GA_AnchorTower")。見つからなければ null。</param>
        public static Mesh SourceMesh(string meshName){
            foreach(UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(ModelPath)){
                if(asset is Mesh mesh&&string.Equals(mesh.name,meshName,StringComparison.Ordinal)){
                    return mesh;
                }
            }
            return null;
        }

        /// <summary>Mesh の bounds を Blender 座標へ戻した軸平行の範囲。X が反転するため min/max を入れ替える。</summary>
        /// <param name="mesh">対象 Mesh。</param>
        public static Bounds BlenderBounds(Mesh mesh){
            Vector3 a=MeshToBlender(mesh.bounds.min);
            Vector3 b=MeshToBlender(mesh.bounds.max);
            var result=new Bounds(a,Vector3.zero);
            result.Encapsulate(b);
            return result;
        }
    }
}
