using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 「単純に拡大した面」の mesh。単位の XZ 平面(物体空間で y=0 ちょうど、x、z は ±0.5)を作り、物体の倍率 S で拡大して使う。
    /// 試験点のまわりは細かく、遠くは粗く分ける。Radial の写像は頂点ごとなので、巨大な三角形は写像後に曲がらない(推測)。実際に、細分しない 1 枚の四角形の壁は、近い側が描かれなかった(Requirements/PlanetWorld.md §20)。
    /// 頂点の座標は「面の原点からの距離 / S」を float にしたもの。座標が 0.5 に近い所では float の刻みが S·2^-25 になるので、試験点の付近の頂点は、その刻みの格子の上にしか置けない。
    /// </summary>
    public static class ScaledPlaneMesh{
        /// <summary>
        /// 座標(m)の列。focus の ±fineHalf は fineStep 刻み、その外は growth 倍ずつ広げ、端は ±half ちょうど。昇順で、重なりなし。
        /// fineStep ≤ 0、growth ≤ 1、half ≤ 0、fineHalf &lt; 0 は ArgumentOutOfRangeException。
        /// </summary>
        public static double[] Axis(double half,double focus,double fineHalf,double fineStep,double growth){
            if(!(half>0)){throw new ArgumentOutOfRangeException(nameof(half));}
            if(!(fineStep>0)){throw new ArgumentOutOfRangeException(nameof(fineStep));}
            if(!(growth>1)){throw new ArgumentOutOfRangeException(nameof(growth));}
            if(!(fineHalf>=0)){throw new ArgumentOutOfRangeException(nameof(fineHalf));}
            var positions=new List<double>();
            positions.Add(-half);positions.Add(half);
            // 細かい区間は整数の番号で進める(double の加算の累積で、端の点が落ちない)。
            int fineCount=(int)Math.Round(fineHalf/fineStep);
            for(int k=-fineCount;k<=fineCount;k++){
                double p=focus+k*fineStep;
                if(p>-half&&p<half){positions.Add(p);}
            }
            double low=focus-fineCount*fineStep;double step=fineStep;
            while(low>-half){
                step*=growth;low-=step;
                if(low>-half){positions.Add(low);}
            }
            double high=focus+fineCount*fineStep;step=fineStep;
            while(high<half){
                step*=growth;high+=step;
                if(high<half){positions.Add(high);}
            }
            positions.Sort();
            var result=new List<double>();
            for(int i=0;i<positions.Count;i++){
                if(result.Count==0||positions[i]>result[result.Count-1]){result.Add(positions[i]);}
            }
            return result.ToArray();
        }

        /// <summary>座標の列を物体空間の float(座標/scale)にし、重なった(量子化で同じ値になった)座標を除く。昇順を保つ。</summary>
        public static float[] ToObjectSpace(double[] positions,double scale){
            var result=new List<float>();
            for(int i=0;i<positions.Length;i++){
                float value=(float)(positions[i]/scale);
                if(result.Count==0||value>result[result.Count-1]){result.Add(value);}
            }
            return result.ToArray();
        }

        /// <summary>
        /// 単位の XZ 平面の mesh(法線 +Y)。focusX、focusZ は面の原点からの座標(m)で、そのまわり ±fineHalf を fineStep 刻みで分ける。
        /// </summary>
        public static Mesh Build(double scale,double focusX,double focusZ,double fineHalf,double fineStep,double growth){
            float[] xs=ToObjectSpace(Axis(scale*0.5,focusX,fineHalf,fineStep,growth),scale);
            float[] zs=ToObjectSpace(Axis(scale*0.5,focusZ,fineHalf,fineStep,growth),scale);
            int nx=xs.Length;int nz=zs.Length;
            var vertices=new Vector3[nx*nz];
            var normals=new Vector3[nx*nz];
            var uvs=new Vector2[nx*nz];
            for(int j=0;j<nz;j++){
                for(int i=0;i<nx;i++){
                    int index=j*nx+i;
                    vertices[index]=new Vector3(xs[i],0f,zs[j]);
                    normals[index]=Vector3.up;
                    uvs[index]=new Vector2(xs[i]+0.5f,zs[j]+0.5f);
                }
            }
            var indices=new int[(nx-1)*(nz-1)*6];
            int write=0;
            for(int j=0;j<nz-1;j++){
                for(int i=0;i<nx-1;i++){
                    int a=j*nx+i;int b=(j+1)*nx+i;int c=j*nx+i+1;int d=(j+1)*nx+i+1;
                    // Unity は時計回りが表。真上から見て (x0,z0)→(x0,z1)→(x1,z0) は時計回り。
                    indices[write++]=a;indices[write++]=b;indices[write++]=c;
                    indices[write++]=b;indices[write++]=d;indices[write++]=c;
                }
            }
            var mesh=new Mesh{name="ScaledPlane",indexFormat=IndexFormat.UInt32,hideFlags=HideFlags.DontSave};
            mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uvs;
            mesh.SetIndices(indices,MeshTopology.Triangles,0);
            mesh.bounds=new Bounds(Vector3.zero,new Vector3(1f,0.001f,1f));
            return mesh;
        }

        /// <summary>
        /// 継ぎ目の壁の mesh。x=0 の面(法線 +X)で、y∈[0, height]、z∈±half。頂点は世界の座標(m)のまま(物体の倍率は 1)。
        /// z 方向は <see cref="Axis"/> で細かく分ける(巨大な 1 枚の四角形にすると、近い側が描かれなかった)。
        /// </summary>
        public static Mesh BuildWall(double height,double half,double focus,double fineHalf,double fineStep,double growth){
            if(!(height>0)){throw new ArgumentOutOfRangeException(nameof(height));}
            double[] zs=Axis(half,focus,fineHalf,fineStep,growth);
            int n=zs.Length;
            var vertices=new Vector3[n*2];
            var normals=new Vector3[n*2];
            var uvs=new Vector2[n*2];
            for(int i=0;i<n;i++){
                vertices[i*2]=new Vector3(0f,0f,(float)zs[i]);
                vertices[i*2+1]=new Vector3(0f,(float)height,(float)zs[i]);
                normals[i*2]=Vector3.right;normals[i*2+1]=Vector3.right;
                uvs[i*2]=new Vector2((float)zs[i],0f);uvs[i*2+1]=new Vector2((float)zs[i],(float)height);
            }
            var indices=new int[(n-1)*6];
            int write=0;
            for(int i=0;i<n-1;i++){
                int a=i*2;int b=i*2+1;int c=(i+1)*2;int d=(i+1)*2+1;
                // 右(+X)から見ると +z が右。(y0,z0)→(y1,z0)→(y0,z1) は時計回り。
                indices[write++]=a;indices[write++]=b;indices[write++]=c;
                indices[write++]=b;indices[write++]=d;indices[write++]=c;
            }
            var mesh=new Mesh{name="StepWall",indexFormat=IndexFormat.UInt32,hideFlags=HideFlags.DontSave};
            mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uvs;
            mesh.SetIndices(indices,MeshTopology.Triangles,0);
            mesh.bounds=new Bounds(new Vector3(0f,(float)height*0.5f,0f),new Vector3(0.01f,(float)height,(float)half*2f));
            return mesh;
        }
    }
}
