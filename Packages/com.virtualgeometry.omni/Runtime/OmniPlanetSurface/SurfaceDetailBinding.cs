using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>月面LODメッシュと固定法線領域の保持参照・UV2を連動させます。</summary>
    public sealed class SurfaceDetailBinding:IDisposable{
        private readonly Mesh mesh;
        private readonly SurfaceDetailAtlas atlas;
        private readonly SurfaceTileKey region;
        private readonly Vector4 mapping;
        private readonly int resolution;
        private readonly bool eligible;
        private SurfaceDetailAtlas.Lease lease;
        private bool visible,disposed;
        public bool IsBound{get;private set;}
        public SurfaceDetailBinding(Mesh mesh,SurfaceTileKey key,int resolution,SurfaceDetailAtlas atlas){
            if(mesh==null||atlas==null){throw new ArgumentNullException(nameof(mesh));}
            if(resolution<1||mesh.vertexCount!=(resolution+1)*(resolution+1)+4*(resolution+1)){
                throw new ArgumentOutOfRangeException(nameof(resolution));
            }
            this.mesh=mesh;this.atlas=atlas;this.resolution=resolution;
            this.eligible=atlas.Regions.TryMap(key,out this.region,out this.mapping);
        }
        /// <summary>非表示では保持参照とUVを解除します。trueはUV変更を表し、同一MeshのBRG再登録は不要です。</summary>
        public bool SetVisible(bool visible){
            if(this.disposed){throw new ObjectDisposedException(nameof(SurfaceDetailBinding));}
            this.visible=visible;
            if(visible){
                if(this.eligible&&this.lease==null){this.atlas.TryAcquire(this.region,out this.lease);}
                return false;
            }
            this.lease?.Dispose();this.lease=null;
            bool changed=this.IsBound;this.IsBound=false;
            if(changed&&this.mesh!=null){SurfacePatchBuilder.SetDetailUnbound(this.mesh,this.mesh.vertexCount);}
            return changed;
        }
        /// <summary>生成済みタイルだけをUV2へ公開します。nowは描画時刻秒で、短い法線フェードの開始に使います。</summary>
        public bool TryPublish(float now){
            if(this.disposed){throw new ObjectDisposedException(nameof(SurfaceDetailBinding));}
            if(!float.IsFinite(now)){throw new ArgumentOutOfRangeException(nameof(now));}
            if(!this.visible||!this.eligible||this.IsBound){return false;}
            if(this.lease==null&&!this.atlas.TryAcquire(this.region,out this.lease)){return false;}
            if(!this.lease.TryGetBinding(out int layer,out _)){return false;}
            var uv=new Vector4[this.mesh.vertexCount];
            int size=this.atlas.Texture.width,core=size-2,side=this.resolution+1,surface=side*side;
            for(int y=0;y<side;y++){
                for(int x=0;x<side;x++){
                    float u=this.mapping.x+this.mapping.z*x/this.resolution;
                    float v=this.mapping.y+this.mapping.w*y/this.resolution;
                    // 領域0を未接続に予約し、GPU既定のw=1だけでは有効タイルにしません。
                    uv[y*side+x]=new Vector4((u*core+1)/size,(v*core+1)/size,layer+1,now+1);
                }
            }
            for(int edge=0;edge<4;edge++){
                for(int i=0;i<side;i++){uv[surface+edge*side+i]=uv[SurfacePatchBuilder.EdgeVertex(edge,i,this.resolution)];}
            }
            this.mesh.SetUVs(2,uv);this.IsBound=true;return true;
        }
        public void Dispose(){
            if(this.disposed){return;}this.SetVisible(false);this.disposed=true;
        }
    }
}
