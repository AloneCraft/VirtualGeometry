using UnityEngine;

namespace VirtualGeometry.World{
    /// <summary>
    /// Cell の格子の、Cell の原点からの相対位置を求める CPU の部品。GPU の PlanetCell.hlsl と同じ式(片方を変えたらもう片方も変える)。
    /// </summary>
    public static class PlanetCellGeometry{
        /// <summary>tan(a0+δ)−tan(a0) を、tan(a0)=x0 と δ から差だけで求める。小さな δ は級数。</summary>
        public static float TangentOffset(float x0,float delta){
            float t;
            if(Mathf.Abs(delta)<0.2f){
                float d2=delta*delta;
                t=delta*(1f+d2*(1f/3f+d2*(2f/15f+d2*(17f/315f+d2*(62f/2835f)))));
            }else{
                t=Mathf.Tan(delta);
            }
            return t*(1f+x0*x0)/(1f-x0*t);
        }

        /// <summary>p = p0 + d の方向の半径 radius の点の、p0 の方向の点からの相対位置 R·(p/|p| − p0/|p0|)(大きな値どうしを引かない)。</summary>
        public static Vector3 SphereOffset(Vector3 p0,Vector3 d,float radius,out Vector3 direction){
            Vector3 p=p0+d;
            float l0=p0.magnitude;float l=p.magnitude;
            direction=p/l;
            return (d/l-p0*((2f*Vector3.Dot(p0,d)+Vector3.Dot(d,d))/(l*l0*(l0+l))))*radius;
        }

        /// <summary>Cell の格子の (a, b) の、半径 radius の球面上の点(Cell の原点からの相対位置)と単位方向(GPU の SpherePoint)。</summary>
        public static Vector3 SpherePoint(in PlanetCellFrame frame,int cellQuads,float a,float b,float radius,out Vector3 direction){
            PlanetCellKey key=frame.Key;
            float quadAngle=Mathf.PI*0.5f*System.MathF.Pow(2f,-key.Level)/cellQuads;
            float half=cellQuads*0.5f;
            Vector2 tangent=frame.ApproximateTangent;
            float dx=TangentOffset(tangent.x,(a-half)*quadAngle);
            float dy=TangentOffset(tangent.y,(b-half)*quadAngle);
            Vector3 n=CubeSphere.Normal(key.Face);Vector3 u=CubeSphere.U(key.Face);Vector3 v=CubeSphere.V(key.Face);
            Vector3 p0=n+tangent.x*u+tangent.y*v;
            return SphereOffset(p0,dx*u+dy*v,radius,out direction);
        }

        /// <summary>単位方向 direction を含む、階層 level の Cell(面の座標の float で探すので、level は 20 程度まで)。</summary>
        public static PlanetCellKey KeyAt(Vector3 direction,int level){
            int face=0;float best=-1;
            for(int candidate=0;candidate<CubeSphere.FaceCount;candidate++){
                float dot=Vector3.Dot(direction,(Vector3)CubeSphere.Normal(candidate));
                if(dot>best){best=dot;face=candidate;}
            }
            float x=Vector3.Dot(direction,(Vector3)CubeSphere.U(face))/best;
            float y=Vector3.Dot(direction,(Vector3)CubeSphere.V(face))/best;
            // 等角写像の逆: s = (atan(x)/(π/4)+1)/2。
            float s=(Mathf.Atan(x)/(Mathf.PI*0.25f)+1f)*0.5f;
            float t=(Mathf.Atan(y)/(Mathf.PI*0.25f)+1f)*0.5f;
            ulong cells=1UL<<level;
            ulong cx=(ulong)Mathf.Clamp(Mathf.FloorToInt(s*cells),0,(int)cells-1);
            ulong cy=(ulong)Mathf.Clamp(Mathf.FloorToInt(t*cells),0,(int)cells-1);
            return new PlanetCellKey(face,level,new PlanetCellCoordinate(cx,0),new PlanetCellCoordinate(cy,0));
        }
    }
}
