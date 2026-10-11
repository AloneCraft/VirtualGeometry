using System;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 緯度経度の格子(北が上、画素の中心が格子点)で持つ実データの高さ(メートル、半径 R からの高さ)。全球の標高、または局所の標高(Override)。
    /// 高さは、頂点の位置を「Cell の原点の緯度経度 + 原点からの差」で表して双線形補間する。差は桁落ちしない式で求めるので(<see cref="PlanetRasterLayer.Delta"/>)、
    /// 半径 10^6 m の天体で 1mm の頂点でも、緯度経度を float の方向から求めたときの量子化(約 0.1 m)が出ない。
    /// 帯域制限: Cell の quad の長さが画素より大きいときは、箱形フィルタで半分にしていった mip を使う(格子の間隔で表せない細部を足さない)。
    /// GPU は PlanetRaster.hlsl が同じ式で、Cell ごとの値(<see cref="PlanetRasterCell"/>)と texture を受け取る。
    /// 座標の規約は Omni の PlanetSurface と同じ: 経度 = atan2(x, y)(+y が経度 0、+x が東)、緯度 = atan2(z, √(x²+y²))(+z が北)。
    /// </summary>
    public sealed class PlanetRasterLayer:IDisposable{
        private readonly float[][] mips;
        // mip ごとの極の値(全球の格子だけ使う。先頭の行、最後の行の平均)。極冠(PoleCap)の頂点の値。
        private readonly float[] poleNorth;
        private readonly float[] poleSouth;
        private Texture2D texture;

        /// <summary>mip 0 の画素数。</summary>
        public int Width{get;}
        public int Height{get;}
        /// <summary>mip 0 の、経度 0・緯度 0 の画素の位置(画素の中心が整数)。列 = CenterColumn + 経度·ColumnsPerRadian、行 = CenterRow − 緯度·RowsPerRadian。</summary>
        public double CenterColumn{get;}
        public double CenterRow{get;}
        public double ColumnsPerRadian{get;}
        public double RowsPerRadian{get;}
        /// <summary>経度の方向に一周するか(全球)。しないなら端の画素の値が続く。</summary>
        public bool WrapLongitude{get;}
        /// <summary>局所の範囲の端から inset 画素の内側まで重み 0、そこから feather 画素かけて 1 にする(mip 0 の画素の単位)。全球は重み 1。</summary>
        public bool IsRegional{get;}
        public float InsetTexels{get;}
        public float FeatherTexels{get;}
        /// <summary>|高さ| の最大(mip 0)。選択の bounds に足す。</summary>
        public float MaximumAbsoluteHeight{get;}
        public int MipCount=>this.mips.Length;

        private PlanetRasterLayer(float[] heights,int width,int height,double centerColumn,double centerRow,double columnsPerRadian,double rowsPerRadian,bool wrap,bool regional,float inset,float feather){
            if(heights==null||heights.Length!=width*height||width<1||height<1){throw new ArgumentException("高さの格子の寸法が不正です。");}
            this.Width=width;this.Height=height;
            this.CenterColumn=centerColumn;this.CenterRow=centerRow;this.ColumnsPerRadian=columnsPerRadian;this.RowsPerRadian=rowsPerRadian;
            this.WrapLongitude=wrap;this.IsRegional=regional;this.InsetTexels=inset;this.FeatherTexels=feather;
            var chain=new System.Collections.Generic.List<float[]>{heights};
            int w=width,h=height;
            float[] current=heights;
            // 縦横とも 2 で割れる間は、2×2 の平均で半分にする。
            while(w%2==0&&h%2==0&&w>1&&h>1){
                int nw=w/2,nh=h/2;
                var next=new float[nw*nh];
                for(int y=0;y<nh;y++){
                    for(int x=0;x<nw;x++){
                        next[y*nw+x]=(current[(2*y)*w+2*x]+current[(2*y)*w+2*x+1]+current[(2*y+1)*w+2*x]+current[(2*y+1)*w+2*x+1])*0.25f;
                    }
                }
                chain.Add(next);current=next;w=nw;h=nh;
            }
            this.mips=chain.ToArray();
            this.poleNorth=new float[this.mips.Length];
            this.poleSouth=new float[this.mips.Length];
            for(int mip=0;mip<this.mips.Length;mip++){
                int mipWidth=this.MipWidth(mip),mipHeight=this.MipHeight(mip);
                double north=0,south=0;
                for(int x=0;x<mipWidth;x++){
                    north+=this.mips[mip][x];
                    south+=this.mips[mip][(mipHeight-1)*mipWidth+x];
                }
                this.poleNorth[mip]=(float)(north/mipWidth);
                this.poleSouth[mip]=(float)(south/mipWidth);
            }
            float maximum=0;
            for(int i=0;i<heights.Length;i++){maximum=Mathf.Max(maximum,Mathf.Abs(heights[i]));}
            this.MaximumAbsoluteHeight=maximum;
        }

        /// <summary>全球の標高。北から南、経度 −180° から +180°(画素の中心が格子点、経度 0 は中央)の等距円筒。</summary>
        public static PlanetRasterLayer CreateGlobal(float[] heights,int width,int height){
            return new PlanetRasterLayer(heights,width,height,width*0.5-0.5,height*0.5-0.5,width/(2.0*Math.PI),height/Math.PI,true,false,0,0);
        }

        /// <summary>
        /// 局所の標高(Override)。画素の格子の中央が (centerLongitude, centerLatitude)(ラジアン)で、1 度あたりの画素数が pixelsPerDegree。
        /// 範囲の端から insetTexels の内側までは全球の値のまま、そこから featherTexels かけて局所の値へ移る。
        /// </summary>
        public static PlanetRasterLayer CreateRegional(float[] heights,int width,int height,double centerLongitude,double centerLatitude,double pixelsPerDegree,float insetTexels,float featherTexels){
            double perRadian=pixelsPerDegree*180.0/Math.PI;
            // 経度 0・緯度 0 の画素の位置 = 中央の画素 − 中央の緯度経度の分。
            double centerColumn=(width-1)*0.5-centerLongitude*perRadian;
            double centerRow=(height-1)*0.5+centerLatitude*perRadian;
            return new PlanetRasterLayer(heights,width,height,centerColumn,centerRow,perRadian,perRadian,false,true,insetTexels,featherTexels);
        }

        /// <summary>float32(リトルエンディアン)の北から南の格子を読む(Omni のデモの .bytes と同じ形式)。</summary>
        public static float[] ReadFloat32(byte[] bytes,int width,int height){
            if(bytes==null||bytes.Length!=width*height*4){throw new ArgumentException("float32 の格子の大きさが合いません: "+(bytes==null?0:bytes.Length)+" バイト、期待 "+width*height*4);}
            var result=new float[width*height];
            Buffer.BlockCopy(bytes,0,result,0,bytes.Length);
            return result;
        }

        public int MipWidth(int mip)=>Math.Max(1,this.Width>>mip);
        public int MipHeight(int mip)=>Math.Max(1,this.Height>>mip);

        /// <summary>mip の北極、南極の値(先頭の行、最後の行の平均)。経度の方向に一周する格子(全球)だけが使う(<see cref="PoleCap"/>)。</summary>
        public float PoleNorth(int mip)=>this.poleNorth[mip];
        public float PoleSouth(int mip)=>this.poleSouth[mip];

        /// <summary>
        /// 極冠。全球の格子は画素の中心が格子点なので、極は先頭の行の半画素北(行の位置 −0.5)、最後の行の半画素南(行の位置 h−0.5)にある。
        /// 先頭の行より北(行の位置 &lt; 0)を先頭の行へ clamp すると、極の近くの高さが経度で決まり(先頭の行は列ごとにばらつく。LOLA の北極で 145 m、南極で 1,090 m の幅)、
        /// 極の 1mm の範囲でも高さが経度で何 m も変わる(経度は極で定まらない)ので、極を含む Cell の頂点が GPU と CPU で一致しなくなる。
        /// そこで極冠(極から半画素)は、極の値 P(先頭の行の平均)から、先頭の行の補間した値 ring へ、極からの距離に比例して移る: 値 = P + (ring − P)·t、t = 極からの距離/半画素。
        /// 経度によらず P に収束して連続になる。場が局所的に平面なら、極冠の中でも平面を正確に再現する(ring = P + g·半画素·cos(経度)、t·ring の変動 = g·距離·cos(経度))。
        /// 南は鏡像。y0 は clamp する前の画素の行、fy は端数、h は mip の高さ、shift は texel から先に引いた高さ。
        /// </summary>
        public static double PoleCap(double value,int y0,double fy,int h,double north,double south,double shift){
            if(y0<0){
                double t=Math.Max(0.0,Math.Min(1.0,2.0*(y0+fy)+1.0));
                double pole=north-shift;
                return pole+(value-pole)*t;
            }
            if(y0>=h-1){
                double t=Math.Max(0.0,Math.Min(1.0,1.0+2.0*((h-1-y0)-fy)));
                double pole=south-shift;
                return pole+(value-pole)*t;
            }
            return value;
        }

        /// <summary>1 画素の北向きの間隔(m)。半径 radius の天体。</summary>
        public double TexelMeters(double radius)=>radius/this.RowsPerRadian;

        /// <summary>quad の長さ quadLength(m)に使う mip(画素が quad より小さくならないように、最小の mip)。</summary>
        public int MipFor(float quadLength,double radius){
            if(quadLength<=0f){return 0;}
            double ratio=quadLength/this.TexelMeters(radius);
            if(ratio<=1.0){return 0;}
            int mip=(int)Math.Ceiling(Math.Log(ratio,2.0));
            return Math.Max(0,Math.Min(mip,this.mips.Length-1));
        }

        /// <summary>GPU の texture(R32F、全 mip)。最初に必要になったときに作る。</summary>
        public Texture2D GetTexture(){
            if(this.texture!=null){return this.texture;}
            this.texture=new Texture2D(this.Width,this.Height,TextureFormat.RFloat,this.mips.Length,true){name="PlanetRasterLayer",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Point};
            for(int mip=0;mip<this.mips.Length;mip++){
                this.texture.SetPixelData(this.mips[mip],mip);
            }
            this.texture.Apply(false,true);
            return this.texture;
        }

        public void Dispose(){
            if(this.texture!=null){
                if(Application.isPlaying){UnityEngine.Object.Destroy(this.texture);}else{UnityEngine.Object.DestroyImmediate(this.texture);}
                this.texture=null;
            }
        }

        /// <summary>
        /// 原点(半径 R の球面上の点 origin、惑星のローカル座標)を中心とする Cell の、Cell ごとの値。
        /// 原点の画素の位置は倍精度で整数部と端数に分け、頂点ごとの緯度経度の差(<see cref="Delta"/>)を端数に足すので、整数部は桁落ちしない。
        /// </summary>
        public bool TryCell(in LogicalVector3 origin,float quadLength,double radius,out PlanetRasterCell cell){
            cell=default;
            if(!origin.X.TryToDouble(out double x)||!origin.Y.TryToDouble(out double y)||!origin.Z.TryToDouble(out double z)){return false;}
            double length=Math.Sqrt(x*x+y*y+z*z);
            if(!(length>0)){return false;}
            x/=length;y/=length;z/=length;
            double longitude=Math.Atan2(x,y);
            double latitude=Math.Atan2(z,Math.Sqrt(x*x+y*y));
            int mip=this.MipFor(quadLength,radius);
            double scale=1.0/(1<<mip);
            // mip の画素の位置 = (mip 0 の位置 + 0.5)/2^mip − 0.5
            double column0=this.CenterColumn+longitude*this.ColumnsPerRadian;
            double row0=this.CenterRow-latitude*this.RowsPerRadian;
            double column=(column0+0.5)*scale-0.5;
            double row=(row0+0.5)*scale-0.5;
            double columnFloor=Math.Floor(column);double rowFloor=Math.Floor(row);
            cell=new PlanetRasterCell{
                Direction=new Vector3((float)x,(float)y,(float)z),
                ColumnIndex=(int)columnFloor,RowIndex=(int)rowFloor,Mip=mip,Wrap=this.WrapLongitude?1:0,
                ColumnFraction=(float)(column-columnFloor),RowFraction=(float)(row-rowFloor),
                ColumnsPerRadian=(float)(this.ColumnsPerRadian*scale),RowsPerRadian=(float)(this.RowsPerRadian*scale),
                Width=this.MipWidth(mip),Height=this.MipHeight(mip),
                RegionalInset=this.InsetTexels,RegionalFeather=this.FeatherTexels,Regional=this.IsRegional?1:0,
                MipScale=(float)scale,BaseWidth=this.Width,BaseHeight=this.Height,
                PoleNorth=this.poleNorth[mip],PoleSouth=this.poleSouth[mip],
            };
            return true;
        }

        /// <summary>この |o.z| 以上(緯度 75° 以上)の原点には、水平成分の大きさから求める極用の式(<see cref="PolarDelta"/>)を使う。PlanetRaster.hlsl の PLANET_POLAR_Z と同じ値。</summary>
        public const float PolarZ=0.966f;

        /// <summary>
        /// 原点の単位方向 o から、球面上の点の差 eps(= n − o、n は頂点の単位方向)の頂点の、緯度経度の差(ラジアン)。
        /// 緯度の差 = asin(nz) − asin(oz)、経度の差 = atan2(nx, ny) − atan2(ox, oy)。どちらも n と o の大きな値どうしの引き算をしない形で求める。
        /// 極の近く(|o.z| ≥ <see cref="PolarZ"/>)は、cos(緯度) = √(1−z²) が極で桁落ちするので、水平成分の大きさから求める(<see cref="PolarDelta"/>)。
        /// </summary>
        public static void Delta(Vector3 o,Vector3 eps,out double longitude,out double latitude){
            if(Math.Abs(o.z)>=PolarZ){
                PolarDelta(o,eps,out longitude,out latitude);
                return;
            }
            double a=o.z+eps.z;double b=o.z;
            double ca=Math.Sqrt(Math.Max(1.0-a*a,0.0));double cb=Math.Sqrt(Math.Max(1.0-b*b,0.0));
            double numerator=eps.z*(cb+b*(a+b)/(ca+cb+1e-30));
            double denominator=ca*cb+a*b;
            latitude=Math.Atan2(numerator,denominator);
            double cross=(double)eps.x*o.y-(double)eps.y*o.x;
            double dot=(double)o.x*o.x+(double)o.y*o.y+(double)eps.x*o.x+(double)eps.y*o.y;
            longitude=Math.Atan2(cross,dot);
        }

        /// <summary>
        /// <see cref="Delta"/> の極用の式。cos(緯度) を z から √(1−z²) で求めると、極の近く(z が 1 に近い)で桁落ちする(float の o.z が 1.0 になる緯度 89.99° より先では緯度が −90° にずれる)ので、
        /// 水平成分の大きさ ho = |o_h|、hn = |n_h| から求める。sin(Δ緯度) ∝ nz·ho − hn·oz = eps.z·ho − (hn−ho)·oz、cos(Δ緯度) ∝ hn·ho + nz·oz。
        /// hn − ho = (|n_h|² − |o_h|²)/(hn + ho) は、大きな値どうしの引き算をしない形。経度の差 = atan2(eps.x·o.y − eps.y·o.x, ho² + o_h·eps_h)。
        /// hn は n_h = o_h + eps_h の長さを成分の和から直接求める(|n_h|² = ho² + d2 の平方根にすると、float の GPU では、頂点が極に近いとき(n_h → 0)に相殺して hn に ho の 3e-4 倍の誤差が出る。GPU と同じ式にそろえる)。
        /// 原点が極そのもの(ho = 0。階層 0 の根だけ)のときは、経度の差を頂点の経度 atan2(eps.x, eps.y) にする(原点の経度は 0)。
        /// </summary>
        public static void PolarDelta(Vector3 o,Vector3 eps,out double longitude,out double latitude){
            double ho2=(double)o.x*o.x+(double)o.y*o.y;
            double ho=Math.Sqrt(ho2);
            double oe=(double)o.x*eps.x+(double)o.y*eps.y;
            double d2=2.0*oe+(double)eps.x*eps.x+(double)eps.y*eps.y;
            double nx=(double)o.x+eps.x;double ny=(double)o.y+eps.y;
            double hn=Math.Sqrt(nx*nx+ny*ny);
            double eh=d2/(hn+ho+1e-30);
            double nz=(double)o.z+eps.z;
            latitude=Math.Atan2((double)eps.z*ho-eh*o.z,hn*ho+nz*o.z);
            if(ho2<1e-30){
                longitude=Math.Atan2(eps.x,eps.y);
            }else{
                longitude=Math.Atan2((double)eps.x*o.y-(double)eps.y*o.x,ho2+oe);
            }
        }

        /// <summary>
        /// Cell ごとの値 cell と、原点からの球面上の点の相対位置 sphere(= R·eps)の高さ(GPU の PlanetRaster.hlsl と同じ式の倍精度版)。
        /// 局所の格子で、範囲の外または重み 0 のときは、weight に 0 を返す(高さは 0)。
        /// </summary>
        public float Sample(in PlanetRasterCell cell,Vector3 sphere,double radius,out float weight){
            return this.Sample(in cell,sphere,radius,0f,out weight);
        }

        /// <summary>
        /// 同じで、高さから shift(Cell の原点を持ち上げた距離)を引いた値を返す(GPU と同じく、texel から先に引いてから補間する。ここは倍精度なので、float にする前に引く)。
        /// 局所の格子で重みが 0 のときは、shift によらず 0(その格子は高さに寄与しない)。
        /// </summary>
        public float Sample(in PlanetRasterCell cell,Vector3 sphere,double radius,float shift,out float weight){
            Vector3 eps=sphere/(float)radius;
            Delta(cell.Direction,eps,out double dLongitude,out double dLatitude);
            double column=cell.ColumnFraction+dLongitude*cell.ColumnsPerRadian;
            double row=cell.RowFraction-dLatitude*cell.RowsPerRadian;
            weight=1f;
            if(this.IsRegional){
                // mip 0 の画素の位置での、範囲の端までの距離(画素)。
                double column0=((cell.ColumnIndex+column)+0.5)/cell.MipScale-0.5;
                double row0=((cell.RowIndex+row)+0.5)/cell.MipScale-0.5;
                double edge=Math.Min(Math.Min(column0,this.Width-1.0-column0),Math.Min(row0,this.Height-1.0-row0));
                double t=(edge-this.InsetTexels)/Math.Max(this.FeatherTexels,1e-6f);
                t=Math.Max(0.0,Math.Min(1.0,t));
                weight=(float)(t*t*(3.0-2.0*t));
                if(weight<=0f){return 0f;}
            }
            double floorColumn=Math.Floor(column);double floorRow=Math.Floor(row);
            double fx=column-floorColumn;double fy=row-floorRow;
            int x0=cell.ColumnIndex+(int)floorColumn;int y0=cell.RowIndex+(int)floorRow;
            int rowUnclamped=y0;
            float[] data=this.mips[cell.Mip];
            int w=cell.Width,h=cell.Height;
            int x1=x0+1,y1=y0+1;
            if(this.WrapLongitude){x0=((x0%w)+w)%w;x1=((x1%w)+w)%w;}
            else{x0=Math.Max(0,Math.Min(w-1,x0));x1=Math.Max(0,Math.Min(w-1,x1));}
            y0=Math.Max(0,Math.Min(h-1,y0));y1=Math.Max(0,Math.Min(h-1,y1));
            double h00=data[y0*w+x0]-(double)shift,h10=data[y0*w+x1]-(double)shift,h01=data[y1*w+x0]-(double)shift,h11=data[y1*w+x1]-(double)shift;
            double top=h00+(h10-h00)*fx;double bottom=h01+(h11-h01)*fx;
            double result=top+(bottom-top)*fy;
            if(this.WrapLongitude){result=PoleCap(result,rowUnclamped,fy,h,cell.PoleNorth,cell.PoleSouth,shift);}
            return (float)result;
        }

        /// <summary>検証用。mip 0 の双線形補間を、方向(単位ベクトル、倍精度)から直接求める。Cell の分割を使わない独立な計算。</summary>
        public double SampleDirection(double x,double y,double z){
            return this.SampleDirection(x,y,z,out double _);
        }

        /// <summary>検証用。同じで、局所の格子の重み(全球は 1)も返す。重みが 0 の所の高さは 0。</summary>
        public double SampleDirection(double x,double y,double z,out double weight){
            weight=1.0;
            double length=Math.Sqrt(x*x+y*y+z*z);
            x/=length;y/=length;z/=length;
            double longitude=Math.Atan2(x,y);
            double latitude=Math.Atan2(z,Math.Sqrt(x*x+y*y));
            double column=this.CenterColumn+longitude*this.ColumnsPerRadian;
            double row=this.CenterRow-latitude*this.RowsPerRadian;
            if(this.IsRegional){
                double edge=Math.Min(Math.Min(column,this.Width-1.0-column),Math.Min(row,this.Height-1.0-row));
                double t=Math.Max(0.0,Math.Min(1.0,(edge-this.InsetTexels)/Math.Max(this.FeatherTexels,1e-6f)));
                weight=t*t*(3.0-2.0*t);
                if(weight<=0.0){return 0.0;}
            }
            double floorColumn=Math.Floor(column);double floorRow=Math.Floor(row);
            double fx=column-floorColumn;double fy=row-floorRow;
            int x0=(int)floorColumn;int y0=(int)floorRow;
            int rowUnclamped=y0;
            int x1=x0+1,y1=y0+1;
            float[] data=this.mips[0];
            int w=this.Width,h=this.Height;
            if(this.WrapLongitude){x0=((x0%w)+w)%w;x1=((x1%w)+w)%w;}
            else{x0=Math.Max(0,Math.Min(w-1,x0));x1=Math.Max(0,Math.Min(w-1,x1));}
            y0=Math.Max(0,Math.Min(h-1,y0));y1=Math.Max(0,Math.Min(h-1,y1));
            double h00=data[y0*w+x0],h10=data[y0*w+x1],h01=data[y1*w+x0],h11=data[y1*w+x1];
            double top=h00+(h10-h00)*fx;double bottom=h01+(h11-h01)*fx;
            double result=top+(bottom-top)*fy;
            if(this.WrapLongitude){result=PoleCap(result,rowUnclamped,fy,h,this.poleNorth[0],this.poleSouth[0],0.0);}
            return result;
        }

        /// <summary>検証用。mip 0 の格子の値。</summary>
        public float Texel(int column,int row){
            return this.mips[0][row*this.Width+column];
        }
    }

    /// <summary><see cref="PlanetRasterLayer.TryCell"/> が Cell ごとに求める値(CPU の <see cref="PlanetRasterLayer.Sample"/> と GPU の PlanetRaster.hlsl が使う)。</summary>
    public struct PlanetRasterCell{
        public Vector3 Direction;
        public int ColumnIndex;
        public int RowIndex;
        public int Mip;
        public int Wrap;
        public float ColumnFraction;
        public float RowFraction;
        public float ColumnsPerRadian;
        public float RowsPerRadian;
        public int Width;
        public int Height;
        public float RegionalInset;
        public float RegionalFeather;
        public int Regional;
        public float MipScale;
        public int BaseWidth;
        public int BaseHeight;
        /// <summary>この mip の北極、南極の値(全球の格子だけ使う)。<see cref="PlanetRasterLayer.PoleCap"/>。</summary>
        public float PoleNorth;
        public float PoleSouth;
    }
}
