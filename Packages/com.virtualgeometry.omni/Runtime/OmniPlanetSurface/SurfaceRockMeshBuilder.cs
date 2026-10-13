using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Omni.PlanetSurface{
    /// <summary>近傍の一意な岩候補から、個数上限まで近いものだけ保持します。</summary>
    public sealed class SurfaceRockSelection{
        private struct Entry{
            internal SurfaceRockCandidate Rock;
            internal double Distance;
        }
        private sealed class Order:IComparer<Entry>{
            internal static readonly Order Instance=new();
            public int Compare(Entry a,Entry b){
                int result=a.Distance.CompareTo(b.Distance);
                if(result!=0){return result;}
                result=a.Rock.Key.Face.CompareTo(b.Rock.Key.Face);if(result!=0){return result;}
                result=a.Rock.Key.Level.CompareTo(b.Rock.Key.Level);if(result!=0){return result;}
                result=a.Rock.Key.X.CompareTo(b.Rock.Key.X);if(result!=0){return result;}
                return a.Rock.Key.Y.CompareTo(b.Rock.Key.Y);
            }
        }
        private readonly Entry[] heap,sorted;
        private SurfaceVector direction;
        private readonly double radius;
        public int Count{get;private set;}
        public SurfaceRockSelection(SurfaceVector direction,double radius,int capacity){
            if(!double.IsFinite(direction.Length)||direction.Length<=0||!double.IsFinite(radius)||radius<=0||capacity<1||capacity>1024){
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            this.radius=radius;this.heap=new Entry[capacity];this.sorted=new Entry[capacity];this.Reset(direction);
        }
        /// <summary>固定heapを再利用し、次の視点位置に対する選択を割り当てなしで開始します。</summary>
        public void Reset(SurfaceVector direction){
            if(!double.IsFinite(direction.Length)||direction.Length<=0){throw new ArgumentOutOfRangeException(nameof(direction));}
            this.direction=direction.Normalized;this.Count=0;
        }
        /// <summary>同じQueryから得た一意な候補を渡します。全件を保持せず最大heapだけ更新します。</summary>
        public void Consider(SurfaceRockCandidate rock){
            var entry=new Entry{Rock=rock,Distance=((rock.Direction-this.direction)*this.radius).Length};
            if(this.Count==this.heap.Length){
                if(Order.Instance.Compare(entry,this.heap[0])>=0){return;}
                this.heap[0]=entry;
                int index=0;
                while(index*2+1<this.Count){
                    int child=index*2+1;
                    if(child+1<this.Count&&Order.Instance.Compare(this.heap[child+1],this.heap[child])>0){child++;}
                    if(Order.Instance.Compare(this.heap[index],this.heap[child])>=0){break;}
                    (this.heap[index],this.heap[child])=(this.heap[child],this.heap[index]);index=child;
                }
            }else{
                int index=this.Count++;this.heap[index]=entry;
                while(index>0){
                    int parent=(index-1)/2;
                    if(Order.Instance.Compare(this.heap[parent],this.heap[index])>=0){break;}
                    (this.heap[index],this.heap[parent])=(this.heap[parent],this.heap[index]);index=parent;
                }
            }
        }
        /// <summary>探索終了時だけ呼び、近い順の固定スナップショットを生成します。</summary>
        public SurfaceRockCandidate[] Snapshot(){
            var result=new SurfaceRockCandidate[this.Count];
            this.CopySnapshotTo(result);
            return result;
        }
        /// <summary>近い順の結果を呼出側の固定bufferへコピーし、実行時配列割り当てを避けます。</summary>
        public int CopySnapshotTo(SurfaceRockCandidate[] destination){
            if(destination==null){throw new ArgumentNullException(nameof(destination));}
            if(destination.Length<this.Count){throw new ArgumentException("選択結果を保持できるbufferが必要です。",nameof(destination));}
            Array.Copy(this.heap,this.sorted,this.Count);Array.Sort(this.sorted,0,this.Count,Order.Instance);
            for(int i=0;i<this.Count;i++){destination[i]=this.sorted[i].Rock;}
            return this.Count;
        }
    }

    /// <summary>月面検証とEarthToMoonの近傍岩を1個ずつ生成する、倍精度原点付き結合メッシュです。</summary>
    public sealed class SurfaceRockMeshBuilder{
        [StructLayout(LayoutKind.Sequential)]
        private struct UploadVertex{
            internal Vector3 Position,Normal;
            internal Vector4 Tangent;
            internal Vector2 Uv;
            internal Vector4 Detail,MorphPosition,MorphNormal;
        }
        private static readonly VertexAttributeDescriptor[] UploadLayout={
            new(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
            new(VertexAttribute.Normal,VertexAttributeFormat.Float32,3),
            new(VertexAttribute.Tangent,VertexAttributeFormat.Float32,4),
            new(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2),
            new(VertexAttribute.TexCoord2,VertexAttributeFormat.Float32,4),
            new(VertexAttribute.TexCoord3,VertexAttributeFormat.Float32,4),
            new(VertexAttribute.TexCoord4,VertexAttributeFormat.Float32,4)
        };
        private readonly UploadVertex[] uploadVertices=new UploadVertex[1024];
        private Mesh uploadTarget;
        // この生成器だけが更新する2つの背面Meshを保持します。外部からの内容変更は禁止です。
        private readonly Mesh[] preparedTargets=new Mesh[2];
        private int nextPreparedTarget,rockCount;
        private int uploadStage,uploadedVertices,uploadedIndices;
        private Bounds uploadBounds;
        public string LastUploadStage{get;private set;}="upload-idle";
        private static readonly int[] Faces={0,11,5,0,5,1,0,1,7,0,7,10,0,10,11,1,5,9,5,11,4,11,10,2,10,7,6,7,1,8,
            3,9,4,3,4,2,3,2,6,3,6,8,3,8,9,4,9,5,2,4,11,6,2,10,8,6,7,9,8,1};
        private readonly SurfaceRockCandidate[] rocks;
        private readonly double radius;
        private readonly IPlanetSurfaceHeightSource source;
        private readonly Vector3[] vertices,normals;
        private readonly Vector4[] tangents,disabledAttributes;
        private readonly Vector2[] uv;
        private readonly int[] triangles;
        private readonly SurfaceVector[] shape=new SurfaceVector[12];
        public SurfaceVector Origin{get;private set;}
        public SurfaceVector[] GroundPositions{get;}
        public bool IsComplete=>this.CompletedRocks==this.rockCount;
        public int BufferPreparationCount{get;private set;}
        public int CompletedRocks{get;private set;}
        private Vector3[][] shapeTemplates;
        /// <summary>
        /// 岩の型(opt-in、既定はnullで従来の20面体)。各型は20面体と同じ並びの12頂点で、原点が岩の中心、+Yが上、水平の直径が1です。
        /// 設定すると、形状seedで型を選び、実寸へ拡大して、型の高さの30〜50%を地面へ埋めます(面の構成と頂点数は従来と同じ)。
        /// </summary>
        public Vector3[][] ShapeTemplates{
            get=>this.shapeTemplates;
            set{
                if(value!=null){
                    if(value.Length==0){throw new ArgumentException("岩の型が空です。",nameof(value));}
                    foreach(Vector3[] template in value){
                        if(template==null||template.Length!=12){throw new ArgumentException("岩の型は12頂点です。",nameof(value));}
                        foreach(Vector3 p in template){
                            if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z)){throw new ArgumentException("岩の型に非有限値があります。",nameof(value));}
                        }
                    }
                }
                this.shapeTemplates=value;
            }
        }
        public SurfaceRockMeshBuilder(SurfaceRockCandidate[] rocks,double radius,IPlanetSurfaceHeightSource source,SurfaceVector origin)
            :this(rocks?.Length??throw new ArgumentNullException(nameof(rocks)),radius,source){
            this.Reset(rocks,origin);
        }
        /// <summary>岩個数の固定上限だけCPU作業領域を確保します。初回確保も呼出側の更新時計へ含めます。</summary>
        public SurfaceRockMeshBuilder(int capacity,double radius,IPlanetSurfaceHeightSource source){
            if(capacity<0||capacity>1024||!double.IsFinite(radius)||radius<=0){
                throw new ArgumentOutOfRangeException(nameof(radius));
            }
            this.source=source??throw new ArgumentNullException(nameof(source));this.radius=radius;
            this.rocks=new SurfaceRockCandidate[capacity];this.GroundPositions=new SurfaceVector[capacity];
            int count=capacity*60;
            this.vertices=new Vector3[count];this.normals=new Vector3[count];this.tangents=new Vector4[count];
            this.uv=new Vector2[count];this.disabledAttributes=new Vector4[count];this.triangles=new int[count];
        }

        /// <summary>保持済みのCPU/GPU作業領域を再利用して、新しい候補へ切り替えます。</summary>
        public void Reset(SurfaceRockCandidate[] rocks,SurfaceVector origin){
            if(rocks==null){throw new ArgumentNullException(nameof(rocks));}
            if(rocks.Length>this.rocks.Length||!double.IsFinite(origin.Length)){throw new ArgumentOutOfRangeException(nameof(rocks));}
            Array.Copy(rocks,this.rocks,rocks.Length);
            this.BeginReset(rocks.Length,origin);
        }

        /// <summary>選択器の固定bufferから直接コピーし、Snapshot配列を生成せず次の岩集合へ切り替えます。</summary>
        public void ResetFromSelection(SurfaceRockSelection selection,SurfaceVector origin){
            if(selection==null){throw new ArgumentNullException(nameof(selection));}
            if(selection.Count>this.rocks.Length||!double.IsFinite(origin.Length)){throw new ArgumentOutOfRangeException(nameof(selection));}
            int count=selection.CopySnapshotTo(this.rocks);
            this.BeginReset(count,origin);
        }

        private void BeginReset(int count,SurfaceVector origin){
            this.rockCount=count;this.Origin=origin;this.CompletedRocks=0;
            this.uploadTarget=null;this.uploadStage=0;this.uploadedVertices=0;this.uploadedIndices=0;
            this.uploadBounds=default;this.LastUploadElementCount=0;this.IsUploadComplete=false;this.LastUploadStage="upload-idle";
        }

        /// <summary>描画へ出す前の背面Meshに固定容量のGPU layoutを確保し、接近フレームのdriver確保を避けます。</summary>
        public void PrepareTarget(Mesh mesh){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            if(this.uploadTarget!=null){throw new InvalidOperationException("転送中に別のGPU targetを準備できません。");}
            for(int i=0;i<this.preparedTargets.Length;i++){
                if(this.preparedTargets[i]==mesh){return;}
            }
            mesh.Clear(false);
            mesh.SetVertexBufferParams(this.vertices.Length,UploadLayout);
            mesh.SetIndexBufferParams(this.triangles.Length,IndexFormat.UInt32);
            this.preparedTargets[this.nextPreparedTarget]=mesh;this.nextPreparedTarget=1-this.nextPreparedTarget;
            this.BufferPreparationCount++;
        }

        /// <summary>共有標高正本へ1個だけ接地させます。LODや品質により位置・形状を変更しません。</summary>
        public bool Step(){
            if(this.IsComplete){return false;}
            int index=this.CompletedRocks;
            SurfaceRockCandidate rock=this.rocks[index];
            SurfaceVector direction=rock.Direction.Normalized;
            double height=this.Height(direction);
            SurfaceVector ground=direction*(this.radius+height);
            this.GroundPositions[index]=ground;
            SurfaceVector reference=new(0,0,1);if(Math.Abs(direction.Z)>0.9){reference=new SurfaceVector(0,1,0);}
            SurfaceVector tangent=SurfaceVector.Cross(reference,direction).Normalized,side=SurfaceVector.Cross(direction,tangent);
            double delta=Math.Max(0.02,rock.DiameterMeters*0.25),angle=delta/this.radius;
            double dx=(this.Height((direction+tangent*angle).Normalized)-this.Height((direction-tangent*angle).Normalized))/(2*delta);
            double dy=(this.Height((direction+side*angle).Normalized)-this.Height((direction-side*angle).Normalized))/(2*delta);
            SurfaceVector normal=(direction-(tangent*dx+side*dy)*(this.radius/(this.radius+height))).Normalized;
            tangent=(tangent-normal*Dot(tangent,normal)).Normalized;side=SurfaceVector.Cross(tangent,normal);
            SurfaceVector center=ground-normal*(rock.DiameterMeters*0.1);
            if(this.shapeTemplates!=null){
                this.ShapeFromTemplate(rock,ground,normal,tangent,side,out center);
            }else{
                const double g=1.6180339887498948482;
                this.shape[0]=new(-1,g,0);this.shape[1]=new(1,g,0);this.shape[2]=new(-1,-g,0);this.shape[3]=new(1,-g,0);
                this.shape[4]=new(0,-1,g);this.shape[5]=new(0,1,g);this.shape[6]=new(0,-1,-g);this.shape[7]=new(0,1,-g);
                this.shape[8]=new(g,0,-1);this.shape[9]=new(g,0,1);this.shape[10]=new(-g,0,-1);this.shape[11]=new(-g,0,1);
                uint state=rock.ShapeSeed;double c=Math.Cos(rock.YawRadians),s=Math.Sin(rock.YawRadians);
                for(int i=0;i<12;i++){
                    unchecked{state=state*1664525u+1013904223u;}
                    double scale=rock.DiameterMeters*0.5*(0.8+0.2*(state&0xffffffu)/16777216.0);
                    SurfaceVector p=this.shape[i].Normalized;
                    double x=p.X*scale,z=p.Z*scale*0.8;
                    this.shape[i]=center+tangent*(c*x+s*z)+normal*(p.Y*scale*0.65)+side*(-s*x+c*z);
                }
            }
            Vector2 colorUv=new((float)(Math.Atan2(direction.X,direction.Y)/(2*Math.PI)+0.5),
                (float)(Math.Asin(Math.Clamp(direction.Z,-1,1))/Math.PI+0.5));
            for(int face=0;face<60;face+=3){
                SurfaceVector a=this.shape[Faces[face]],b=this.shape[Faces[face+1]],d=this.shape[Faces[face+2]];
                SurfaceVector n=SurfaceVector.Cross(b-a,d-a).Normalized;
                if(Dot(n,(a+b+d)/3-center)<0){(b,d)=(d,b);n=n*-1;}
                Vector3 nf=Float(n),tf=Float((b-a).Normalized);
                int offset=index*60+face;
                this.vertices[offset]=Float(a-this.Origin);this.vertices[offset+1]=Float(b-this.Origin);this.vertices[offset+2]=Float(d-this.Origin);
                for(int j=0;j<3;j++){
                    this.normals[offset+j]=nf;this.tangents[offset+j]=new Vector4(tf.x,tf.y,tf.z,1);
                    this.uv[offset+j]=colorUv;this.triangles[offset+j]=offset+j;
                    Vector3 vertex=this.vertices[offset+j];
                    if(offset+j==0){this.uploadBounds=new Bounds(vertex,Vector3.zero);}
                    else{this.uploadBounds.Encapsulate(vertex);}
                }
            }
            this.CompletedRocks++;return true;
        }

        /// <summary>型(ShapeTemplates)から岩の12頂点を置きます。型の高さの30〜50%(形状seedで決まる)を地面へ埋めます。</summary>
        private void ShapeFromTemplate(SurfaceRockCandidate rock,SurfaceVector ground,SurfaceVector normal,SurfaceVector tangent,SurfaceVector side,out SurfaceVector center){
            uint state=rock.ShapeSeed;
            unchecked{state=state*2654435761u+97u;}
            Vector3[] template=this.shapeTemplates[(int)(state%(uint)this.shapeTemplates.Length)];
            double bottom=double.MaxValue,top=double.MinValue;
            foreach(Vector3 p in template){bottom=Math.Min(bottom,p.y);top=Math.Max(top,p.y);}
            unchecked{state=state*1664525u+1013904223u;}
            double buried=0.3+0.2*(state&0xffffu)/65536.0;
            double d=rock.DiameterMeters;
            center=ground-normal*((bottom+buried*(top-bottom))*d);
            double c=Math.Cos(rock.YawRadians),s=Math.Sin(rock.YawRadians);
            for(int i=0;i<12;i++){
                unchecked{state=state*1664525u+1013904223u;}
                double jitter=0.92+0.16*(state&0xffffffu)/16777216.0;
                double x=template[i].x*d*jitter,y=template[i].y*d*jitter,z=template[i].z*d*jitter;
                this.shape[i]=center+tangent*(c*x+s*z)+normal*y+side*(-s*x+c*z);
            }
        }

        /// <summary>完成後に再利用する背面Meshへ転送します。途中の形状を描画へ公開しません。</summary>
        public void ApplyTo(Mesh mesh){
            if(!this.IsComplete){throw new InvalidOperationException("岩の生成が未完了です。");}
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            for(int i=0;i<this.preparedTargets.Length;i++){if(this.preparedTargets[i]==mesh){this.preparedTargets[i]=null;}}
            int count=this.rockCount*60;
            mesh.Clear();mesh.SetVertices(this.vertices,0,count);mesh.SetNormals(this.normals,0,count);
            mesh.SetTangents(this.tangents,0,count);mesh.SetUVs(0,this.uv,0,count);
            mesh.SetUVs(2,this.disabledAttributes,0,count);mesh.SetUVs(3,this.disabledAttributes,0,count);mesh.SetUVs(4,this.disabledAttributes,0,count);
            mesh.SetTriangles(this.triangles,0,count,0,true);
        }
        /// <summary>描画していない背面Meshへ、1段階だけ転送します。</summary>
        public bool UploadStep(Mesh mesh,int maximumElements=1024){
            if(!this.IsComplete){throw new InvalidOperationException("岩の生成が未完了です。");}
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            if(maximumElements<1||maximumElements>this.uploadVertices.Length){throw new ArgumentOutOfRangeException(nameof(maximumElements));}
            if(this.uploadTarget!=null&&this.uploadTarget!=mesh){throw new InvalidOperationException("転送先Meshは途中で変更できません。");}
            this.LastUploadElementCount=0;
            if(this.IsUploadComplete){return true;}
            this.uploadTarget=mesh;
            int vertexCount=this.rockCount*60;
            // バッファ設定・頂点転送・索引転送・公開準備を別stepにし、全配列を一括転送しません。
            const MeshUpdateFlags flags=MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices;
            if(this.uploadStage==0){
                for(int i=0;i<this.preparedTargets.Length;i++){
                    if(this.preparedTargets[i]!=mesh){continue;}
                    if(mesh.vertexCount==this.vertices.Length&&mesh.vertexBufferCount==1&&
                        mesh.GetVertexBufferStride(0)==96&&mesh.indexFormat==IndexFormat.UInt32){
                        this.LastUploadStage="upload-reuse";this.uploadStage=3;
                        if(vertexCount==0){this.uploadStage=5;}
                        return false;
                    }
                    this.preparedTargets[i]=null;
                }
                this.LastUploadStage="upload-clear";mesh.Clear(false);
                if(vertexCount==0){this.IsUploadComplete=true;return true;}
                this.uploadStage=1;return false;
            }
            if(this.uploadStage==1){
                this.LastUploadStage="upload-vertex-buffer";
                mesh.SetVertexBufferParams(this.vertices.Length,UploadLayout);this.uploadStage=2;return false;
            }
            if(this.uploadStage==2){
                this.LastUploadStage="upload-index-buffer";
                mesh.SetIndexBufferParams(this.triangles.Length,IndexFormat.UInt32);
                this.preparedTargets[this.nextPreparedTarget]=mesh;this.nextPreparedTarget=1-this.nextPreparedTarget;
                this.BufferPreparationCount++;this.uploadStage=3;return false;
            }
            if(this.uploadStage==3){
                this.LastUploadStage="upload-vertices";
                int count=Math.Min(maximumElements,vertexCount-this.uploadedVertices);
                for(int i=0;i<count;i++){
                    int sourceIndex=this.uploadedVertices+i;
                    this.uploadVertices[i]=new UploadVertex{Position=this.vertices[sourceIndex],Normal=this.normals[sourceIndex],
                        Tangent=this.tangents[sourceIndex],Uv=this.uv[sourceIndex]};
                }
                mesh.SetVertexBufferData(this.uploadVertices,0,this.uploadedVertices,count,0,flags);
                this.uploadedVertices+=count;this.LastUploadElementCount=count;
                if(this.uploadedVertices==vertexCount){this.uploadStage=4;}
                return false;
            }
            if(this.uploadStage==4){
                this.LastUploadStage="upload-indices";
                int count=Math.Min(maximumElements,vertexCount-this.uploadedIndices);
                mesh.SetIndexBufferData(this.triangles,this.uploadedIndices,this.uploadedIndices,count,flags);
                this.uploadedIndices+=count;this.LastUploadElementCount=count;
                if(this.uploadedIndices==vertexCount){this.uploadStage=5;}
                return false;
            }
            this.LastUploadStage="upload-finish";
            // SetIndexBufferParams後のsubMeshCount削減は索引バッファを縮めるため行いません。
            // float丸めで境界上の頂点を除外しないよう、1mmだけ保守的に拡張します。
            Bounds bounds=this.uploadBounds;bounds.Expand(0.002f);
            mesh.SetSubMesh(0,new SubMeshDescriptor(0,vertexCount,MeshTopology.Triangles){
                firstVertex=0,vertexCount=vertexCount,bounds=bounds
            },flags);
            mesh.bounds=bounds;this.IsUploadComplete=true;return true;
        }
        public int LastUploadElementCount{get;private set;}
        public bool IsUploadComplete{get;private set;}
        private double Height(SurfaceVector direction){
            double height=this.source.SampleHeightMeters(direction.X,direction.Y,direction.Z);
            if(!double.IsFinite(height)||this.radius+height<=0){throw new ArithmeticException("岩の接地標高が不正です。");}
            return height;
        }
        private static double Dot(SurfaceVector a,SurfaceVector b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        private static Vector3 Float(SurfaceVector p)=>new((float)p.X,(float)p.Y,(float)p.Z);
    }
}
