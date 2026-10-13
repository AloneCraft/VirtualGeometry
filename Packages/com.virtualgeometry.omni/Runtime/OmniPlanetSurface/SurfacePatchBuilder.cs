using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Omni.PlanetSurface{
    /// <summary>
    /// 1頂点または1接続ずつ進められるパッチ生成器です。月面LODの2ms枠からStepを呼び、
    /// 完成前は親メッシュを残します。原点を引いた後だけfloatへ変換します。
    /// </summary>
    public sealed class SurfacePatchBuilder{
        /// <summary>GPU微細noiseが厳密に繰り返す天体ローカル座標の周期（m）です。</summary>
        public const float MicroPeriodMeters=8192f;
        private static Vector4[] disabledMorphAttributes=Array.Empty<Vector4>();
        private readonly IPlanetSurfaceHeightSource source;
        private readonly double radius;
        private readonly double minU,minV,span;
        private readonly float skirtDepth;
        private readonly int surfaceCount;
        private readonly bool positiveWinding;
        private readonly SurfaceVector microAnchor;
        private readonly Vector3[] naturalEdgeVertices,naturalEdgeSkirts,naturalEdgeNormals;
        private readonly Vector4[] naturalEdgeTangents;
        private int cursor;
        private int indexCursor;
        private int phase;
        public SurfaceTileKey Key{get;}
        public int Resolution{get;}
        public double SpacingMeters{get;}
        public SurfaceVector Origin{get;}
        public Vector3[] Vertices{get;}
        public Vector3[] Normals{get;}
        public Vector4[] Tangents{get;}
        public Vector2[] AlbedoUv{get;}
        /// <summary>巨大な絶対座標を避けた、周期noise用の天体ローカル座標です。</summary>
        public Vector3[] MicroPosition{get;}
        public int[] Triangles{get;}
        public bool IsComplete=>this.phase==4;
        public int CompletedSurfaceVertices{get{
            if(this.phase==0){return this.cursor;}
            return this.surfaceCount;
        }}
        public long CpuBytes=>(long)this.Vertices.Length*(12+12+16+8+12)+this.Triangles.Length*4L+
            this.naturalEdgeVertices.Length*52L;

        /// <summary>半径メートル、偶数の辺分割数、正本の標高源を渡します。</summary>
        public SurfacePatchBuilder(SurfaceTileKey key,double radiusMeters,int resolution,
            IPlanetSurfaceHeightSource source,double skirtDepthScale=2){
            if(resolution<2||resolution>128||resolution%2!=0||radiusMeters<=0||!double.IsFinite(radiusMeters)){
                throw new ArgumentOutOfRangeException(nameof(resolution));
            }
            this.source=source??throw new ArgumentNullException(nameof(source));
            if(key.Face<0||key.Face>5||key.Level<0||key.Level>20||key.X<0||key.Y<0||
                key.X>=(1<<key.Level)||key.Y>=(1<<key.Level)){
                throw new ArgumentOutOfRangeException(nameof(key));
            }
            this.Key=key;this.radius=radiusMeters;this.Resolution=resolution;
            this.span=2.0/(1<<key.Level);
            this.minU=-1+key.X*this.span;this.minV=-1+key.Y*this.span;
            this.SpacingMeters=radiusMeters*this.span/resolution;
            this.skirtDepth=(float)Math.Max(0.05,this.SpacingMeters*skirtDepthScale);
            this.surfaceCount=(resolution+1)*(resolution+1);
            int count=this.surfaceCount+4*(resolution+1);
            this.Vertices=new Vector3[count];this.Normals=new Vector3[count];
            this.Tangents=new Vector4[count];this.AlbedoUv=new Vector2[count];this.MicroPosition=new Vector3[count];
            int edgeCount=4*(resolution+1);
            this.naturalEdgeVertices=new Vector3[edgeCount];this.naturalEdgeSkirts=new Vector3[edgeCount];
            this.naturalEdgeNormals=new Vector3[edgeCount];this.naturalEdgeTangents=new Vector4[edgeCount];
            this.Triangles=new int[resolution*resolution*6+resolution*48];
            SurfaceVector direction=SurfaceGeometry.CubeDirection(key.Face,this.minU+this.span*0.5,this.minV+this.span*0.5);
            this.Origin=direction*(radiusMeters+this.Height(direction));
            this.microAnchor=new SurfaceVector(
                Math.Floor(this.Origin.X/MicroPeriodMeters)*MicroPeriodMeters,
                Math.Floor(this.Origin.Y/MicroPeriodMeters)*MicroPeriodMeters,
                Math.Floor(this.Origin.Z/MicroPeriodMeters)*MicroPeriodMeters);
            SurfaceVector du=SurfaceGeometry.CubeDirection(key.Face,this.minU+this.span,this.minV)-
                SurfaceGeometry.CubeDirection(key.Face,this.minU,this.minV);
            SurfaceVector dv=SurfaceGeometry.CubeDirection(key.Face,this.minU,this.minV+this.span)-
                SurfaceGeometry.CubeDirection(key.Face,this.minU,this.minV);
            SurfaceVector cross=SurfaceVector.Cross(dv,du);
            this.positiveWinding=cross.X*direction.X+cross.Y*direction.Y+cross.Z*direction.Z>0;
        }

        /// <summary>有限量の作業だけを行います。falseは完成済みを示します。</summary>
        public bool Step(){
            if(this.phase==0){
                int row=this.cursor/(this.Resolution+1),column=this.cursor%(this.Resolution+1);
                this.Vertex(column,row,this.cursor);
                this.cursor++;
                if(this.cursor==this.surfaceCount){this.phase=1;this.cursor=0;}
                return true;
            }
            if(this.phase==1){
                int edge=this.cursor/(this.Resolution+1),step=this.cursor%(this.Resolution+1);
                int sourceIndex=EdgeVertex(edge,step,this.Resolution);
                int destination=this.surfaceCount+this.cursor;
                SurfaceVector position=this.Origin+Double(this.Vertices[sourceIndex]);
                this.Vertices[destination]=this.Vertices[sourceIndex]-Float(position.Normalized)*this.skirtDepth;
                this.Normals[destination]=this.Normals[sourceIndex];this.Tangents[destination]=this.Tangents[sourceIndex];
                this.AlbedoUv[destination]=this.AlbedoUv[sourceIndex];this.MicroPosition[destination]=this.MicroPosition[sourceIndex];
                this.naturalEdgeVertices[this.cursor]=this.Vertices[sourceIndex];
                this.naturalEdgeSkirts[this.cursor]=this.Vertices[destination];
                this.naturalEdgeNormals[this.cursor]=this.Normals[sourceIndex];
                this.naturalEdgeTangents[this.cursor]=this.Tangents[sourceIndex];
                this.cursor++;
                if(this.cursor==4*(this.Resolution+1)){this.phase=2;this.cursor=0;}
                return true;
            }
            if(this.phase==2){
                int row=this.cursor/this.Resolution,column=this.cursor%this.Resolution;
                int a=row*(this.Resolution+1)+column,b=a+1,c=a+this.Resolution+1,d=c+1;
                if(this.positiveWinding){this.Quad(a,c,b,d);}else{this.Quad(a,b,c,d);}
                this.cursor++;
                if(this.cursor==this.Resolution*this.Resolution){this.phase=3;this.cursor=0;}
                return true;
            }
            if(this.phase==3){
                int edge=this.cursor/this.Resolution,step=this.cursor%this.Resolution;
                int a=EdgeVertex(edge,step,this.Resolution),b=EdgeVertex(edge,step+1,this.Resolution);
                int c=this.surfaceCount+edge*(this.Resolution+1)+step,d=c+1;
                this.Quad(a,b,c,d);this.Quad(a,c,b,d);
                this.cursor++;
                if(this.cursor==4*this.Resolution){this.phase=4;}
                return true;
            }
            return false;
        }

        /// <summary>隣接LODが変わったとき、前回の接続補正を除去して自然地形へ戻します。</summary>
        public void RestoreEdges(){
            if(!this.IsComplete){throw new InvalidOperationException("生成途中の境界は復元できません。");}
            for(int edge=0;edge<4;edge++){
                for(int step=0;step<=this.Resolution;step++){
                    int slot=edge*(this.Resolution+1)+step;
                    int index=EdgeVertex(edge,step,this.Resolution),skirt=this.surfaceCount+slot;
                    this.Vertices[index]=this.naturalEdgeVertices[slot];this.Vertices[skirt]=this.naturalEdgeSkirts[slot];
                    this.Normals[index]=this.naturalEdgeNormals[slot];this.Normals[skirt]=this.naturalEdgeNormals[slot];
                    this.Tangents[index]=this.naturalEdgeTangents[slot];this.Tangents[skirt]=this.naturalEdgeTangents[slot];
                }
            }
        }

        /// <summary>完成したCPUデータをMeshへ送ります。生成途中の公開は拒否します。</summary>
        public Mesh CreateMesh(){
            if(!this.IsComplete){throw new InvalidOperationException("パッチの生成が完了していません。");}
            var mesh=new Mesh{name=$"PlanetSurface_{this.Key}",hideFlags=HideFlags.DontSave,indexFormat=IndexFormat.UInt16};
            mesh.MarkDynamic();
            mesh.vertices=this.Vertices;mesh.normals=this.Normals;mesh.tangents=this.Tangents;mesh.uv=this.AlbedoUv;
            mesh.SetUVs(1,this.MicroPosition);
            SetDetailUnbound(mesh,this.Vertices.Length);
            SetMorphDisabled(mesh,this.Vertices.Length);
            mesh.SetTriangles(this.Triangles,0,true);mesh.RecalculateBounds();
            // 全変位とskirtが既に頂点に入っているため、全球最大標高による巨大なBounds拡張は不要です。
            return mesh;
        }

        /// <summary>UV3/UV4を削除せずゼロ化し、LOD transition前後でGPU頂点layoutを固定します。</summary>
        public static void SetMorphDisabled(Mesh mesh,int vertexCount){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            if(vertexCount<0||vertexCount>mesh.vertexCount){throw new ArgumentOutOfRangeException(nameof(vertexCount));}
            if(disabledMorphAttributes.Length<vertexCount){disabledMorphAttributes=new Vector4[vertexCount];}
            mesh.SetUVs(3,disabledMorphAttributes,0,vertexCount);
            mesh.SetUVs(4,disabledMorphAttributes,0,vertexCount);
        }

        /// <summary>
        /// UV2を削除せず未接続値で保持し、BRG登録後のatlas公開・解除で頂点layoutを変えません。
        /// z=0はshader側の未接続契約です。
        /// </summary>
        public static void SetDetailUnbound(Mesh mesh,int vertexCount){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            if(vertexCount<0||vertexCount>mesh.vertexCount){throw new ArgumentOutOfRangeException(nameof(vertexCount));}
            if(disabledMorphAttributes.Length<vertexCount){disabledMorphAttributes=new Vector4[vertexCount];}
            mesh.SetUVs(2,disabledMorphAttributes,0,vertexCount);
        }

        /// <summary>境界の接続処理と同じ頂点順を共有します。</summary>
        public static int EdgeVertex(int edge,int step,int resolution)=>edge switch{
            0=>step,1=>step*(resolution+1)+resolution,
            2=>resolution*(resolution+1)+resolution-step,_=>(resolution-step)*(resolution+1)
        };

        private void Vertex(int column,int row,int index){
            double u=this.minU+this.span*column/this.Resolution;
            double v=this.minV+this.span*row/this.Resolution;
            SurfaceVector direction=SurfaceGeometry.CubeDirection(this.Key.Face,u,v);
            double height=this.Height(direction);
            SurfaceVector absolutePosition=direction*(this.radius+height);
            this.Vertices[index]=Float(absolutePosition-this.Origin);
            this.MicroPosition[index]=Float(absolutePosition-this.microAnchor);
            SurfaceVector reference=new(0,0,1);
            if(Math.Abs(direction.Z)>0.9){reference=new SurfaceVector(0,1,0);}
            SurfaceVector t=SurfaceVector.Cross(reference,direction).Normalized;
            SurfaceVector b=SurfaceVector.Cross(direction,t);
            double delta=Math.Max(0.01,this.SpacingMeters*0.5),angle=delta/this.radius;
            double shadingHeight=height;
            if(this.source is ISurfaceShadingHeightSource){shadingHeight=this.ShadingHeight(direction);}
            double dx=(this.ShadingHeight((direction+t*angle).Normalized)-this.ShadingHeight((direction-t*angle).Normalized))/(2*delta);
            double dy=(this.ShadingHeight((direction+b*angle).Normalized)-this.ShadingHeight((direction-b*angle).Normalized))/(2*delta);
            SurfaceVector normal=(direction-(t*dx+b*dy)*(this.radius/(this.radius+shadingHeight))).Normalized;
            this.Normals[index]=Float(normal);
            Vector3 cubeTangent=Float(SurfaceGeometry.CubeDirection(this.Key.Face,u+1e-6,v)-
                SurfaceGeometry.CubeDirection(this.Key.Face,u-1e-6,v));
            cubeTangent=(cubeTangent-this.Normals[index]*Vector3.Dot(cubeTangent,this.Normals[index])).normalized;
            Vector3 cubeVertical=Float(SurfaceGeometry.CubeDirection(this.Key.Face,u,v+1e-6)-
                SurfaceGeometry.CubeDirection(this.Key.Face,u,v-1e-6));
            float handedness=1;
            if(Vector3.Dot(Vector3.Cross(this.Normals[index],cubeTangent),cubeVertical)<0){handedness=-1;}
            this.Tangents[index]=new Vector4(cubeTangent.x,cubeTangent.y,cubeTangent.z,handedness);
            double textureU=Math.Atan2(direction.X,direction.Y)/(2*Math.PI)+0.5;
            double centerU=Math.Atan2(this.Origin.X,this.Origin.Y)/(2*Math.PI)+0.5;
            if(textureU-centerU>0.5){textureU-=1;}
            else if(textureU-centerU<-0.5){textureU+=1;}
            this.AlbedoUv[index]=new Vector2((float)textureU,(float)(Math.Asin(Math.Clamp(direction.Z,-1,1))/Math.PI+0.5));
        }

        private double Height(SurfaceVector direction){
            if(this.source is IBandLimitedSurfaceHeightSource filtered){return filtered.SampleHeightMeters(direction,this.SpacingMeters);}
            return this.source.SampleHeightMeters(direction.X,direction.Y,direction.Z);
        }
        /// <summary>頂点法線に使う高さです。ISurfaceShadingHeightSourceを実装しない源では形状と同じ高さです。</summary>
        private double ShadingHeight(SurfaceVector direction){
            if(this.source is ISurfaceShadingHeightSource shading){return shading.SampleShadingHeightMeters(direction,this.SpacingMeters);}
            return this.Height(direction);
        }
        private void Quad(int a,int b,int c,int d){
            this.Triangles[this.indexCursor++]=a;this.Triangles[this.indexCursor++]=b;this.Triangles[this.indexCursor++]=c;
            this.Triangles[this.indexCursor++]=c;this.Triangles[this.indexCursor++]=b;this.Triangles[this.indexCursor++]=d;
        }
        private static Vector3 Float(SurfaceVector v)=>new((float)v.X,(float)v.Y,(float)v.Z);
        private static SurfaceVector Double(Vector3 v)=>new(v.x,v.y,v.z);
    }
}
