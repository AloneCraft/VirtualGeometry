using System;

namespace Omni.PlanetSurface{
    /// <summary>
    /// 北から南への画素中心標高を通るC1補間です。月面検証と自然地形評価器で共有します。
    /// Zを北極、+Yを経度0とし、極冠では一意な極値とゼロ勾配へHermite接続します。
    /// </summary>
    public sealed class SphericalHeightGrid:IPlanetSurfaceHeightSource{
        private readonly Func<int,int,double> sample;
        private readonly double northPole;
        private readonly double southPole;

        /// <summary>入力配列はコピーしません。評価器の寿命中、値を変更しないでください。</summary>
        public SphericalHeightGrid(float[] values,int width,int height)
            :this(width,height,CreateSampler(values,width,height),Maximum(values)){
        }

        /// <summary>生成標高の制約補間用です。関数は画素中心の確定値を返してください。</summary>
        public SphericalHeightGrid(int width,int height,Func<int,int,double> sample,double maximum){
            if(width<4||width%2!=0||height<2){
                throw new ArgumentOutOfRangeException(nameof(width),"偶数幅4以上・高さ2以上が必要です。");
            }
            this.Width=width;
            this.Height=height;
            this.sample=sample??throw new ArgumentNullException(nameof(sample));
            this.MaximumAbsoluteHeightMeters=maximum*2;
            for(int x=0;x<width;x++){
                this.northPole+=sample(x,0)/width;
                this.southPole+=sample(x,height-1)/width;
            }
        }

        /// <summary>元標高の横サンプル数です。</summary>
        public int Width{get;}
        /// <summary>元標高の縦サンプル数です。</summary>
        public int Height{get;}
        /// <inheritdoc/>
        public double MaximumAbsoluteHeightMeters{get;}

        /// <summary>1つの生成要求内で使う局所補間キャッシュです。並列の要求では別々に作成します。</summary>
        public Func<double,double,double> CreateCachedSampler()=>new CachedSampler(this).SampleGrid;

        /// <summary>要求ごとに16係数を保持し、同じセルの元サンプル取得を繰り返さない補間器です。</summary>
        private sealed class CachedSampler{
            private readonly SphericalHeightGrid grid;
            private readonly double[] coefficients=new double[16];
            private int cellX=int.MinValue;
            private int cellY=int.MinValue;
            internal CachedSampler(SphericalHeightGrid grid){this.grid=grid;}

            internal double SampleGrid(double column,double row){
                if(!double.IsFinite(column)||!double.IsFinite(row)){
                    throw new ArgumentOutOfRangeException(nameof(column));
                }
                column-=Math.Floor(column/this.grid.Width)*this.grid.Width;
                row=Math.Clamp(row,-0.5,this.grid.Height-0.5);
                int x=(int)Math.Floor(column);
                int y=Math.Clamp((int)Math.Floor(row),0,this.grid.Height-1);
                if(x!=this.cellX||y!=this.cellY){this.Prepare(x,y);}
                double t=column-x;
                double a=this.Row(0,t),b=this.Row(4,t),c=this.Row(8,t),d=this.Row(12,t);
                if(row<0){return Hermite(this.grid.northPole,b,0,(c-a)*0.25,(row+0.5)*2);}
                if(row>this.grid.Height-1){
                    return Hermite(b,this.grid.southPole,(c-a)*0.25,0,(row-this.grid.Height+1)*2);
                }
                return Cubic(a,b,c,d,row-y);
            }

            private void Prepare(int x,int y){
                for(int i=0;i<4;i++){
                    int row=y+i-1,column=x;
                    if(row<0){row=-row-1;column+=this.grid.Width/2;}
                    else if(row>=this.grid.Height){row=2*this.grid.Height-row-1;column+=this.grid.Width/2;}
                    double a=this.grid.At(column-1,row),b=this.grid.At(column,row);
                    double c=this.grid.At(column+1,row),d=this.grid.At(column+2,row);
                    int offset=i*4;
                    this.coefficients[offset]=b;
                    this.coefficients[offset+1]=(c-a)*0.5;
                    this.coefficients[offset+2]=a-2.5*b+2*c-0.5*d;
                    this.coefficients[offset+3]=(-a+3*b-3*c+d)*0.5;
                }
                this.cellX=x;this.cellY=y;
            }

            private double Row(int offset,double t)=>((this.coefficients[offset+3]*t+
                this.coefficients[offset+2])*t+this.coefficients[offset+1])*t+this.coefficients[offset];
        }

        /// <inheritdoc/>
        public double SampleHeightMeters(double unitX,double unitY,double unitZ){
            if(!double.IsFinite(unitX)||!double.IsFinite(unitY)||!double.IsFinite(unitZ)||
                unitX*unitX+unitY*unitY+unitZ*unitZ==0){
                throw new ArgumentOutOfRangeException(nameof(unitX));
            }
            double longitude=Math.Atan2(unitX,unitY);
            double latitude=Math.Atan2(unitZ,Math.Sqrt(unitX*unitX+unitY*unitY));
            return this.SampleGrid((longitude/(2*Math.PI)+0.5)*this.Width-0.5,
                (0.5-latitude/Math.PI)*this.Height-0.5);
        }

        /// <summary>元格子上の連続座標を補間します。整数座標は元サンプルを通ります。</summary>
        public double SampleGrid(double column,double row){
            if(!double.IsFinite(column)||!double.IsFinite(row)){
                throw new ArgumentOutOfRangeException(nameof(column));
            }
            column-=Math.Floor(column/this.Width)*this.Width;
            row=Math.Clamp(row,-0.5,this.Height-0.5);
            if(row<0){
                double ring=this.Row(column,0);
                double slope=(this.Row(column,1)-this.Row(column,-1))*0.5;
                return Hermite(this.northPole,ring,0,slope*0.5,(row+0.5)*2);
            }
            if(row>this.Height-1){
                double ring=this.Row(column,this.Height-1);
                double slope=(this.Row(column,this.Height)-this.Row(column,this.Height-2))*0.5;
                return Hermite(ring,this.southPole,slope*0.5,0,(row-this.Height+1)*2);
            }
            int y=(int)Math.Floor(row);
            return Cubic(this.Row(column,y-1),this.Row(column,y),this.Row(column,y+1),
                this.Row(column,y+2),row-y);
        }

        /// <summary>NASA画素中心の天体ローカル単位方向をdoubleで返します。</summary>
        public SurfaceVector SampleDirection(int column,int row){
            double longitude=((column+0.5)/this.Width-0.5)*2*Math.PI;
            double latitude=(0.5-(row+0.5)/this.Height)*Math.PI;
            return new SurfaceVector(Math.Sin(longitude)*Math.Cos(latitude),
                Math.Cos(longitude)*Math.Cos(latitude),Math.Sin(latitude));
        }

        private double Row(double column,int row){
            if(row<0){
                row=-row-1;
                column+=this.Width*0.5;
            }else if(row>=this.Height){
                row=2*this.Height-row-1;
                column+=this.Width*0.5;
            }
            int x=(int)Math.Floor(column);
            return Cubic(this.At(x-1,row),this.At(x,row),this.At(x+1,row),this.At(x+2,row),column-x);
        }

        private double At(int x,int y){
            x%=this.Width;
            if(x<0){x+=this.Width;}
            return this.sample(x,y);
        }

        private static double Cubic(double a,double b,double c,double d,double t){
            return Hermite(b,c,(c-a)*0.5,(d-b)*0.5,t);
        }

        private static double Hermite(double a,double b,double da,double db,double t){
            double t2=t*t;
            double t3=t2*t;
            return (2*t3-3*t2+1)*a+(t3-2*t2+t)*da+(-2*t3+3*t2)*b+(t3-t2)*db;
        }

        private static Func<int,int,double> CreateSampler(float[] values,int width,int height){
            if(values==null||values.LongLength!=(long)width*height){
                throw new ArgumentException("標高の寸法が一致しません。",nameof(values));
            }
            return (x,y)=>values[y*width+x];
        }

        private static double Maximum(float[] values){
            double maximum=0;
            if(values==null){throw new ArgumentNullException(nameof(values));}
            foreach(float value in values){
                if(!float.IsFinite(value)){throw new ArgumentException("標高に非有限値があります。");}
                maximum=Math.Max(maximum,Math.Abs(value));
            }
            return maximum;
        }
    }

    /// <summary>天体中心との差分をfloat化前に求める倍精度ベクトルです。</summary>
    public readonly struct SurfaceVector{
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        /// <summary>メートルまたは単位方向の3成分を保持します。</summary>
        public SurfaceVector(double x,double y,double z){this.X=x;this.Y=y;this.Z=z;}
        public double Length=>Math.Sqrt(this.X*this.X+this.Y*this.Y+this.Z*this.Z);
        public SurfaceVector Normalized=>this/this.Length;
        public static SurfaceVector operator +(SurfaceVector a,SurfaceVector b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static SurfaceVector operator -(SurfaceVector a,SurfaceVector b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static SurfaceVector operator *(SurfaceVector a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
        public static SurfaceVector operator /(SurfaceVector a,double b)=>new(a.X/b,a.Y/b,a.Z/b);
        public static SurfaceVector Cross(SurfaceVector a,SurfaceVector b)=>
            new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    }
}
