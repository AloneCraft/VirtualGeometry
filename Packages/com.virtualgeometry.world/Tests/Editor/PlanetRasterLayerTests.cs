using System;
using NUnit.Framework;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World.Tests{
    public sealed class PlanetRasterLayerTests{
        private const double Radius=1737400.0;

        // 全球の格子(経度の周期と緯度の変化を持つなめらかな関数、メートル)。
        private static PlanetRasterLayer CreateGlobal(int width,int height,double longitudeFrequency=3.0){
            var heights=new float[width*height];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    double longitude=((x+0.5)/width-0.5)*2.0*Math.PI;
                    double latitude=(0.5-(y+0.5)/height)*Math.PI;
                    heights[y*width+x]=(float)(3000.0*Math.Sin(longitudeFrequency*longitude)*Math.Cos(2.0*latitude)+800.0*Math.Sin(7.0*latitude+longitude));
                }
            }
            return PlanetRasterLayer.CreateGlobal(heights,width,height);
        }

        private static LogicalVector3 Origin(double x,double y,double z){
            Assert.That(LogicalScalar.TryFromDouble(x*Radius,out LogicalScalar a),Is.True);
            Assert.That(LogicalScalar.TryFromDouble(y*Radius,out LogicalScalar b),Is.True);
            Assert.That(LogicalScalar.TryFromDouble(z*Radius,out LogicalScalar c),Is.True);
            return new LogicalVector3(a,b,c);
        }

        private static double[] Normalize(double x,double y,double z){
            double length=Math.Sqrt(x*x+y*y+z*z);
            return new[]{x/length,y/length,z/length};
        }

        // 原点の単位方向 o から tangent(接平面の変位、メートル)だけ離れた点の、原点からの球面上の相対位置 R(n−o)(倍精度で求め、float にする)。
        private static Vector3 Sphere(double[] o,double[] tangent,out double[] n){
            n=Normalize(o[0]*Radius+tangent[0],o[1]*Radius+tangent[1],o[2]*Radius+tangent[2]);
            return new Vector3((float)(Radius*(n[0]-o[0])),(float)(Radius*(n[1]-o[1])),(float)(Radius*(n[2]-o[2])));
        }

        [Test]
        public void ReadFloat32(){
            var bytes=new byte[16];
            Buffer.BlockCopy(new[]{1.5f,-2.25f,0f,1000f},0,bytes,0,16);
            float[] values=PlanetRasterLayer.ReadFloat32(bytes,2,2);
            Assert.That(values,Is.EqualTo(new[]{1.5f,-2.25f,0f,1000f}));
            Assert.Throws<ArgumentException>(()=>PlanetRasterLayer.ReadFloat32(bytes,3,2));
        }

        [Test]
        public void Delta(){
            // 緯度経度の差は、方向から直接求めた差と一致する(差が大きいとき)。
            var random=new System.Random(4);
            for(int i=0;i<200;i++){
                double[] o=Normalize(random.NextDouble()*2-1,random.NextDouble()*2-1,random.NextDouble()*2-1);
                double[] t={(random.NextDouble()*2-1)*20000,(random.NextDouble()*2-1)*20000,(random.NextDouble()*2-1)*20000};
                Vector3 sphere=Sphere(o,t,out double[] n);
                PlanetRasterLayer.Delta(new Vector3((float)o[0],(float)o[1],(float)o[2]),sphere/(float)Radius,out double longitude,out double latitude);
                double expectedLongitude=Math.Atan2(n[0],n[1])-Math.Atan2(o[0],o[1]);
                while(expectedLongitude>Math.PI){expectedLongitude-=2*Math.PI;}
                while(expectedLongitude<-Math.PI){expectedLongitude+=2*Math.PI;}
                double expectedLatitude=Math.Asin(n[2])-Math.Asin(o[2]);
                Assert.That(longitude,Is.EqualTo(expectedLongitude).Within(2e-5),"経度 "+i);
                Assert.That(latitude,Is.EqualTo(expectedLatitude).Within(2e-5),"緯度 "+i);
            }
        }

        [Test]
        public void Sample(){
            PlanetRasterLayer layer=CreateGlobal(360,180);
            {
                // 経度・緯度 0 は 4 画素の平均と一致し、経度 ±180° の継ぎ目でもなめらか
                // 経度 0・緯度 0 は、4 つの画素 (179..180, 89..90) の中央
                float center=(layer.Texel(179,89)+layer.Texel(180,89)+layer.Texel(179,90)+layer.Texel(180,90))*0.25f;
                Assert.That(layer.SampleDirection(0.0,1.0,0.0),Is.EqualTo(center).Within(1e-3));
                double before=layer.SampleDirection(Math.Sin(Math.PI-1e-6),Math.Cos(Math.PI-1e-6),0.1);
                double after=layer.SampleDirection(Math.Sin(-Math.PI+1e-6),Math.Cos(-Math.PI+1e-6),0.1);
                Assert.That(before,Is.EqualTo(after).Within(0.05),"経度の継ぎ目");
            }
            {
                // Cell の原点 + 差で求めた値が、方向から直接求めた値と一致する(粗い Cell から 1mm の Cell まで、quad の長さ 0 = mip 0)
                var random=new System.Random(8);
                foreach(double extent in new[]{200000.0,3000.0,40.0,0.5,0.001}){
                    for(int i=0;i<100;i++){
                        double[] o=Normalize(random.NextDouble()*2-1,random.NextDouble()*2-1,random.NextDouble()*0.8-0.4);
                        Assert.That(layer.TryCell(Origin(o[0],o[1],o[2]),0f,Radius,out PlanetRasterCell cell),Is.True);
                        // 原点と同じ点(差 0)と、extent 離れた点の差
                        double[] t={(random.NextDouble()*2-1)*extent,(random.NextDouble()*2-1)*extent,(random.NextDouble()*2-1)*extent};
                        Vector3 sphere=Sphere(o,t,out double[] n);
                        float height=layer.Sample(in cell,sphere,Radius,out float weight);
                        double expected=layer.SampleDirection(n[0],n[1],n[2]);
                        Assert.That(weight,Is.EqualTo(1f));
                        // 高さは float で返す(約 3000 m で刻み 2.4e-4 m)ので、丸め 1.2e-4 + 格子の傾き(約 1 m/m)× 位置の誤差(float の差 1e-7 × extent)
                        Assert.That(height,Is.EqualTo(expected).Within(3e-4+extent*2e-7),"extent "+extent+" #"+i);
                    }
                }
            }
            {
                // 1mm の間隔の点列の高さの差が、方向から直接求めた差と一致する(float の方向から緯度経度を求めると、半径 1.7×10^6 m では 0.1 m に量子化されて差が出ない)
                // 傾きが大きい格子(経度に 100 周期、約 0.1 m/m)にする。方向の量子化(0.1 m)があれば、高さに約 1e-2 m の階段が出る。
                PlanetRasterLayer steep=CreateGlobal(2880,1440,100.0);
                double[] o=Normalize(0.3,1.0,0.2);
                Assert.That(steep.TryCell(Origin(o[0],o[1],o[2]),0f,Radius,out PlanetRasterCell cell),Is.True);
                // 東向きの単位ベクトル(接平面)
                double[] east=Normalize(o[1],-o[0],0.0);
                double maximumError=0;double maximumStep=0;
                double previousExpected=0;double previousHeight=0;
                for(int k=0;k<200;k++){
                    double d=k*0.001;
                    double[] t={east[0]*d,east[1]*d,east[2]*d};
                    Vector3 sphere=Sphere(o,t,out double[] n);
                    float height=steep.Sample(in cell,sphere,Radius,out float _);
                    double expected=steep.SampleDirection(n[0],n[1],n[2]);
                    if(k>0){
                        maximumError=Math.Max(maximumError,Math.Abs((height-previousHeight)-(expected-previousExpected)));
                        maximumStep=Math.Max(maximumStep,Math.Abs(expected-previousExpected));
                    }
                    previousHeight=height;previousExpected=expected;
                }
                Assert.That(maximumStep,Is.GreaterThan(5e-5),"1mm あたりの高さの変化(傾き)があること");
                // float の高さ(約 1e3、刻み 1.2e-4 m)の丸めが 2 回入った差の誤差
                Assert.That(maximumError,Is.LessThan(5e-4));
            }
        }

        [Test]
        public void SampleMip(){
            // quad の長さが画素より大きいと mip が上がり、箱形フィルタ(2×2 の平均)の値になる
            PlanetRasterLayer layer=CreateGlobal(360,180);
            double texel=layer.TexelMeters(Radius);
            Assert.That(layer.MipFor(0f,Radius),Is.EqualTo(0));
            Assert.That(layer.MipFor((float)(texel*0.9),Radius),Is.EqualTo(0));
            Assert.That(layer.MipFor((float)(texel*1.1),Radius),Is.EqualTo(1));
            Assert.That(layer.MipFor((float)(texel*3.9),Radius),Is.EqualTo(2));
            Assert.That(layer.MipFor((float)(texel*1e9),Radius),Is.EqualTo(layer.MipCount-1));
            // mip 1 の画素 (10, 20) は mip 0 の画素 (20..21, 40..41) の 2×2 の平均。その中心(mip 0 の列 20.5、行 40.5)の緯度経度で確かめる。
            {
                double longitude=(20.5-layer.CenterColumn)/layer.ColumnsPerRadian;
                double latitude=-(40.5-layer.CenterRow)/layer.RowsPerRadian;
                double[] o={Math.Sin(longitude)*Math.Cos(latitude),Math.Cos(longitude)*Math.Cos(latitude),Math.Sin(latitude)};
                Assert.That(layer.TryCell(Origin(o[0],o[1],o[2]),(float)(texel*1.5),Radius,out PlanetRasterCell cell),Is.True);
                Assert.That(cell.Mip,Is.EqualTo(1));
                float height=layer.Sample(in cell,Vector3.zero,Radius,out float _);
                float average=(layer.Texel(20,40)+layer.Texel(21,40)+layer.Texel(20,41)+layer.Texel(21,41))*0.25f;
                Assert.That(height,Is.EqualTo(average).Within(1e-2f));
            }
        }

        [Test]
        public void Regional(){
            // 局所の格子: 端から inset の内側まで重み 0、feather で滑らかに 1 へ移り、範囲の外は重み 0・高さ 0。
            var heights=new float[64*64];
            for(int i=0;i<heights.Length;i++){heights[i]=500f;}
            // 経度 0・緯度 0 が中心、1 度あたり 16 画素(64 画素 = 4°)、端から 8 画素まで重み 0、そこから 8 画素かけて 1
            PlanetRasterLayer regional=PlanetRasterLayer.CreateRegional(heights,64,64,0.0,0.0,16.0,8f,8f);
            // mip 0 の列 c の経度(行は緯度 0 の行)
            double Longitude(double column){return (column-regional.CenterColumn)/regional.ColumnsPerRadian;}
            float HeightAt(double column,out float weight){
                double longitude=Longitude(column);
                Assert.That(regional.TryCell(Origin(Math.Sin(longitude),Math.Cos(longitude),0),0f,Radius,out PlanetRasterCell cell),Is.True);
                return regional.Sample(in cell,Vector3.zero,Radius,out weight);
            }
            Assert.That(HeightAt(31.5,out float center),Is.EqualTo(500f).Within(1e-3f));
            Assert.That(center,Is.EqualTo(1f).Within(1e-4f));
            // 端(列 0)から 12 画素(inset 8 + feather の半分): 重み 0.5
            HeightAt(12.0,out float half);
            Assert.That(half,Is.EqualTo(0.5f).Within(2e-3f));
            // 端から 8 画素以内は重み 0
            Assert.That(HeightAt(8.0,out float inset),Is.EqualTo(0f));
            Assert.That(inset,Is.EqualTo(0f));
            // 範囲の外(列 −5)
            Assert.That(HeightAt(-5.0,out float outside),Is.EqualTo(0f));
            Assert.That(outside,Is.EqualTo(0f));
        }
    }
}
