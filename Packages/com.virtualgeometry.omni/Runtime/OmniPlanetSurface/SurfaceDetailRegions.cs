using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>月面と他天体で共有する、メッシュLODから独立した実寸法線領域です。</summary>
    public sealed class SurfaceDetailRegions{
        public int Level{get;}
        public double SpanMeters{get;}
        public SurfaceDetailRegions(double radius){
            if(!double.IsFinite(radius)||radius<=0){throw new ArgumentOutOfRangeException(nameof(radius));}
            this.Level=Math.Max(11,(int)Math.Ceiling(Math.Log(2*radius/16,2)));
            if(this.Level>20){throw new ArgumentOutOfRangeException(nameof(radius),"現在の近傍位置識別子の範囲を超えています。");}
            this.SpanMeters=2*radius/(1<<this.Level);
        }

        /// <summary>近傍メッシュを覆う一意な領域とUVのoffset/scaleを返します。粗いメッシュは対象外です。</summary>
        public bool TryMap(SurfaceTileKey patch,out SurfaceTileKey region,out Vector4 uv){
            region=default;uv=default;
            if(patch.Face<0||patch.Face>5||patch.Level<0||patch.Level>20||patch.X<0||patch.Y<0||
                patch.X>=(1<<patch.Level)||patch.Y>=(1<<patch.Level)){
                throw new ArgumentOutOfRangeException(nameof(patch));
            }
            if(patch.Level<this.Level){return false;}
            int shift=patch.Level-this.Level,divisor=1<<shift;
            region=new SurfaceTileKey(patch.Face,this.Level,patch.X>>shift,patch.Y>>shift);
            uv=new Vector4((patch.X%divisor)/(float)divisor,(patch.Y%divisor)/(float)divisor,
                1f/divisor,1f/divisor);
            return true;
        }
    }
}
