using System;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>格納tangentがないv3向け、三角形の位置差とUV差からtangent/bitangentを作るCPU参照(VirtualGeometryVertexInput.hlslのVGFaceTangentと同じ式)。UV縮退時はfalse。</summary>
    public static class VirtualGeometryNormalMapping{
        public static bool TryBuildFrame(Vector3 normal,Vector3 dx,Vector3 dy,Vector2 uvx,Vector2 uvy,out Vector3 tangent,out Vector3 bitangent){
            float determinant=uvx.x*uvy.y-uvx.y*uvy.x;tangent=Vector3.zero;bitangent=Vector3.zero;
            if(!float.IsFinite(determinant)||Mathf.Abs(determinant)<1e-10f){return false;}
            normal.Normalize();tangent=(dx*uvy.y-dy*uvx.y)/determinant;bitangent=(-dx*uvy.x+dy*uvx.x)/determinant;
            tangent-=normal*Vector3.Dot(normal,tangent);bitangent-=normal*Vector3.Dot(normal,bitangent);
            if(tangent.sqrMagnitude<1e-12f||bitangent.sqrMagnitude<1e-12f){return false;}
            tangent.Normalize();bitangent.Normalize();return true;
        }
    }
}