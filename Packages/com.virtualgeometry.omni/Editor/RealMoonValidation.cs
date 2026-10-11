using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualGeometry.World;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 月の実データ(LOLA 全球標高と、0N/0E の SLDEM2015 の局所標高。Omni のデモのデータをコピーした LocalData/OmniDemo、git 管理外)で月を描く検証。
    /// - 格子の統計と、CPU の高さ(Cell の球面上の原点 + 差)が、方向から直接求めた双線形補間と一致すること(全球と、局所の重み付きの両方)
    /// - GPU が生成した頂点が、CPU の式(PlanetRasterLayer.Sample)と一致すること
    /// - 1mm の Cell の頂点の高さの 2 階差分が、方向の量子化(約 0.1 m)の階段を持たないこと
    /// - 画像(月全体、地表の近く、SLDEM の中心の地上)と、月全体で穴が無いこと
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.RealMoonValidation.Run
    /// </summary>
    public static class RealMoonValidation{
        private const string DataFolder="LocalData/OmniDemo";
        private const string GlobalFile="lola-5760x2880-meters.bytes";
        private const string RegionalFile="sldem-0n-0e-344x344-meters.bytes";
        private const double MoonRadius=1737400.0;
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        // この階層以上の Cell を、頂点の精度の厳しい許容値で比べる(原点を持ち上げた Cell。持ち上げない |高さ| < 1 km の Cell も、float の刻みは 61 µm 以下)。
        private const int LiftedLevel=14;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static string results;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static PlanetTerrainSource moon;
        private static PlanetRasterLayer globalLayer;
        private static PlanetRasterLayer regionalLayer;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        private static int onlySite=-1;
        // true なら、実データ(LocalData)の代わりに、合成の高起伏の格子(−7,300 m〜+9,000 m、極で連続)を使う(データの無い環境でも走る)。
        private static bool synthetic;
        // true なら、場所を極の近く(緯度 ±80°〜±90°)にする。極の近くは緯度経度の格子の特異点なので、GPU と CPU の頂点の比較から外していた(|緯度| > 75°)のを、全 Cell で比べる。
        private static bool polar;
        // 実データ(LOLA)で極の近くを見る実行。LOLA の極の行(行 0)は列ごとにばらつき(北 −80〜+65 m、南 +342〜+1,431 m)、隣の列と北で 27 m、南で 306 m 違う
        // (1 列は極から 947 m の輪で約 1 m)ので、極冠の外周に崖が残る。そこでは float の位置の丸め(約 0.1 mm)が、崖の勾配(数百)倍の高さの違いになり、合成の滑らかな場より緩い許容値が要る。
        private static bool RealPolar=>polar&&!synthetic;

        public static void RunSouth(){
            onlySite=3;synthetic=false;polar=false;
            Begin("TestResults/Planet/RealMoonSouth");
        }

        public static void Run(){
            onlySite=-1;synthetic=false;polar=false;
            Begin("TestResults/Planet/RealMoon");
        }

        /// <summary>
        /// 実データ(LOLA)で、極の近く(経度 0 と −172°、緯度 ±{80, 85, 89, 89.9, 89.969, 90}°)を比べる。GPU と CPU の頂点の差、描画頂点と CPU の地表の高さの差、非有限の頂点を見る。
        /// 実データの極は列ごとに値がばらつく(北 145 m、南 1,090 m の幅)ので、1 mm の 2 階差分は記録だけで、合否に使わない。結果は TestResults/Planet/RealMoonPolar。
        /// </summary>
        public static void RunPolar(){
            onlySite=-1;synthetic=false;polar=true;
            Begin("TestResults/Planet/RealMoonPolar");
        }

        /// <summary>合成の高起伏(極で連続)で、同じ極の近くの場所を比べる。滑らかな場なので、1 mm の 2 階差分も合否に使う。結果は TestResults/Planet/SyntheticPolar。</summary>
        public static void RunPolarSynthetic(){
            onlySite=-1;synthetic=true;polar=true;
            Begin("TestResults/Planet/SyntheticPolar");
        }

        /// <summary>
        /// 合成の高起伏の月(半径 1,737.4 km、高さ −7,300 m〜+9,000 m、勾配は最大約 0.3、極で連続)で、同じ検証をする(実データが無くても走る)。
        /// 場所は、格子の最大・最小の位置と、経度・緯度 0。結果は TestResults/Planet/SyntheticRelief。
        /// </summary>
        public static void RunSynthetic(){
            onlySite=-1;synthetic=true;polar=false;
            Begin("TestResults/Planet/SyntheticRelief");
        }

        private static void Begin(string output){
            results=output;
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(results);
            Application.logMessageReceived+=Record;
            try{
                Setup();
                steps=Steps();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string condition,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                if(condition.Contains("[OmniConversion] ValidatedOnly")||condition.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition);
            }
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("RealMoon validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            regionalLayer=null;
            if(synthetic){
                globalLayer=PlanetRasterLayer.CreateGlobal(SyntheticHeights(SyntheticWidth,SyntheticHeight),SyntheticWidth,SyntheticHeight);
            }else{
                string root=Path.GetDirectoryName(Application.dataPath);
                string globalPath=Path.Combine(root,DataFolder,GlobalFile);
                string regionalPath=Path.Combine(root,DataFolder,RegionalFile);
                if(!File.Exists(globalPath)||!File.Exists(regionalPath)){
                    throw new FileNotFoundException("実データがありません。Omni のデモ(Assets/Omni/Demos/PlanetSurface/Data)の "+GlobalFile+" と "+RegionalFile+" を "+DataFolder+" へコピーしてください(git 管理外)。合成データの検証は RunSynthetic。");
                }
                globalLayer=PlanetRasterLayer.CreateGlobal(PlanetRasterLayer.ReadFloat32(File.ReadAllBytes(globalPath),5760,2880),5760,2880);
                // SLDEM: 344×344、512 画素/度、中心は 0N/0E。±9.5 km(約 ±(9.5km/59m)=161 画素)の内側を使う。端から 14 画素(約 0.8 km)の内側まで重み 0、そこから 26 画素(約 1.5 km)で 1。
                regionalLayer=PlanetRasterLayer.CreateRegional(PlanetRasterLayer.ReadFloat32(File.ReadAllBytes(regionalPath),344,344),344,344,0.0,0.0,512.0,14f,26f);
            }

            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var body=new GameObject("Moon");body.SetActive(false);
            body.AddComponent<global::Omni.Logical.Transform>();
            moon=body.AddComponent<PlanetTerrainSource>();
            body.AddComponent<OmniPlanetPlacement>();
            // 手続きの高さは 0(実データだけ)。階層は 1 quad が 1mm になる 28 まで。
            MoonBallSceneBuilder.Configure(moon,(float)MoonRadius,0f,3800f,1,32,4096,64,true);
            HeightShiftOverride.Apply(moon);
            VertexHashBaseline.Begin("gpu="+SystemInfo.graphicsDeviceName+";api="+SystemInfo.graphicsDeviceType+";unity="+Application.unityVersion+";shift="+HeightShiftOverride.Describe()+";data="+DatasetName());
            report.Add("heightShiftThreshold: "+HeightShiftOverride.Describe()+" (component "+moon.HeightShiftThreshold.ToString("G",CultureInfo.InvariantCulture)+")");
            moon.Material=AssetDatabase.LoadAssetAtPath<Material>(MoonBallSceneBuilder.MoonMaterialPath);
            moon.SetRasters(globalLayer,regionalLayer);
            body.SetActive(true);

            var cameraObject=new GameObject("Main Camera");cameraObject.tag="MainCamera";
            camera=cameraObject.AddComponent<Camera>();
            camera.fieldOfView=50;camera.nearClipPlane=0.3f;camera.farClipPlane=100000;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(1,0,1,1);
            camera.allowHDR=false;camera.allowMSAA=false;
            cameraObject.AddComponent<global::Omni.Logical.Transform>();
            cameraLogical=cameraObject.GetComponent<global::Omni.Logical.Transform>();
            global::Omni.RadialCamera radial=cameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            var sun=new GameObject("Sun");
            Light light=sun.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.3f;light.color=new Color(1f,0.96f,0.9f);light.shadows=LightShadows.None;
            sun.transform.rotation=Quaternion.Euler(35f,-40f,0f);
            RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.03f,0.03f,0.04f);
            RenderSettings.skybox=null;
        }

        private const int SyntheticWidth=2880;
        private const int SyntheticHeight=1440;
        private const float SyntheticMinimum=-7300f;
        private const float SyntheticMaximum=9000f;

        private static string DatasetName(){
            string suffix="";
            if(polar){suffix="+polar";}
            if(synthetic){return "synthetic"+SyntheticWidth+"x"+SyntheticHeight+suffix;}
            return "LOLA5760x2880+SLDEM344x344"+suffix;
        }

        // 合成の高さ(m)。方向 n=(sin λ cos φ, cos λ cos φ, sin φ) の滑らかな関数なので極で連続。−7,300 m と +9,000 m に格子の中で届くよう、最小・最大に合わせて線形に写す。
        // 勾配は、k=60 で最大約 0.3(16,300 m × 78 / 1,737 km)。経度の周期 3 と緯度の変化を持つ(全球の格子の座標の規約は PlanetRasterLayer と同じ)。
        private static float[] SyntheticHeights(int width,int height){
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

        // 検証の場所(経度、緯度。度)。実データは、SLDEM の中心、静かの海の周り、ティコの周り、南極エイトケン盆地の近く。
        // 合成は、格子の最大の位置、最小の位置、経度・緯度 0、その中間の高さ(勾配が大きい所)。
        private static double[][] Sites(){
            if(polar){
                var list=new List<double[]>();
                foreach(double sign in new[]{1.0,-1.0}){
                    foreach(double latitude in new[]{80.0,85.0,89.0,89.9,89.969,90.0}){
                        foreach(double longitude in new[]{0.0,-172.0}){list.Add(new[]{longitude,sign*latitude});}
                    }
                }
                return list.ToArray();
            }
            if(!synthetic){
                return new[]{
                    new[]{0.0,0.0},            // SLDEM の中心(局所の Override の中)
                    new[]{23.47,0.67},         // 静かの海の周り
                    new[]{-11.2,-43.3},        // ティコの周り(高地のクレーター)
                    new[]{-172.0,-70.0},       // 南極エイトケン盆地の近く(最も低い所の周り)
                };
            }
            int columnMin=0,rowMin=0,columnMax=0,rowMax=0;
            double minimum=double.MaxValue;double maximum=double.MinValue;
            for(int r=0;r<globalLayer.Height;r++){
                for(int c=0;c<globalLayer.Width;c++){
                    float value=globalLayer.Texel(c,r);
                    if(value<minimum){minimum=value;columnMin=c;rowMin=r;}
                    if(value>maximum){maximum=value;columnMax=c;rowMax=r;}
                }
            }
            double LongitudeOf(int c){return ((c+0.5)/globalLayer.Width-0.5)*360.0;}
            double LatitudeOf(int r){return (0.5-(r+0.5)/globalLayer.Height)*180.0;}
            // 勾配が大きい所: 最大の位置から経度 1.5°、緯度 1° ずらした点(極から遠い)。
            return new[]{
                new[]{LongitudeOf(columnMax),LatitudeOf(rowMax)},
                new[]{LongitudeOf(columnMin),LatitudeOf(rowMin)},
                new[]{0.0,0.0},
                new[]{LongitudeOf(columnMax)+1.5,Math.Max(-60.0,Math.Min(60.0,LatitudeOf(rowMax)-1.0))},
            };
        }

        // 月面の経度・緯度(度)の単位方向(Omni の規約: 経度 = atan2(x, y)、緯度 = atan2(z, √(x²+y²)))。
        private static double3 Direction(double longitudeDegrees,double latitudeDegrees){
            double lon=longitudeDegrees*Math.PI/180.0;double lat=latitudeDegrees*Math.PI/180.0;
            return new double3(Math.Sin(lon)*Math.Cos(lat),Math.Cos(lon)*Math.Cos(lat),Math.Sin(lat));
        }

        private static LogicalVector3 UnitLogical(double3 direction){
            if(!LogicalVector3.TryFromFloat3(new float3((float)direction.x,(float)direction.y,(float)direction.z),out LogicalVector3 rough)||!LogicalVector3.TryNormalize(rough,out LogicalVector3 unit)){
                throw new InvalidOperationException("方向を Logical にできません。");
            }
            return unit;
        }

        // 月面の点(半径 R の球面上、Logical)の地表の高さ(CPU、Logical)。
        private static LogicalScalar SurfaceHeight(double3 direction){
            LogicalVector3 unit=UnitLogical(direction);
            if(!LogicalVector3.TryScale(in unit,moon.Selector.LogicalRadius,out LogicalVector3 onSphere)||!moon.TrySampleSurface(in onSphere,out LogicalScalar height,out LogicalScalar _)){
                throw new InvalidOperationException("地表の高さを求められません。");
            }
            return height;
        }

        // 経度・緯度の地表の上 altitude の高さに camera を置き、北向き(heading 0)から heading 度回し、水平から pitch 度下を向く。
        private static void PlaceCamera(double longitudeDegrees,double latitudeDegrees,double altitude,double headingDegrees,double pitchDegrees){
            double3 d=Direction(longitudeDegrees,latitudeDegrees);
            LogicalScalar height=SurfaceHeight(d);
            LogicalVector3 unit=UnitLogical(d);
            if(!LogicalScalar.TryFromDouble(altitude,out LogicalScalar above)||
                !LogicalScalar.TryAdd(moon.Selector.LogicalRadius,in height,out LogicalScalar a)||!LogicalScalar.TryAdd(in a,in above,out LogicalScalar distance)||
                !LogicalVector3.TryScale(in unit,in distance,out LogicalVector3 position)){
                throw new InvalidOperationException("camera の位置を Logical にできません。");
            }
            double lon=longitudeDegrees*Math.PI/180.0;double lat=latitudeDegrees*Math.PI/180.0;
            Vector3 up=new Vector3((float)d.x,(float)d.y,(float)d.z);
            Vector3 east=new Vector3((float)Math.Cos(lon),(float)(-Math.Sin(lon)),0f);
            Vector3 north=new Vector3((float)(-Math.Sin(lon)*Math.Sin(lat)),(float)(-Math.Cos(lon)*Math.Sin(lat)),(float)Math.Cos(lat));
            double heading=headingDegrees*Math.PI/180.0;double pitch=pitchDegrees*Math.PI/180.0;
            Vector3 horizontal=north*(float)Math.Cos(heading)+east*(float)Math.Sin(heading);
            Vector3 forward=horizontal*(float)Math.Cos(pitch)-up*(float)Math.Sin(pitch);
            Quaternion rotation=Quaternion.LookRotation(forward.normalized,up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){throw new InvalidOperationException("camera の回転を Logical にできません。");}
            cameraLogical.LocalPosition=position;cameraLogical.LocalRotation=logical;cameraLogical.SyncUnityTransform();
        }

        private static IEnumerator Steps(){
            string regionalText="none";
            if(regionalLayer!=null){regionalText=regionalLayer.Width+"x"+regionalLayer.Height+" (texel "+regionalLayer.TexelMeters(MoonRadius).ToString("F1",CultureInfo.InvariantCulture)+"m, mips "+regionalLayer.MipCount+")";}
            report.Add("data: "+DatasetName()+" global "+globalLayer.Width+"x"+globalLayer.Height+" (texel "+globalLayer.TexelMeters(MoonRadius).ToString("F0",CultureInfo.InvariantCulture)+"m, mips "+globalLayer.MipCount+") regional "+regionalText);
            int waited=0;
            while(moon.Selector==null){if(waited++>100){throw new InvalidOperationException("月が有効になりません。");}yield return null;}
            GlobalStatistics();
            CheckAgainstDirect();
            // 画像と GPU の確認
            {
                // 月全体(高さ 2.5R)
                PlaceCamera(10.0,5.0,MoonRadius*2.5,0.0,90.0);
                foreach(object step in Converge("Whole")){yield return step;}
                Texture2D image=Capture();Save("Whole",image);
                int holes=CountHoles(image,MoonRadius*2.5);
                report.Add("whole moon: holes="+holes+" cells="+moon.LastSelected.Count);
                if(holes!=0){failures.Add("月全体: 穴が "+holes+" 画素ありました。");}
                CheckGpu("Whole",float.MaxValue);
            }
            double[][] sites=Sites();
            if(onlySite>=0){sites=new[]{sites[onlySite]};}
            foreach(double[] site in sites){
                // 極の近くは、緯度 89.969° と 90° が小数 1 桁で同じ名前(90.0)になるので、小数 3 桁にする(画像・ハッシュの基準・報告の名前が重ならないように)。
                string latitudeFormat="F1";
                if(polar){latitudeFormat="F3";}
                string name="Site"+site[0].ToString("F1",CultureInfo.InvariantCulture)+"_"+site[1].ToString(latitudeFormat,CultureInfo.InvariantCulture);
                // 上空 150 km から斜めに見下ろす。
                PlaceCamera(site[0],site[1],150000.0,0.0,35.0);
                if(onlySite>=0){DiagnoseChain(site[0],site[1]);}
                foreach(object step in Converge(name+"High")){yield return step;}
                if(onlySite>=0){DiagnoseChain(site[0],site[1]);}
                Texture2D high=Capture();Save(name+"High",high);
                CheckGpu(name+" high",3000f);
                // 地表の近く(10 m から 0.17 m)
                foreach(double altitude in new[]{1000.0,10.0,0.17}){
                    PlaceCamera(site[0],site[1],altitude,0.0,35.0);
                    foreach(object step in Converge(name+"Alt"+altitude)){yield return step;}
                    if(altitude==10.0||altitude==0.17){Save(name+"Alt"+altitude.ToString(CultureInfo.InvariantCulture),Capture());}
                    CheckGpu(name+" altitude "+altitude+"m",float.MaxValue);
                }
                CheckMillimeter(name,site[0],site[1]);
            }
            report.Add("pool: used="+moon.UsedSlots+"/"+moon.PoolCapacity+" evictions="+moon.Evictions+" starved="+moon.StarvedRequests+" lastError="+(moon.LastError??"(none)")+" invalidBounds="+moon.InvalidBounds);
            if(polar&&moon.InvalidBounds>0){failures.Add("極の近くで、非有限の頂点を持つ Cell が "+moon.InvalidBounds+" 件ありました。");}
            float averageMicroseconds=0f;
            if(moon.HeightShiftEvaluations>0){averageMicroseconds=moon.HeightShiftMilliseconds*1000f/moon.HeightShiftEvaluations;}
            report.Add("height shift: evaluations="+moon.HeightShiftEvaluations+" applied="+moon.HeightShiftApplied+" totalMilliseconds="+moon.HeightShiftMilliseconds.ToString("F1",CultureInfo.InvariantCulture)+
                " average="+averageMicroseconds.ToString("F1",CultureInfo.InvariantCulture)+"us/evaluation");
            if(moon.LastError!=null){failures.Add("設定エラー: "+moon.LastError);}
        }

        // 診断: camera の真下の Cell の、階層 1 から 14 までの鎖が、生成済みか・選ばれたか。
        private static void DiagnoseChain(double longitude,double latitude){
            double3 d=Direction(longitude,latitude);
            Vector3 direction=new Vector3((float)d.x,(float)d.y,(float)d.z);
            string text="chain at lon "+longitude+" lat "+latitude+": selected="+moon.LastSelected.Count+" used="+moon.UsedSlots+" pending="+moon.PendingGeneration+" |";
            var selected=new HashSet<PlanetCellKey>(moon.LastSelected);
            for(int level=0;level<=14;level++){
                PlanetCellKey key=PlanetCellGeometry.KeyAt(direction,level);
                moon.Selector.Bounds(in key,out Vector3 offset,out float radius);
                bool hasBounds=moon.TryGetBounds(in key,out Vector3 actualOffset,out float actualRadius);
                text+=" L"+level+":"+(moon.IsReady(in key)?"ready":"-")+(selected.Contains(key)?"*":"")+(hasBounds?" b="+actualRadius.ToString("G3",CultureInfo.InvariantCulture)+"/"+radius.ToString("G3",CultureInfo.InvariantCulture):"")+" |";
            }
            report.Add(text);
        }

        // 格子の統計: |高さ| の最大と、その位置。
        private static void GlobalStatistics(){
            double minimum=double.MaxValue,maximum=double.MinValue;int minC=0,minR=0,maxC=0,maxR=0;
            for(int r=0;r<globalLayer.Height;r++){
                for(int c=0;c<globalLayer.Width;c++){
                    float value=globalLayer.Texel(c,r);
                    if(value<minimum){minimum=value;minC=c;minR=r;}
                    if(value>maximum){maximum=value;maxC=c;maxR=r;}
                }
            }
            double LongitudeOf(int c){return ((c+0.5)/globalLayer.Width-0.5)*360.0;}
            double LatitudeOf(int r){return (0.5-(r+0.5)/globalLayer.Height)*180.0;}
            report.Add("LOLA range: min="+minimum.ToString("F0",CultureInfo.InvariantCulture)+"m at lon "+LongitudeOf(minC).ToString("F1",CultureInfo.InvariantCulture)+" lat "+LatitudeOf(minR).ToString("F1",CultureInfo.InvariantCulture)+
                "  max="+maximum.ToString("F0",CultureInfo.InvariantCulture)+"m at lon "+LongitudeOf(maxC).ToString("F1",CultureInfo.InvariantCulture)+" lat "+LatitudeOf(maxR).ToString("F1",CultureInfo.InvariantCulture)+
                "  moon selection amplitude="+moon.RasterMaximumHeight.ToString("F0",CultureInfo.InvariantCulture)+"m");
            if(synthetic){
                if(minimum>SyntheticMinimum+1||maximum<SyntheticMaximum-1){failures.Add("合成の格子の範囲が想定("+SyntheticMinimum+"〜"+SyntheticMaximum+")と違います: "+minimum+" 〜 "+maximum);}
            }else if(minimum>-8000||maximum<9000){
                failures.Add("LOLA の範囲が想定(約 −9 km〜+10.8 km)と違います: "+minimum+" 〜 "+maximum);
            }
        }

        // CPU の TrySampleSurface(Cell の原点 + 差)が、方向から直接求めた双線形補間(全球 + 局所の重み付き)と一致すること。
        private static void CheckAgainstDirect(){
            var random=new System.Random(21);
            double worst=0;int count=0;double worstRegional=0;int regionalCount=0;
            for(int i=0;i<3000;i++){
                // 全球は一様な方向、局所は SLDEM の範囲(±10 km = 約 ±0.33° の内側)を多く。
                double longitude,latitude;
                if(i%3==0){longitude=(random.NextDouble()*2-1)*0.3;latitude=(random.NextDouble()*2-1)*0.3;}
                else{longitude=(random.NextDouble()*2-1)*180.0;latitude=Math.Asin(random.NextDouble()*2-1)*180.0/Math.PI;}
                double3 d=Direction(longitude,latitude);
                if(!SurfaceHeight(d).TryToDouble(out double height)){continue;}
                // 直接求める方向は、CPU の高さに渡した Logical の点と同じ(float から作った単位ベクトル)にする。
                LogicalVector3 unit=UnitLogical(d);
                if(!unit.X.TryToDouble(out double ux)||!unit.Y.TryToDouble(out double uy)||!unit.Z.TryToDouble(out double uz)){continue;}
                d=new double3(ux,uy,uz);
                double global=globalLayer.SampleDirection(d.x,d.y,d.z);
                double regional=global;double weight=0;
                if(regionalLayer!=null){regional=regionalLayer.SampleDirection(d.x,d.y,d.z,out weight);}
                double expected=global+(regional-global)*weight;
                double error=Math.Abs(height-expected);
                worst=Math.Max(worst,error);count++;
                if(weight>0){worstRegional=Math.Max(worstRegional,error);regionalCount++;}
            }
            report.Add("CPU vs direct bilinear: samples="+count+" worstError="+worst.ToString("G3",CultureInfo.InvariantCulture)+"m (localOverride samples="+regionalCount+" worstError="+worstRegional.ToString("G3",CultureInfo.InvariantCulture)+"m)");
            // 高さは Logical(実データは倍精度)で返す。比べる方向は同じ Logical の点なので、数値の丸めだけ(1 µm 以内)。
            if(worst>1e-6){failures.Add("CPU の高さが、方向から直接求めた値と "+worst+"m 違います。");}
        }

        // 選んだ Cell のうち、camera から nearLimit(m)以内の Cell について、GPU が生成した頂点と CPU の式の頂点を比べる。
        private static void CheckGpu(string name,float nearLimit){
            int cells=0;double worst=0;PlanetCellKey worstKey=default;int side=PlanetLayerSource.CellQuads+1;
            double worstVertexError=0;string worstDetail="";
            // 読み戻した全頂点(位置・法線)の FNV ハッシュ。頂点の生成を変えない変更で、ビット単位で変わっていないことを確かめる。
            uint hash=2166136261u;
            var byLevel=new SortedDictionary<int,double>();
            var order=new List<KeyValuePair<float,PlanetCellKey>>();
            foreach(PlanetCellKey key in moon.LastSelected){
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TrySubtract(frame.Origin,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 relative)){continue;}
                float distance=((Vector3)relative).magnitude;
                if(distance>nearLimit&&nearLimit<float.MaxValue){continue;}
                // 極の近く(|緯度| > 75°)の Cell は、この実行(既定)の照合から外す(ハッシュの基準を保つため)。極は RunPolar / RunPolarSynthetic が全 Cell で比べる。
                Vector3 approximate=frame.ApproximateOrigin.normalized;
                if(!polar&&Math.Abs(approximate.z)>0.966f){continue;}
                order.Add(new KeyValuePair<float,PlanetCellKey>(distance,key));
            }
            order.Sort((a,b)=>a.Key.CompareTo(b.Key));
            // 近い順に最大 12 Cell
            for(int n=0;n<order.Count&&n<12;n++){
                PlanetCellKey key=order[n].Value;
                if(!moon.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                cells++;
                hash=HashVertices(hash,vertices);
                double cellWorst=0;
                // 頂点は 65×65 のうち、間引いて(各 4 つおき)比べる。
                for(int j=0;j<side;j+=4){
                    for(int i=0;i<side;i+=4){
                        if(!moon.TryEvaluateVertexForDiagnostics(in key,i,j,out Vector3 expected)){continue;}
                        double error=(vertices[j*side+i].Position-expected).magnitude;
                        cellWorst=Math.Max(cellWorst,error);
                        if(error>worstVertexError){
                            worstVertexError=error;
                            Vector3 upDirection=moon.Selector.Frame(in key).ApproximateOrigin.normalized;
                            Vector3 delta=vertices[j*side+i].Position-expected;
                            PlanetCellFrame detailFrame=moon.Selector.Frame(in key);
                            Vector3 sphereOnly=PlanetCellGeometry.SpherePoint(in detailFrame,PlanetLayerSource.CellQuads,i,j,(float)MoonRadius,out Vector3 vertexDirection);
                            // 頂点 = sphere·(1+H/R) + direction·(h−H)(H = 原点を持ち上げた距離)。高さ h = 高さ方向の成分 + H。
                            float shift=detailFrame.HeightShift;
                            Vector3 sphereLifted=sphereOnly+sphereOnly*(shift/(float)MoonRadius);
                            float gpuHeight=Vector3.Dot(vertices[j*side+i].Position-sphereLifted,vertexDirection)+shift;
                            float cpuHeight=Vector3.Dot(expected-sphereLifted,vertexDirection)+shift;
                            Vector3 absolute=detailFrame.ApproximateSphereOrigin+sphereOnly;
                            double directHeight=globalLayer.SampleDirection(absolute.x,absolute.y,absolute.z);
                            worstDetail="heights gpu="+gpuHeight.ToString("G7",CultureInfo.InvariantCulture)+" cpu="+cpuHeight.ToString("G7",CultureInfo.InvariantCulture)+" directMip0="+directHeight.ToString("G7",CultureInfo.InvariantCulture)+" mip="+moon.RasterMip(in key)+" vertex("+i+","+j+") gpu="+vertices[j*side+i].Position.ToString("G6")+" cpu="+expected.ToString("G6")+" diff(radial)="+Vector3.Dot(delta,upDirection).ToString("G3",CultureInfo.InvariantCulture)+" diff(lateral)="+(delta-upDirection*Vector3.Dot(delta,upDirection)).magnitude.ToString("G3",CultureInfo.InvariantCulture)+" radial="+Vector3.Dot(expected,upDirection).ToString("G6",CultureInfo.InvariantCulture);
                        }
                    }
                }
                byLevel.TryGetValue(key.Level,out double levelWorst);if(cellWorst>levelWorst){byLevel[key.Level]=cellWorst;}
                // 許す差。階層 14 以上は、原点を地表の高さまで持ち上げた頂点(高さ方向の成分が小さい)なので、float の刻みは高さによらず数 µm〜数十 µm
                // (持ち上げない |高さ| < 1 km の Cell は刻み 61 µm 以下)。0.3 mm + Cell の大きさの 10^-6。
                // 粗い Cell(階層 13 以下)は、原点からの差 eps の float の丸めが、緯度経度の差に 1 m ほどの位置の誤差を作る(Cell の大きさの 1.5×10^-5 + 2 mm)。
                double cellSize=moon.Selector.QuadArc(key.Level)*PlanetLayerSource.CellQuads;
                double allowed=2e-3+1.5e-5*cellSize;
                if(key.Level>=LiftedLevel){allowed=3e-4+1e-6*cellSize;}
                // 実データの極の近く(階層 14 以上)。実測の最大は北 0.37 mm、南 0.92 mm(合成の滑らかな場は、持ち上げない緯度 80° の Cell の 0.19 mm 以外は 35 µm 以下)。2 mm までを許す。極冠が無いと 109 m〜715 m 違った。
                if(RealPolar&&key.Level>=LiftedLevel){allowed=Math.Max(allowed,2e-3);}
                if(cellWorst>allowed){failures.Add("GPU の頂点が CPU の式と "+cellWorst+"m 違います(許す差 "+allowed+"m、"+name+"、"+key+")。");}
                if(cellWorst>worst){worst=cellWorst;worstKey=key;}
            }
            string levels="";
            foreach(KeyValuePair<int,double> pair in byLevel){levels+=pair.Key+":"+pair.Value.ToString("G3",CultureInfo.InvariantCulture)+" ";}
            report.Add("GPU vs CPU "+name+": cells="+cells+" worst="+worst.ToString("G3",CultureInfo.InvariantCulture)+"m at "+worstKey+" worstByLevel: "+levels+" vertexHash="+hash.ToString("X8")+" | "+worstDetail);
            VertexHashBaseline.Record(name,hash);
        }

        private static uint HashVertices(uint hash,VirtualGeometryPackedVertex[] vertices){
            unchecked{
                for(int i=0;i<vertices.Length;i++){
                    Vector3 p=vertices[i].Position;Vector3 n=vertices[i].Normal;
                    hash=(hash^(uint)BitConverter.SingleToInt32Bits(p.x))*16777619u;hash=(hash^(uint)BitConverter.SingleToInt32Bits(p.y))*16777619u;hash=(hash^(uint)BitConverter.SingleToInt32Bits(p.z))*16777619u;
                    hash=(hash^(uint)BitConverter.SingleToInt32Bits(n.x))*16777619u;hash=(hash^(uint)BitConverter.SingleToInt32Bits(n.y))*16777619u;hash=(hash^(uint)BitConverter.SingleToInt32Bits(n.z))*16777619u;
                }
            }
            return hash;
        }

        // 地表 0.17m の視点で、camera に最も近い(最も細かい)Cell の頂点の高さ(Cell の原点からの up 方向の成分)の、格子に沿った 2 階差分を測る。
        // 緯度経度を float の方向から求めると、半径 1.7×10^6 m では約 0.1 m に量子化されて、傾きの大きい所で高さに階段(数 mm 以上)が出る。
        // 頂点の位置は、原点を地表の高さまで持ち上げた float(高さ方向の成分が小さいので、起伏が 9 km あっても刻みは µm の桁。持ち上げない |高さ| < 1 km でも 61 µm 以下)。
        private static void CheckMillimeter(string name,double longitude,double latitude){
            PlaceCamera(longitude,latitude,0.17,0.0,35.0);
            int side=PlanetLayerSource.CellQuads+1;
            PlanetCellKey nearest=default;float nearestDistance=float.MaxValue;
            foreach(PlanetCellKey key in moon.LastSelected){
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TrySubtract(frame.Origin,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 relative)){continue;}
                float distance=((Vector3)relative).magnitude;
                if(distance<nearestDistance){nearestDistance=distance;nearest=key;}
            }
            if(!moon.TryReadbackCellForDiagnostics(in nearest,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frameOfNearest)){failures.Add(name+": 最も近い Cell を読み戻せません。");return;}
            Vector3 up=frameOfNearest.ApproximateOrigin.normalized;
            // 内積は double で求める(float の内積の丸め、|成分| 500 m で 61 µm が、格納された float の頂点の丸めの上に乗って、2 階差分を 2 倍ほど大きく見せる)。
            double worst=0;double slope=0;
            for(int j=1;j<side-1;j++){
                for(int i=1;i<side-1;i++){
                    double center=Dot(vertices[j*side+i].Position,up);
                    double left=Dot(vertices[j*side+i-1].Position,up);double right=Dot(vertices[j*side+i+1].Position,up);
                    double below=Dot(vertices[(j-1)*side+i].Position,up);double above=Dot(vertices[(j+1)*side+i].Position,up);
                    worst=Math.Max(worst,Math.Max(Math.Abs(left-2*center+right),Math.Abs(below-2*center+above)));
                    slope=Math.Max(slope,Math.Abs(right-left)*0.5);
                }
            }
            // CPU の地表の高さ(TrySampleSurface。接点・降下カメラの地面)と、描いている頂点(持ち上げた Cell の原点 + 相対位置)の高さの差。
            // 頂点の高さ = |原点 + 相対位置| − R を Logical で求める。物を 1 mm の精度で地面に置くには、この差が µm の桁でなければならない。
            double worstSurface=0;int surfaceSamples=0;
            for(int j=0;j<side;j+=8){
                for(int i=0;i<side;i+=8){
                    Vector3 position=vertices[j*side+i].Position;
                    if(!LogicalVector3.TryFromFloat3(new float3(position.x,position.y,position.z),out LogicalVector3 relativePosition)||!LogicalVector3.TryAdd(frameOfNearest.Origin,relativePosition,out LogicalVector3 vertexPoint)||
                        !LogicalVector3.TryLength(in vertexPoint,out LogicalScalar vertexDistance)||!LogicalScalar.TrySubtract(in vertexDistance,moon.Selector.LogicalRadius,out LogicalScalar vertexHeight)||
                        !moon.TrySampleSurface(in vertexPoint,out LogicalScalar surfaceHeight,out LogicalScalar _)||!LogicalScalar.TrySubtract(in vertexHeight,in surfaceHeight,out LogicalScalar heightDifference)||
                        !heightDifference.TryToDouble(out double surfaceError)){
                        continue;
                    }
                    worstSurface=Math.Max(worstSurface,Math.Abs(surfaceError));surfaceSamples++;
                }
            }
            float quad=moon.Selector.QuadArc(nearest.Level);
            report.Add("surface "+name+": CPU TrySampleSurface vs rendered vertex height: samples="+surfaceSamples+" worst="+(worstSurface*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um");
            // 原点を持ち上げた Cell は µm の桁(float の頂点の丸め + GPU の補間の丸め)。持ち上げない |高さ| < 1 km の Cell は float の刻み 61 µm の丸めで 50 µm ほど(0.1 mm までを許す)。
            // 持ち上げが無いと、|高さ| 1.9〜7.3 km の地点で 0.16〜0.55 mm になる。
            // 実データの極の近くは 1 mm まで(実測の最大は 0.41 mm。極冠が無いと 29 m 違った)。
            double surfaceAllowed=1e-4;
            if(RealPolar){surfaceAllowed=1e-3;}
            if(surfaceSamples==0||worstSurface>surfaceAllowed){failures.Add(name+": CPU の地表の高さと、描いている頂点の高さが "+worstSurface+"m 違います(標本 "+surfaceSamples+" 点、許す差 "+surfaceAllowed+"m)。");}
            // 地表 0.17m の視点で、最も近い Cell が 1 mm の細かさまで細分化されていること(細分化が止まると mm の精度で物を置けない)。
            // 実データの極の近くは対象外: 南極の行 0 の崖で、細分化が収束しない(pool が足りなくなる)ことがある。報告には quad の大きさが残る。
            if(!RealPolar&&quad>1.0e-3f){failures.Add(name+": 地表 0.17m の視点で最も近い Cell の quad が "+quad*1000f+"mm で、1mm の細かさに届いていません。");}
            report.Add("millimeter "+name+": nearest "+nearest+" quad="+(quad*1000f).ToString("G3",CultureInfo.InvariantCulture)+"mm distance="+nearestDistance.ToString("G3",CultureInfo.InvariantCulture)+"m maxSecondDifference="+(worst*1e3).ToString("G3",CultureInfo.InvariantCulture)+"mm maxStep="+(slope*1e3).ToString("G3",CultureInfo.InvariantCulture)+"mm");
            // 2 階差分は float の丸め(持ち上げない |高さ| < 1 km で 61 µm)の数倍以内。方向の量子化があれば、傾き 0.1 で 1e-2 m 以上の階段になる。
            // 持ち上げが無いと、高さ −7 km で 1.46 mm、−2.8 km で 0.98 mm(float の刻み)。
            if(worst>2e-4&&(!polar||synthetic)){failures.Add(name+": 最も細かい Cell の頂点の高さの 2 階差分が "+worst+"m あります(量子化の階段の疑い)。");}
        }

        private static double Dot(Vector3 a,Vector3 b){
            return (double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z;
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Destroy(Capture(Width,ImageHeight));
                int signature=moon.LastSelected.Count*31;
                foreach(PlanetCellKey key in moon.LastSelected){signature^=key.GetHashCode();}
                if(moon.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Destroy(Texture2D image){
            if(image!=null){UnityEngine.Object.DestroyImmediate(image);}
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        // 月全体が見える高さでの、背景の画素のうち、月の内側の球(R−Hmax)の中を向く光線(真下からの角度が地平線の角度より 2 画素以上小さい)の数。
        private static int CountHoles(Texture2D image,double altitude){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            double inner=MoonRadius-moon.RasterMaximumHeight-moon.HeightAmplitude;
            double dip=Math.Asin(Math.Max(-1.0,Math.Min(1.0,inner/(MoonRadius+altitude))));
            double pixelAngle=camera.fieldOfView*Mathf.Deg2Rad/height;
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            // camera の真下 = 月の中心の向き(camera の位置の方向の逆)。
            if(!LogicalVector3.TryLength(cameraLogical.LocalPosition,out LogicalScalar length)||!cameraLogical.LocalPosition.X.TryToDouble(out double cx)||!cameraLogical.LocalPosition.Y.TryToDouble(out double cy)||!cameraLogical.LocalPosition.Z.TryToDouble(out double cz)||!length.TryToDouble(out double l)){return -1;}
            Vector3 down=new Vector3((float)(-cx/l),(float)(-cy/l),(float)(-cz/l));
            int holes=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    Color32 pixel=pixels[y*width+x];
                    if(!(pixel.r>200&&pixel.g<60&&pixel.b>200)){continue;}
                    Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
                    Vector3 direction=(camera.transform.rotation*local).normalized;
                    double angle=Math.Acos(Math.Max(-1.0,Math.Min(1.0,Vector3.Dot(direction,down))));
                    if(angle<dip-2*pixelAngle){holes++;}
                }
            }
            return holes;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            VertexHashBaseline.Complete(report,failures);
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            if(moon!=null){moon.SetRasters(null,null);}
            if(globalLayer!=null){globalLayer.Dispose();}
            if(regionalLayer!=null){regionalLayer.Dispose();}
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
