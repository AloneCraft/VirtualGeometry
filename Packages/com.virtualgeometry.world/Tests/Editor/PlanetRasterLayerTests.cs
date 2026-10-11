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
        public void DeltaNearPole(){
            // 極の近く(緯度 76°〜89.9999°、両半球)で、緯度経度の差が、方向から直接(倍精度で)求めた差と一致する。
            // 緯度を cos = √(1−z²) から求める旧式は、極で桁落ちする(このテストの入力で、緯度 89.9° で 26 m、89.97° で 61 m、89.99° で約 300 m 外れる。float の GPU では、89.99° より先で緯度が 90° ずれる)。
            // 一致の許容は、緯度も経度も 0.1 m + extent×1e-6 m(経度の差は、頂点の水平成分の大きさを掛けて m にする)。
            var random=new System.Random(11);
            double worstLatitudeMeters=0;double worstLongitudeMeters=0;
            foreach(double sign in new[]{1.0,-1.0}){
                foreach(double latitudeDegrees in new[]{76.0,80.0,85.0,89.0,89.9,89.97,89.99,89.999,89.9999}){
                    foreach(double extent in new[]{1.0,100.0,1000.0,100000.0}){
                        double lat=sign*latitudeDegrees*Math.PI/180.0;
                        double lon=(random.NextDouble()*2-1)*Math.PI;
                        double[] o={Math.Sin(lon)*Math.Cos(lat),Math.Cos(lon)*Math.Cos(lat),Math.Sin(lat)};
                        // 原点は float の方向(Cell の原点の単位方向 o も float)。点は、原点の接平面の変位 extent(m)だけ離す。
                        var originFloat=new Vector3((float)o[0],(float)o[1],(float)o[2]);
                        double[] originExact={originFloat.x,originFloat.y,originFloat.z};
                        double[] t={(random.NextDouble()*2-1)*extent,(random.NextDouble()*2-1)*extent,(random.NextDouble()*2-1)*extent};
                        Vector3 sphere=Sphere(originExact,t,out double[] n);
                        PlanetRasterLayer.Delta(originFloat,sphere/(float)Radius,out double longitude,out double latitude);
                        // 基準: 点 n と原点 o(float の値そのもの)の緯度経度の差。asin の代わりに atan2(z, 水平成分) で、極でも精度を保つ。
                        double originHorizontal=Math.Sqrt(originExact[0]*originExact[0]+originExact[1]*originExact[1]);
                        double pointHorizontal=Math.Sqrt(n[0]*n[0]+n[1]*n[1]);
                        double expectedLatitude=Math.Atan2(n[2],pointHorizontal)-Math.Atan2(originExact[2],originHorizontal);
                        double expectedLongitude=Math.Atan2(n[0],n[1])-Math.Atan2(originExact[0],originExact[1]);
                        while(expectedLongitude>Math.PI){expectedLongitude-=2*Math.PI;}
                        while(expectedLongitude<-Math.PI){expectedLongitude+=2*Math.PI;}
                        // Delta の入力 sphere は、倍精度の n から作った R·(n−o) を float に丸めた値なので、その丸め(extent の相対 6e-8 ほど)を超える差は許す(extent×1e-6 m)。
                        double latitudeMeters=Math.Abs(latitude-expectedLatitude)*Radius;
                        worstLatitudeMeters=Math.Max(worstLatitudeMeters,latitudeMeters);
                        string where="lat="+sign*latitudeDegrees+" extent="+extent;
                        Assert.That(latitudeMeters,Is.LessThan(0.1+extent*1e-6),"緯度 "+where+" 差 "+latitudeMeters+"m");
                        double longitudeDifference=Math.Abs(longitude-expectedLongitude);
                        if(longitudeDifference>Math.PI){longitudeDifference=2*Math.PI-longitudeDifference;}
                        double longitudeMeters=longitudeDifference*pointHorizontal*Radius;
                        worstLongitudeMeters=Math.Max(worstLongitudeMeters,longitudeMeters);
                        Assert.That(longitudeMeters,Is.LessThan(0.1+extent*1e-6),"経度 "+where+" 差 "+longitudeMeters+"m");
                    }
                }
            }
            Assert.That(worstLatitudeMeters,Is.LessThan(1.0));Assert.That(worstLongitudeMeters,Is.LessThan(1.0));
        }

        [Test]
        public void DeltaAtPole(){
            {
                // 原点が極そのもの(階層 0 の根)でも NaN にならず、経度の差は頂点の経度、緯度の差は −(極からの角度)。
                var north=new Vector3(0f,0f,1f);
                foreach(double lon in new[]{0.3,-2.0,3.0}){
                    double angle=0.01;
                    // 点: 極から angle だけ離れた、経度 lon の方向。
                    double nx=Math.Sin(angle)*Math.Sin(lon);double ny=Math.Sin(angle)*Math.Cos(lon);double nz=Math.Cos(angle);
                    var eps=new Vector3((float)nx,(float)ny,(float)(nz-1.0));
                    PlanetRasterLayer.Delta(north,eps,out double longitude,out double latitude);
                    Assert.That(double.IsNaN(longitude)||double.IsNaN(latitude),Is.False);
                    Assert.That(longitude,Is.EqualTo(lon).Within(1e-5),"経度");
                    Assert.That(latitude,Is.EqualTo(-angle).Within(1e-6),"緯度");
                }
            }
            {
                // 原点が極のごく近く(float の o.z が 1.0)で、点が原点と同じ位置のとき、差は 0。
                var origin=new Vector3(1e-5f,2e-5f,1f);
                PlanetRasterLayer.Delta(origin,Vector3.zero,out double longitude,out double latitude);
                Assert.That(longitude,Is.EqualTo(0.0).Within(1e-12));
                Assert.That(latitude,Is.EqualTo(0.0).Within(1e-12));
            }
        }

        // 極(経度が定まらない所)で、先頭の行と最後の行が列ごとにばらつく全球の格子(LOLA の極の風車状のばらつきの縮小版)。
        // 一般の行は、地点の方向の滑らかな関数 g·R·(nx+2ny)+500·nz(nz=sin(緯度)。北極で +500、南極で −500)、先頭・最後の行には経度ごとの ±spread の揺れを足す。
        private static PlanetRasterLayer CreateWindmill(int width,int height,double gradient,float spread){
            var heights=new float[width*height];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    double longitude=((x+0.5)/width-0.5)*2.0*Math.PI;
                    double latitude=(0.5-(y+0.5)/height)*Math.PI;
                    double nx=Math.Sin(longitude)*Math.Cos(latitude);double ny=Math.Cos(longitude)*Math.Cos(latitude);double nz=Math.Sin(latitude);
                    double value=gradient*Radius*(nx+2.0*ny)+500.0*nz;
                    if(y==0||y==height-1){value+=spread*Math.Sin(3.0*longitude+y);}
                    heights[y*width+x]=(float)value;
                }
            }
            return PlanetRasterLayer.CreateGlobal(heights,width,height);
        }

        [Test]
        public void PoleCap(){
            // 極の近くの高さが、経度によらず極の値に収束して連続になる(先頭の行より北を先頭の行へ clamp すると、極の近くの高さが経度で何 m も変わる)。
            // 北極と南極。mip 0 の Cell(quadLength 0)に、mip が上がった Cell(quadLength が 3 画素)と shift ≠ 0 の確認を足す。
            PlanetRasterLayer layer=CreateWindmill(256,128,0.02,40f);
            foreach(double sign in new[]{1.0,-1.0}){
                {
                    // 極の値は先頭(最後)の行の平均。
                    double expected=0;int row=0;if(sign<0){row=layer.Height-1;}
                    for(int x=0;x<layer.Width;x++){expected+=layer.Texel(x,row);}
                    expected/=layer.Width;
                    float pole=layer.PoleNorth(0);if(sign<0){pole=layer.PoleSouth(0);}
                    Assert.That(pole,Is.EqualTo(expected).Within(1e-3),"極の値 "+sign);
                }
                {
                    // 極そのもの(経度を決める成分がほぼ 0)と、極から 1 m・100 m・1 km(半画素 = 約 21 km の内。128 行・半径 1,737.4 km)で、経度を回しても高さは極からの距離に比例した範囲でしか変わらない。
                    double pole=layer.SampleDirection(0.0,0.0,sign);
                    double halfTexel=Radius*Math.PI/layer.Height*0.5;
                    foreach(double distance in new[]{1.0,100.0,1000.0}){
                        double low=double.MaxValue,high=double.MinValue;
                        for(int k=0;k<64;k++){
                            double lon=k/64.0*2.0*Math.PI;
                            double angle=distance/Radius;
                            double value=layer.SampleDirection(Math.Sin(angle)*Math.Sin(lon),Math.Sin(angle)*Math.Cos(lon),sign*Math.Cos(angle));
                            low=Math.Min(low,value);high=Math.Max(high,value);
                        }
                        // 先頭の行の ring の振れ幅(傾き 0.02 の局所的な平面 + 揺れ 40 m)に、距離/半画素 を掛けた範囲。極の値からの差もその範囲。
                        double bound=(0.02*halfTexel*2.0*2.24+80.0)*(distance/halfTexel)+0.01;
                        Assert.That(high-low,Is.LessThan(bound),"経度で変わる幅 "+sign+" 距離 "+distance+"m: "+(high-low)+"m");
                        Assert.That(Math.Abs(low-pole),Is.LessThan(bound),"極からの差 "+sign+" 距離 "+distance+"m");
                        Assert.That(Math.Abs(high-pole),Is.LessThan(bound),"極からの差 "+sign+" 距離 "+distance+"m");
                    }
                    // 1mm でも同じ(高さは極の値の ±1mm の傾き分)。
                    double near=layer.SampleDirection(Math.Sin(1e-3/Radius)*Math.Sin(1.0),Math.Sin(1e-3/Radius)*Math.Cos(1.0),sign*Math.Cos(1e-3/Radius));
                    Assert.That(Math.Abs(near-pole),Is.LessThan(0.01),"1mm の高さの変わり "+sign);
                }
                {
                    // mip が上がった Cell(quad の長さが 3 画素)は、その mip の極の値(mip 0 の先頭(最後)の 2^mip 行の平均)を持ち、
                    // 極の原点で shift を引いた極の値を返す(極冠の t = 0)。mip の極の値でなく mip 0 の値を渡す不具合を検出する。
                    Assert.That(layer.TryCell(Origin(0.0,0.0,sign),(float)(layer.TexelMeters(Radius)*3.0),Radius,out PlanetRasterCell coarse),Is.True);
                    Assert.That(coarse.Mip,Is.GreaterThanOrEqualTo(1));
                    int rowsInMip=1<<coarse.Mip;
                    double sum=0;
                    for(int r=0;r<rowsInMip;r++){
                        int row=r;if(sign<0){row=layer.Height-1-r;}
                        for(int x=0;x<layer.Width;x++){sum+=layer.Texel(x,row);}
                    }
                    double expectedPole=sum/(rowsInMip*layer.Width);
                    float cellPole=coarse.PoleNorth;if(sign<0){cellPole=coarse.PoleSouth;}
                    Assert.That(cellPole,Is.EqualTo(expectedPole).Within(1e-3),"mip の極の値 "+sign);
                    float layerPole=layer.PoleNorth(coarse.Mip);if(sign<0){layerPole=layer.PoleSouth(coarse.Mip);}
                    Assert.That(cellPole,Is.EqualTo(layerPole),"Cell の極の値は層の mip の値");
                    float shift=123.5f;
                    float atPole=layer.Sample(in coarse,Vector3.zero,Radius,shift,out float _);
                    Assert.That(atPole,Is.EqualTo(expectedPole-shift).Within(2e-3),"極で shift を引いた極の値 "+sign);
                }
                {
                    // Cell の原点が極のごく近く・極そのもの・極から 100 m で、CPU の Sample(原点 + 差)が SampleDirection(方向から直接)と一致する。
                    var random=new System.Random(21);
                    foreach(double polarDistance in new[]{0.0,0.3,100.0,2000.0}){
                        double angle=polarDistance/Radius;
                        double lonOrigin=random.NextDouble()*2.0*Math.PI;
                        double[] o={Math.Sin(angle)*Math.Sin(lonOrigin),Math.Sin(angle)*Math.Cos(lonOrigin),sign*Math.Cos(angle)};
                        var originFloat=new Vector3((float)o[0],(float)o[1],(float)o[2]);
                        // 原点は float の方向そのもの(TryCell が正規化する)。
                        Assert.That(layer.TryCell(Origin(originFloat.x,originFloat.y,originFloat.z),0f,Radius,out PlanetRasterCell cell),Is.True);
                        for(int i=0;i<40;i++){
                            double[] tangent={(random.NextDouble()*2-1)*5.0,(random.NextDouble()*2-1)*5.0,(random.NextDouble()*2-1)*5.0};
                            double[] originExact={originFloat.x,originFloat.y,originFloat.z};
                            Vector3 sphere=Sphere(originExact,tangent,out double[] n);
                            float height=layer.Sample(in cell,sphere,Radius,out float _);
                            double expected=layer.SampleDirection(n[0],n[1],n[2]);
                            // 極の近くでは経度が速く変わるので、位置の誤差 1e-4 m が高さにかかる傾き(極冠で 0.02+揺れ/半画素)を許す。
                            Assert.That(height,Is.EqualTo(expected).Within(0.05),"原点 極から "+polarDistance+"m "+sign+" #"+i);
                        }
                    }
                }
            }
            {
                // 局所的に平面の場なら、極冠の中でも平面を再現する(clamp では ring の値になって、極の近くの勾配が 0 になる)。
                // 揺れなし・傾きのある平面: 高さ = g·R·(nx+2ny)+500·nz。北極の P は 500(g·R·(nx+2ny) の ring の平均は 0)、半画素の ring の平均も 500。
                PlanetRasterLayer plane=CreateWindmill(512,256,0.02,0f);
                double halfTexel=Radius*Math.PI/plane.Height*0.5;
                double distance=halfTexel*0.4;
                double angle=distance/Radius;
                double lon=1.0;
                double value=plane.SampleDirection(Math.Sin(angle)*Math.Sin(lon),Math.Sin(angle)*Math.Cos(lon),Math.Cos(angle));
                double truth=0.02*Radius*(Math.Sin(angle)*Math.Sin(lon)+2.0*Math.Sin(angle)*Math.Cos(lon))+500.0*Math.Cos(angle);
                // 北緯 90° の 500·nz は 500 が極の値。ring の経度の依存 0.02·R·sin(angle')·(...)は、極冠の線形補間で正しい勾配になる。
                Assert.That(value-500.0,Is.EqualTo(truth-500.0).Within(0.1),"平面の再現(clamp なら約 250 m ずれる)");
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
            {
                // 高さから shift(Cell の原点を持ち上げた距離)を引いた値を返す。float にする前に引くので、|高さ| が数 km でも 1 µm の桁で一致する
                PlanetRasterLayer steep=CreateGlobal(2880,1440,100.0);
                double[] o=Normalize(0.3,1.0,0.2);
                Assert.That(steep.TryCell(Origin(o[0],o[1],o[2]),0f,Radius,out PlanetRasterCell cell),Is.True);
                float shift=(float)steep.SampleDirection(o[0],o[1],o[2]);
                Assert.That(Math.Abs(shift),Is.GreaterThan(500f),"持ち上げた距離が大きい場所で確かめること");
                double[] east=Normalize(o[1],-o[0],0.0);
                double maximumShiftedError=0;double maximumUnshiftedError=0;double maximumSecondDifference=0;
                float previous=0;float beforePrevious=0;
                for(int k=0;k<200;k++){
                    double d=k*0.001;
                    double[] t={east[0]*d,east[1]*d,east[2]*d};
                    Vector3 sphere=Sphere(o,t,out double[] n);
                    float shifted=steep.Sample(in cell,sphere,Radius,shift,out float weight);
                    float unshifted=steep.Sample(in cell,sphere,Radius,out float _);
                    Assert.That(weight,Is.EqualTo(1f));
                    double expected=steep.SampleDirection(n[0],n[1],n[2]);
                    maximumShiftedError=Math.Max(maximumShiftedError,Math.Abs(shifted-(expected-shift)));
                    maximumUnshiftedError=Math.Max(maximumUnshiftedError,Math.Abs(unshifted-expected));
                    if(k>=2){maximumSecondDifference=Math.Max(maximumSecondDifference,Math.Abs(shifted-2*previous+beforePrevious));}
                    beforePrevious=previous;previous=shifted;
                }
                // shift を引いた値は 1 m 以下(float の刻み 1e-7 m)なので、方向から直接求めた値 − shift と 2e-5 m 以内で一致する(shift を引かない float の高さは約 3,000 m で刻み 2.4e-4 m)
                Assert.That(maximumShiftedError,Is.LessThan(2e-5));
                // 1mm 間隔の点列の 2 階差分(傾きが一定なら 0)は、float の丸めの桁に収まる
                Assert.That(maximumSecondDifference,Is.LessThan(1e-5));
                Assert.That(maximumUnshiftedError,Is.LessThan(5e-4));
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
