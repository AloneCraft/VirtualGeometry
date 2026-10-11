using System;
using System.IO;
using UnityEngine;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 月の高さの格子の供給元(検証用)。実データ(LOLA 全球標高と、0N/0E の SLDEM2015 の局所標高。Omni のデモのデータをコピーした LocalData/OmniDemo、git 管理外)と、
    /// データが無い環境でも走る合成の高起伏の格子(極で連続)。<see cref="RealMoonValidation"/> と <see cref="MoonBallValidation"/> が使う。
    /// </summary>
    internal static class RealMoonData{
        public const string DataFolder="LocalData/OmniDemo";
        public const string GlobalFile="lola-5760x2880-meters.bytes";
        public const string RegionalFile="sldem-0n-0e-344x344-meters.bytes";
        public const double MoonRadius=1737400.0;
        public const int SyntheticWidth=2880;
        public const int SyntheticHeight=1440;
        public const float SyntheticMinimum=-7300f;
        public const float SyntheticMaximum=9000f;

        /// <summary>コマンドラインの <c>-poolCapacity N</c>(cluster の pool と描画 slot の数)。無ければ fallback。pool の大きさと Cell 数の関係を測るための上書き。</summary>
        public static int PoolCapacity(int fallback){
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]=="-poolCapacity"&&int.TryParse(arguments[i+1],System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out int value)&&value>0){return value;}
            }
            return fallback;
        }

        /// <summary>コマンドラインの <c>-maxDrawSlots N</c>(camera ごとの描画 slot の数。pool と分けて小さくできる)。無ければ fallback。</summary>
        public static int MaximumDrawSlots(int fallback){
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]=="-maxDrawSlots"&&int.TryParse(arguments[i+1],System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out int value)&&value>0){return value;}
            }
            return fallback;
        }

        /// <summary>実データを読む。無いときは、コピーする場所を示して例外にする。</summary>
        public static void LoadReal(out PlanetRasterLayer global,out PlanetRasterLayer regional){
            string root=Path.GetDirectoryName(Application.dataPath);
            string globalPath=Path.Combine(root,DataFolder,GlobalFile);
            string regionalPath=Path.Combine(root,DataFolder,RegionalFile);
            if(!File.Exists(globalPath)||!File.Exists(regionalPath)){
                throw new FileNotFoundException("実データがありません。Omni のデモ(Assets/Omni/Demos/PlanetSurface/Data)の "+GlobalFile+" と "+RegionalFile+" を "+DataFolder+" へコピーしてください(git 管理外)。合成データで走る検証もあります。");
            }
            global=PlanetRasterLayer.CreateGlobal(PlanetRasterLayer.ReadFloat32(File.ReadAllBytes(globalPath),5760,2880),5760,2880);
            // SLDEM: 344×344、512 画素/度、中心は 0N/0E。±9.5 km(約 ±(9.5km/59m)=161 画素)の内側を使う。端から 14 画素(約 0.8 km)の内側まで重み 0、そこから 26 画素(約 1.5 km)で 1。
            regional=PlanetRasterLayer.CreateRegional(PlanetRasterLayer.ReadFloat32(File.ReadAllBytes(regionalPath),344,344),344,344,0.0,0.0,512.0,14f,26f);
        }

        /// <summary>合成の高起伏の全球の格子(<see cref="SyntheticHeights"/>)。</summary>
        public static PlanetRasterLayer CreateSynthetic(){
            return PlanetRasterLayer.CreateGlobal(SyntheticHeights(SyntheticWidth,SyntheticHeight),SyntheticWidth,SyntheticHeight);
        }

        // 合成の高さ(m)。方向 n=(sin λ cos φ, cos λ cos φ, sin φ) の滑らかな関数なので極で連続。−7,300 m と +9,000 m に格子の中で届くよう、最小・最大に合わせて線形に写す。
        // 勾配は、k=60 で最大約 0.3(16,300 m × 78 / 1,737 km)。経度の周期 3 と緯度の変化を持つ(全球の格子の座標の規約は PlanetRasterLayer と同じ)。
        public static float[] SyntheticHeights(int width,int height){
            var raw=new double[width*height];
            double minimum=double.MaxValue;double maximum=double.MinValue;
            for(int y=0;y<height;y++){
                double lat=(0.5-(y+0.5)/height)*Math.PI;
                for(int x=0;x<width;x++){
                    double lon=((x+0.5)/width-0.5)*2.0*Math.PI;
                    double nx=Math.Sin(lon)*Math.Cos(lat);double ny=Math.Cos(lon)*Math.Cos(lat);double nz=Math.Sin(lat);
                    double value=Math.Sin(60.0*nx+0.7)*Math.Cos(60.0*ny+0.3)+0.5*Math.Sin(37.0*nz+1.1);
                    raw[y*width+x]=value;
                    minimum=Math.Min(minimum,value);maximum=Math.Max(maximum,value);
                }
            }
            var result=new float[width*height];
            for(int i=0;i<result.Length;i++){
                result[i]=(float)(SyntheticMinimum+(raw[i]-minimum)/(maximum-minimum)*(SyntheticMaximum-SyntheticMinimum));
            }
            return result;
        }

        /// <summary>
        /// 全球の格子の検証の場所(経度、緯度。度): 最大の位置、最小の位置、経度・緯度 0、最大の位置の近くの勾配が大きい所(最大の位置から経度 1.5°、緯度 1° ずらした点。極から遠い)。
        /// </summary>
        public static double[][] ExtremeSites(PlanetRasterLayer layer){
            int columnMin=0,rowMin=0,columnMax=0,rowMax=0;
            double minimum=double.MaxValue;double maximum=double.MinValue;
            for(int r=0;r<layer.Height;r++){
                for(int c=0;c<layer.Width;c++){
                    float value=layer.Texel(c,r);
                    if(value<minimum){minimum=value;columnMin=c;rowMin=r;}
                    if(value>maximum){maximum=value;columnMax=c;rowMax=r;}
                }
            }
            double LongitudeOf(int c){return ((c+0.5)/layer.Width-0.5)*360.0;}
            double LatitudeOf(int r){return (0.5-(r+0.5)/layer.Height)*180.0;}
            return new[]{
                new[]{LongitudeOf(columnMax),LatitudeOf(rowMax)},
                new[]{LongitudeOf(columnMin),LatitudeOf(rowMin)},
                new[]{0.0,0.0},
                new[]{LongitudeOf(columnMax)+1.5,Math.Max(-60.0,Math.Min(60.0,LatitudeOf(rowMax)-1.0))},
            };
        }
    }
}
