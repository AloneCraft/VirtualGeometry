using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Omni.BatchRendering;
using VirtualGeometry.World;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// MoonTriple デモ(<see cref="MoonTripleDemo"/>)の検証。3 つの月は描き方が違う(方式 1 = 単純な巨大化の普通の月、方式 2 = VG の月 A、方式 3 = PlanetSurface の月 B)。
    /// VG の月 A は単色(緑)で描く。普通の月、PlanetSurface の月 B、サッカーボールは Omni の CosmicPlanetLit / Patch で、単色にできない。
    /// そこで、普通の月とボールは表示を切り替えて、形(シルエット)を解析と比べる(月 B とボールは同じ「その他」の画素)。
    /// - Contact1(接触点 1: VG の月 A と PlanetSurface の月 B。普通の月は非表示): ボールを y 方向(月の間の軸)に −5〜+5mm(1mm 刻み)動かし、表示の composite(A、B、ボール)を解析と比べる。
    /// - Contact0Composite(接触点 0: 普通の月、A、ボールを表示): composite を解析と比べる。
    /// - Contact0BallOnly(普通の月を非表示): ボールだけの形を、A と B の前後関係も含めて解析と比べる。
    /// - Contact0PlainOnly(ボールを非表示): 普通の月だけの形を、A と B の前後関係も含めて解析と比べる。
    /// 描画の種類は、背景、VG の月 A、VG の月 B、その他(普通の月かボール)の 4 つ。解析は表示した物体だけで、一番手前の物体を求める。
    /// 普通の月は単純な球のままで比べる(誤差を測る対象であり、VG のように補正しない)。そのため、描画の正しさは、理想の球ではなく、描かれている三角形と比べる。
    /// 理想の球(半径 R)との違いは、誤差として MeshVsIdeal に数えて報告する(合否には使わない)。
    /// 位置づけ(観測用): この Edit Mode のバッチは、回帰に入れない(常に FAILED になる)。方式 3(PlanetSurface の月 B)が Edit Mode では階層 12 から細分化されず、
    /// 普通の月(方式 1)は近距離で描画が欠ける(解析上の約 26% しか描かれない)ため。方式 2(VG)とボールの一致、普通の月の欠けの大きさを見る観測と、
    /// 1 視点だけ撮る <see cref="RunProbe"/> に使う。キー・テレポート・細分化・半分を埋めた画像・掃引・mm 未満の誤差・原子の比較は、Play Mode の <see cref="MoonTriplePlaySmoke"/> が確かめる。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonTripleValidation.Run
    /// </summary>
    public static class MoonTripleValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        // 表示の切り替えの後、Omni の一括描画に反映されるまでの待ち(frame)。
        private const int SettleFrames=6;
        // 描画の種類(画素の分類)。
        // その他 = 普通の月(方式 1)、PlanetSurface の月 B(方式 3)、ボール(いずれも Omni の CosmicPlanetLit / Patch の模様で、単色にできない)。
        private const int RBackground=0;
        private const int RA=1;
        private const int ROther=2;
        private const int ClassCount=3;
        private const int MinimumPixels=1000;
        // 1 画素の帯の外で違う画素の、合否の上限(回帰の検出用。境界の揺れを許す)。
        private const int MismatchTolerance=50;
        private static readonly double[] Offsets={-5,-4,-3,-2,-1,0,1,2,3,4,5};
        // 撮影の距離(m)と俯角 0 度(水平)。ボールは両方の月の間の隙間(0.22 m)にあり、その高さより上の位置は月の内側になる(解析は外側からの光線だけを数える)。
        // 水平に見れば、カメラはボールと同じ高さの隙間の中に置かれる。6 cm の近さでは、普通の月の表面の大部分が描かれない。
        private const float ViewDistance=1.5f;
        // 普通の月の三角形のうち、接触点からこの距離の内側だけを使う(光線が当たるのは近くの面だけ。21 km の三角形が数個入る)。
        private const double PlainCapMeters=50000;

        private static string results;
        private static MoonTripleObjects objects;
        private static Camera camera;
        private static MoonTripleDemo demo;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly List<string> csv=new List<string>();
        private static readonly List<FrameMetrics> frames=new List<FrameMetrics>();
        // 普通の月の実際の三角形(倍精度、接触点の近く)。描画の当たりは、この三角形で求める。
        private static readonly List<double3[]> plainTriangles=new List<double3[]>();
        // 普通の月の三角形の、理想の球からのずれ(m)。面の中心の半径の不足の最大(弦の落ち込み)と、頂点の半径の誤差の最大。
        private static double plainSagMeters;
        private static double plainVertexErrorMeters;

        private sealed class FrameMetrics{
            public string Phase;
            public double Offset;
            public int[] Rendered=new int[ClassCount];
            public int[] Analytic=new int[ClassCount];
            // 1 画素の帯の外で、描画と解析で種類が違う画素の数(1 つの違う画素を 1 回だけ数える)。
            public int Mismatch;
            // 1 画素の帯の外で、普通の月の三角形と理想の球で種類が違う画素の数(誤差。描画の誤りではない)。
            public int MeshVsIdeal;
        }

        public static void Run(){
            results="TestResults/Planet/MoonTriple";
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

        // probe: 普通の月の描画が欠けるかを、普通の月の分割(-plainLongitude、-plainLatitude)を変えて 1 視点だけ見る。判定はしない(観測のみ、結果は TestResults/Planet/MoonTripleProbe)。
        public static void RunProbe(){
            results="TestResults/Planet/MoonTripleProbe";
            report.Clear();failures.Clear();errors.Clear();csv.Clear();frames.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(results);
            Application.logMessageReceived+=Record;
            try{
                ReadProbeArguments();
                Setup();
                steps=ProbeSteps();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        // probe の接触点(-probeContact 0 = 普通の月と VG の月 A、1 = VG の月 A と PlanetSurface の月 B)。0 のときは普通の月だけ、1 のときはボールだけを表示する。
        private static int probeContact;
        // probe のボールの埋め方(-probeBury lower = 下の月に半分埋める、upper = 上の月に半分埋める、既定は埋めない)。ボールの中心を理想の面の高さに置き、赤道の線と面の境界のずれを見る。
        private static int probeBury;
        // probe の、接触点からの横の距離(m。-probeLateral)。既定は、埋めないときはボールが両側の面に接する距離、埋めるときは 1,500 m(月どうしの隙間が約 1.3 m で、ボールと camera が入る)。
        private static double probeLateral;

        private static void ReadProbeArguments(){
            string[] args=Environment.GetCommandLineArgs();
            probeContact=0;probeBury=0;probeLateral=0;
            for(int i=0;i+1<args.Length;i++){
                if(args[i]=="-plainLongitude"){MoonTripleSceneBuilder.PlainLongitude=int.Parse(args[i+1],CultureInfo.InvariantCulture);}
                if(args[i]=="-plainLatitude"){MoonTripleSceneBuilder.PlainLatitude=int.Parse(args[i+1],CultureInfo.InvariantCulture);}
                if(args[i]=="-probeContact"){probeContact=int.Parse(args[i+1],CultureInfo.InvariantCulture);}
                if(args[i]=="-probeLateral"){probeLateral=double.Parse(args[i+1],CultureInfo.InvariantCulture);}
                if(args[i]=="-probeBury"){
                    if(args[i+1]=="lower"){probeBury=1;}
                    if(args[i+1]=="upper"){probeBury=2;}
                }
            }
            report.Add("probe contact="+probeContact+" bury="+(probeBury==1?"lower":(probeBury==2?"upper":"none")));
            report.Add("probe plain sphere="+(MoonTripleSceneBuilder.PlainLongitude>0?MoonTripleSceneBuilder.PlainLongitude+"x"+MoonTripleSceneBuilder.PlainLatitude:"asset CosmicSphere (512x256)"));
        }

        private static IEnumerator ProbeSteps(){
            int waited=0;
            while(!demo.MoonsReady()){
                if(waited++>100){throw new InvalidOperationException("月(VG の月 A、PlanetSurface の月 B)が有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            report.Add("plain moon triangles(in cap)="+plainTriangles.Count+" sag="+plainSagMeters.ToString("F3",CultureInfo.InvariantCulture)+" m");
            // 上の月の下面に埋めるときは、下面が見えるよう、光を下から当てる(太陽光の向きの反対側を向く面は暗い)。
            if(probeBury==2){Shader.SetGlobalVector("_CosmicKeyLight",new Vector4(0.7f,-0.55f,0.25f,1.3f));}
            bool showPlain=probeContact==0;
            bool showBall=probeContact!=0||probeBury!=0;
            Show(showPlain,showBall);
            double lateral=probeLateral;
            if(!(lateral>0)){
                if(probeBury!=0){lateral=1500;}else{lateral=demo.Layout.TouchDistance(probeContact);}
            }
            long touch=(long)Math.Round(lateral/MoonTripleDemo.UnitMeters);
            demo.SetState(probeContact,0,0,touch,180f,0f,ViewDistance);
            report.Add("probe lateral distance="+lateral.ToString("F3",CultureInfo.InvariantCulture)+" m");
            if(probeBury!=0){
                // ボールの中心を理想の面の高さに置く(半分が埋まる)。camera はボールの中心を向き、地面が見えるよう、下の月に埋めたときは上から見下ろし(15 度)、上の月に埋めたときは下から見上げる(−15 度)。
                demo.BuryBall(probeBury==1);
                demo.FrameBall(180f,probeBury==1?15f:-15f,ViewDistance);
                demo.Apply();
            }
            for(int i=0;i<SettleFrames;i++){yield return null;}
            foreach(object step in Converge("Probe",Width,ImageHeight)){yield return step;}
            Texture2D image=Capture();
            string suffix="_c"+probeContact+"_"+(probeBury==1?"buryLower":(probeBury==2?"buryUpper":"touch"));
            Save("Probe"+suffix,image);
            report.Add("saved image: Probe"+suffix+".png");
            global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patchState=objects.MoonB.Lod.Diagnostics;
            report.Add("PlanetSurface moon B: leaves="+patchState.LeafCount+" renderers="+patchState.RendererCount+" deepestLevel="+patchState.DeepestLevel+" desiredLevel="+patchState.DesiredLevel+" viewerAltitude="+patchState.ViewerAltitudeMeters.ToString("F3",CultureInfo.InvariantCulture)+" m");
            FrameMetrics metrics=Measure(image,showPlain,showBall);
            metrics.Phase="Probe";metrics.Offset=0;
            Destroy(image);
            frames.Add(metrics);
            AddCsv("Probe","0",metrics);
            report.Add("probe renderedBg="+metrics.Rendered[RBackground]+" renderedA="+metrics.Rendered[RA]+" renderedOther="+metrics.Rendered[ROther]+
                " | analyticBg="+metrics.Analytic[RBackground]+" analyticA="+metrics.Analytic[RA]+" analyticOther="+metrics.Analytic[ROther]+
                " | mismatch="+metrics.Mismatch+" meshVsIdeal="+metrics.MeshVsIdeal);
            failures.Add("probe: 観測のみ(判定なし)");
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
                if(EditorApplication.timeSinceStartup-started>6000){throw new TimeoutException("MoonTriple validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            objects=MoonTripleSceneBuilder.Populate();
            camera=objects.Camera;camera.backgroundColor=new Color(1,0,1,1);
            demo=objects.Demo;
            BuildPlainMesh();
            // VG の月 A は単色(緑)。VG の経路は材質の差し替えが描画に反映される(PlanetSurface の月 B は単色にできない)。
            objects.MoonA.Material=Flat(objects.MoonAMaterial,new Color(0.1f,1f,0.1f,1));
            // 検証の間は Update を回さない(操作の入力を読まない)。
            demo.enabled=false;
        }

        // 普通の月の三角形を、Logical の配置(中心 (0, −R, 0)、直径 2R)と同じ式で倍精度に直す。接触点の近く(PlainCapMeters 以内)の三角形だけを残す。
        // 頂点が理想の球の上にあるか、面の中心がどれだけ内側か(弦の落ち込み)を報告する。
        private static void BuildPlainMesh(){
            plainTriangles.Clear();
            plainSagMeters=0;plainVertexErrorMeters=0;
            Mesh mesh=objects.PlainMoon.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices=mesh.vertices;
            int[] indices=mesh.triangles;
            MoonTripleLayout layout=MoonTripleLayout.Default;
            double scale=2*layout.Radius;
            double3 center=new double3(0,layout.CenterPlain,0);
            double3 contact=double3.zero;
            for(int i=0;i+2<indices.Length;i+=3){
                var triangle=new double3[3];
                for(int k=0;k<3;k++){
                    Vector3 v=vertices[indices[i+k]];
                    triangle[k]=center+new double3(v.x,v.y,v.z)*scale;
                    plainVertexErrorMeters=Math.Max(plainVertexErrorMeters,Math.Abs(math.length(triangle[k]-center)-layout.Radius));
                }
                if(math.length(triangle[0]-contact)>PlainCapMeters&&math.length(triangle[1]-contact)>PlainCapMeters&&math.length(triangle[2]-contact)>PlainCapMeters){continue;}
                plainTriangles.Add(triangle);
                double3 centroid=(triangle[0]+triangle[1]+triangle[2])/3;
                plainSagMeters=Math.Max(plainSagMeters,layout.Radius-math.length(centroid-center));
            }
            report.Add("plain moon triangles(total="+(indices.Length/3)+", within "+PlainCapMeters.ToString("F0",CultureInfo.InvariantCulture)+" m of contact="+plainTriangles.Count+")");
            report.Add("plain moon max sag(facet center inside the ideal sphere)="+plainSagMeters.ToString("F3",CultureInfo.InvariantCulture)+" m");
            report.Add("plain moon max vertex radius error="+plainVertexErrorMeters.ToString("G4",CultureInfo.InvariantCulture)+" m");
            if(plainTriangles.Count==0){failures.Add("普通の月の三角形が接触点の近くにありません。");}
        }

        private static Material Flat(Material source,Color tint){
            // VG の単色用の材質。元の材質の値を複製し、単色の表示を重ねる(RadialMoonGrid の _FlatColor)。
            var material=new Material(source);material.SetFloat("_FlatColor",1);material.SetColor("_FlatTint",tint);
            return material;
        }

        // 普通の月とボールの表示を切り替える。Omni の一括描画は、自動で集めた MeshRenderer を描くので、
        // enabled だけでは消えない。非表示は一括描画の登録を外してから enabled を切り、表示は登録し直す。
        private static readonly Dictionary<MeshRenderer,RadialBatchInstanceHandle> registered=new Dictionary<MeshRenderer,RadialBatchInstanceHandle>();

        public static void SetVisible(MeshRenderer renderer,bool visible){
            if(visible){
                renderer.enabled=true;
                if(!registered.ContainsKey(renderer)){registered[renderer]=RadialBatchRendererRegistry.Register(renderer);}
            }else{
                // 自動で集めた Renderer は、先に登録してから外す(外したものは、登録し直すまで一括描画に入らない)。
                if(registered.TryGetValue(renderer,out RadialBatchInstanceHandle handle)){
                    RadialBatchRendererRegistry.Unregister(handle);
                    registered.Remove(renderer);
                }else{
                    RadialBatchRendererRegistry.Unregister(RadialBatchRendererRegistry.Register(renderer));
                }
                renderer.enabled=false;
            }
        }

        private static void Show(bool plain,bool ball){
            SetVisible(objects.PlainMoon.GetComponent<MeshRenderer>(),plain);
            SetVisible(objects.Ball.GetComponent<MeshRenderer>(),ball);
        }

        private static IEnumerator Steps(){
            MoonTripleLayout layout=MoonTripleLayout.Default;
            report.Add("layout: R="+layout.Radius.ToString("G8",CultureInfo.InvariantCulture)+" touchDistance0="+layout.TouchDistance(0).ToString("F3",CultureInfo.InvariantCulture)+"m camera near logical="+0.3f/10f+"m");
            int waited=0;
            while(!demo.MoonsReady()){
                if(waited++>100){throw new InvalidOperationException("月(VG の月 A、PlanetSurface の月 B)が有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            report.Add("plain moon mesh vertices="+objects.PlainMoon.GetComponent<MeshFilter>().sharedMesh.vertexCount);

            foreach(object step in Sweep("Contact1",1,false,true)){yield return step;}
            foreach(object step in Sweep("Contact0Composite",0,true,true)){yield return step;}
            foreach(object step in Sweep("Contact0BallOnly",0,false,true)){yield return step;}
            foreach(object step in Sweep("Contact0PlainOnly",0,true,false)){yield return step;}
            foreach(object step in FarContactView("Contact0FarPlainA")){yield return step;}
            Show(true,true);
            Summarize();
            PlanetTerrainSource vgMoon=objects.MoonA;
            report.Add("pool "+vgMoon.name+": used="+vgMoon.UsedSlots+"/"+vgMoon.PoolCapacity+" evictions="+vgMoon.Evictions+" starved="+vgMoon.StarvedRequests+" lastError="+(vgMoon.LastError??"(none)"));
            if(vgMoon.LastError!=null){failures.Add("設定エラー("+vgMoon.name+"): "+vgMoon.LastError);}
            global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patch=objects.MoonB.Lod.Diagnostics;
            report.Add("PlanetSurface moon B: leaves="+patch.LeafCount+" renderers="+patch.RendererCount+" deepestLevel="+patch.DeepestLevel+" desiredLevel="+patch.DesiredLevel);
        }

        // ボールを両側の面に接する距離(接触点から +z)に置き、y(月の間の軸)を −5〜+5mm に振る。plain、ball は表示する物体。
        private static IEnumerable<object> Sweep(string phase,int contact,bool plain,bool ball){
            Show(plain,ball);
            long touch=(long)Math.Round(demo.Layout.TouchDistance(contact)/MoonTripleDemo.UnitMeters);
            foreach(double offset in Offsets){
                demo.SetState(contact,0,(long)Math.Round(offset*10),touch,180f,0f,ViewDistance);
                for(int i=0;i<SettleFrames;i++){yield return null;}
                foreach(object step in Converge(phase+" "+offset,Width,ImageHeight)){yield return step;}
                Texture2D image=Capture();
                if(offset==0){Save(phase+"_0mm",image);}
                FrameMetrics metrics=Measure(image,plain,ball);
                metrics.Phase=phase;metrics.Offset=offset;
                Destroy(image);
                frames.Add(metrics);
                AddCsv(phase,offset.ToString("G6",CultureInfo.InvariantCulture),metrics);
            }
        }

        // 接触の遠景: 接触点から 1,000 m 離れた位置に、水平から 0.2 m 上の高さのカメラを置く。
        // ここでは両方の月の外側にある(普通の月の上端は −0.29 m、VG の月 A の下端は +0.29 m。カメラは +0.2 m)。
        // 普通の月と VG の月 A の境界は、接触の隙間が閉じる位置(接触点)で合流する。ボールは非表示。
        private static IEnumerable<object> FarContactView(string phase){
            Show(true,false);
            const double distance=1000;
            float pitch=(float)(Math.Asin(0.2/distance)*180.0/Math.PI);
            demo.SetState(0,0,0,0,180f,pitch,(float)distance);
            for(int i=0;i<SettleFrames;i++){yield return null;}
            foreach(object step in Converge(phase,Width,ImageHeight)){yield return step;}
            Texture2D image=Capture();
            Save(phase,image);
            FrameMetrics metrics=Measure(image,true,false);
            metrics.Phase=phase;metrics.Offset=0;
            Destroy(image);
            frames.Add(metrics);
            AddCsv(phase,"0",metrics);
        }

        private static void AddCsv(string phase,string yMm,FrameMetrics metrics){
            csv.Add(string.Join(",",new object[]{phase,yMm,
                metrics.Rendered[RBackground],metrics.Rendered[RA],metrics.Rendered[ROther],
                metrics.Analytic[RBackground],metrics.Analytic[RA],metrics.Analytic[ROther],metrics.Mismatch,metrics.MeshVsIdeal}));
        }

        // 選択は撮影と同じ解像度で収束させる(別の解像度だと、画面誤差が変わり、撮影の frame で新しい Cell の生成が始まる)。
        private static IEnumerable<object> Converge(string name,int width,int height){
            int stable=0;int previous=-1;int count=0;
            while(count<ConvergeLimit){
                Destroy(Capture(width,height));
                int signature=0;int pending=0;
                PlanetTerrainSource vgMoon=objects.MoonA;
                signature=signature*31+vgMoon.LastSelected.Count;
                foreach(PlanetCellKey key in vgMoon.LastSelected){signature^=key.GetHashCode();}
                pending+=vgMoon.PendingGeneration;
                // PlanetSurface の月 B: 最深の階層が要求の階層に届き、葉の数が変わらなくなるまで待つ(1 フレーム 1 段ずつ進むので、途中は葉の数が変わらない間がある)。
                global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patch=objects.MoonB.Lod.Diagnostics;
                signature=signature*31+patch.LeafCount;
                signature=signature*31+patch.DeepestLevel;
                if(patch.DeepestLevel<patch.DesiredLevel){pending++;}
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

        // 描画の画素の分類: 背景(マゼンタ)、VG の月 A(緑)、それ以外(普通の月、PlanetSurface の月 B、ボール)。
        private static int ClassifyRendered(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return RBackground;}
            if(pixel.g>200&&pixel.r<90&&pixel.b<90){return RA;}
            return ROther;
        }

        // 画素ごとの光線(camera の向き、画角)。向きは demo が置いた回転(camera.transform は使わない)。
        private static double3 Ray(Quaternion rotation,int x,int y,int width,int height){
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            Vector3 direction=(rotation*local).normalized;
            return new double3(direction.x,direction.y,direction.z);
        }

        // 光線が球の外側から当たる最初の距離(当たらなければ double.MaxValue)。中心 − camera の差(倍精度)で解く。
        private static double Hit(double3 center,double radius,double3 cameraPosition,double3 direction){
            double3 relative=center-cameraPosition;
            double b=math.dot(direction,relative);
            double c=math.dot(relative,relative)-radius*radius;
            double discriminant=b*b-c;
            if(c>0&&b>0&&discriminant>=0){return c/(b+math.sqrt(discriminant));}
            return double.MaxValue;
        }

        // 光線と三角形の当たり(Möller–Trumbore、倍精度)。表・裏は区別しない。当たらなければ double.MaxValue。
        private static double Intersect(double3 origin,double3 direction,double3 a,double3 b,double3 c){
            double3 edge1=b-a;
            double3 edge2=c-a;
            double3 p=math.cross(direction,edge2);
            double determinant=math.dot(edge1,p);
            if(math.abs(determinant)<1e-12){return double.MaxValue;}
            double inverse=1/determinant;
            double3 s=origin-a;
            double u=math.dot(s,p)*inverse;
            if(u<0||u>1){return double.MaxValue;}
            double3 q=math.cross(s,edge1);
            double v=math.dot(direction,q)*inverse;
            if(v<0||u+v>1){return double.MaxValue;}
            double t=math.dot(edge2,q)*inverse;
            if(t>0){return t;}
            return double.MaxValue;
        }

        // 普通の月の実際の三角形で、光線が最初に当たる距離(当たらなければ double.MaxValue)。
        private static double MeshHit(double3 cameraPosition,double3 direction){
            double best=double.MaxValue;
            foreach(double3[] triangle in plainTriangles){
                double t=Intersect(cameraPosition,direction,triangle[0],triangle[1],triangle[2]);
                if(t<best){best=t;}
            }
            return best;
        }

        private static void Keep(double t,int cls,ref double best,ref int result){
            if(t<best){best=t;result=cls;}
        }

        // 表示する物体だけで、光線が最初に当たる物体の描画の種類(普通の月とボールは同じ「その他」)。
        // mesh は普通の月の三角形、ideal は理想の球(半径 R)で求めた種類。VG の月 A・B とボールは両方とも同じ。
        private static int AnalyticClass(double3 cameraPosition,double3 direction,bool plain,bool ball,out int ideal){
            MoonTripleLayout layout=demo.Layout;
            double bestMesh=double.MaxValue;double bestIdeal=double.MaxValue;
            int result=RBackground;ideal=RBackground;
            double t;
            if(ball){
                t=Hit(demo.BallCenterDouble(),MoonTripleLayout.BallRadius,cameraPosition,direction);
                Keep(t,ROther,ref bestMesh,ref result);
                Keep(t,ROther,ref bestIdeal,ref ideal);
            }
            if(plain){
                Keep(MeshHit(cameraPosition,direction),ROther,ref bestMesh,ref result);
                Keep(Hit(new double3(0,layout.CenterPlain,0),layout.Radius,cameraPosition,direction),ROther,ref bestIdeal,ref ideal);
            }
            t=Hit(new double3(0,layout.CenterA,0),layout.Radius,cameraPosition,direction);
            Keep(t,RA,ref bestMesh,ref result);
            Keep(t,RA,ref bestIdeal,ref ideal);
            // PlanetSurface の月 B(方式 3)は、普通の月・ボールと同じ「その他」。理想の球(半径 R)で求める。
            t=Hit(new double3(0,layout.CenterB,0),layout.Radius,cameraPosition,direction);
            Keep(t,ROther,ref bestMesh,ref result);
            Keep(t,ROther,ref bestIdeal,ref ideal);
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

        private static FrameMetrics Measure(Texture2D image,bool plain,bool ball){
            double3 cameraPosition=demo.CameraPosition;
            Quaternion rotation=demo.CameraRotation;
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            var analytic=new int[pixels.Length];
            var ideal=new int[pixels.Length];
            var rendered=new int[pixels.Length];
            var metrics=new FrameMetrics();
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int i=y*width+x;
                    analytic[i]=AnalyticClass(cameraPosition,Ray(rotation,x,y,width,height),plain,ball,out ideal[i]);
                    rendered[i]=ClassifyRendered(pixels[i]);
                    metrics.Rendered[rendered[i]]++;metrics.Analytic[analytic[i]]++;
                }
            }
            // どれかの種類の領域の境から 1 画素以内の画素は、描画と解析で種類が違っても数えない。
            var anyBand=new bool[pixels.Length];
            for(int cls=0;cls<ClassCount;cls++){
                var mask=new bool[pixels.Length];
                for(int i=0;i<pixels.Length;i++){mask[i]=analytic[i]==cls;}
                bool[] band=Band(mask,width,height);
                for(int i=0;i<pixels.Length;i++){
                    if(band[i]){anyBand[i]=true;}
                }
            }
            for(int i=0;i<pixels.Length;i++){
                if(rendered[i]!=analytic[i]&&!anyBand[i]){metrics.Mismatch++;}
                if(ideal[i]!=analytic[i]&&!anyBand[i]){metrics.MeshVsIdeal++;}
            }
            return metrics;
        }

        // ---- 集計と合否

        private static void Summarize(){
            foreach(string phase in new[]{"Contact1","Contact0Composite","Contact0BallOnly","Contact0PlainOnly","Contact0FarPlainA"}){
                int count=0;int maxMismatch=0;int sumMismatch=0;int minOther=int.MaxValue;int minA=int.MaxValue;int maxMeshVsIdeal=0;
                foreach(FrameMetrics m in frames){
                    if(m.Phase!=phase){continue;}
                    count++;sumMismatch+=m.Mismatch;maxMismatch=Math.Max(maxMismatch,m.Mismatch);maxMeshVsIdeal=Math.Max(maxMeshVsIdeal,m.MeshVsIdeal);
                    minOther=Math.Min(minOther,m.Analytic[ROther]);minA=Math.Min(minA,m.Analytic[RA]);
                }
                if(count==0){failures.Add(phase+" の frame がありません。");continue;}
                report.Add("summary "+phase+": frames="+count+" maxMismatchPixels="+maxMismatch+" sumMismatchPixels="+sumMismatch+
                    " maxMeshVsIdealPixels="+maxMeshVsIdeal+" minAnalytic(other)="+minOther+" minAnalytic(A)="+minA);
                if(maxMismatch>MismatchTolerance){failures.Add(phase+": 解析と 1 画素の帯の外で最大 "+maxMismatch+" 画素違います(許す差 "+MismatchTolerance+")。");}
                if(minOther<MinimumPixels){failures.Add(phase+": 解析上の普通の月かボールの画素が少なすぎます("+minOther+")。空振りの疑い。");}
            }
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            var lines=new List<string>{"phase,yMm,renderedBg,renderedA,renderedOther,analyticBg,analyticA,analyticOther,mismatchPixels,meshVsIdealPixels"};
            lines.AddRange(csv);
            File.WriteAllLines(results+"/Metrics.csv",lines);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
