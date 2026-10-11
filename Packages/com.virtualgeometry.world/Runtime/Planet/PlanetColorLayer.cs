using System;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 全球の色(アルベド)の格子。北が上・経度 −180° から +180°・画素の中心が格子点の等距円筒(<see cref="PlanetRasterLayer.CreateGlobal"/> の高さの格子と同じ幾何)の texture(sRGB、mip 付き)。
    /// GPU の RadialMoonGrid.shader の fragment が、Cell の原点の緯度経度(<see cref="PlanetColorCell"/>)と、画素ごとの緯度経度の差(PlanetLonLat.hlsl)から texture を引く。
    /// 経度の継ぎ目と極でも同じ式(経度は繰り返し、緯度は端の画素が続く)。CPU の <see cref="SampleDirection"/> は、検証用の mip 0 の双線形補間(線形色空間、倍精度)。
    /// texture は呼び出し側が作る(読み取り可能、sRGB、mip 付き。wrap は経度が Repeat・緯度が Clamp にする)。
    /// </summary>
    public sealed class PlanetColorLayer{
        private readonly Texture2D texture;
        private readonly Color32[] pixels;

        public Texture2D Texture=>this.texture;
        public int Width{get;}
        public int Height{get;}
        /// <summary>経度 0・緯度 0 の画素の位置(画素の中心が整数、北が行 0)。列 = CenterColumn + 経度·ColumnsPerRadian、行 = CenterRow − 緯度·RowsPerRadian。</summary>
        public double CenterColumn{get;}
        public double CenterRow{get;}
        public double ColumnsPerRadian{get;}
        public double RowsPerRadian{get;}

        /// <summary>1 画素の北向きの間隔(m)。半径 radius の天体。</summary>
        public double TexelMeters(double radius)=>radius/this.RowsPerRadian;

        public PlanetColorLayer(Texture2D readableTexture){
            if(readableTexture==null){throw new ArgumentNullException(nameof(readableTexture));}
            this.texture=readableTexture;
            this.Width=readableTexture.width;this.Height=readableTexture.height;
            this.pixels=readableTexture.GetPixels32(0);
            this.CenterColumn=this.Width*0.5-0.5;this.CenterRow=this.Height*0.5-0.5;
            this.ColumnsPerRadian=this.Width/(2.0*Math.PI);this.RowsPerRadian=this.Height/Math.PI;
        }

        /// <summary>
        /// 原点(惑星のローカル座標の点、Logical)を中心とする Cell の、Cell ごとの値。原点の画素の位置は倍精度で整数部と端数に分け、GPU は画素ごとの緯度経度の差を端数に足す(<see cref="PlanetRasterLayer.TryCell"/> と同じ)。
        /// </summary>
        public bool TryCell(in LogicalVector3 origin,out PlanetColorCell cell){
            cell=default;
            if(!origin.X.TryToDouble(out double x)||!origin.Y.TryToDouble(out double y)||!origin.Z.TryToDouble(out double z)){return false;}
            double length=Math.Sqrt(x*x+y*y+z*z);
            if(!(length>0)){return false;}
            x/=length;y/=length;z/=length;
            double longitude=Math.Atan2(x,y);
            double latitude=Math.Atan2(z,Math.Sqrt(x*x+y*y));
            double column=this.CenterColumn+longitude*this.ColumnsPerRadian;
            double row=this.CenterRow-latitude*this.RowsPerRadian;
            double columnFloor=Math.Floor(column);double rowFloor=Math.Floor(row);
            cell=new PlanetColorCell{
                Direction=new Vector3((float)x,(float)y,(float)z),
                ColumnIndex=(int)columnFloor,RowIndex=(int)rowFloor,
                ColumnFraction=(float)(column-columnFloor),RowFraction=(float)(row-rowFloor),
                ColumnsPerRadian=(float)this.ColumnsPerRadian,RowsPerRadian=(float)this.RowsPerRadian,
                Width=this.Width,Height=this.Height,
            };
            return true;
        }

        /// <summary>
        /// 検証用。方向(単位ベクトルでなくてよい、倍精度)の色を、mip 0 の双線形補間で求める(線形色空間。GPU の sRGB texture の補間と同じく、sRGB を線形へ直してから補間する)。
        /// 経度は繰り返し、緯度は端の画素が続く。
        /// </summary>
        public Vector3 SampleDirection(double x,double y,double z){
            double length=Math.Sqrt(x*x+y*y+z*z);
            x/=length;y/=length;z/=length;
            double longitude=Math.Atan2(x,y);
            double latitude=Math.Atan2(z,Math.Sqrt(x*x+y*y));
            double column=this.CenterColumn+longitude*this.ColumnsPerRadian;
            double row=this.CenterRow-latitude*this.RowsPerRadian;
            double floorColumn=Math.Floor(column);double floorRow=Math.Floor(row);
            double fx=column-floorColumn;double fy=row-floorRow;
            int x0=(int)floorColumn;int y0=(int)floorRow;
            int x1=x0+1,y1=y0+1;
            x0=((x0%this.Width)+this.Width)%this.Width;x1=((x1%this.Width)+this.Width)%this.Width;
            y0=Math.Max(0,Math.Min(this.Height-1,y0));y1=Math.Max(0,Math.Min(this.Height-1,y1));
            Vector3 c00=this.Linear(x0,y0),c10=this.Linear(x1,y0),c01=this.Linear(x0,y1),c11=this.Linear(x1,y1);
            Vector3 top=c00+(c10-c00)*(float)fx;Vector3 bottom=c01+(c11-c01)*(float)fx;
            return top+(bottom-top)*(float)fy;
        }

        // 行 row(北が 0)・列 column の画素の線形色。texture は下が行 0 なので、行を反転して読む。
        private Vector3 Linear(int column,int row){
            Color32 pixel=this.pixels[(this.Height-1-row)*this.Width+column];
            return new Vector3(Mathf.GammaToLinearSpace(pixel.r/255f),Mathf.GammaToLinearSpace(pixel.g/255f),Mathf.GammaToLinearSpace(pixel.b/255f));
        }
    }

    /// <summary><see cref="PlanetColorLayer.TryCell"/> が Cell ごとに求める値(GPU の RadialMoonGrid.shader が使う)。</summary>
    public struct PlanetColorCell{
        public Vector3 Direction;
        public int ColumnIndex;
        public int RowIndex;
        public float ColumnFraction;
        public float RowFraction;
        public float ColumnsPerRadian;
        public float RowsPerRadian;
        public int Width;
        public int Height;
    }
}
