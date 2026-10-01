using UnityEngine;
namespace VirtualizedGeometry{
    /// <summary>max-axis sphere が保守的になる直交TRSだけを専用経路へ渡す。</summary>
    public static class VirtualGeometryTransformSupport{
        public static string GetUnsupportedReason(Matrix4x4 matrix){
            for(int i=0;i<16;i++){if(!float.IsFinite(matrix[i])){return "非有限なtransformは専用描画で扱えません。";}}
            Vector3 x=matrix.GetColumn(0),y=matrix.GetColumn(1),z=matrix.GetColumn(2);
            if(x.sqrMagnitude<1e-12f||y.sqrMagnitude<1e-12f||z.sqrMagnitude<1e-12f){return "ゼロscaleのtransformは通常Proxyを使用します。";}
            x.Normalize();y.Normalize();z.Normalize();
            if(Mathf.Abs(Vector3.Dot(x,y))>1e-4f||Mathf.Abs(Vector3.Dot(x,z))>1e-4f||Mathf.Abs(Vector3.Dot(y,z))>1e-4f){return "親の非一様scaleによるshearは通常Proxyを使用します。";}
            return null;
        }
    }
}