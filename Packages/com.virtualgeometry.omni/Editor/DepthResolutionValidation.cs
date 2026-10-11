using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using VirtualGeometry.World;
using VirtualizedGeometry.Editor;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 深度の分解能の実測(Omni の SV_Depth)。半径 10^20 m の天体を 2 つ(青 A、緑 B)重ね、B の中心を A の中心から up 方向へ Δ ずらす。
    /// camera は A の頂上の上 d の高さから真下を向く。Δ > 0 なら B が手前(正しい色は緑)、Δ &lt; 0 なら A が手前(青)。
    /// 深度が Δ/d を分けられなければ、描く順番(同じ深度なら後から描いた方が勝つ)で決まる色になり、片方の符号で必ず間違う。
    /// Δ/d を 1e-9 から 2 倍ずつ振って、両方の符号で誤った色の画素が 0 になる最小の |Δ|/d(3 点連続で合格)を距離 d ごとに求める。
    /// 画面の全画素は同じ Δ/d になる(斜めの光線は d も Δ も 1/cosθ 倍)ので、1 枚で全画素を使う。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.DepthResolutionValidation.Run(結果は TestResults/Planet/DepthResolution)
    /// 距離 d は 1.7 m〜10^20 m、View Scale M と Camera.near を振る組み合わせ、深度の形式(D24、D32F)を明示した撮影を含む。
    /// </summary>
    public static class DepthResolutionValidation{
        private const int Width=160;
        private const int ImageHeight=120;
        private const int ConvergeLimit=900;
        private const float BodyRadius=1e20f;
        // Δ/d の最小値と刻み(2 倍)、段数。1e-9 から 2^24 倍(約 1.7e-2)まで。
        private const double FirstRatio=1e-9;
        private const int RatioCount=25;
        // 合格とみなす連続の段数。
        private const int RequiredRun=3;
        // 「分解能が足りている」とみなす上限(これより粗いと不合格)。実測の最悪(距離 10^8 m 以上で 5.2e-4)の約 2 倍。回帰の検出用で、仕様ではない。
        private const double AcceptableRatio=1e-3;
        // この距離までを合否に使う。これより遠い(10^15 m、10^20 m)は、実測の記録だけ(下の限界)。
        private const double GatedDistance=1e12;

        private static readonly Color BlueTint=new Color(0.1f,0.2f,0.9f,1f);
        private static readonly Color GreenTint=new Color(0.1f,0.85f,0.2f,1f);

        private static string results;
        private static Camera camera;
        private static LogicalTransform cameraLogical;
        private static LogicalTransform bodyALogical;
        private static LogicalTransform bodyBLogical;
        private static PlanetTerrainSource bodyA;
        private static PlanetTerrainSource bodyB;
        private static global::Omni.RadialCamera radial;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> table=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        /// <summary>1 つの条件(距離、View Scale、near、深度の形式)の結果。</summary>
        private struct Resolution{
            public double Positive;
            public double Negative;
        }

        public static void Run(){
            results="TestResults/Planet/DepthResolution";
            report.Clear();table.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
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
                if(EditorApplication.timeSinceStartup-started>3400){throw new TimeoutException("DepthResolution validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static PlanetTerrainSource CreateBody(string name,Color tint,out LogicalTransform logical){
            var body=new GameObject(name);body.SetActive(false);
            logical=body.AddComponent<LogicalTransform>();
            PlanetTerrainSource source=body.AddComponent<PlanetTerrainSource>();
            body.AddComponent<OmniPlanetPlacement>();
            // 高さのない球(手続きの高さ 0)。階層は 1 quad が 1 mm 以下になる深さまで。
            MoonBallSceneBuilder.Configure(source,BodyRadius,0f,1e19f,1,90,1024,64,true);
            var material=new Material(AssetDatabase.LoadAssetAtPath<Material>(MoonBallSceneBuilder.MoonMaterialPath));
            material.SetFloat("_FlatColor",1);material.SetColor("_FlatTint",tint);
            source.Material=material;
            body.SetActive(true);
            return source;
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            bodyA=CreateBody("Body A",BlueTint,out bodyALogical);
            bodyB=CreateBody("Body B",GreenTint,out bodyBLogical);
            HeightShiftOverride.ApplyToScene();

            var cameraObject=new GameObject("Main Camera");cameraObject.tag="MainCamera";
            camera=cameraObject.AddComponent<Camera>();
            camera.fieldOfView=50;camera.nearClipPlane=0.3f;camera.farClipPlane=100000;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(1,0,1,1);
            camera.allowHDR=false;camera.allowMSAA=false;
            cameraLogical=cameraObject.AddComponent<LogicalTransform>();
            radial=cameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            var sun=new GameObject("Sun");
            Light light=sun.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.3f;light.shadows=LightShadows.None;
            sun.transform.rotation=Quaternion.Euler(60f,-40f,0f);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.03f,0.03f,0.04f);
            RenderSettings.skybox=null;
        }

        // 2 つの天体の中心は Y 軸上。A は原点、B は Δ(m)だけ上。camera は A の頂上の上 d の高さで、真下を向く。
        private static void Place(double distance,double delta){
            LogicalScalar radius=bodyA.Selector.LogicalRadius;
            if(!LogicalScalar.TryFromDouble(distance,out LogicalScalar d)||!LogicalScalar.TryAdd(in radius,in d,out LogicalScalar y)){
                throw new InvalidOperationException("camera の高さを Logical にできません。");
            }
            LogicalScalar zero=LogicalScalar.Zero;
            cameraLogical.LocalPosition=new LogicalVector3(zero,y,zero);
            Quaternion rotation=Quaternion.LookRotation(Vector3.down,Vector3.forward);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){throw new InvalidOperationException("camera の回転を Logical にできません。");}
            cameraLogical.LocalRotation=logical;
            cameraLogical.SyncUnityTransform();
            bodyALogical.LocalPosition=new LogicalVector3(zero,zero,zero);bodyALogical.SyncUnityTransform();
            SetDelta(delta);
        }

        private static void SetDelta(double delta){
            if(!LogicalScalar.TryFromDouble(delta,out LogicalScalar offset)){throw new InvalidOperationException("Δ を Logical にできません。");}
            LogicalScalar zero=LogicalScalar.Zero;
            bodyBLogical.LocalPosition=new LogicalVector3(zero,offset,zero);
            bodyBLogical.SyncUnityTransform();
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Destroy(M0Validation.Capture(camera,Width,ImageHeight));
                int signature=bodyA.LastSelected.Count*31+bodyB.LastSelected.Count;
                foreach(PlanetCellKey key in bodyA.LastSelected){signature^=key.GetHashCode();}
                foreach(PlanetCellKey key in bodyB.LastSelected){signature^=key.GetHashCode()*17;}
                if(bodyA.PendingGeneration==0&&bodyB.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
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

        // 撮影。depthFormat が None なら M0Validation.Capture(24 ビットの深度)、そうでなければ深度の形式を明示した RenderTexture に描く。
        private static Texture2D Capture(GraphicsFormat depthFormat){
            if(depthFormat==GraphicsFormat.None){return M0Validation.Capture(camera,Width,ImageHeight);}
            var descriptor=new RenderTextureDescriptor(Width,ImageHeight,GraphicsFormat.R8G8B8A8_UNorm,depthFormat){msaaSamples=1,sRGB=false};
            RenderTexture target=RenderTexture.GetTemporary(descriptor);
            RenderTexture previous=RenderTexture.active;
            RenderTexture oldTarget=camera.targetTexture;
            try{
                camera.targetTexture=target;
                var request=new RenderPipeline.StandardRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(camera,request)){throw new InvalidOperationException("Active pipeline does not support StandardRequest.");}
                RenderPipeline.SubmitRenderRequest(camera,request);
                RenderTexture.active=target;
                var result=new Texture2D(Width,ImageHeight,TextureFormat.RGBA32,false,true);
                result.ReadPixels(new Rect(0,0,Width,ImageHeight),0,0);
                result.Apply();
                return result;
            }finally{
                camera.targetTexture=oldTarget;
                RenderTexture.active=previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // 0: 背景(マゼンタ)、1: A(青)、2: B(緑)、3: それ以外。
        private static int Classify(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return 0;}
            if(pixel.b>pixel.g+60&&pixel.b>pixel.r+60){return 1;}
            if(pixel.g>pixel.r+60&&pixel.g>pixel.b+60){return 2;}
            return 3;
        }

        // 画面の画素のうち、期待の色(expected: 1 = A、2 = B)でない天体の画素の数(背景は数えない。10^20 m では天体が画面の隅まで届かない)。
        private static int CountWrong(Texture2D image,int expected){
            int wrong=0;
            foreach(Color32 pixel in image.GetPixels32()){
                int kind=Classify(pixel);
                if(kind!=expected&&kind!=0){wrong++;}
            }
            return wrong;
        }

        private static int CountBackground(Texture2D image){
            int count=0;
            foreach(Color32 pixel in image.GetPixels32()){if(Classify(pixel)==0){count++;}}
            return count;
        }

        // 現在の camera と天体の位置(Place 済み)で、Δ/d を振って分解能を測る。選択の収束は呼ぶ側(Converge)で済ませる。
        private static Resolution Measure(double distance,GraphicsFormat depthFormat,string label){
            var result=new Resolution{Positive=double.PositiveInfinity,Negative=double.PositiveInfinity};
            for(int sign=1;sign>=-1;sign-=2){
                int run=0;double first=double.PositiveInfinity;
                for(int step=0;step<RatioCount;step++){
                    double ratio=FirstRatio*Math.Pow(2.0,step);
                    SetDelta(sign*ratio*distance);
                    Destroy(Capture(depthFormat));
                    Texture2D image=Capture(depthFormat);
                    // Δ > 0 は B が手前(緑 = 2)、Δ < 0 は A が手前(青 = 1)。
                    int expected=1;
                    if(sign>0){expected=2;}
                    int wrong=CountWrong(image,expected);
                    Destroy(image);
                    if(wrong==0){
                        run++;
                        if(run==1){first=ratio;}
                        if(run>=RequiredRun){break;}
                    }else{
                        run=0;first=double.PositiveInfinity;
                    }
                }
                if(run<RequiredRun){first=double.PositiveInfinity;}
                if(double.IsPositiveInfinity(first)){Diagnose(label,distance,sign,depthFormat);}
                if(sign>0){result.Positive=first;}else{result.Negative=first;}
            }
            SetDelta(0.0);
            table.Add(label+","+distance.ToString("G6",CultureInfo.InvariantCulture)+","+Format(result.Positive)+","+Format(result.Negative));
            return result;
        }

        // 分けられなかった条件の診断: 最も大きい Δ/d で撮り、色の内訳を報告に書き、画像を保存する。
        private static void Diagnose(string label,double distance,int sign,GraphicsFormat depthFormat){
            double ratio=FirstRatio*Math.Pow(2.0,RatioCount-1);
            SetDelta(sign*ratio*distance);
            Destroy(Capture(depthFormat));
            Texture2D image=Capture(depthFormat);
            var counts=new int[4];
            foreach(Color32 pixel in image.GetPixels32()){counts[Classify(pixel)]++;}
            string signName="plus";
            if(sign<0){signName="minus";}
            string fileName=label.Replace(' ','_').Replace('=','-')+"_d"+distance.ToString("G3",CultureInfo.InvariantCulture)+"_"+signName+".png";
            File.WriteAllBytes(results+"/"+fileName,image.EncodeToPNG());
            Destroy(image);
            report.Add("unresolved: "+label+" d="+distance.ToString("G3",CultureInfo.InvariantCulture)+" sign="+sign+" at ratio "+ratio.ToString("G3",CultureInfo.InvariantCulture)+": background="+counts[0]+" A="+counts[1]+" B="+counts[2]+" other="+counts[3]+" ("+fileName+")");
        }

        private static string Format(double ratio){
            if(double.IsPositiveInfinity(ratio)){return "none";}
            return ratio.ToString("G3",CultureInfo.InvariantCulture);
        }

        private static IEnumerator Steps(){
            int waited=0;
            while(bodyA.Selector==null||bodyB.Selector==null){
                if(waited++>100){throw new InvalidOperationException("天体が有効になりません。");}
                yield return null;
            }
            report.Add("environment: GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType+" reversedZ="+SystemInfo.usesReversedZBuffer+
                " defaultDepthStencil="+SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil)+" D24="+SystemInfo.IsFormatSupported(GraphicsFormat.D24_UNorm_S8_UInt,GraphicsFormatUsage.Render)+
                " D32F="+SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,GraphicsFormatUsage.Render)+" camera near="+camera.nearClipPlane+" far="+camera.farClipPlane);
            report.Add("columns: label,distance(m),minimum resolvable |delta|/d (delta>0: B in front),(delta<0: A in front)");

            // 1. 距離ごと(M = 10、near = 0.3、24 ビット深度)。
            double[] distances={1.7,10,100,1e3,1e4,1e5,1e6,1e8,1e10,1e12,1e15,1e20};
            double worstDistanceRatio=0;
            foreach(double d in distances){
                Place(d,0.0);
                foreach(object step in Converge("d="+d)){yield return step;}
                {
                    // 穴の確認: Δ = 0 で背景の画素が無いこと(10^20 m は、天体の見かけの半径 30° が画面の水平半画角 32° より小さく、隅に背景が出る)。
                    Texture2D image=Capture(GraphicsFormat.None);
                    int background=CountBackground(image);
                    Destroy(image);
                    report.Add("holes: d="+d.ToString("G3",CultureInfo.InvariantCulture)+" backgroundPixels="+background);
                    if(background!=0&&d<1e19){failures.Add("距離 "+d+" m: 天体の上に背景の画素が "+background+" 個あります(穴)。");}
                }
                Resolution r=Measure(d,GraphicsFormat.None,"distance");
                double worst=Math.Max(r.Positive,r.Negative);
                if(d>GatedDistance){continue;}
                if(double.IsPositiveInfinity(worst)){failures.Add("距離 "+d+" m: Δ/d を 2e-3 まで振っても、深度が両方の符号で分けられません。");}
                else{
                    worstDistanceRatio=Math.Max(worstDistanceRatio,worst);
                    if(worst>AcceptableRatio){failures.Add("距離 "+d+" m: 深度の分解能 Δ/d = "+worst+" が許す上限 "+AcceptableRatio+" より粗いです。");}
                }
            }
            report.Add("worst resolution over distances up to "+GatedDistance.ToString("G3",CultureInfo.InvariantCulture)+" m (M=10, near=0.3, default depth): "+worstDistanceRatio.ToString("G3",CultureInfo.InvariantCulture));

            // 2. View Scale M(d = 1.7、1e3、1e6、1e12)。
            foreach(float scale in new[]{1f,10f,100f,1000f}){
                radial.SetViewScale(scale);
                foreach(double d in new[]{1.7,1e3,1e6,1e12}){
                    Place(d,0.0);
                    foreach(object step in Converge("M="+scale+" d="+d)){yield return step;}
                    Measure(d,GraphicsFormat.None,"M="+scale.ToString(CultureInfo.InvariantCulture));
                }
            }
            radial.SetViewScale(10f);

            // 3. Camera.near × 深度の形式(d = 1e3、1e9)。
            var formats=new List<GraphicsFormat>();
            if(SystemInfo.IsFormatSupported(GraphicsFormat.D24_UNorm_S8_UInt,GraphicsFormatUsage.Render)){formats.Add(GraphicsFormat.D24_UNorm_S8_UInt);}
            if(SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt,GraphicsFormatUsage.Render)){formats.Add(GraphicsFormat.D32_SFloat_S8_UInt);}
            foreach(float near in new[]{0.03f,0.3f,3f}){
                camera.nearClipPlane=near;
                foreach(GraphicsFormat format in formats){
                    foreach(double d in new[]{1e3,1e9}){
                        Place(d,0.0);
                        foreach(object step in Converge("near="+near+" "+format+" d="+d)){yield return step;}
                        Measure(d,format,"near="+near.ToString(CultureInfo.InvariantCulture)+" "+format);
                    }
                }
            }
            camera.nearClipPlane=0.3f;
            report.Add("pool: A used="+bodyA.UsedSlots+"/"+bodyA.PoolCapacity+" B used="+bodyB.UsedSlots+"/"+bodyB.PoolCapacity+" lastError="+(bodyA.LastError??"(none)")+"/"+(bodyB.LastError??"(none)"));
            if(bodyA.LastError!=null||bodyB.LastError!=null){failures.Add("設定エラー: "+bodyA.LastError+" "+bodyB.LastError);}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            report.AddRange(table);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
