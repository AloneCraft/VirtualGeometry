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
using ViewSnapshot=Omni.Rendering.Radial.ViewSnapshot;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 月とサッカーボールのデモ(<see cref="MoonBallDemo"/>)の検証。ボールを月の表面にぎりぎり接して置き、1mm 単位で動かして次を確かめる。
    /// - 接触: ボールと月の、描いている頂点(メッシュ)の間の隙間(マイクロメートル)
    /// - 位置の精度: 1mm ずつ動かした各 frame で、画面に描かれたボールが、Logical の位置から光線で求めた球と 1 画素の帯の内で一致する
    ///   (Omni の COSMIC-PRECISION-001 の現象 = ボールの縁を月の面が越える、もここで数える)
    /// - 月全体から接触の視点まで高さを縮める間、穴が無く、月全体の輪郭が解析的な円と一致する
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonBallValidation.Run(月)/ RunGiant(半径 10^20 m)
    /// 起伏のある地面の確認: RunRealMoon(実データの月)/ RunSyntheticMoon(合成の高起伏の月)で、4 地点にボールを接して置き、接触の隙間・1mm ずつの動き・画像を確かめる。
    /// RunReliefBall: 起伏のあるボール(原点の持ち上げ H ≠ 0 の RadialBall)を、持ち上げなしと画像・頂点で比べる。
    /// </summary>
    public static class MoonBallValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private enum Mode{Default,Raster,Relief,Observer,ViewScale}

        private static MoonBallSettings settings;
        private static Mode mode;
        // 観察 camera(eye が warp 中心にない camera)。Observer の検証だけ。
        private static Camera observerCamera;
        private static global::Omni.Logical.Transform observerLogical;
        private static ViewSnapshot observerSnapshot;
        private static bool observerSnapshotValid;
        private static bool realData;
        private static PlanetRasterLayer rasterGlobal;
        private static string results;
        private static MoonBallObjects objects;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static global::Omni.Logical.Transform moonLogical;
        private static global::Omni.Logical.Transform ballLogical;
        private static PlanetTerrainSource moon;
        private static PlanetTerrainSource ball;
        private static MoonBallDemo demo;
        private static Material moonFlat;
        private static Material ballFlat;
        private static Material moonShaded;
        private static Material ballShaded;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            settings=MoonBallSettings.Moon;results="TestResults/Planet/MoonBall";mode=Mode.Default;
            Start();
        }

        public static void RunGiant(){
            settings=MoonBallSettings.Giant;results="TestResults/Planet/GiantBall";mode=Mode.Default;
            Start();
        }

        /// <summary>実データ(LOLA 全球 + SLDEM の局所)の月に、ボールを 4 地点で接して置く。結果は TestResults/Planet/RealMoonBall。</summary>
        public static void RunRealMoon(){
            settings=MoonBallSettings.Raster;results="TestResults/Planet/RealMoonBall";mode=Mode.Raster;realData=true;
            Start();
        }

        /// <summary>合成の高起伏(−7,300 m〜+9,000 m、勾配は最大約 0.3)の月に、ボールを 4 地点で接して置く(データが無くても走る)。結果は TestResults/Planet/SyntheticMoonBall。</summary>
        public static void RunSyntheticMoon(){
            settings=MoonBallSettings.Raster;results="TestResults/Planet/SyntheticMoonBall";mode=Mode.Raster;realData=false;
            Start();
        }

        /// <summary>
        /// eye が warp 中心にない camera(観察 camera。Omni の <c>RadialWarpSource.Observer</c>)で描く(fail-open の経路)。結果は TestResults/Planet/ObserverCamera。
        /// 観察 camera を主 camera のごく近く(10^-6、10^-4 m)に置くと主 camera の画像と一致し、遠く(1 m〜10 km)に置いても月に穴が無い。
        /// </summary>
        public static void RunObserver(){
            settings=MoonBallSettings.Moon;results="TestResults/Planet/ObserverCamera";mode=Mode.Observer;
            Start();
        }

        /// <summary>View Scale M を 10^-3〜10^6 に振って、接触の視点でボールが解析の球と一致し、月全体の輪郭が解析の円と一致することを確かめる。結果は TestResults/Planet/ViewScale。</summary>
        public static void RunViewScale(){
            settings=MoonBallSettings.Moon;results="TestResults/Planet/ViewScale";mode=Mode.ViewScale;
            Start();
        }

        /// <summary>起伏のあるボールを、原点を持ち上げない(しきい値 1e9)と全 Cell 持ち上げる(0)で比べる。結果は TestResults/Planet/ReliefBall。</summary>
        public static void RunReliefBall(){
            settings=MoonBallSettings.ReliefBall;results="TestResults/Planet/ReliefBall";mode=Mode.Relief;
            Start();
        }

        private static void Start(){
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
                // Omni が Unity の版の違いで出す診断は、この検証の対象外(Requirements/PlanetWorld.md §8.4)。
                if(condition.Contains("[OmniConversion] ValidatedOnly")||condition.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition);
            }
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("MoonBall validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            objects=MoonBallSceneBuilder.Populate(settings);
            if(mode==Mode.Raster){
                PlanetRasterLayer regional=null;
                if(realData){RealMoonData.LoadReal(out rasterGlobal,out regional);}else{rasterGlobal=RealMoonData.CreateSynthetic();}
                objects.MoonSource.SetRasters(rasterGlobal,regional);
            }
            // 起伏のあるボールの検証は、しきい値を自分で切り替えるので、コマンドラインの上書きは使わない。
            if(mode!=Mode.Relief){HeightShiftOverride.ApplyToScene();}
            camera=objects.Camera;camera.backgroundColor=new Color(1,0,1,1);
            cameraLogical=objects.CameraObject.GetComponent<global::Omni.Logical.Transform>();
            moonLogical=objects.Moon.GetComponent<global::Omni.Logical.Transform>();
            ballLogical=objects.Ball.GetComponent<global::Omni.Logical.Transform>();
            moon=objects.MoonSource;ball=objects.BallSource;demo=objects.Demo;
            // 単色(照明・模様なし)の material: 月は灰色、ボールは赤。見た目の material は別に持つ。
            moonShaded=new Material(objects.MoonMaterial);ballShaded=new Material(objects.BallMaterial);
            moonFlat=new Material(objects.MoonMaterial);moonFlat.SetFloat("_FlatColor",1);moonFlat.SetColor("_FlatTint",new Color(0.5f,0.5f,0.5f,1));
            ballFlat=new Material(objects.BallMaterial);ballFlat.SetFloat("_FlatColor",1);ballFlat.SetColor("_FlatTint",new Color(1f,0.1f,0.1f,1));
            // 検証の間は Update を回さない(操作の入力を読まない)。
            demo.enabled=false;
            SetFlat(true);
            if(mode==Mode.Observer){CreateObserver();}
        }

        // 観察 camera: 主 camera の RadialCamera(warp 中心)を借りて、別の位置から描く。
        private static void CreateObserver(){
            var observerObject=new GameObject("Observer Camera");
            observerCamera=observerObject.AddComponent<Camera>();
            observerCamera.fieldOfView=camera.fieldOfView;observerCamera.nearClipPlane=camera.nearClipPlane;observerCamera.farClipPlane=camera.farClipPlane;
            observerCamera.clearFlags=CameraClearFlags.SolidColor;observerCamera.backgroundColor=camera.backgroundColor;
            observerCamera.allowHDR=false;observerCamera.allowMSAA=false;
            observerLogical=observerObject.AddComponent<global::Omni.Logical.Transform>();
            global::Omni.RadialCamera driver=objects.CameraObject.GetComponent<global::Omni.RadialCamera>();
            global::Omni.RadialWarpSource source=global::Omni.RadialWarpSource.Observer(driver);
            global::Omni.RadialGlobalWarpSourceRegistry.Register(observerCamera,in source,null);
            // 描画の終わりに、観察 camera の ViewSnapshot を覚える(その frame にしか無い)。
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering+=OnEndCameraRendering;
        }

        private static void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext context,Camera rendered){
            if(rendered!=observerCamera){return;}
            observerSnapshotValid=global::Omni.RadialViewRegistry.TryGetCurrent(rendered.GetEntityId(),out observerSnapshot);
        }

        private static void SetFlat(bool flat){
            if(flat){moon.Material=moonFlat;ball.Material=ballFlat;}
            else{moon.Material=moonShaded;ball.Material=ballShaded;}
            // 月の格子の軸は Initialize が material に書くので、見た目の material には書き直す。
            if(demo.IsInitialized){
                Vector3 u=demo.AxisU;Vector3 v=demo.AxisV;
                moonShaded.SetVector("_GridAxisU",new Vector4(u.x,u.y,u.z,0));moonShaded.SetVector("_GridAxisV",new Vector4(v.x,v.y,v.z,0));
            }
        }

        private static IEnumerator Steps(){
            if(mode==Mode.Raster){return StepsRaster();}
            if(mode==Mode.Relief){return StepsRelief();}
            if(mode==Mode.Observer){return StepsObserver();}
            if(mode==Mode.ViewScale){return StepsViewScale();}
            return StepsDefault();
        }

        private static IEnumerator StepsDefault(){
            report.Add("moon: radius="+moon.Radius.ToString("G6",CultureInfo.InvariantCulture)+" height="+settings.MoonHeight+" ball radius="+ball.Radius+" camera near logical="+0.3f/10f+"m");
            // 月とボールの Selector が使えるまで待つ。
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            report.Add("contact: direction="+demo.Up.ToString("F6")+" surfaceHeight="+demo.SurfaceHeight.ToString("G6",CultureInfo.InvariantCulture)+"m");
            demo.ShowContactView();
            SetFlat(true);
            foreach(object step in Converge("ContactView")){yield return step;}
            {
                Texture2D image=Capture();
                Save("ContactFlat",image);
                CompareBall("ContactFlat",image);
                // 見た目(照明・模様・格子あり)。
                SetFlat(false);
                foreach(object step in Converge("ContactShaded")){yield return step;}
                Save("ContactShaded",Capture());
                SetFlat(true);
            }

            // 1mm ずつ動かす: ボールを月の面から離す(隙間 0〜12mm)、面に沿って転がす(0〜8mm)、手前へ(0〜8mm)。
            int[] gapSteps={0,1,2,3,4,5,6,7,8,9,10,11,12};
            foreach(int k in gapSteps){
                demo.SetOffset(0,10*k,0);
                foreach(object step in Converge("Gap"+k)){yield return step;}
                Texture2D image=Capture();
                if(k==0||k==1||k==2||k==12){Save("Gap"+k+"mm",image);}
                CompareBall("Gap"+k+"mm",image);
            }
            for(int k=1;k<=8;k++){
                demo.SetOffset(10*k,0,0);
                foreach(object step in Converge("Roll"+k)){yield return step;}
                Texture2D image=Capture();
                if(k==1||k==8){Save("Roll"+k+"mm",image);}
                CompareBall("Roll"+k+"mm",image);
            }
            for(int k=1;k<=8;k++){
                demo.SetOffset(0,0,10*k);
                foreach(object step in Converge("Depth"+k)){yield return step;}
                CompareBall("Depth"+k+"mm",Capture());
            }
            // 0.1mm 単位でも位置が変わる(画素より細かいので、画素の境を越える所だけで 1 画素動く)ことを、解析の位置との一致で確かめる。
            for(int k=0;k<=9;k++){
                demo.SetOffset(0,k,0);
                CompareBall("Tenth"+k,Capture());
            }
            demo.SetOffset(0,0,0);

            // 近づいた視点(面から 1cm、ボールの中心から水平 25cm、接点を見る): 隙間の 1mm が約 4 画素になる。
            demo.PlaceCamera(0.01,0.25,true);
            foreach(object step in Converge("Macro")){yield return step;}
            Save("MacroFlat",Capture());
            // 接触の隙間は、接点が見える近接の視点で測る(真横の視点では、ボールの最下の Cell が地平線の向こう(カメラから見えない側)として選ばれない)。
            ContactMeshGap();
            for(int k=0;k<=6;k++){
                demo.SetOffset(0,10*k,0);
                foreach(object step in Converge("MacroGap"+k)){yield return step;}
                CompareBall("MacroGap"+k+"mm",Capture());
            }
            demo.SetOffset(0,0,0);
            SetFlat(false);
            demo.SetOffset(0,0,0);
            foreach(object step in Converge("MacroShaded")){yield return step;}
            Save("MacroShaded",Capture());
            demo.SetOffset(0,20,0);
            foreach(object step in Converge("MacroShadedGap2")){yield return step;}
            Save("MacroShadedGap2mm",Capture());
            demo.SetOffset(0,0,0);
            SetFlat(true);

            // 月全体から接触の視点まで高さを縮める。毎段、穴が無いこと。月全体が見える段では、輪郭が解析的な円と一致すること。
            double radius=moon.Radius;
            double altitude=radius*2.5;
            int level=0;
            while(true){
                demo.PlaceCamera(altitude,demo.ContactHorizontal());
                foreach(object step in Converge("Zoom"+level)){yield return step;}
                Texture2D image=Capture();
                int holes=CountHoles(image,altitude);
                int moonPixels=CountClass(image,2);
                int ballPixels=CountClass(image,1);
                string silhouette="";
                if(altitude>=radius*0.5){
                    int outside=CompareMoonDisc(image,altitude);
                    silhouette=" moonDiscOutside1px="+outside;
                    if(outside!=0){failures.Add("高さ "+altitude+"m: 月の輪郭が解析的な円と 1 画素の帯の外で "+outside+" 画素違いました。");}
                }
                report.Add("zoom "+level+": altitude="+altitude.ToString("G6",CultureInfo.InvariantCulture)+"m holes="+holes+" moonPixels="+moonPixels+" ballPixels="+ballPixels+
                    " cells moon="+moon.LastSelected.Count+" ball="+ball.LastSelected.Count+silhouette);
                if(level%3==0){Save("Zoom"+level,image);}
                if(holes!=0){failures.Add("高さ "+altitude+"m: 穴が "+holes+" 画素ありました。");}
                if(altitude<=demo.ViewHeight*1.0001){break;}
                altitude=Math.Max(altitude/20.0,demo.ViewHeight);
                level++;
                if(level>40){failures.Add("高さを縮める段が 40 を超えました。");break;}
            }
            report.Add("pool: moon used="+moon.UsedSlots+"/"+moon.PoolCapacity+" ball used="+ball.UsedSlots+"/"+ball.PoolCapacity+" lastError="+(moon.LastError??"(none)")+"/"+(ball.LastError??"(none)"));
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
        }

        // 起伏のある地面(実データ・合成の高起伏の月)に、ボールを接して置く。4 地点それぞれで:
        // - 接触の視点で、ボールの隙間 0〜3 mm の各段の描画が、Logical の位置から求めた球と 1 画素の帯の内で一致する
        // - 近接の視点(隙間 0)で、(1) 接点の真下の月のメッシュの高さが CPU の地表の高さ(接点)に一致し、
        //   (2) 解析の球(半径 r)と月のメッシュの隙間の最小が、接点の外向き up に沿って置いた球が傾き s の面に食い込む深さ −r(√(1+s²)−1) に一致し、
        //   (3) ボールのメッシュの頂点が球面(半径 r)の上にある
        // - 隙間 1〜3 mm では、(4) 同じ Cell の頂点が、隙間 0 から up 方向へちょうど k mm、接平面の方向には 0 動く(ボールの Logical の位置が半径 1.7×10^6 m でも 1 mm のまま)
        // 月のメッシュの検証は解析の球で測る(ボールのメッシュは、camera に見える Cell だけが選ばれ、傾いた面では最も食い込む点の Cell が選ばれないことがある)。
        private static IEnumerator StepsRaster(){
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            string dataName="synthetic";
            if(realData){dataName="LOLA5760x2880+SLDEM344x344";}
            report.Add("raster moon: data="+dataName+" radius="+moon.Radius.ToString("G6",CultureInfo.InvariantCulture)+" rasterMaximumHeight="+moon.RasterMaximumHeight.ToString("G6",CultureInfo.InvariantCulture)+
                " ball radius="+ball.Radius+" heightShiftThreshold="+HeightShiftOverride.Describe()+" (component "+moon.HeightShiftThreshold.ToString("G",CultureInfo.InvariantCulture)+")");
            double[][] sites;
            if(realData){
                sites=new[]{
                    new[]{0.0,0.0},          // SLDEM の中心(局所の Override の中。高さ約 −720 m。原点を持ち上げない)
                    new[]{-172.0,-70.0},     // 南極エイトケン盆地の近く(最も低い所の周り。−7,304 m、勾配 0.025)
                    new[]{-154.3,-39.3},     // 低く平坦な所(−7,051 m)
                    new[]{-158.3,3.3},       // 高い所(+8,415 m。正の持ち上げ、勾配 0.064)
                };
            }else{
                sites=RealMoonData.ExtremeSites(rasterGlobal);
            }
            foreach(double[] site in sites){
                string name="Site"+site[0].ToString("F1",CultureInfo.InvariantCulture)+"_"+site[1].ToString("F1",CultureInfo.InvariantCulture);
                double lon=site[0]*Math.PI/180.0;double lat=site[1]*Math.PI/180.0;
                demo.ContactDirection=new Vector3((float)(Math.Sin(lon)*Math.Cos(lat)),(float)(Math.Cos(lon)*Math.Cos(lat)),(float)Math.Sin(lat));
                if(!demo.Initialize()){throw new InvalidOperationException(name+" Initialize: "+demo.Failure);}
                report.Add(name+": contact surfaceHeight="+demo.SurfaceHeight.ToString("G6",CultureInfo.InvariantCulture)+"m");
                // 原点を持ち上げる Cell(|高さ| がしきい値以上)なら、頂点の刻みは µm の桁。持ち上げない Cell は float の刻み 61 µm(|高さ| < 1 km)以上。
                bool lifted=Math.Abs(demo.SurfaceHeight)>=moon.HeightShiftThreshold;
                double minimumTolerance=MinimumGapToleranceLifted;float contactTolerance=ContactHeightToleranceLifted;
                if(!lifted){minimumTolerance=MinimumGapToleranceUnlifted;contactTolerance=ContactHeightToleranceUnlifted;}
                SetFlat(true);
                demo.ShowContactView();
                // 接触の視点: 隙間 0〜3 mm
                for(int k=0;k<=3;k++){
                    demo.SetOffset(0,10*k,0);
                    foreach(object step in Converge(name+" Gap"+k,Width,ImageHeight)){yield return step;}
                    Texture2D image=Capture();
                    if(k==0||k==3){Save(name+"Gap"+k+"mm",image);}
                    CompareBall(name+" Gap"+k+"mm",image);
                }
                // 近接の視点(面から 1cm、ボールの中心から水平 25cm、接点を見る)
                demo.SetOffset(0,0,0);
                demo.PlaceCamera(0.01,0.25,true);
                Dictionary<PlanetCellKey,Vector3[]> baseline=null;
                for(int k=0;k<=3;k++){
                    demo.SetOffset(0,10*k,0);
                    foreach(object step in Converge(name+" MacroGap"+k,Width,ImageHeight)){yield return step;}
                    Texture2D image=Capture();
                    if(k==0){Save(name+"MacroFlat",image);}
                    CompareBall(name+" MacroGap"+k+"mm",image);
                    Dictionary<PlanetCellKey,Vector3[]> positions=ReadBallPositions();
                    if(k==0){
                        baseline=positions;
                        if(!MeasureRasterContact(name,positions,out RasterContact contact)){continue;}
                        double minimumError=contact.MinimumGap-contact.ExpectedMinimum;
                        report.Add(name+": contact slope="+contact.Slope.ToString("G3",CultureInfo.InvariantCulture)+" expectedMinimumGap="+(contact.ExpectedMinimum*1e6).ToString("G4",CultureInfo.InvariantCulture)+"um measured="+
                            (contact.MinimumGap*1e6).ToString("G4",CultureInfo.InvariantCulture)+"um error="+(minimumError*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um moonHeightAtContact="+(contact.MoonAtContact*1e6f).ToString("G4",CultureInfo.InvariantCulture)+
                            "um ballVertexOffSphere="+(contact.WorstSphereError*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um (lifted="+lifted+")");
                        if(!(Math.Abs(minimumError)<=minimumTolerance)){failures.Add(name+": 月のメッシュと解析の球の隙間の最小が、幾何から決まる値と "+minimumError*1e6+"um 違います(許す差 "+minimumTolerance*1e6+"um)。");}
                        if(!(Mathf.Abs(contact.MoonAtContact)<=contactTolerance)){failures.Add(name+": 接点の真下の月のメッシュの高さが、CPU の地表の高さと "+contact.MoonAtContact*1e6f+"um 違います(許す差 "+contactTolerance*1e6+"um)。");}
                        if(!(contact.WorstSphereError<=BallSphereTolerance)){failures.Add(name+": ボールのメッシュの頂点が球面から "+contact.WorstSphereError*1e6+"um 外れています(許す差 "+BallSphereTolerance*1e6+"um)。");}
                    }else if(baseline!=null){
                        RigidRise(name,k,baseline,positions);
                    }
                }
                // 見た目(照明・模様・格子あり)
                demo.SetOffset(0,0,0);
                demo.ShowContactView();
                SetFlat(false);
                foreach(object step in Converge(name+" Shaded")){yield return step;}
                Save(name+"Shaded",Capture());
                SetFlat(true);
            }
            report.Add("pool: moon used="+moon.UsedSlots+"/"+moon.PoolCapacity+" ball used="+ball.UsedSlots+"/"+ball.PoolCapacity+" lastError="+(moon.LastError??"(none)")+"/"+(ball.LastError??"(none)")+" invalidBounds="+moon.InvalidBounds+"/"+ball.InvalidBounds);
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
            if(moon.InvalidBounds!=0||ball.InvalidBounds!=0){failures.Add("非有限の頂点を持つ Cell があります(月 "+moon.InvalidBounds+"、ボール "+ball.InvalidBounds+")。");}
        }

        // 許容値。持ち上げた Cell は頂点の刻みが µm の桁(実測の最大は 30 µm)なので、40 µm / 20 µm。
        // 持ち上げない Cell(|高さ| < 1 km)は float の刻み 61 µm の丸めが月のメッシュに乗り、傾きの推定(±20 mm の中心差分)の誤差も加わるので、150 µm / 100 µm。
        private const double MinimumGapToleranceLifted=40e-6;
        private const float ContactHeightToleranceLifted=20e-6f;
        private const double MinimumGapToleranceUnlifted=150e-6;
        private const float ContactHeightToleranceUnlifted=100e-6f;
        // ボールの頂点の球面からのずれ、1 mm ずつ上げたときの剛体の動きのずれ(float の頂点の丸めだけ)。
        private const double BallSphereTolerance=2e-6;
        private const double RigidTolerance=2e-6;

        // 隙間 0 の位置 baseline から隙間 k mm へ上げたとき、同じ Cell の頂点が up 方向へ k mm、接平面の方向へ 0 動いているか。
        private static void RigidRise(string name,int k,Dictionary<PlanetCellKey,Vector3[]> baseline,Dictionary<PlanetCellKey,Vector3[]> positions){
            Vector3 expectedMove=demo.Up*(k*1e-3f);
            int common=0;double worst=0;
            foreach(KeyValuePair<PlanetCellKey,Vector3[]> pair in baseline){
                if(!positions.TryGetValue(pair.Key,out Vector3[] moved)){continue;}
                common++;
                for(int i=0;i<pair.Value.Length;i++){worst=Math.Max(worst,(moved[i]-pair.Value[i]-expectedMove).magnitude);}
            }
            report.Add(name+": gap "+k+"mm: commonBallCells="+common+" rigidRiseError="+(worst*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um");
            if(common<10){failures.Add(name+": 隙間 "+k+" mm で、隙間 0 と共通のボールの Cell が少なすぎます("+common+")。");}
            if(!(worst<=RigidTolerance)){failures.Add(name+": ボールを "+k+" mm 上げたとき、頂点の動きが "+worst*1e6+"um ずれています(許す差 "+RigidTolerance*1e6+"um)。");}
        }

        // 観察 camera(eye が warp 中心にない camera)の検証。観察 camera は主 camera の RadialCamera(warp 中心)を借りて、主 camera から up 方向へ ε だけ離れた位置から描く。
        // - ε = 10^-6、10^-4 m(主 camera とほぼ同じ位置、同じ向き): 主 camera の画像と、ボールの輪郭が 1 画素の帯の外で一致する(連続性)
        // - ε = 1 m 同じ向き: 月の画素の下に背景が出ない。ε = 1、10^2、10^4 m 真下: 画面全体が月(背景 0)。
        //   warp 中心から離れた eye は、対数圧縮で月の形が変わるので、ピンホールの幾何(CountHoles)は使えず、位相(月の下は月)で判定する。
        // 全ての ε で、観察 camera の ViewSnapshot が eye が warp 中心にない・錐台が使えない状態(fail-open の経路)であることを確かめる。
        private static IEnumerator StepsObserver(){
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            demo.ShowContactView();
            SetFlat(true);
            foreach(object step in Converge("Owner",Width,ImageHeight)){yield return step;}
            Texture2D ownerImage=Capture();
            Save("Owner",ownerImage);
            CompareBall("Owner",ownerImage);
            bool ownerSnapshot=global::Omni.RadialViewRegistry.TryGetCurrent(camera.GetEntityId(),out ViewSnapshot owner);
            string ownerText="owner snapshot: found="+ownerSnapshot;
            if(ownerSnapshot){ownerText+=" radialActive="+owner.IsRadialActive+" eyeAtWarpCenter="+owner.IsEyeAtWarpCenter+" canUseConeCulling="+owner.CanUseConeCulling;}
            report.Add(ownerText);
            // (ε、真下を向くか)。ε が小さいときは主 camera と同じ向き。大きいときは、同じ向きだと月が視野に入らない(warp 中心から ε 離れると、対数圧縮で月は遠い面になる)ので、真下を向く。
            // ε = 1 m は、同じ向きの画像(月の画素の下に背景が出ない)と真下の画像の両方を見る。
            var cases=new List<KeyValuePair<double,bool>>{
                new KeyValuePair<double,bool>(1e-6,false),new KeyValuePair<double,bool>(1e-4,false),
                new KeyValuePair<double,bool>(1.0,false),new KeyValuePair<double,bool>(1.0,true),
                new KeyValuePair<double,bool>(1e2,true),new KeyValuePair<double,bool>(1e4,true),
            };
            foreach(KeyValuePair<double,bool> item in cases){
                double epsilon=item.Key;bool lookDown=item.Value;
                string label=epsilon.ToString("G3",CultureInfo.InvariantCulture);
                if(lookDown){label+="_down";}
                PlaceObserver(epsilon,lookDown);
                foreach(object step in ConvergeObserver("Observer"+label,Width,ImageHeight)){yield return step;}
                observerSnapshotValid=false;
                Texture2D image=M0Validation.Capture(observerCamera,Width,ImageHeight);
                string name="Observer "+label+"m";
                Save("Observer_"+label,image);
                string snapshotText="snapshot: found="+observerSnapshotValid;
                bool coneCulling=true;
                bool eyeAtWarpCenter=true;
                if(observerSnapshotValid){
                    coneCulling=observerSnapshot.CanUseConeCulling;eyeAtWarpCenter=observerSnapshot.IsEyeAtWarpCenter;
                    snapshotText+=" radialActive="+observerSnapshot.IsRadialActive+" eyeAtWarpCenter="+eyeAtWarpCenter+" failOpen="+observerSnapshot.IsFailOpen+" canUseConeCulling="+coneCulling;
                }
                if(epsilon<=1e-4){
                    // 主 camera の画像との一致(ボールの輪郭、1 画素の帯の外)。
                    Color32[] a=ownerImage.GetPixels32();Color32[] b=image.GetPixels32();
                    var mask=new bool[a.Length];
                    for(int i=0;i<a.Length;i++){mask[i]=Classify(a[i])==1;}
                    bool[] band=Band(mask,image.width,image.height);
                    int mismatch=0;int anomaly=0;
                    for(int i=0;i<a.Length;i++){
                        int cb=Classify(b[i]);
                        if(cb==3){anomaly++;}
                        if((Classify(a[i])!=cb)&&!band[i]){mismatch++;}
                    }
                    report.Add(name+": mismatchWithOwnerOutside1px="+mismatch+" anomalyPixels="+anomaly+" "+snapshotText);
                    if(mismatch!=0){failures.Add(name+": 主 camera の画像と 1 画素の帯の外で "+mismatch+" 画素違います。");}
                    if(anomaly!=0){failures.Add(name+": 背景・ボール・月のどれでもない画素が "+anomaly+" ありました。");}
                }else{
                    // 穴の判定: warp 中心から離れた観察 camera は、対数圧縮で月の形が変わるので、ピンホールの幾何(CountHoles)は使えない。
                    // 真下を向く: 画面全体が月(とボール)。同じ向き: 月の画素の下(画面の下側)に背景が出ない(地平線より下は、必ず月)。
                    Color32[] pixels=image.GetPixels32();
                    int width=image.width;int height=image.height;
                    int background=0;int anomaly=0;int belowMoon=0;
                    for(int x=0;x<width;x++){
                        bool seenSurface=false;
                        for(int y=height-1;y>=0;y--){
                            int c=Classify(pixels[y*width+x]);
                            if(c==0){
                                background++;
                                if(seenSurface){belowMoon++;}
                            }else{
                                seenSurface=true;
                                if(c==3){anomaly++;}
                            }
                        }
                    }
                    int moonPixels=CountClass(image,2);
                    report.Add(name+": background="+background+" belowMoon="+belowMoon+" anomaly="+anomaly+" moonPixels="+moonPixels+" ballPixels="+CountClass(image,1)+" "+snapshotText);
                    if(moonPixels==0){failures.Add(name+": 月が描かれていません。");}
                    if(anomaly!=0){failures.Add(name+": 背景・ボール・月のどれでもない画素が "+anomaly+" ありました。");}
                    if(lookDown){
                        if(background!=0){failures.Add(name+": 真下を向いているのに、背景の画素が "+background+" あります(月に穴)。");}
                    }else{
                        if(belowMoon!=0){failures.Add(name+": 月の画素の下に背景の画素が "+belowMoon+" あります(月に穴)。");}
                    }
                    if(!observerSnapshotValid||coneCulling||eyeAtWarpCenter){failures.Add(name+": 観察 camera の ViewSnapshot が、eye が warp 中心にない・錐台が使えない状態になっていません(fail-open の経路を通っていません。見つかった "+observerSnapshotValid+"、warp 中心 "+eyeAtWarpCenter+"、錐台 "+coneCulling+")。");}
                }
                UnityEngine.Object.DestroyImmediate(image);
            }
            UnityEngine.Object.DestroyImmediate(ownerImage);
            report.Add("pool: moon used="+moon.UsedSlots+"/"+moon.PoolCapacity+" ball used="+ball.UsedSlots+"/"+ball.PoolCapacity+" lastError="+(moon.LastError??"(none)")+"/"+(ball.LastError??"(none)"));
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
        }

        // 観察 camera を、主 camera の位置から up 方向へ epsilon(m)離れた所に置く(Logical のまま)。向きは主 camera と同じ。lookDown なら真下(−up)を向き、画面の上は主 camera の前方。
        private static void PlaceObserver(double epsilon,bool lookDown){
            if(!LogicalScalar.TryFromDouble(epsilon,out LogicalScalar distance)||!LogicalVector3.TryFromFloat3(demo.Up,out LogicalVector3 up)||!LogicalVector3.TryScale(in up,in distance,out LogicalVector3 offset)||
                !LogicalVector3.TryAdd(cameraLogical.LocalPosition,in offset,out LogicalVector3 position)){
                throw new InvalidOperationException("観察 camera の位置を Logical にできません。");
            }
            observerLogical.LocalPosition=position;
            observerLogical.LocalRotation=cameraLogical.LocalRotation;
            if(lookDown){
                Vector3 ownerForward=camera.transform.rotation*Vector3.forward;
                Quaternion down=Quaternion.LookRotation(-demo.Up,ownerForward);
                if(!LogicalQuaternion.TryFromFloat4(down.x,down.y,down.z,down.w,out LogicalQuaternion logicalDown)){
                    throw new InvalidOperationException("観察 camera の向きを Logical にできません。");
                }
                observerLogical.LocalRotation=logicalDown;
            }
            observerLogical.SyncUnityTransform();
        }

        private static IEnumerable<object> ConvergeObserver(string name,int width,int height){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Destroy(M0Validation.Capture(observerCamera,width,height));
                int signature=moon.LastSelected.Count*31+ball.LastSelected.Count;
                foreach(PlanetCellKey key in moon.LastSelected){signature^=key.GetHashCode();}
                foreach(PlanetCellKey key in ball.LastSelected){signature^=key.GetHashCode()*17;}
                if(moon.PendingGeneration==0&&ball.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
        }

        // View Scale M を振る。接触の視点でボールが解析の球と一致し(隙間 0、1 mm)、月全体の視点(高さ 2.5R)で輪郭が解析の円と一致し、穴が無い。
        private static IEnumerator StepsViewScale(){
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            global::Omni.RadialCamera radial=objects.CameraObject.GetComponent<global::Omni.RadialCamera>();
            SetFlat(true);
            foreach(float scale in new[]{1e-3f,0.1f,1f,10f,1e3f,1e6f}){
                // View Scale M = Camera の Near ÷ 論理 Near Clip(SetViewScale は論理 Near を Camera の Near ÷ M にする)。論理 Near Clip の平面より手前は描かれない。
                // 接触の視点は、ボールまで約 0.18 m なので、論理 Near が 0.03 m 前後になるように Camera の Near を 0.03·M にする(Unity の下限 0.01、デモの既定 0.3 の間)。
                // M が小さいと(M = 10^-3 で論理 Near は 10 m)ボールは Near の内側に入るので、接触の比較は、論理 Near がボールの面までの距離の半分より近いときだけ行う。月全体の視点(2.5R)は全ての M で行う。
                camera.nearClipPlane=Mathf.Clamp(0.03f*scale,0.01f,0.3f);
                radial.SetViewScale(scale);
                double logicalNear=camera.nearClipPlane/(double)scale;
                string name="M="+scale.ToString("G3",CultureInfo.InvariantCulture);
                demo.ShowContactView();
                demo.SetOffset(0,0,0);
                bool contactVisible=BallCenterFromCamera(out Vector3 ballCenter)&&logicalNear<0.5*(ballCenter.magnitude-ball.Radius);
                report.Add(name+": cameraNear="+camera.nearClipPlane.ToString("G3",CultureInfo.InvariantCulture)+" logicalNear="+logicalNear.ToString("G3",CultureInfo.InvariantCulture)+"m contactChecked="+contactVisible);
                foreach(int gap in new[]{0,10}){
                    if(!contactVisible){break;}
                    demo.SetOffset(0,gap,0);
                    foreach(object step in Converge(name+" Gap"+gap,Width,ImageHeight)){yield return step;}
                    Texture2D image=Capture();
                    if(gap==0){Save("Contact_"+name,image);}
                    CompareBall(name+" Gap"+gap/10+"mm",image);
                    UnityEngine.Object.DestroyImmediate(image);
                }
                demo.SetOffset(0,0,0);
                double altitude=moon.Radius*2.5;
                demo.PlaceCamera(altitude,demo.ContactHorizontal());
                foreach(object step in Converge(name+" Whole",Width,ImageHeight)){yield return step;}
                Texture2D whole=Capture();
                Save("Whole_"+name,whole);
                int holes=CountHoles(whole,altitude);
                int outside=CompareMoonDisc(whole,altitude);
                report.Add(name+": whole moon holes="+holes+" moonDiscOutside1px="+outside+" moonPixels="+CountClass(whole,2));
                if(holes!=0){failures.Add(name+": 月全体で穴が "+holes+" 画素ありました。");}
                if(outside!=0){failures.Add(name+": 月の輪郭が解析的な円と 1 画素の帯の外で "+outside+" 画素違いました。");}
                UnityEngine.Object.DestroyImmediate(whole);
            }
            camera.nearClipPlane=0.3f;
            radial.SetViewScale(10f);
            report.Add("pool: moon used="+moon.UsedSlots+"/"+moon.PoolCapacity+" ball used="+ball.UsedSlots+"/"+ball.PoolCapacity+" lastError="+(moon.LastError??"(none)")+"/"+(ball.LastError??"(none)"));
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
        }

        // 起伏のあるボール(振幅 20 mm)で、Cell の原点を持ち上げた場合(H ≠ 0。RadialBall の _PlanetCellUp.w = R + H の経路)と、持ち上げない場合を比べる。
        // 同じ視点・同じ位置で、単色と見た目の画像が 1 画素の帯の外で一致し、読み戻した頂点の位置(接点からの相対)が一致する。
        private static IEnumerator StepsRelief(){
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            report.Add("relief ball: radius="+ball.Radius+" amplitude="+settings.BallHeight+"m wavelength="+settings.BallWavelength+"m octaves="+settings.BallOctaves);
            demo.ShowContactView();
            var flatImages=new Texture2D[2];
            var shadedImages=new Texture2D[2];
            var positions=new Dictionary<PlanetCellKey,Vector3[]>[2];
            int[] liftedCells=new int[2];
            int[] selectedCells=new int[2];
            float[] largestShift=new float[2];
            int[] appliedCounts=new int[2];
            float[] thresholds={1e9f,0f};
            string[] labels={"NoLift","Lift"};
            for(int mode2=0;mode2<2;mode2++){
                ball.HeightShiftThreshold=thresholds[mode2];
                SetFlat(true);
                foreach(object step in Converge("Relief"+labels[mode2]+"Flat",Width,ImageHeight)){yield return step;}
                flatImages[mode2]=Capture();Save("Relief"+labels[mode2]+"Flat",flatImages[mode2]);
                positions[mode2]=ReadBallPositions();
                selectedCells[mode2]=ball.LastSelected.Count;
                foreach(PlanetCellKey key in ball.LastSelected){
                    float shift=ball.Selector.Frame(in key).HeightShift;
                    if(shift!=0f){liftedCells[mode2]++;largestShift[mode2]=Mathf.Max(largestShift[mode2],Mathf.Abs(shift));}
                }
                appliedCounts[mode2]=ball.HeightShiftApplied;
                SetFlat(false);
                foreach(object step in Converge("Relief"+labels[mode2]+"Shaded",Width,ImageHeight)){yield return step;}
                shadedImages[mode2]=Capture();Save("Relief"+labels[mode2]+"Shaded",shadedImages[mode2]);
                SetFlat(true);
                report.Add("relief "+labels[mode2]+": ballCells="+selectedCells[mode2]+" liftedCells="+liftedCells[mode2]+" largestShift="+(largestShift[mode2]*1e3f).ToString("G4",CultureInfo.InvariantCulture)+"mm heightShiftApplied="+appliedCounts[mode2]);
            }
            if(liftedCells[0]!=0){failures.Add("持ち上げなし(しきい値 1e9)なのに、持ち上げた Cell が "+liftedCells[0]+" あります。");}
            if(liftedCells[1]==0){failures.Add("全 Cell を持ち上げる設定(しきい値 0)なのに、持ち上げた Cell がありません(H ≠ 0 の経路を通っていません)。");}

            // 頂点の位置(接点からの相対)
            int common=0;int onlyNoLift=0;int onlyLift=0;double worstDifference=0;
            foreach(KeyValuePair<PlanetCellKey,Vector3[]> pair in positions[0]){
                if(!positions[1].TryGetValue(pair.Key,out Vector3[] lifted)){onlyNoLift++;continue;}
                common++;
                for(int i=0;i<pair.Value.Length;i++){worstDifference=Math.Max(worstDifference,(pair.Value[i]-lifted[i]).magnitude);}
            }
            foreach(PlanetCellKey key in positions[1].Keys){if(!positions[0].ContainsKey(key)){onlyLift++;}}
            report.Add("relief vertices: commonCells="+common+" onlyNoLift="+onlyNoLift+" onlyLift="+onlyLift+" worstPositionDifference="+(worstDifference*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um");
            if(common==0){failures.Add("持ち上げの有無で共通の Cell がありません。");}
            if(onlyNoLift+onlyLift>(common+onlyNoLift+onlyLift)/100){failures.Add("持ち上げの有無で選ばれる Cell が違います(共通 "+common+"、持ち上げなしだけ "+onlyNoLift+"、持ち上げありだけ "+onlyLift+")。");}
            if(!(worstDifference<=1e-6)){failures.Add("持ち上げの有無で頂点の位置が "+worstDifference*1e6+"um 違います(許す差 1um)。");}

            // 画像: 単色(ボールの輪郭と、月・背景)は 1 画素の帯の外で一致し、見た目(模様)は帯の外の画素の差が小さい。
            Color32[] flatA=flatImages[0].GetPixels32();Color32[] flatB=flatImages[1].GetPixels32();
            int width=flatImages[0].width;int height=flatImages[0].height;
            var ballMask=new bool[flatA.Length];
            int ballPixels=0;
            for(int i=0;i<flatA.Length;i++){ballMask[i]=Classify(flatA[i])==1;if(ballMask[i]){ballPixels++;}}
            bool[] band=Band(ballMask,width,height);
            int flatMismatch=0;
            for(int i=0;i<flatA.Length;i++){
                if(Classify(flatA[i])!=Classify(flatB[i])&&!band[i]){flatMismatch++;}
            }
            Color32[] shadedA=shadedImages[0].GetPixels32();Color32[] shadedB=shadedImages[1].GetPixels32();
            int shadedMismatch=0;int largestChannelDifference=0;
            for(int i=0;i<shadedA.Length;i++){
                if(band[i]){continue;}
                int difference=Math.Max(Math.Abs(shadedA[i].r-shadedB[i].r),Math.Max(Math.Abs(shadedA[i].g-shadedB[i].g),Math.Abs(shadedA[i].b-shadedB[i].b)));
                largestChannelDifference=Math.Max(largestChannelDifference,difference);
                if(difference>ShadedDifferenceLevel){shadedMismatch++;}
            }
            report.Add("relief images: ballPixels="+ballPixels+" flatMismatchOutside1px="+flatMismatch+" shadedMismatchOutside1px(>"+ShadedDifferenceLevel+")="+shadedMismatch+" largestChannelDifference="+largestChannelDifference);
            if(ballPixels<2000){failures.Add("ボールの画素が少なすぎます("+ballPixels+")。視点が合っていません。");}
            if(flatMismatch!=0){failures.Add("持ち上げの有無で、単色の画像が 1 画素の帯の外で "+flatMismatch+" 画素違います。");}
            if(shadedMismatch!=0){failures.Add("持ち上げの有無で、見た目の画像が 1 画素の帯の外で "+shadedMismatch+" 画素(差 "+ShadedDifferenceLevel+" 超)違います。");}
            report.Add("pool: ball used="+ball.UsedSlots+"/"+ball.PoolCapacity+" lastError="+(ball.LastError??"(none)")+" invalidBounds="+moon.InvalidBounds+"/"+ball.InvalidBounds);
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
            if(moon.InvalidBounds!=0||ball.InvalidBounds!=0){failures.Add("非有限の頂点を持つ Cell があります(月 "+moon.InvalidBounds+"、ボール "+ball.InvalidBounds+")。");}
        }

        // 見た目の画像の 1 チャンネルの差がこれ(0〜255)を超える画素を、違う画素として数える。
        private const int ShadedDifferenceLevel=24;

        // 選んだボールの Cell ごとの、全頂点の位置(接点からの相対、Logical で引いてから float)。
        private static Dictionary<PlanetCellKey,Vector3[]> ReadBallPositions(){
            var result=new Dictionary<PlanetCellKey,Vector3[]>();
            if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,demo.Contact,out LogicalVector3 contactWorld)){failures.Add("接点を求められません。");return result;}
            int grid=(PlanetLayerSource.CellQuads+1)*(PlanetLayerSource.CellQuads+1);
            foreach(PlanetCellKey key in ball.LastSelected){
                if(!ball.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frame)){continue;}
                if(!LogicalVector3.TryAdd(ballLogical.LocalPosition,frame.Origin,out LogicalVector3 originWorld)||!LogicalVector3.TrySubtract(in originWorld,in contactWorld,out LogicalVector3 d)||!d.TryToFloat3(out float3 o)){continue;}
                var points=new Vector3[grid];
                for(int i=0;i<grid;i++){points[i]=(Vector3)o+vertices[i].Position;}
                result[key]=points;
            }
            return result;
        }

        // 選択が収束するまで描く。width / height は撮影と同じ解像度にする(別の解像度だと、撮影の 1 frame 目は子の生成待ちの粗い選択になる)。
        private static IEnumerable<object> Converge(string name,int width=320,int height=240){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Destroy(Capture(width,height));
                int signature=moon.LastSelected.Count*31+ball.LastSelected.Count;
                foreach(PlanetCellKey key in moon.LastSelected){signature^=key.GetHashCode();}
                foreach(PlanetCellKey key in ball.LastSelected){signature^=key.GetHashCode()*17;}
                if(moon.PendingGeneration==0&&ball.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
        }

        private static void Destroy(UnityEngine.Object value){
            if(value!=null){UnityEngine.Object.DestroyImmediate(value);}
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        // 0: 背景(マゼンタ)、1: ボール(赤)、2: 月(灰色。単色の出力は 0.5 が 56 ほどになる)、3: それ以外。
        private static int Classify(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return 0;}
            if(pixel.r>200&&pixel.g<90&&pixel.b<90){return 1;}
            if(Mathf.Abs(pixel.r-pixel.g)<12&&Mathf.Abs(pixel.g-pixel.b)<12&&pixel.r>20&&pixel.r<200){return 2;}
            return 3;
        }

        private static int CountClass(Texture2D image,int value){
            int count=0;
            foreach(Color32 pixel in image.GetPixels32()){if(Classify(pixel)==value){count++;}}
            return count;
        }

        // camera の Logical の位置から、ボールの中心への差(float)。Logical で引いてから float にする。
        private static bool BallCenterFromCamera(out Vector3 center){
            center=default;
            if(!LogicalVector3.TrySubtract(ballLogical.LocalPosition,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 value)){return false;}
            center=value;
            return true;
        }

        // 画素ごとの光線(camera の向き、画角)。
        private static Vector3 Ray(int x,int y,int width,int height,Camera view=null){
            if(view==null){view=camera;}
            float tangent=Mathf.Tan(view.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            return (view.transform.rotation*local).normalized;
        }

        // 描かれたボールの画素と、Logical の位置から光線で求めた球の画素を比べる。解析の球の輪郭から 1 画素の帯の外で違う画素を数える。
        private static void CompareBall(string name,Texture2D image){
            if(!BallCenterFromCamera(out Vector3 center)){failures.Add(name+": ボールの中心を求められません。");return;}
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            float radius=ball.Radius;
            var analytic=new bool[pixels.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    Vector3 direction=Ray(x,y,width,height);
                    float b=Vector3.Dot(direction,center);
                    float c=Vector3.Dot(center,center)-radius*radius;
                    analytic[y*width+x]=b*b-c>=0&&b>0;
                }
            }
            bool[] band=Band(analytic,width,height);
            int expected=0;int rendered=0;int missing=0;int extra=0;int anomaly=0;
            int lowestRendered=-1;int lowestAnalytic=-1;
            for(int i=0;i<pixels.Length;i++){
                int c=Classify(pixels[i]);
                bool isBall=c==1;
                if(c==3){anomaly++;}
                if(analytic[i]){expected++;if(lowestAnalytic<0||i/width<lowestAnalytic){lowestAnalytic=i/width;}}
                if(isBall){rendered++;if(lowestRendered<0||i/width<lowestRendered){lowestRendered=i/width;}}
                if(isBall==analytic[i]||band[i]){continue;}
                if(analytic[i]){missing++;}else{extra++;}
            }
            report.Add(name+": ballPixels rendered="+rendered+" analytic="+expected+" missingOutside1px="+missing+" extraOutside1px="+extra+" anomalyPixels="+anomaly+
                " lowestRow rendered="+lowestRendered+" analytic="+lowestAnalytic);
            if(missing!=0||extra!=0){failures.Add(name+": 描かれたボールが、Logical の位置から求めた球と 1 画素の帯の外で違います(足りない "+missing+"、余分 "+extra+")。");}
            if(anomaly!=0){failures.Add(name+": 背景・ボール・月のどれでもない画素が "+anomaly+" ありました。");}
        }

        // 値が変わる境から 1 画素以内の画素(4 近傍の境から 3×3)。
        private static bool[] Band(bool[] mask,int width,int height){
            var band=new bool[mask.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    bool value=mask[y*width+x];
                    bool edge=false;
                    if(x>0&&mask[y*width+x-1]!=value){edge=true;}
                    if(x<width-1&&mask[y*width+x+1]!=value){edge=true;}
                    if(y>0&&mask[(y-1)*width+x]!=value){edge=true;}
                    if(y<height-1&&mask[(y+1)*width+x]!=value){edge=true;}
                    if(!edge){continue;}
                    for(int dy=-1;dy<=1;dy++){
                        for(int dx=-1;dx<=1;dx++){
                            int px=x+dx;int py=y+dy;
                            if(px>=0&&px<width&&py>=0&&py<height){band[py*width+px]=true;}
                        }
                    }
                }
            }
            return band;
        }

        // camera の真下(惑星の中心の向き)からの角度。altitude は接点の上の高さ。
        private static double NadirAngle(Vector3 direction){
            double dot=-Vector3.Dot(direction,demo.Up);
            return Math.Acos(Math.Max(-1.0,Math.Min(1.0,dot)));
        }

        // 背景の画素のうち、月の地平線より下を向く光線(真下からの角度が、地平線の角度より 2 画素以上小さい)の数。
        // 地平線の角度(真下から)= asin((R−Hmax)/(R+altitude))(内側の球の接線)。惑星の中心の位置は使わない(半径が大きいと float で潰れるため)。
        private static int CountHoles(Texture2D image,double altitude,Camera view=null){
            if(view==null){view=camera;}
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            double radius=moon.Radius;
            double ratio=(radius-(moon.HeightAmplitude+moon.RasterMaximumHeight))/(radius+altitude);
            double dip=Math.Asin(Math.Max(-1.0,Math.Min(1.0,ratio)));
            double pixelAngle=view.fieldOfView*Mathf.Deg2Rad/height;
            int holes=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    if(Classify(pixels[y*width+x])!=0){continue;}
                    if(NadirAngle(Ray(x,y,width,height,view))<dip-2*pixelAngle){holes++;}
                }
            }
            return holes;
        }

        // 月全体が見える高さでの、月の輪郭: 真下からの角度が asin(R/(R+altitude)) 未満の画素(解析)と、描かれた月とボールの画素の比較。
        private static int CompareMoonDisc(Texture2D image,double altitude){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            double radius=moon.Radius;
            double angular=Math.Asin(radius/(radius+altitude));
            var analytic=new bool[pixels.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    analytic[y*width+x]=NadirAngle(Ray(x,y,width,height))<angular;
                }
            }
            bool[] band=Band(analytic,width,height);
            int outside=0;
            for(int i=0;i<pixels.Length;i++){
                int c=Classify(pixels[i]);
                bool drawn=c==2||c==1||c==3;
                if(drawn!=analytic[i]&&!band[i]){outside++;}
            }
            return outside;
        }

        // 接触の隙間の測定値(メートル)。
        private struct ContactMeasure{
            // ボールの最下の頂点の、真下の月のメッシュとの高さの差(up 方向)。
            public float Lowest;
            // ボールの下側の 64 頂点の、真下の月のメッシュとの高さの差の最小(負なら食い込み)。
            public float Minimum;
            public int Found;
            public bool HasMoon;
            // 接点の真下の月のメッシュの高さ(接点の平面から up 方向)。
            public float MoonAtContact;
            public int BallVertices;
            public int Triangles;
        }

        // ボールと月の、描いている頂点(メッシュ)の隙間。
        // 接点からの相対位置(Logical で引いてから float)で、ボールの下側の頂点と、その真下の月のメッシュの三角形の高さを比べる。
        private static void ContactMeshGap(){
            demo.SetOffset(0,0,0);
            if(!MeasureContactMesh(out ContactMeasure measure)){return;}
            if(measure.Found==0){failures.Add("接触の隙間: ボールの下の月のメッシュが見つかりません。");}
            else if(!(Mathf.Abs(measure.Minimum)<=1e-4f&&Mathf.Abs(measure.Lowest)<=1e-3f)){failures.Add("接触の隙間が大きすぎます(最小 "+measure.Minimum*1e6f+"um、最下の頂点 "+measure.Lowest*1e6f+"um)。");}
        }

        // 接点の近くの月のメッシュの三角形(接点からの相対位置、Logical で引いてから float)。選んだ Cell のうち、接平面の方向に 0.3 m + Cell の大きさ以内のもの。
        private static List<Vector3> ReadMoonTriangles(LogicalVector3 contactWorld,Vector3 u,Vector3 v,out float nearest,out int nearestLevel){
            int side=PlanetLayerSource.CellQuads+1;
            var triangles=new List<Vector3>();
            nearest=float.MaxValue;nearestLevel=-1;
            foreach(PlanetCellKey key in moon.LastSelected){
                float cellSize=moon.Selector.QuadArc(key.Level)*PlanetLayerSource.CellQuads;
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,frame.Origin,out LogicalVector3 originWorld)||!LogicalVector3.TrySubtract(in originWorld,in contactWorld,out LogicalVector3 d)||!d.TryToFloat3(out float3 o)){continue;}
                // Cell の原点は地表の高さまで持ち上げた点(持ち上げない Cell は半径 R の球面上の点)で、起伏は頂点の位置に入っている。接点との距離は、接平面の向き(u、v)だけで測る。
                float distance=new Vector2(Vector3.Dot(o,u),Vector3.Dot(o,v)).magnitude;
                if(distance<nearest){nearest=distance;nearestLevel=key.Level;}
                if(distance>0.3f+cellSize){continue;}
                if(!moon.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                for(int y=0;y<side-1;y++){
                    for(int x=0;x<side-1;x++){
                        Vector3 a=(Vector3)o+vertices[y*side+x].Position;Vector3 b=(Vector3)o+vertices[y*side+x+1].Position;
                        Vector3 c=(Vector3)o+vertices[(y+1)*side+x].Position;Vector3 e=(Vector3)o+vertices[(y+1)*side+x+1].Position;
                        triangles.Add(a);triangles.Add(c);triangles.Add(b);
                        triangles.Add(b);triangles.Add(c);triangles.Add(e);
                    }
                }
            }
            return triangles;
        }

        // 今の位置のボールと月のメッシュを読み戻して、接触の隙間を測る(ボールの位置は動かさない)。ボールの高さ方向に低い 64 頂点で測る(傾きの小さい面で、最下の頂点の近くが最も近い)。
        // メッシュを読み戻せなければ false(failures に書く)。
        private static bool MeasureContactMesh(out ContactMeasure measure){
            measure=default;
            LogicalVector3 contactWorld;
            if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,demo.Contact,out contactWorld)){failures.Add("接点を求められません。");return false;}
            Vector3 up=demo.Up;Vector3 u=demo.AxisU;Vector3 v=demo.AxisV;
            int side=PlanetLayerSource.CellQuads+1;
            int grid=side*side;
            // ボールのメッシュ(回転なし)
            var ballPoints=new List<Vector3>();
            foreach(PlanetCellKey key in ball.LastSelected){
                if(!ball.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frame)){continue;}
                if(!LogicalVector3.TryAdd(ballLogical.LocalPosition,frame.Origin,out LogicalVector3 originWorld)||!LogicalVector3.TrySubtract(in originWorld,in contactWorld,out LogicalVector3 d)||!d.TryToFloat3(out float3 o)){continue;}
                for(int i=0;i<grid;i++){ballPoints.Add((Vector3)o+vertices[i].Position);}
            }
            // 月のメッシュ(接点の近く)
            List<Vector3> triangles=ReadMoonTriangles(contactWorld,u,v,out float nearest,out int nearestLevel);
            report.Add("contact mesh: moonCells="+moon.LastSelected.Count+" nearestCellOriginLateral="+nearest.ToString("G4",CultureInfo.InvariantCulture)+"m (level "+nearestLevel+")");
            if(ballPoints.Count==0||triangles.Count==0){failures.Add("接触の隙間: ボールまたは月のメッシュを読み戻せません(ボール "+ballPoints.Count+"、月の三角形 "+triangles.Count/3+")。");return false;}
            // ボールの頂点のうち、高い方向(up)に最も低い 64 個について、真下の月のメッシュの高さとの差を求める。
            ballPoints.Sort((a,b)=>Vector3.Dot(a,up).CompareTo(Vector3.Dot(b,up)));
            float minimum=float.MaxValue;float lowest=0;int found=0;
            for(int i=0;i<Mathf.Min(64,ballPoints.Count);i++){
                Vector3 p=ballPoints[i];
                if(!MeshHeightAt(triangles,u,v,up,Vector3.Dot(p,u),Vector3.Dot(p,v),out float height)){continue;}
                found++;
                float gap=Vector3.Dot(p,up)-height;
                if(gap<minimum){minimum=gap;}
                if(i==0){lowest=gap;}
            }
            {
                // 診断: ボールの最下の頂点の高さと、接点の真下の月のメッシュの高さ(接点の平面からの高さ)。
                Vector3 bottom=ballPoints[0];
                bool hasMoon=MeshHeightAt(triangles,u,v,up,0,0,out float moonAtContact);
                report.Add("contact mesh detail: ballLowestHeight="+(Vector3.Dot(bottom,up)*1e6f).ToString("G5",CultureInfo.InvariantCulture)+"um lateral=("+
                    (Vector3.Dot(bottom,u)*1e3f).ToString("G4",CultureInfo.InvariantCulture)+","+(Vector3.Dot(bottom,v)*1e3f).ToString("G4",CultureInfo.InvariantCulture)+")mm moonHeightAtContact="+
                    (hasMoon?(moonAtContact*1e6f).ToString("G5",CultureInfo.InvariantCulture)+"um":"(none)"));
                measure=new ContactMeasure{HasMoon=hasMoon,MoonAtContact=moonAtContact};
            }
            report.Add("contact mesh gap: ballVertices="+ballPoints.Count+" moonTriangles="+triangles.Count/3+" lowestVertexGap="+(lowest*1e6f).ToString("G4",CultureInfo.InvariantCulture)+"um minimumGap(64 lowest)="+
                (minimum*1e6f).ToString("G4",CultureInfo.InvariantCulture)+"um (checked "+found+")");
            measure.Lowest=lowest;measure.Minimum=minimum;measure.Found=found;measure.BallVertices=ballPoints.Count;measure.Triangles=triangles.Count/3;
            return true;
        }

        // 起伏のある地面の接触の測定値(メートル)。
        private struct RasterContact{
            // 接点の真下の月のメッシュの高さ(接点の平面から up 方向)。
            public float MoonAtContact;
            // 接点の面の傾き(±20 mm の中心差分の勾配の大きさ)。
            public float Slope;
            // 解析の球と月のメッシュの隙間(up 方向)の最小と、傾き s から決まる値 −r(√(1+s²)−1)。
            public double MinimumGap;
            public double ExpectedMinimum;
            // ボールのメッシュの頂点の、解析の球面からのずれの最大。
            public double WorstSphereError;
        }

        // 隙間 0 の位置で、月のメッシュと解析の球の隙間、ボールの頂点の球面からのずれを測る。ballPositions は <see cref="ReadBallPositions"/> の値(接点からの相対)。
        private static bool MeasureRasterContact(string name,Dictionary<PlanetCellKey,Vector3[]> ballPositions,out RasterContact result){
            result=default;
            if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,demo.Contact,out LogicalVector3 contactWorld)){failures.Add(name+": 接点を求められません。");return false;}
            Vector3 up=demo.Up;Vector3 u=demo.AxisU;Vector3 v=demo.AxisV;
            List<Vector3> all=ReadMoonTriangles(contactWorld,u,v,out float nearest,out int nearestLevel);
            // 問い合わせは接点の近く(水平 0.1 m 以内)の三角形だけで足りる。
            var triangles=new List<Vector3>();
            for(int i=0;i<all.Count;i+=3){
                bool near=false;
                for(int c=0;c<3;c++){
                    Vector3 point=all[i+c];
                    if(Mathf.Abs(Vector3.Dot(point,u))<=0.1f&&Mathf.Abs(Vector3.Dot(point,v))<=0.1f){near=true;break;}
                }
                if(near){triangles.Add(all[i]);triangles.Add(all[i+1]);triangles.Add(all[i+2]);}
            }
            report.Add(name+": moon mesh: cells="+moon.LastSelected.Count+" nearestCellOriginLateral="+nearest.ToString("G4",CultureInfo.InvariantCulture)+"m (level "+nearestLevel+") trianglesNear="+triangles.Count/3);
            if(triangles.Count==0){failures.Add(name+": 接点の近くの月のメッシュを読み戻せません。");return false;}
            // 傾き: 接点の三角形の 0.6 mm の勾配は float の刻み(持ち上げない Cell で 61 µm)で 0.1 ほど狂うので、±20 mm の中心差分にする。
            const float baseline=0.02f;
            if(!MeshHeightAt(triangles,u,v,up,0,0,out float centerHeight)||!MeshHeightAt(triangles,u,v,up,baseline,0,out float plusU)||!MeshHeightAt(triangles,u,v,up,-baseline,0,out float minusU)||
                !MeshHeightAt(triangles,u,v,up,0,baseline,out float plusV)||!MeshHeightAt(triangles,u,v,up,0,-baseline,out float minusV)){
                failures.Add(name+": 接点の近くの月のメッシュの高さを求められません。");return false;
            }
            double gradientU=(plusU-minusU)/(2.0*baseline);double gradientV=(plusV-minusV)/(2.0*baseline);
            double slope=Math.Sqrt(gradientU*gradientU+gradientV*gradientV);
            if(double.IsNaN(slope)||double.IsInfinity(slope)||float.IsNaN(centerHeight)||float.IsInfinity(centerHeight)){failures.Add(name+": 接点の近くの月のメッシュの高さが有限ではありません(NaN または inf の頂点)。");return false;}
            double radius=ball.Radius;
            double gapOffset=demo.OffsetY*MoonBallDemo.UnitMeters;
            // 最も食い込む点は、最下の点から上り方向へ r·s/√(1+s²)。その周り ±2.5 mm を 0.25 mm 刻みで、解析の球の高さ − 月のメッシュの高さ の最小を探す。
            double distanceToOptimum=radius*slope/Math.Sqrt(1.0+slope*slope);
            double centerU=0;double centerV=0;
            if(slope>0){centerU=distanceToOptimum*gradientU/slope;centerV=distanceToOptimum*gradientV/slope;}
            double best=double.MaxValue;
            for(int iu=-10;iu<=10;iu++){
                for(int iv=-10;iv<=10;iv++){
                    double qu=centerU+iu*0.25e-3;double qv=centerV+iv*0.25e-3;
                    double rho2=qu*qu+qv*qv;
                    if(rho2>=radius*radius){continue;}
                    if(!MeshHeightAt(triangles,u,v,up,(float)qu,(float)qv,out float meshHeight)){continue;}
                    double sphereHeight=gapOffset+radius-Math.Sqrt(radius*radius-rho2);
                    best=Math.Min(best,sphereHeight-meshHeight);
                }
            }
            if(best==double.MaxValue||double.IsNaN(best)||double.IsInfinity(best)){failures.Add(name+": 食い込みの最小を求める点で、月のメッシュが見つからないか、高さが有限ではありません。");return false;}
            // ボールの頂点: 中心 = 接点 + up·(r + 隙間)からの距離が r。
            Vector3 center=up*(float)(radius+gapOffset);
            double worstSphere=0;
            foreach(KeyValuePair<PlanetCellKey,Vector3[]> pair in ballPositions){
                for(int i=0;i<pair.Value.Length;i++){worstSphere=Math.Max(worstSphere,Math.Abs((pair.Value[i]-center).magnitude-radius));}
            }
            result=new RasterContact{MoonAtContact=centerHeight,Slope=(float)slope,MinimumGap=best,ExpectedMinimum=-radius*(Math.Sqrt(1.0+slope*slope)-1.0),WorstSphereError=worstSphere};
            return true;
        }

        // 接点の接平面の座標 (qu, qv) の真上下にある、月のメッシュの高さ(up 方向)。
        private static bool MeshHeightAt(List<Vector3> triangles,Vector3 u,Vector3 v,Vector3 up,float qu,float qv,out float height){
            return MeshHeightAt(triangles,u,v,up,qu,qv,out height,out float _,out float _);
        }

        // 同じで、三角形の平面の高さの勾配(u、v の方向)も返す。
        private static bool MeshHeightAt(List<Vector3> triangles,Vector3 u,Vector3 v,Vector3 up,float qu,float qv,out float height,out float gradientU,out float gradientV){
            height=0;gradientU=0;gradientV=0;
            for(int t=0;t<triangles.Count;t+=3){
                Vector3 a=triangles[t];Vector3 b=triangles[t+1];Vector3 c=triangles[t+2];
                float au=Vector3.Dot(a,u),av=Vector3.Dot(a,v),bu=Vector3.Dot(b,u),bv=Vector3.Dot(b,v),cu=Vector3.Dot(c,u),cv=Vector3.Dot(c,v);
                float denominator=(bv-cv)*(au-cu)+(cu-bu)*(av-cv);
                if(Mathf.Abs(denominator)<1e-18f){continue;}
                float w0=((bv-cv)*(qu-cu)+(cu-bu)*(qv-cv))/denominator;
                float w1=((cv-av)*(qu-cu)+(au-cu)*(qv-cv))/denominator;
                float w2=1f-w0-w1;
                const float epsilon=-1e-5f;
                if(w0<epsilon||w1<epsilon||w2<epsilon){continue;}
                float ah=Vector3.Dot(a,up),bh=Vector3.Dot(b,up),ch=Vector3.Dot(c,up);
                height=w0*ah+w1*bh+w2*ch;
                // 三角形の平面の高さの勾配(u、v の方向)の大きさ。
                float e1u=bu-au,e1v=bv-av,e1h=bh-ah,e2u=cu-au,e2v=cv-av,e2h=ch-ah;
                float determinant=e1u*e2v-e1v*e2u;
                if(Mathf.Abs(determinant)>1e-18f){
                    gradientU=(e1h*e2v-e1v*e2h)/determinant;
                    gradientV=(e1u*e2h-e1h*e2u)/determinant;
                }
                return true;
            }
            return false;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(observerCamera!=null){
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering-=OnEndCameraRendering;
                global::Omni.RadialGlobalWarpSourceRegistry.Unregister(observerCamera);
            }
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
