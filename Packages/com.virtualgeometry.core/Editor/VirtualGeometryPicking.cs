using UnityEditor;
using UnityEngine;
namespace VirtualizedGeometry.Editor{
    /// <summary>VG描画中は通常MeshRendererの描画を止めるため、代替Meshへのraycastで、Scene viewのクリックからそのGameObjectを選択できるようにする。</summary>
    [InitializeOnLoad]
    public static class VirtualGeometryPicking{
        static VirtualGeometryPicking(){SceneView.duringSceneGui+=OnSceneGui;}
        public static VirtualGeometryRenderer Pick(Ray worldRay,out float distance){
            distance=float.PositiveInfinity;VirtualGeometryRenderer result=null;
            foreach(var renderer in Object.FindObjectsByType<VirtualGeometryRenderer>()){
                if(!renderer.isActiveAndEnabled||renderer.MeshAsset?.ProxyMesh==null){continue;}Mesh mesh=renderer.MeshAsset.ProxyMesh;
                Matrix4x4 inverse=renderer.transform.worldToLocalMatrix;Vector3 origin=inverse.MultiplyPoint(worldRay.origin),direction=inverse.MultiplyVector(worldRay.direction);
                Vector3[] vertices=mesh.vertices;int[] indices=mesh.triangles;
                for(int i=0;i<indices.Length;i+=3){
                    Vector3 a=vertices[indices[i]],edge1=vertices[indices[i+1]]-a,edge2=vertices[indices[i+2]]-a;
                    Vector3 p=Vector3.Cross(direction,edge2);float det=Vector3.Dot(edge1,p);if(Mathf.Abs(det)<1e-10f){continue;}
                    Vector3 relative=origin-a;float u=Vector3.Dot(relative,p)/det;if(u<0||u>1){continue;}Vector3 q=Vector3.Cross(relative,edge1);float v=Vector3.Dot(direction,q)/det;if(v<0||u+v>1){continue;}
                    float t=Vector3.Dot(edge2,q)/det;if(t>=0&&t<distance){distance=t;result=renderer;}
                }
            }
            return result;
        }
        private static void OnSceneGui(SceneView view){
            Event input=Event.current;if(input.type!=EventType.MouseDown||input.button!=0||input.alt||Tools.current==Tool.View){return;}
            Ray ray=HandleUtility.GUIPointToWorldRay(input.mousePosition);var renderer=Pick(ray,out float distance);if(renderer==null){return;}
            // 手前に別のGameObjectのColliderがあればUnity標準の選択に任せる。同じGameObjectの当たり判定(代替Mesh)は自分として扱う。
            if(Physics.Raycast(ray,out RaycastHit hit,distance)&&hit.collider.gameObject!=renderer.gameObject){return;}
            Selection.activeGameObject=renderer.gameObject;input.Use();
        }
    }
}