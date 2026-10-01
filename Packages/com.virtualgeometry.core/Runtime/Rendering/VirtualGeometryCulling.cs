using System;
using UnityEngine;

namespace VirtualizedGeometry{
    public readonly struct VirtualGeometryWorldSphere{
        public Vector3 Center{get;}
        public float Radius{get;}
        public VirtualGeometryWorldSphere(Vector3 center,float radius){this.Center=center;this.Radius=radius;}
    }

    /// <summary>GPU cullingと同じ保守規則をテスト可能なCPU関数として定義する。</summary>
    public static class VirtualGeometryCulling{
        public static VirtualGeometryWorldSphere TransformSphere(Vector3 localCenter,float localRadius,Matrix4x4 localToWorld){
            if(!float.IsFinite(localRadius)||localRadius<0){throw new ArgumentOutOfRangeException(nameof(localRadius));}
            float x=new Vector3(localToWorld.m00,localToWorld.m10,localToWorld.m20).magnitude;
            float y=new Vector3(localToWorld.m01,localToWorld.m11,localToWorld.m21).magnitude;
            float z=new Vector3(localToWorld.m02,localToWorld.m12,localToWorld.m22).magnitude;
            float scale=Mathf.Max(x,Mathf.Max(y,z));
            return new VirtualGeometryWorldSphere(localToWorld.MultiplyPoint3x4(localCenter),localRadius*scale);
        }

        public static bool IsSphereInsideFrustum(Vector3 center,float radius,Plane[] planes){
            if(planes==null){throw new ArgumentNullException(nameof(planes));}
            if(!float.IsFinite(radius)||radius<0){throw new ArgumentOutOfRangeException(nameof(radius));}
            for(int i=0;i<planes.Length;i++){
                if(planes[i].GetDistanceToPoint(center)<-radius){return false;}
            }
            return true;
        }

        public static float ReduceDepth(float a,float b,float c,float d,bool reversedZ){
            return reversedZ?Mathf.Min(Mathf.Min(a,b),Mathf.Min(c,d)):Mathf.Max(Mathf.Max(a,b),Mathf.Max(c,d));
        }

        public static bool IsOccludedDepth(float objectNearestDepth,float hzbFarthestDepth,bool reversedZ,float bias){
            if(!float.IsFinite(objectNearestDepth)||!float.IsFinite(hzbFarthestDepth)||!float.IsFinite(bias)||bias<0){
                throw new ArgumentOutOfRangeException(nameof(bias));
            }
            return reversedZ?objectNearestDepth<hzbFarthestDepth-bias:objectNearestDepth>hzbFarthestDepth+bias;
        }
    }
}
