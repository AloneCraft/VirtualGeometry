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

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// PlaneSeam デモ(<see cref="PlaneSeamDemo"/>)の検証。左(単純に拡大した面)と右(VG の面)を同じ高さで継ぎ目 x=0 に隣り合わせて撮り、
    /// 描かれた単色の画像(左 青、右 緑、背景 マゼンタ)を、Logical の位置から倍精度の光線で求めた 2 枚の面(左 y=0 の x&lt;0、右 y=δ の x≥0)と、1 画素の帯の外で比べる。
    /// - (a) 右だけ(左の面を消す): 右の面(VG)が解析の位置にあること。D によらず、全 frame で不一致の画素が RightOnlyTolerance 以下。
    /// - (b) 左右とも: 継ぎ目まわりで、左(単純な拡大)の誤差が、面の原点までの距離 D と共に増えること。右の高さの差 δ を −3〜+3mm(1mm 刻み)に振る。
    /// 解析は継ぎ目の段差の面(右の縁の skirt)を含めない。段差の面は δ が数 mm、縁の太さは 1 画素の帯に入る。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlaneSeamValidation.Run
    /// </summary>
    public static class PlaneSeamValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private const int Background=0;
        private const int Left=1;
        private const int Right=2;
        private const int Other=3;
        private const int ClassCount=4;
        // 右(VG)の合否: 解析上の右の面の画素の下限(空振りで通らない)。
        private const int MinimumRightPixels=1000;
        // 右(VG)だけの、1 frame あたりの不一致の画素の上限。観測は 0〜2 画素(継ぎ目の線と、平面の遠い端の線の角の、1 画素の帯の外 1〜2 画素。画像 307,200 画素の 0.001% 以下)。
        private const int RightOnlyTolerance=4;
        private static readonly double[] Distances={1e4,1e5,1e6,1e7};
        // δ(右の面の高さの差、0.1mm 単位): −3、−2、−1、0、+1、+2、+3 mm。
        private static readonly long[] Offsets={-30,-20,-10,0,10,20,30};
        // 継ぎ目を見る視点: (camera の位置、見る点)。
        private static readonly (double3 From,double3 Target)[] Poses={
            (new double3(-0.2,0.3,-0.8),new double3(0,0,0)),
            (new double3(0.15,0.2,-0.5),new double3(0,0,0)),
            (new double3(-0.05,0.1,-0.3),new double3(0,0,0)),
        };

        private static string results;
        private static bool exitWhenDone=true;
        private static PlaneSeamObjects objects;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static PlaneSeamDemo demo;
        private static Material leftFlat,groundFlat;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly List<string> csv=new List<string>();
        private static readonly List<FrameMetrics> frames=new List<FrameMetrics>();

        private sealed class FrameMetrics{
            public string Phase;
            public double Distance;
            public double Offset;
            public int Pose;
            public int[] Rendered=new int[ClassCount];
            public int[] Analytic=new int[ClassCount];
            // どの種類の帯にも入らない、描画と解析で種類が違う画素の数(1 つの違う画素を 1 回だけ数える)。合否に使う。
            public int MismatchPixels;
            public int Anomaly;
        }

        public static void Run(){
            exitWhenDone=true;
            Begin("TestResults/Planet/PlaneSeam");
        }

        /// <summary>開いている Editor の中で走らせる(終了しない)。scene は新しい空の scene に替わる。結果は同じ TestResults/Planet/PlaneSeam と、<see cref="Result"/>。</summary>
        public static void RunInEditor(){
            exitWhenDone=false;
            Result="running";
            Begin("TestResults/Planet/PlaneSeam");
        }

        /// <summary>RunInEditor の状態: running、PASSED、FAILED(失敗は Report.txt)。</summary>
        public static string Result{get;private set;}

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
                if(EditorApplication.timeSinceStartup-started>3400){throw new TimeoutException("PlaneSeam validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            objects=PlaneSeamSceneBuilder.Populate();
            camera=objects.Camera;camera.backgroundColor=new Color(1,0,1,1);
            cameraLogical=objects.CameraObject.GetComponent<global::Omni.Logical.Transform>();
            demo=objects.Demo;
            // 単色(照明・模様なし)の material: 左の面は青、右の面は緑。見た目の material は別に持つ。
            leftFlat=new Material(objects.LeftMaterial);leftFlat.SetFloat("_FlatColor",1);leftFlat.SetColor("_FlatTint",new Color(0.1f,0.1f,1f,1));
            groundFlat=new Material(objects.GroundMaterial);groundFlat.SetFloat("_FlatColor",1);groundFlat.SetColor("_FlatTint",new Color(0.1f,1f,0.1f,1));
            // 検証の間は Update を回さない(操作の入力を読まない)。
            demo.enabled=false;
        }

        private static void SetFlat(bool value){
            if(value){
                objects.GroundSource.Material=groundFlat;demo.SetLeftMaterial(leftFlat);
            }else{
                objects.GroundSource.Material=objects.GroundMaterial;demo.SetLeftMaterial(objects.LeftMaterial);
            }
        }

        private static IEnumerator Steps(){
            PlaneSeamLayout layout=PlaneSeamLayout.Default;
            report.Add("layout: D="+layout.OriginDistance.ToString("G6",CultureInfo.InvariantCulture)+" S="+layout.PlaneScale.ToString("G7",CultureInfo.InvariantCulture)+" quantum="+(layout.LeftQuantum()*1000).ToString("G4",CultureInfo.InvariantCulture)+
                "mm rightExtent="+layout.RightExtent.ToString("G6",CultureInfo.InvariantCulture)+" camera near logical="+0.3f/10f+"m");
            int waited=0;
            while(objects.GroundSource.Selector==null){
                if(waited++>100){throw new InvalidOperationException("右の面が有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            SetFlat(true);
            MeshRenderer leftRenderer=objects.ScaledPlane.GetComponent<MeshRenderer>();
            Mesh planeMesh=objects.ScaledPlane.GetComponent<MeshFilter>().sharedMesh;
            report.Add("meshes: left plane vertices="+planeMesh.vertexCount+" indexFormat="+planeMesh.indexFormat);

            // (a) 右だけ(左の面を消す)。D は変えない(右の面は D に依存しない)。
            leftRenderer.enabled=false;
            demo.SetLayout(PlaneSeamLayout.Default);
            foreach(object step in Sweep("RightOnly",false)){yield return step;}
            leftRenderer.enabled=true;
            // (b) 左右とも。D を振る。
            foreach(double distance in Distances){
                PlaneSeamLayout next=PlaneSeamLayout.Default;
                next.OriginDistance=distance;
                demo.SetLayout(next);
                foreach(object step in Sweep("Both D"+distance.ToString("G3",CultureInfo.InvariantCulture),true)){yield return step;}
            }
            demo.SetLayout(PlaneSeamLayout.Default);
            // 見た目(照明と格子)。
            SetFlat(false);
            for(int pose=0;pose<Poses.Length;pose++){
                demo.LookFrom(Poses[pose].From,Poses[pose].Target);
                demo.SetState(0,Poses[pose].From,demo.CameraYaw,demo.CameraPitch);
                foreach(object step in Converge("Shaded"+pose,Width,ImageHeight)){yield return step;}
                Texture2D image=Capture();Save("Shaded_pose"+pose,image);Destroy(image);
            }
            SetFlat(true);
            Summarize();
            PlanarTerrainSource source=objects.GroundSource;
            report.Add("pool "+source.name+": used="+source.UsedSlots+"/"+source.PoolCapacity+" selected="+source.LastSelected.Count+" evictions="+source.Evictions+" starved="+source.StarvedRequests+" lastError="+(source.LastError??"(none)"));
            if(source.LastError!=null){failures.Add("設定エラー("+source.name+"): "+source.LastError);}
        }

        // δ と視点を振って撮る。
        private static IEnumerable<object> Sweep(string phase,bool both){
            for(int pose=0;pose<Poses.Length;pose++){
                demo.LookFrom(Poses[pose].From,Poses[pose].Target);
                foreach(long offset in Offsets){
                    demo.SetState(offset,Poses[pose].From,demo.CameraYaw,demo.CameraPitch);
                    foreach(object step in Observe(phase,pose,offset*0.1,both,pose==0&&(offset==0||offset==10))){yield return step;}
                }
            }
        }

        // ---- 1 frame の撮影と解析

        // 今の状態(demo)の画像を撮り、解析と比べて記録する。選択は撮影と同じ解像度で収束させてから撮る。
        private static IEnumerable<object> Observe(string phase,int pose,double offset,bool both,bool save){
            foreach(object step in Converge(phase+" pose"+pose+" "+offset,Width,ImageHeight)){yield return step;}
            Texture2D image=Capture();
            if(save){Save(phase.Replace(' ','_')+"_pose"+pose+"_"+offset.ToString("0.#",CultureInfo.InvariantCulture)+"mm",image);}
            FrameMetrics metrics=Measure(image,both);
            metrics.Phase=phase;metrics.Pose=pose;metrics.Offset=offset;metrics.Distance=demo.Layout.OriginDistance;
            Destroy(image);
            frames.Add(metrics);
            csv.Add(string.Join(",",new object[]{phase,pose,offset.ToString("G6",CultureInfo.InvariantCulture),metrics.Distance.ToString("G3",CultureInfo.InvariantCulture),
                metrics.Rendered[Left],metrics.Analytic[Left],metrics.Rendered[Right],metrics.Analytic[Right],metrics.MismatchPixels,metrics.Anomaly}));
        }

        private static string Describe(FrameMetrics m){
            return "rendered bg/left/right/other="+m.Rendered[0]+"/"+m.Rendered[1]+"/"+m.Rendered[2]+"/"+m.Rendered[3]+" analytic="+m.Analytic[0]+"/"+m.Analytic[1]+"/"+m.Analytic[2]+
                " mismatchPixels="+m.MismatchPixels+" anomaly="+m.Anomaly;
        }

        // 選択は撮影と同じ解像度で収束させる(別の解像度だと、画面誤差が変わり、撮影の frame で新しい Cell の生成が始まる)。
        private static IEnumerable<object> Converge(string name,int width,int height){
            int stable=0;int previous=-1;int count=0;
            while(count<ConvergeLimit){
                Destroy(Capture(width,height));
                PlanarTerrainSource source=objects.GroundSource;
                int signature=source.LastSelected.Count;
                foreach(PlanetCellKey key in source.LastSelected){signature^=key.GetHashCode();}
                if(source.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
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

        // 0: 背景(マゼンタ)、1: 左の面(青)、2: 右の面(緑)、3: それ以外。
        private static int Classify(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return Background;}
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

        // 光線が最初に当たる面の種類(world 座標。camera の位置も倍精度。位置は原点の近く(数 m)なので倍精度で十分)。
        // 左の面 y=0 の x∈[−2(D+m)、0]・|z|≤D+m、右の面 y=δ の x∈[0、一辺]・|z|≤一辺/2、どちらも camera が面より上のときの上面だけ。面の外(地平線の向こう)は背景。
        private static int AnalyticClass(double3 camera,double rightY,double3 direction,bool both,PlaneSeamLayout layout){
            double best=double.MaxValue;int result=Background;
            if(both){
                double relative=0-camera.y;
                if(direction.y<0&&relative<0){
                    double t=relative/direction.y;
                    double x=camera.x+t*direction.x;double z=camera.z+t*direction.z;
                    double half=layout.OriginDistance+layout.EdgeMargin;
                    if(x<0&&x>=-2*half&&Math.Abs(z)<=half&&t<best){best=t;result=Left;}
                }
            }
            double rightRelative=rightY-camera.y;
            if(direction.y<0&&rightRelative<0){
                double t=rightRelative/direction.y;
                double x=camera.x+t*direction.x;double z=camera.z+t*direction.z;
                if(x>=0&&x<=layout.RightExtent&&Math.Abs(z)<=layout.RightExtent*0.5&&t<best){best=t;result=Right;}
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

        private static FrameMetrics Measure(Texture2D image,bool both){
            if(!cameraLogical.LocalPosition.X.TryToDouble(out double cx)||!cameraLogical.LocalPosition.Y.TryToDouble(out double cy)||!cameraLogical.LocalPosition.Z.TryToDouble(out double cz)){
                throw new InvalidOperationException("camera の位置を倍精度にできません。");
            }
            var cameraPosition=new double3(cx,cy,cz);
            double rightY=demo.OffsetY*PlaneSeamDemo.UnitMeters;
            Quaternion rotation=demo.CameraRotation;
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            var analytic=new int[pixels.Length];
            var rendered=new int[pixels.Length];
            var metrics=new FrameMetrics();
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int i=y*width+x;
                    analytic[i]=AnalyticClass(cameraPosition,rightY,Ray(rotation,x,y,width,height),both,demo.Layout);
                    rendered[i]=Classify(pixels[i]);
                    metrics.Rendered[rendered[i]]++;metrics.Analytic[analytic[i]]++;
                    if(rendered[i]==Other){metrics.Anomaly++;}
                }
            }
            // 種類のどれかの領域の境から 1 画素以内の画素は、描画と解析で種類が違っても数えない。
            var anyBand=new bool[pixels.Length];
            int[] compared={Background,Left,Right};
            foreach(int cls in compared){
                var mask=new bool[pixels.Length];
                for(int i=0;i<pixels.Length;i++){mask[i]=analytic[i]==cls;}
                bool[] band=Band(mask,width,height);
                for(int i=0;i<pixels.Length;i++){
                    if(band[i]){anyBand[i]=true;}
                }
            }
            for(int i=0;i<pixels.Length;i++){
                if(rendered[i]!=analytic[i]&&!anyBand[i]){metrics.MismatchPixels++;}
            }
            return metrics;
        }

        // ---- 集計と合否

        private static void Summarize(){
            var groups=new List<string>();
            foreach(FrameMetrics m in frames){
                if(!groups.Contains(m.Phase)){groups.Add(m.Phase);}
            }
            var worst=new Dictionary<string,int>();
            foreach(string phase in groups){
                int count=0;int maxPixels=0;int sumPixels=0;int maxAnomaly=0;
                foreach(FrameMetrics m in frames){
                    if(m.Phase!=phase){continue;}
                    count++;sumPixels+=m.MismatchPixels;
                    maxPixels=Math.Max(maxPixels,m.MismatchPixels);
                    maxAnomaly=Math.Max(maxAnomaly,m.Anomaly);
                }
                worst[phase]=maxPixels;
                report.Add("summary "+phase+": frames="+count+" maxMismatchPixels="+maxPixels+" sumMismatchPixels="+sumPixels+" maxAnomaly="+maxAnomaly);
            }
            // (a) 右(VG)だけ: 全 frame で、解析と 1 画素の帯の外で一致し、空振りではないこと。
            int rightFrames=0;
            foreach(FrameMetrics m in frames){
                if(m.Phase!="RightOnly"){continue;}
                rightFrames++;
                string name="右(VG)だけ pose"+m.Pose+" δ="+m.Offset.ToString("G4",CultureInfo.InvariantCulture)+"mm";
                if(m.MismatchPixels>RightOnlyTolerance){failures.Add(name+": 解析と 1 画素の帯の外で "+m.MismatchPixels+" 画素違います(許す差 "+RightOnlyTolerance+"。"+Describe(m)+")。");}
                if(m.Anomaly!=0){failures.Add(name+": どの種類でもない画素が "+m.Anomaly+" ありました。");}
                if(m.Analytic[Right]<MinimumRightPixels){failures.Add(name+": 解析上の右の面の画素("+m.Analytic[Right]+")が少なすぎます(空振りの疑い)。");}
                if(m.Rendered[Left]!=0){failures.Add(name+": 左の面を消したのに左の色の画素が "+m.Rendered[Left]+" あります。");}
            }
            if(rightFrames!=Poses.Length*Offsets.Length){failures.Add("右(VG)だけの frame が "+rightFrames+" 件です("+Poses.Length*Offsets.Length+" 件のはず)。");}
            // (b) 左右とも: D と共に誤差が増えること(左の誤差が再現すること)。しきい値は観測した範囲で決め、Requirements/PlanetWorld.md に記録する。
            var byDistance=new int[Distances.Length];
            for(int i=0;i<Distances.Length;i++){
                string phase="Both D"+Distances[i].ToString("G3",CultureInfo.InvariantCulture);
                if(!worst.TryGetValue(phase,out byDistance[i])){failures.Add(phase+" の段がありません。");byDistance[i]=-1;}
            }
            report.Add("both worst by D (1e4,1e5,1e6,1e7): "+string.Join(",",byDistance));
            // 2026-10-13 の初回の観測(RTX 4060 Ti): 0、1597、9835、67701。しきい値は観測の範囲の約半分で、回帰の検出用(仕様ではない)。
            if(byDistance[0]>100){failures.Add("左(単純な拡大): D=1e4 でも不一致が "+byDistance[0]+" 画素あります(刻み 0.6 mm なので 100 画素以下のはず)。");}
            for(int i=1;i<Distances.Length;i++){
                if(byDistance[i]>=0&&byDistance[i-1]>=0&&byDistance[i]<byDistance[i-1]){failures.Add("左(単純な拡大): D="+Distances[i].ToString("G3",CultureInfo.InvariantCulture)+" の不一致 "+byDistance[i]+" が、1 つ小さい D の "+byDistance[i-1]+" より少ないです(D と共に増えるはず)。");}
            }
            if(byDistance[2]<1000){failures.Add("左(単純な拡大): D=1e6 の不一致が "+byDistance[2]+" 画素しかなく、誤差が再現していません(1000 画素以上のはず)。");}
            if(byDistance[2]>=0&&byDistance[3]>=0&&byDistance[3]<3*byDistance[2]){failures.Add("左(単純な拡大): D=1e7 の不一致 "+byDistance[3]+" が、D=1e6 の "+byDistance[2]+" の 3 倍未満です。");}
            foreach(FrameMetrics m in frames){
                if(m.Phase.StartsWith("Both",StringComparison.Ordinal)&&m.Anomaly!=0){failures.Add(m.Phase+" pose"+m.Pose+" δ="+m.Offset.ToString("G4",CultureInfo.InvariantCulture)+"mm: どの種類でもない画素が "+m.Anomaly+" ありました。");}
            }
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            // デモが material のアセットに書いた値を戻す(戻さないと、検証の最後の値がアセットに残り、git の差分になる)。
            if(demo!=null){demo.RestoreWrittenAsset();}
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            var lines=new List<string>{"phase,pose,offsetMm,D,leftRendered,leftAnalytic,rightRendered,rightAnalytic,mismatchPixels,anomaly"};
            lines.AddRange(csv);
            File.WriteAllLines(results+"/Metrics.csv",lines);
            int code=1;
            if(failures.Count==0){code=0;}
            if(failures.Count==0){Result="PASSED";}else{Result="FAILED";}
            if(exitWhenDone){EditorApplication.Exit(code);}
        }
    }
}
