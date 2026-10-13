using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>異なるLODの境界を、既に描画している粗いメッシュ上へ接続します。</summary>
    public static class SurfacePatchSampler{
        /// <summary>生成済みの細パッチの指定辺を粗パッチへ接続します。辺番号は生成器と共通です。</summary>
        public static void StitchEdge(SurfacePatchBuilder fine,SurfacePatchBuilder coarse,int edge){
            if(fine==null||coarse==null||!fine.IsComplete||!coarse.IsComplete||edge<0||edge>3||
                fine.Key.Level!=coarse.Key.Level+1){throw new ArgumentException("接続する生成済みLOD辺が不正です。");}
            int resolution=fine.Resolution;
            double span=2.0/(1<<fine.Key.Level);
            for(int step=0;step<=resolution;step++){
                int index=SurfacePatchBuilder.EdgeVertex(edge,step,resolution);
                int column=index%(resolution+1),row=index/(resolution+1);
                SurfaceVector direction=SurfaceGeometry.CubeDirection(fine.Key.Face,
                    -1+span*(fine.Key.X+column/(double)resolution),
                    -1+span*(fine.Key.Y+row/(double)resolution));
                SampleSurface(coarse,direction,out SurfaceVector position,out Vector3 normal);
                SurfaceVector relative=position-fine.Origin;
                Vector3 replacement=new((float)relative.X,(float)relative.Y,(float)relative.Z);
                Vector3 delta=replacement-fine.Vertices[index];
                fine.Vertices[index]=replacement;fine.Normals[index]=normal;
                Vector4 oldTangent=fine.Tangents[index];
                Vector3 tangent=new(oldTangent.x,oldTangent.y,oldTangent.z);
                tangent=(tangent-normal*Vector3.Dot(tangent,normal)).normalized;
                fine.Tangents[index]=new Vector4(tangent.x,tangent.y,tangent.z,oldTangent.w);
                int skirt=(resolution+1)*(resolution+1)+edge*(resolution+1)+step;
                fine.Vertices[skirt]+=delta;fine.Normals[skirt]=normal;fine.Tangents[skirt]=fine.Tangents[index];
            }
        }

        /// <summary>球面方向を実メッシュの三角形へ対応させます。親子形状補間にも共用します。</summary>
        public static void SampleSurface(SurfacePatchBuilder patch,SurfaceVector direction,
            out SurfaceVector position,out Vector3 normal){
            if(patch==null||!patch.IsComplete){throw new ArgumentException("完成したパッチが必要です。");}
            double a,b,denominator;
            switch(patch.Key.Face){
                case 0:case 1:denominator=Math.Abs(direction.Y);a=direction.X/denominator;b=direction.Z/denominator;break;
                case 2:case 3:denominator=Math.Abs(direction.X);a=direction.Y/denominator;b=direction.Z/denominator;break;
                default:denominator=Math.Abs(direction.Z);a=direction.X/denominator;b=direction.Y/denominator;break;
            }
            int resolution=patch.Resolution,scale=1<<patch.Key.Level;
            double x=((a+1)*0.5*scale-patch.Key.X)*resolution;
            double y=((b+1)*0.5*scale-patch.Key.Y)*resolution;
            if(!double.IsFinite(x)||!double.IsFinite(y)||x< -1e-5||y< -1e-5||x>resolution+1e-5||y>resolution+1e-5){
                throw new ArgumentOutOfRangeException(nameof(direction),"方向が指定パッチの外側です。");
            }
            x=Math.Clamp(x,0,resolution);y=Math.Clamp(y,0,resolution);
            int column=Math.Min(resolution-1,(int)x),row=Math.Min(resolution-1,(int)y);
            float u=(float)(x-column),v=(float)(y-row);
            int p=row*(resolution+1)+column,q=p+1,r=p+resolution+1,s=r+1;
            Vector3 point;
            if(u+v<=1){
                point=patch.Vertices[p]*(1-u-v)+patch.Vertices[q]*u+patch.Vertices[r]*v;
                normal=(patch.Normals[p]*(1-u-v)+patch.Normals[q]*u+patch.Normals[r]*v).normalized;
            }else{
                point=patch.Vertices[q]*(1-v)+patch.Vertices[r]*(1-u)+patch.Vertices[s]*(u+v-1);
                normal=(patch.Normals[q]*(1-v)+patch.Normals[r]*(1-u)+patch.Normals[s]*(u+v-1)).normalized;
            }
            position=patch.Origin+new SurfaceVector(point.x,point.y,point.z);
        }
    }
}
