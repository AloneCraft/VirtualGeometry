using UnityEngine;

namespace VirtualGeometry.World{
    /// <summary>
    /// cube-sphere の面の軸と、面の座標から球面の方向への写像(等角写像)。
    /// 面 f の点は n + a·u + b·v(a、b は −1〜1)。外向き法線は cross(v,u)=n になる向きに u・v を選ぶ
    /// (VirtualGeometryGridLayout の三角形が外から時計回りに見える向き)。
    /// 面の番号: 0:+X、1:−X、2:+Y、3:−Y、4:+Z、5:−Z。GPU の生成(PlanetTerrainGenerate.compute)も同じ表を持つ。
    /// </summary>
    public static class CubeSphere{
        public const int FaceCount=6;

        private static readonly Vector3Int[] normals={
            new Vector3Int(1,0,0),new Vector3Int(-1,0,0),new Vector3Int(0,1,0),
            new Vector3Int(0,-1,0),new Vector3Int(0,0,1),new Vector3Int(0,0,-1),
        };
        private static readonly Vector3Int[] us={
            new Vector3Int(0,0,1),new Vector3Int(0,0,-1),new Vector3Int(1,0,0),
            new Vector3Int(1,0,0),new Vector3Int(-1,0,0),new Vector3Int(1,0,0),
        };
        private static readonly Vector3Int[] vs={
            new Vector3Int(0,1,0),new Vector3Int(0,1,0),new Vector3Int(0,0,1),
            new Vector3Int(0,0,-1),new Vector3Int(0,1,0),new Vector3Int(0,1,0),
        };

        public static Vector3Int Normal(int face){return normals[face];}
        public static Vector3Int U(int face){return us[face];}
        public static Vector3Int V(int face){return vs[face];}

        /// <summary>法線が axis の面。</summary>
        public static int FaceWithNormal(Vector3Int axis){
            for(int face=0;face<FaceCount;face++){
                if(normals[face]==axis){return face;}
            }
            throw new System.ArgumentException("Not a face axis: "+axis);
        }

        /// <summary>面の座標(s、t は 0〜1)を球面の単位方向へ写す。等角写像(各軸の角度が均等)。</summary>
        public static Vector3 Direction(int face,float s,float t){
            float x=Mathf.Tan((2f*s-1f)*(Mathf.PI*0.25f));
            float y=Mathf.Tan((2f*t-1f)*(Mathf.PI*0.25f));
            Vector3 n=normals[face];Vector3 u=us[face];Vector3 v=vs[face];
            return (n+x*u+y*v).normalized;
        }
    }
}
