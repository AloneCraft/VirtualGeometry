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
using VirtualizedGeometry.Editor;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// PlaneSink デモ(<see cref="PlaneSinkDemo"/>)の検証。左(単純に拡大した面)と右(VG の面)に、同じボールを 1mm ずつ沈めて撮り、
    /// 描かれた画像を、Logical の位置から倍精度の光線で求めた球と面と、1 画素の帯の外で比べる。単純な拡大の左で誤差が再現し、VG の右では 0 であること。
    /// - 沈め: +2mm から −60mm まで 1mm 刻み(camera はボールに付いて動く)
    /// - 水平: 沈めたまま +X へ 0〜20mm、周回: yaw 0〜90°、距離: 0.350〜0.370m
    /// - 左だけ、面の原点までの距離 D を {1e4, 1e5, 1e6, 1e7} に変えて、誤差が D と共に増えること
    /// 合否(右): 全 frame で、不一致の画素 0、どの種類でもない画素 0、解析上のボールと右の面の画素が十分にあること(空振りで通らない)、ボールの画素数の差と最下の行の差が小さいこと、
    /// ボールが全部見える frame で、ボールの重心が画像の中央にあること。合否(左): 沈めで誤差が再現し、ボールの画素数が逆戻りし、D=1e4 では小さく、D と共に増えること。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlaneSinkValidation.Run(RunSpike は少ない frame で素の MeshRenderer が描かれることと D への依存を見るだけで、合否の閾値は見ない)
    /// </summary>
    public static class PlaneSinkValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private const int Background=0;
        private const int Ball=1;
        private const int Left=2;
        private const int Right=3;
        private const int Other=4;
        private const int ClassCount=5;
        // 右の面の合否: frame ごとに、ボールの画素数の解析との差の上限、解析上の画素数の下限、ボールの重心の画像の中央からのずれの上限(画素)。
        private const int BallCountTolerance=40;
        private const int MinimumAnalyticPixels=1000;
        private const double CentroidTolerance=3.0;
        private static readonly double[] Distances={1e4,1e5,1e6,1e7};

        private static string results;
        private static bool spike;
        private static PlaneSinkObjects objects;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static PlaneSinkDemo demo;
        private static Material leftFlat,groundFlat,ballFlat;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly List<string> csv=new List<string>();
        private static readonly List<FrameMetrics> frames=new List<FrameMetrics>();

        private sealed class FrameMetrics{
            public string Side;
            public string Phase;
            public double Parameter;
            public double Distance;
            public int[] Rendered=new int[ClassCount];
            public int[] Analytic=new int[ClassCount];
            // 種類ごとに、その種類の領域の境から 1 画素の帯の外で違う画素の数(1 つの違う画素が、2 つの種類で数えられる)。
            public int[] Mismatch=new int[ClassCount];
            // どの種類の帯にも入らない、描画と解析で種類が違う画素の数(1 つの違う画素を 1 回だけ数える)。合否に使う。
            public int MismatchPixels;
            public int Anomaly;
            public int LowestRendered;
            public int LowestAnalytic;
            // ボールの画素の重心(画像の座標。描画と解析)。ボールの画素が無ければ NaN。
            public double CentroidX;
            public double CentroidY;
            public double AnalyticCentroidX;
            public double AnalyticCentroidY;
        }

        public static void Run(){
            spike=false;
            Begin("TestResults/Planet/PlaneSink");
        }

        /// <summary>少ない frame で、素の MeshRenderer が描かれるか、誤差が D と共に増えるかを確かめる(結果は TestResults/Planet/PlaneSinkSpike)。</summary>
        public static void RunSpike(){
            spike=true;
            Begin("TestResults/Planet/PlaneSinkSpike");
        }

        private static void Begin(string output){
            results=output;
            report.Clear();failures.Clear();errors.Clear();csv.Clear();frames.Clear();started=EditorApplication.timeSinceStartup;
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
                if(EditorApplication.timeSinceStartup-started>3400){throw new TimeoutException("PlaneSink validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            objects=PlaneSinkSceneBuilder.Populate();
            HeightShiftOverride.ApplyToScene();
            camera=objects.Camera;camera.backgroundColor=new Color(1,0,1,1);
            cameraLogical=objects.CameraObject.GetComponent<global::Omni.Logical.Transform>();
            demo=objects.Demo;
            // 単色(照明・模様なし)の material: 左の面は青、右の面は緑、ボールは赤。見た目の material は別に持つ。
            leftFlat=new Material(objects.LeftMaterial);leftFlat.SetFloat("_FlatColor",1);leftFlat.SetColor("_FlatTint",new Color(0.1f,0.1f,1f,1));
            groundFlat=new Material(objects.GroundMaterial);groundFlat.SetFloat("_FlatColor",1);groundFlat.SetColor("_FlatTint",new Color(0.1f,1f,0.1f,1));
            ballFlat=new Material(objects.BallMaterial);ballFlat.SetFloat("_FlatColor",1);ballFlat.SetColor("_FlatTint",new Color(1f,0.1f,0.1f,1));
            // 検証の間は Update を回さない(操作の入力を読まない)。壁は解析に入れないので隠す。
            demo.enabled=false;
            objects.Wall.SetActive(false);
        }

        private static void SetFlat(bool value){
            if(value){
                objects.GroundSource.Material=groundFlat;objects.BallSource.Material=ballFlat;demo.SetLeftMaterial(leftFlat);
            }else{
                objects.GroundSource.Material=objects.GroundMaterial;objects.BallSource.Material=objects.BallMaterial;demo.SetLeftMaterial(objects.LeftMaterial);
            }
        }

        private static IEnumerator Steps(){
            PlaneSinkLayout layout=PlaneSinkLayout.Default;
            report.Add("layout: D="+layout.OriginDistance.ToString("G6",CultureInfo.InvariantCulture)+" S="+layout.PlaneScale.ToString("G7",CultureInfo.InvariantCulture)+" quantum="+(layout.LeftQuantum()*1000).ToString("G4",CultureInfo.InvariantCulture)+
                "mm stepHeight="+layout.StepHeight+" rightRadius="+layout.RightRadius.ToString("G6",CultureInfo.InvariantCulture)+" camera near logical="+0.3f/10f+"m");
            int waited=0;
            while(objects.GroundSource.Selector==null||objects.BallSource.Selector==null){
                if(waited++>100){throw new InvalidOperationException("右の天体とボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            SetFlat(true);
            Mesh planeMesh=objects.ScaledPlane.GetComponent<MeshFilter>().sharedMesh;
            Mesh wallMesh=objects.Wall.GetComponent<MeshFilter>().sharedMesh;
            report.Add("meshes: left plane vertices="+planeMesh.vertexCount+" indexFormat="+planeMesh.indexFormat+" wall vertices="+wallMesh.vertexCount);

            if(spike){
                foreach(object step in SpikeSteps()){yield return step;}
            }else{
                foreach(object step in FullSteps()){yield return step;}
            }
            for(int i=0;i<2;i++){
                PlanetTerrainSource source=objects.GroundSource;
                if(i==1){source=objects.BallSource;}
                report.Add("pool "+source.name+": used="+source.UsedSlots+"/"+source.PoolCapacity+" selected="+source.LastSelected.Count+" evictions="+source.Evictions+" starved="+source.StarvedRequests+" lastError="+(source.LastError??"(none)"));
                if(source.LastError!=null){failures.Add("設定エラー("+source.name+"): "+source.LastError);}
            }
        }

        // ---- spike

        private static IEnumerable<object> SpikeSteps(){
            // 左右の面に、ボールを 30mm 沈めた frame と、浮かせた frame。
            foreach(PlaneSinkDemo.Area area in new[]{PlaneSinkDemo.Area.Left,PlaneSinkDemo.Area.Right}){
                string side=area.ToString();
                foreach(long y in new long[]{20,0,-300}){
                    demo.SetState(area,0,y,0,0f,20f,0.35f);
                    foreach(object step in Observe(side,"spike",y*0.1,true)){yield return step;}
                }
            }
            // 左だけ、D を変えて 30mm 沈めた frame と、水平に 10mm 動いた frame。
            PlaneSinkLayout layout=demo.Layout;
            foreach(double distance in Distances){
                layout.OriginDistance=distance;
                demo.SetLayout(layout);
                foreach(long x in new long[]{0,100}){
                    demo.SetState(PlaneSinkDemo.Area.Left,x,-300,0,0f,20f,0.35f);
                    foreach(object step in Observe("Left","distance"+distance.ToString("G3",CultureInfo.InvariantCulture),x*0.1,true)){yield return step;}
                }
            }
        }

        // ---- 本番

        private static IEnumerable<object> FullSteps(){
            foreach(PlaneSinkDemo.Area area in new[]{PlaneSinkDemo.Area.Left,PlaneSinkDemo.Area.Right}){
                string side=area.ToString();
                PlaneSinkLayout layout=PlaneSinkLayout.Default;
                demo.SetLayout(layout);
                // (a) 沈め: +2mm から −60mm まで 1mm 刻み。
                for(int k=20;k>=-600;k-=10){
                    demo.SetState(area,0,k,0,0f,20f,0.35f);
                    bool save=k==20||k==-300||k==-600;
                    foreach(object step in Observe(side,"sink",k*0.1,save)){yield return step;}
                }
                // (b) 水平: 30mm 沈めたまま +X へ 0〜20mm。
                for(int x=0;x<=200;x+=10){
                    demo.SetState(area,x,-300,0,0f,20f,0.35f);
                    foreach(object step in Observe(side,"roll",x*0.1,false)){yield return step;}
                }
                // (c) 周回: yaw 0〜90°(3° 刻み)。
                for(int yaw=0;yaw<=90;yaw+=3){
                    demo.SetState(area,0,-300,0,yaw,20f,0.35f);
                    foreach(object step in Observe(side,"orbit",yaw,yaw==45)){yield return step;}
                }
                // (d) 距離: 0.350〜0.370m(1mm 刻み)。
                for(int d=0;d<=20;d++){
                    demo.SetState(area,0,-300,0,0f,20f,0.35f+d*0.001f);
                    foreach(object step in Observe(side,"distance",d,false)){yield return step;}
                }
            }
            // (e) 左だけ、面の原点までの距離 D を変える(沈め 2mm 刻みと、水平 2mm 刻み)。
            foreach(double distance in Distances){
                PlaneSinkLayout layout=PlaneSinkLayout.Default;
                layout.OriginDistance=distance;
                demo.SetLayout(layout);
                string phase=DistancePhase(distance);
                for(int k=20;k>=-400;k-=20){
                    demo.SetState(PlaneSinkDemo.Area.Left,0,k,0,0f,20f,0.35f);
                    foreach(object step in Observe("Left",phase+"-sink",k*0.1,k==-300&&distance==1e7)){yield return step;}
                }
                for(int x=0;x<=200;x+=20){
                    demo.SetState(PlaneSinkDemo.Area.Left,x,-300,0,0f,20f,0.35f);
                    foreach(object step in Observe("Left",phase+"-roll",x*0.1,false)){yield return step;}
                }
            }
            PlaneSinkLayout restore=PlaneSinkLayout.Default;
            demo.SetLayout(restore);
            // 見た目(照明と格子、壁あり)。
            objects.Wall.SetActive(true);
            SetFlat(false);
            foreach(PlaneSinkDemo.Area area in new[]{PlaneSinkDemo.Area.Left,PlaneSinkDemo.Area.Right}){
                demo.SetState(area,0,-300,0,0f,20f,0.35f);
                foreach(object step in Converge("Shaded"+area,Width,ImageHeight)){yield return step;}
                Texture2D image=Capture();Save("Shaded"+area+"_sink30mm",image);Destroy(image);
                demo.SetState(area,0,-300,0,0f,35f,12f);
                foreach(object step in Converge("Overview"+area,Width,ImageHeight)){yield return step;}
                image=Capture();Save("Shaded"+area+"_overview",image);Destroy(image);
            }
            objects.Wall.SetActive(false);
            SetFlat(true);
            Summarize();
        }

        private static string DistancePhase(double distance){
            return "D"+distance.ToString("G3",CultureInfo.InvariantCulture);
        }

        // ---- 1 frame の撮影と解析

        // 今の状態(demo)の画像を撮り、解析と比べて記録する。選択は撮影と同じ解像度で収束させてから撮る。
        private static IEnumerable<object> Observe(string side,string phase,double parameter,bool save){
            foreach(object step in Converge(side+phase+parameter,Width,ImageHeight)){yield return step;}
            Texture2D image=Capture();
            if(save){Save(side+"_"+phase+"_"+parameter.ToString("0.#",CultureInfo.InvariantCulture),image);}
            FrameMetrics metrics=Measure(image);
            metrics.Side=side;metrics.Phase=phase;metrics.Parameter=parameter;metrics.Distance=demo.Layout.OriginDistance;
            Destroy(image);
            frames.Add(metrics);
            csv.Add(string.Join(",",new object[]{side,phase,parameter.ToString("G6",CultureInfo.InvariantCulture),metrics.Distance.ToString("G3",CultureInfo.InvariantCulture),
                metrics.Rendered[Ball],metrics.Analytic[Ball],metrics.MismatchPixels,metrics.Mismatch[Ball],metrics.Mismatch[Left],metrics.Mismatch[Right],metrics.Mismatch[Background],metrics.Anomaly,
                metrics.LowestRendered,metrics.LowestAnalytic,metrics.CentroidX.ToString("F2",CultureInfo.InvariantCulture),metrics.CentroidY.ToString("F2",CultureInfo.InvariantCulture),
                metrics.AnalyticCentroidX.ToString("F2",CultureInfo.InvariantCulture),metrics.AnalyticCentroidY.ToString("F2",CultureInfo.InvariantCulture)}));
            if(spike||(phase=="sink"&&(Math.Abs(parameter+30)<0.01||Math.Abs(parameter-2)<0.01))){
                report.Add(side+" "+phase+" "+parameter.ToString("0.#",CultureInfo.InvariantCulture)+" D="+metrics.Distance.ToString("G3",CultureInfo.InvariantCulture)+": "+Describe(metrics));
            }
        }

        private static string Describe(FrameMetrics m){
            return "rendered bg/ball/left/right/other="+m.Rendered[0]+"/"+m.Rendered[1]+"/"+m.Rendered[2]+"/"+m.Rendered[3]+"/"+m.Rendered[4]+" analytic="+m.Analytic[0]+"/"+m.Analytic[1]+"/"+m.Analytic[2]+"/"+m.Analytic[3]+
                " mismatchPixels="+m.MismatchPixels+" (outside 1px: ball "+m.Mismatch[Ball]+" left "+m.Mismatch[Left]+" right "+m.Mismatch[Right]+" bg "+m.Mismatch[Background]+") anomaly="+m.Anomaly+" lowestBallRow="+m.LowestRendered+"/"+m.LowestAnalytic+
                " ballCentroid rendered=("+m.CentroidX.ToString("F1",CultureInfo.InvariantCulture)+","+m.CentroidY.ToString("F1",CultureInfo.InvariantCulture)+") analytic=("+m.AnalyticCentroidX.ToString("F1",CultureInfo.InvariantCulture)+","+m.AnalyticCentroidY.ToString("F1",CultureInfo.InvariantCulture)+")";
        }

        // 選択は撮影と同じ解像度で収束させる(別の解像度だと、画面誤差が変わり、撮影の frame で新しい Cell の生成が始まる)。
        private static IEnumerable<object> Converge(string name,int width,int height){
            int stable=0;int previous=-1;int count=0;
            while(count<ConvergeLimit){
                Destroy(Capture(width,height));
                int signature=0;
                int pending=0;
                for(int i=0;i<2;i++){
                    PlanetTerrainSource source=objects.GroundSource;
                    if(i==1){source=objects.BallSource;}
                    signature=signature*31+source.LastSelected.Count;
                    foreach(PlanetCellKey key in source.LastSelected){signature^=key.GetHashCode()*(i+1);}
                    pending+=source.PendingGeneration;
                }
                if(pending==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                count++;
                if(stable>=2){break;}
                yield return null;
            }
            if(count>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
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

        // 0: 背景(マゼンタ)、1: ボール(赤)、2: 左の面(青)、3: 右の面(緑)、4: それ以外。
        private static int Classify(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return Background;}
            if(pixel.r>200&&pixel.g<90&&pixel.b<90){return Ball;}
            if(pixel.b>200&&pixel.r<90&&pixel.g<90){return Left;}
            if(pixel.g>200&&pixel.r<90&&pixel.b<90){return Right;}
            return Other;
        }

        // 画素ごとの光線(camera の向き、画角)。向きは demo が置いた回転(camera.transform は使わない)。
        private static double3 Ray(Quaternion rotation,int x,int y,int width,int height){
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            Vector3 direction=(rotation*local).normalized;
            return new double3(direction.x,direction.y,direction.z);
        }

        private static bool TryDouble3(in LogicalVector3 value,out double3 result){
            result=default;
            if(!value.X.TryToDouble(out double x)||!value.Y.TryToDouble(out double y)||!value.Z.TryToDouble(out double z)){return false;}
            result=new double3(x,y,z);
            return true;
        }

        // 解析の面・球(world 座標。camera の位置も倍精度。位置は原点の近く(数 m)なので倍精度で十分)。
        private sealed class AnalyticScene{
            public double3 Camera;
            public double3 BallCenter;
            public double BallRadius;
            public double LeftY;
            public double3 GroundCenter;
            public double GroundRadius;
        }

        private static AnalyticScene BuildScene(){
            var scene=new AnalyticScene();
            if(!TryDouble3(cameraLogical.LocalPosition,out scene.Camera)){throw new InvalidOperationException("camera の位置を倍精度にできません。");}
            if(!demo.TryGetBallCenter(out LogicalVector3 center)||!TryDouble3(in center,out scene.BallCenter)){throw new InvalidOperationException("ボールの位置を倍精度にできません。");}
            scene.BallRadius=objects.BallSource.Radius;
            PlaneSinkLayout layout=demo.Layout;
            scene.LeftY=layout.StepHeight;
            scene.GroundCenter=new double3(layout.RightSpotX,-layout.RightRadius,0);
            scene.GroundRadius=objects.GroundSource.Radius;
            return scene;
        }

        // 光線が最初に当たるものの種類。球は外側から入る向きだけ(裏面は描かない)。左の面は camera が面より上のときの、x<0 の側だけ(右端が x=0)。
        private static int AnalyticClass(AnalyticScene scene,double3 direction){
            double best=double.MaxValue;int result=Background;
            double3 ballRelative=scene.BallCenter-scene.Camera;
            {
                double b=math.dot(direction,ballRelative);
                double c=math.dot(ballRelative,ballRelative)-scene.BallRadius*scene.BallRadius;
                double discriminant=b*b-c;
                if(c>0&&b>0&&discriminant>=0){
                    double t=c/(b+math.sqrt(discriminant));
                    if(t<best){best=t;result=Ball;}
                }
            }
            double leftRelative=scene.LeftY-scene.Camera.y;
            if(direction.y<0&&leftRelative<0){
                double t=leftRelative/direction.y;
                if(scene.Camera.x+t*direction.x<0&&t<best){best=t;result=Left;}
            }
            {
                double3 groundRelative=scene.GroundCenter-scene.Camera;
                double b=math.dot(direction,groundRelative);
                double c=math.dot(groundRelative,groundRelative)-scene.GroundRadius*scene.GroundRadius;
                double discriminant=b*b-c;
                if(c>0&&b>0&&discriminant>=0){
                    double t=c/(b+math.sqrt(discriminant));
                    if(t<best){best=t;result=Right;}
                }
            }
            return result;
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

        private static FrameMetrics Measure(Texture2D image){
            AnalyticScene scene=BuildScene();
            Quaternion rotation=demo.CameraRotation;
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            var analytic=new int[pixels.Length];
            var rendered=new int[pixels.Length];
            var metrics=new FrameMetrics();
            double sumX=0;double sumY=0;int ballPixels=0;
            double analyticSumX=0;double analyticSumY=0;int analyticBallPixels=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int i=y*width+x;
                    analytic[i]=AnalyticClass(scene,Ray(rotation,x,y,width,height));
                    rendered[i]=Classify(pixels[i]);
                    metrics.Rendered[rendered[i]]++;metrics.Analytic[analytic[i]]++;
                    if(rendered[i]==Other){metrics.Anomaly++;}
                    if(rendered[i]==Ball){sumX+=x+0.5;sumY+=y+0.5;ballPixels++;}
                    if(analytic[i]==Ball){analyticSumX+=x+0.5;analyticSumY+=y+0.5;analyticBallPixels++;}
                }
            }
            metrics.CentroidX=double.NaN;metrics.CentroidY=double.NaN;metrics.AnalyticCentroidX=double.NaN;metrics.AnalyticCentroidY=double.NaN;
            if(ballPixels>0){metrics.CentroidX=sumX/ballPixels;metrics.CentroidY=sumY/ballPixels;}
            if(analyticBallPixels>0){metrics.AnalyticCentroidX=analyticSumX/analyticBallPixels;metrics.AnalyticCentroidY=analyticSumY/analyticBallPixels;}
            int[] compared={Background,Ball,Left,Right};
            var anyBand=new bool[pixels.Length];
            foreach(int cls in compared){
                var mask=new bool[pixels.Length];
                for(int i=0;i<pixels.Length;i++){mask[i]=analytic[i]==cls;}
                bool[] band=Band(mask,width,height);
                for(int i=0;i<pixels.Length;i++){
                    if(band[i]){anyBand[i]=true;}
                    bool isClass=rendered[i]==cls;
                    if(isClass!=mask[i]&&!band[i]){metrics.Mismatch[cls]++;}
                }
            }
            for(int i=0;i<pixels.Length;i++){
                if(rendered[i]!=analytic[i]&&!anyBand[i]){metrics.MismatchPixels++;}
            }
            // 中央の列で、最も下(行 0 が下)にあるボールの行。
            metrics.LowestRendered=-1;metrics.LowestAnalytic=-1;
            int column=width/2;
            for(int y=0;y<height;y++){
                if(metrics.LowestRendered<0&&rendered[y*width+column]==Ball){metrics.LowestRendered=y;}
                if(metrics.LowestAnalytic<0&&analytic[y*width+column]==Ball){metrics.LowestAnalytic=y;}
            }
            return metrics;
        }

        // ---- 集計と合否

        private static void Summarize(){
            // 側と段ごとの集計。
            var groups=new List<string>();
            foreach(FrameMetrics m in frames){
                string key=m.Side+"|"+m.Phase;
                if(!groups.Contains(key)){groups.Add(key);}
            }
            var worstPixels=new Dictionary<string,int>();
            var reversalCounts=new Dictionary<string,int>();
            foreach(string key in groups){
                int count=0;int maxPixels=0;int sumPixels=0;int maxAnomaly=0;int maxRowDifference=0;int reversals=0;int previousBall=-1;int maxCountDifference=0;
                bool sinkPhase=key.EndsWith("sink",StringComparison.Ordinal);
                foreach(FrameMetrics m in frames){
                    if(m.Side+"|"+m.Phase!=key){continue;}
                    count++;sumPixels+=m.MismatchPixels;
                    maxPixels=Math.Max(maxPixels,m.MismatchPixels);
                    maxAnomaly=Math.Max(maxAnomaly,m.Anomaly);
                    if(m.LowestRendered>=0&&m.LowestAnalytic>=0){maxRowDifference=Math.Max(maxRowDifference,Math.Abs(m.LowestRendered-m.LowestAnalytic));}
                    maxCountDifference=Math.Max(maxCountDifference,Math.Abs(m.Rendered[Ball]-m.Analytic[Ball]));
                    // 沈めの段は、沈めるほどボールの見える画素は減るはず。2 画素を超えて増えたら逆戻り。
                    if(sinkPhase&&previousBall>=0&&m.Rendered[Ball]>previousBall+2){reversals++;}
                    previousBall=m.Rendered[Ball];
                }
                worstPixels[key]=maxPixels;reversalCounts[key]=reversals;
                string reversalText="n/a (not a sink phase)";
                if(sinkPhase){reversalText=reversals.ToString(CultureInfo.InvariantCulture);}
                report.Add("summary "+key.Replace("|"," ")+": frames="+count+" maxMismatchPixels="+maxPixels+" sumMismatchPixels="+sumPixels+" maxAnomaly="+maxAnomaly+
                    " maxLowestRowDiff="+maxRowDifference+" maxBallCountDiff="+maxCountDifference+" ballCountReversals(>2px)="+reversalText);
            }

            // 右(VG): 全 frame で、解析と 1 画素の帯の外で一致し、空振りではないこと。
            int rightFrames=0;
            foreach(FrameMetrics m in frames){
                if(m.Side!="Right"){continue;}
                rightFrames++;
                string name="右(VG) "+m.Phase+" "+m.Parameter.ToString("G4",CultureInfo.InvariantCulture);
                if(m.MismatchPixels!=0){failures.Add(name+": 解析と 1 画素の帯の外で "+m.MismatchPixels+" 画素違います("+Describe(m)+")。");}
                if(m.Anomaly!=0){failures.Add(name+": どの種類でもない画素が "+m.Anomaly+" ありました。");}
                if(m.Analytic[Ball]<MinimumAnalyticPixels||m.Analytic[Right]<MinimumAnalyticPixels){failures.Add(name+": 解析上のボール("+m.Analytic[Ball]+")か右の面("+m.Analytic[Right]+")の画素が少なすぎます(空振りの疑い)。");}
                if(Math.Abs(m.Rendered[Ball]-m.Analytic[Ball])>BallCountTolerance){failures.Add(name+": ボールの画素数が解析と "+Math.Abs(m.Rendered[Ball]-m.Analytic[Ball])+" 違います(許す差 "+BallCountTolerance+")。");}
                if(m.LowestAnalytic>=0&&(m.LowestRendered<0||Math.Abs(m.LowestRendered-m.LowestAnalytic)>1)){failures.Add(name+": 中央の列の最下のボールの行が解析と 1 行を超えて違います("+m.LowestRendered+"/"+m.LowestAnalytic+")。");}
            }
            if(rightFrames<100){failures.Add("右(VG)の frame が "+rightFrames+" 件しかありません(100 件以上のはず)。");}

            // ボールが全部見える frame(沈めの段で、浮いている・接している: 0mm 以上)で、ボールの重心が画像の中央にある(camera が常にボールを向く)。
            // 解析のボールの重心は全 frame で調べる(demo の camera とボールの位置の関係)。描画のボールの重心は、右(VG)だけで調べる
            // (左は誤差で面がボールの手前に出て、重心がずれうる。それは左の誤差であって、camera の向きではない)。
            int centroidFrames=0;
            foreach(FrameMetrics m in frames){
                if(!m.Phase.EndsWith("sink",StringComparison.Ordinal)||m.Parameter<0){continue;}
                centroidFrames++;
                double ax=m.AnalyticCentroidX-Width*0.5;double ay=m.AnalyticCentroidY-ImageHeight*0.5;
                string name=m.Side+" "+m.Phase+" "+m.Parameter.ToString("G4",CultureInfo.InvariantCulture);
                if(double.IsNaN(ax)||double.IsNaN(ay)||Math.Abs(ax)>CentroidTolerance||Math.Abs(ay)>CentroidTolerance){
                    failures.Add(name+": 解析のボールの重心が画像の中央から ("+ax.ToString("F2",CultureInfo.InvariantCulture)+","+ay.ToString("F2",CultureInfo.InvariantCulture)+") 画素ずれています(許す差 "+CentroidTolerance+")。");
                }
                if(m.Side=="Right"){
                    double dx=m.CentroidX-Width*0.5;double dy=m.CentroidY-ImageHeight*0.5;
                    if(double.IsNaN(dx)||double.IsNaN(dy)||Math.Abs(dx)>CentroidTolerance||Math.Abs(dy)>CentroidTolerance){
                        failures.Add(name+": 描画のボールの重心が画像の中央から ("+dx.ToString("F2",CultureInfo.InvariantCulture)+","+dy.ToString("F2",CultureInfo.InvariantCulture)+") 画素ずれています(許す差 "+CentroidTolerance+")。");
                    }
                }
            }
            if(centroidFrames==0){failures.Add("重心を調べる frame がありません。");}

            // 左(単純な拡大): 誤差が再現すること(D=10^6 m の沈めで、不一致の画素が最大 100 以上、ボールの画素数の逆戻りが 1 回以上)。
            int leftSink=0;int leftReversals=0;
            if(worstPixels.TryGetValue("Left|sink",out int value)){leftSink=value;}else{failures.Add("左の沈めの段がありません。");}
            if(reversalCounts.TryGetValue("Left|sink",out int reversalValue)){leftReversals=reversalValue;}
            if(leftSink<100){failures.Add("左(単純な拡大): 沈めの不一致が最大 "+leftSink+" 画素しかなく、誤差が再現していません(100 画素以上のはず)。");}
            if(leftReversals<1){failures.Add("左(単純な拡大): 沈めで、ボールの画素数が逆戻りしませんでした(沈めが飛ぶはず)。");}

            // D を変える段: D=10^4 m(物体空間の float の刻みが約 0.6 mm)では右に近く(不一致 100 画素以下)、D と共に増えること(D=10^6 を 5 倍以上超えるのが D=10^7)。
            int[] byDistance=new int[Distances.Length];
            for(int i=0;i<Distances.Length;i++){
                string key="Left|"+DistancePhase(Distances[i])+"-sink";
                if(!worstPixels.TryGetValue(key,out byDistance[i])){failures.Add("左の D="+Distances[i].ToString("G3",CultureInfo.InvariantCulture)+" の沈めの段がありません。");byDistance[i]=-1;}
            }
            if(byDistance[0]>100){failures.Add("左(単純な拡大): D=1e4 でも不一致が "+byDistance[0]+" 画素あります(刻み 0.6 mm なので 100 画素以下のはず)。");}
            for(int i=1;i<Distances.Length;i++){
                if(byDistance[i]>=0&&byDistance[i-1]>=0&&byDistance[i]<byDistance[i-1]){failures.Add("左(単純な拡大): D="+Distances[i].ToString("G3",CultureInfo.InvariantCulture)+" の不一致 "+byDistance[i]+" が、1 つ小さい D の "+byDistance[i-1]+" より少ないです(D と共に増えるはず)。");}
            }
            if(byDistance[2]>=0&&byDistance[3]>=0&&byDistance[3]<5*byDistance[2]){failures.Add("左(単純な拡大): D=1e7 の不一致 "+byDistance[3]+" が、D=1e6 の "+byDistance[2]+" の 5 倍未満です。");}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(spike){
                // spike は左右の閾値を見ない(設定エラーと Unity のエラーだけ)。PASSED とは書かない。
                report.Add("(spike: left/right thresholds are not checked)");
                if(failures.Count==0){report.Add("SPIKE-DONE");}else{report.Add("FAILED");report.AddRange(failures);}
            }else{
                if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            }
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            var lines=new List<string>{"side,phase,parameter,D,ballRendered,ballAnalytic,mismatchPixels,mismatchBall,mismatchLeft,mismatchRight,mismatchBackground,anomaly,lowestRowRendered,lowestRowAnalytic,centroidX,centroidY,analyticCentroidX,analyticCentroidY"};
            lines.AddRange(csv);
            File.WriteAllLines(results+"/Metrics.csv",lines);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
