using Omni;
using UnityEngine;
using LogicalMatrix3x3=Omni.Logical.Matrix3x3;
using Frustum=Omni.Rendering.Radial.Frustum;
using ViewSnapshot=Omni.Rendering.Radial.ViewSnapshot;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// Omni の Radial 対数描画で VG の cull を行う空間の共通処理(惑星の Cell と、VG の部品で同じ)。
    /// cull は eye を原点とする world 軸の物理空間で行い、far は無限遠、HZB は使わない(深度が対数写像のため)。
    /// </summary>
    internal static class OmniRadialCull{
        // far plane は使わない(Radial は遠方を圧縮して描くので、Unity の far で切らない)。
        private const float UnboundedDistance=1e30f;

        /// <summary>この camera と frame の Omni の ViewSnapshot と near。使えなければ false と理由。</summary>
        public static bool TryGetSnapshot(Camera camera,out ViewSnapshot snapshot,out float near,out string failure){
            near=0f;failure=null;
            if(camera==null||!RadialViewRegistry.TryGetCurrent(camera.GetEntityId(),out snapshot)){
                snapshot=default;
                failure="Omni の ViewSnapshot がこの camera と frame に無い。";
                return false;
            }
            if(!snapshot.GpuFrame.IsUsable||!snapshot.Frustum.IsValid){
                failure="Omni の GpuFrame または錐台が使えない。";
                return false;
            }
            if(snapshot.Frustum.IsOrthographic){
                failure="正投影の camera は未対応。";
                return false;
            }
            if(!snapshot.NearClip.TryToFloat(out near)||!(near>0.0f)){
                failure="Omni の near を float にできない。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// eye を原点とする world 軸の空間で、Omni の錐台から side と near の平面を planes(6 個)に書き、その空間の ViewProjection を返す。far は無限遠。
        /// Radial で eye が warp 中心に無いときは錐台が写像後の可視域と一致しないので、全面を通す(fail-open)。
        /// </summary>
        public static Matrix4x4 BuildPlanes(in ViewSnapshot snapshot,float near,Plane[] planes){
            Frustum frustum=snapshot.Frustum;
            Quaternion rotation=Quaternion.LookRotation(frustum.Forward,frustum.Up);
            Matrix4x4 worldToView=Matrix4x4.Scale(new Vector3(1.0f,1.0f,-1.0f))*Matrix4x4.Rotate(Quaternion.Inverse(rotation));
            Matrix4x4 projection=Matrix4x4.Perspective(frustum.VerticalFieldOfViewDegrees,frustum.Aspect,near,near*1e6f);
            Matrix4x4 viewProjection=projection*worldToView;
            if(snapshot.CanUseConeCulling){
                GeometryUtility.CalculateFrustumPlanes(viewProjection,planes);
                planes[5]=new Plane(-frustum.Forward,UnboundedDistance);
            }else{
                for(int i=0;i<planes.Length;i++){
                    planes[i]=new Plane(Vector3.up,UnboundedDistance);
                }
            }
            return viewProjection;
        }

        /// <summary>Logical の 3×3 を float の行列(平行移動なし)にする。</summary>
        public static bool TryToMatrix(in LogicalMatrix3x3 value,out Matrix4x4 result){
            result=Matrix4x4.identity;
            if(!value.M00.TryToFloat(out float m00)||!value.M01.TryToFloat(out float m01)||!value.M02.TryToFloat(out float m02)||
                !value.M10.TryToFloat(out float m10)||!value.M11.TryToFloat(out float m11)||!value.M12.TryToFloat(out float m12)||
                !value.M20.TryToFloat(out float m20)||!value.M21.TryToFloat(out float m21)||!value.M22.TryToFloat(out float m22)){
                return false;
            }
            result.m00=m00;result.m01=m01;result.m02=m02;
            result.m10=m10;result.m11=m11;result.m12=m12;
            result.m20=m20;result.m21=m21;result.m22=m22;
            return true;
        }
    }
}
