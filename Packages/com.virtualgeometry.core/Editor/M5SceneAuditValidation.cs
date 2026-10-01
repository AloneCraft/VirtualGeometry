using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// sample sceneを利用者が開いたときと同じ状態(保存されたままの設定とprojectの既定render pipeline)で点検する。sceneは保存しない。
    /// 有効なVGの部品ごとに、変換データと代替Meshがあり、物体が画面いっぱいに入る距離で予算不足にならず詳細化を終え、
    /// 近くでは遠くより多くの三角形を描くことを確かめる。
    /// 検証用の厳しい設定(閾値・予算・遅延)をsceneへ保存してしまうと、開いても距離でLODが変わらなくなるため、その再発を防ぐ。
    /// </summary>
    public static class M5SceneAuditValidation{
        private enum Phase{Budget,Close,Far}
        private const string Root="Assets/VirtualizedGeometrySample";private const string Output="TestResults/M5/SceneAudit";
        // Game viewに近い解像度で測る。LODの細かさは画面上の誤差(pixel)で決まるため、解像度が低いと必要なpageが少なく見積もられる。
        private const int Width=1280,Height=720,SettleFrames=3,MaxFrames=900;
        // 距離はbounds半径の倍数。2倍(縦の視野60度)で物体全体が画面いっぱいに入る。
        // LODの比較は、平らな面や細長い物体でも細かい段が選ばれる0.5倍と、大きな部品を取り除いた段の誤差も無視できる400倍(数pixel)で行う。
        private const float BudgetDistance=2,CloseDistance=0.5f,FarDistance=400;
        private static readonly List<string> results=new List<string>();private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();private static readonly List<string> warnings=new List<string>();
        private static readonly Queue<string> scenes=new Queue<string>();private static readonly Queue<VirtualGeometryRenderer> pending=new Queue<VirtualGeometryRenderer>();
        private static string scenePath;private static Vector3 viewDirection;private static Camera camera;private static VirtualGeometryRenderer current;
        private static Phase phase;private static int frames;private static int settled;private static long budgetTriangles;private static long closeTriangles;private static double started;

        public static void Run(){
            Directory.CreateDirectory(Output);results.Clear();failures.Clear();errors.Clear();warnings.Clear();scenes.Clear();pending.Clear();current=null;
            started=EditorApplication.timeSinceStartup;Application.logMessageReceived+=Log;
            try{
                // Benchmarkのsceneは計測時にBenchmarkBuildが生成する一時物なので対象外。
                foreach(string path in AssetDatabase.FindAssets("t:Scene",new[]{Root}).Select(AssetDatabase.GUIDToAssetPath).Where(path=>!path.Contains("/Benchmark")).OrderBy(path=>path,StringComparer.Ordinal)){scenes.Enqueue(path);}
                results.Add("Pipeline="+GraphicsSettings.currentRenderPipeline?.name+", scenes="+scenes.Count+", resolution="+Width+"x"+Height);
                EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }

        // 警告は記録だけにする(利用者のsceneのlighting dataなど、VGと無関係なものも出るため)。errorと例外は失敗にする。
        private static void Log(string message,string stack,LogType type){
            if(type==LogType.Warning){warnings.Add(message);}
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1800){throw new TimeoutException("Scene audit timeout.");}
                if(current==null){
                    if(pending.Count==0){
                        if(scenes.Count==0){Complete();return;}
                        OpenNextScene();return;
                    }
                    Begin(pending.Dequeue());return;
                }
                Texture2D image=M0Validation.Capture(camera,Width,Height);Object.DestroyImmediate(image);frames++;
                if(Settled(phase==Phase.Budget)){settled++;}else{settled=0;}
                bool timeout=frames>MaxFrames;
                if(settled<SettleFrames&&!timeout){return;}
                long triangles=current.ReadMetricsForDiagnostics(camera).DrawnTriangles;
                if(phase==Phase.Budget){
                    if(timeout){
                        Fail(Name(current)+": 物体が画面いっぱいに入る距離で"+MaxFrames+" frame待っても詳細化が完了しません("+Detail()+")。予算不足か読込が止まっています。");
                        current=null;return;
                    }
                    budgetTriangles=triangles;
                    // 1段しかない変換データ(1 cluster程度の小さなMesh)は切り替える段が無いため、VGで描けて予算内に収まることだけを確かめる。
                    if(current.MeshAsset.LevelCount<=1){results.Add(Name(current)+": screen="+triangles+", single level: PASS");current=null;return;}
                    // 近距離では視錐台カリングで画面外のclusterが除かれ、LODとは別に三角形数が減って比べられないため、比べる間は止める(sceneは保存しない)。
                    current.SetCullingSettings(false,false);phase=Phase.Close;Place(CloseDistance);return;
                }
                if(phase==Phase.Close){closeTriangles=triangles;phase=Phase.Far;Place(FarDistance);return;}
                string line=Name(current)+": screen="+budgetTriangles+", close="+closeTriangles+", far="+triangles+", leaf="+current.MeshAsset.LeafTriangleCount;
                if(closeTriangles<=triangles){Fail(line+" -- 距離でLODが切り替わりません。");}else{results.Add(line+": PASS");}
                current=null;
            }catch(Exception error){Finish(error);}
        }

        private static void OpenNextScene(){
            scenePath=scenes.Dequeue();
            EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Single);
            // 保存されたcameraの向きから眺める。cameraが無いsceneは正面(+Z方向)から。
            viewDirection=Vector3.forward;
            Camera saved=Object.FindObjectsByType<Camera>().Where(found=>found.isActiveAndEnabled).OrderBy(found=>found.name,StringComparer.Ordinal).FirstOrDefault();
            if(saved!=null){viewDirection=saved.transform.forward;}
            // sceneは保存しないため、一時cameraはsceneに置き、次のsceneを開くときに一緒に破棄させる。
            camera=new GameObject("Scene audit camera").AddComponent<Camera>();camera.fieldOfView=60;camera.nearClipPlane=0.01f;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;
            int count=0,skipped=0;var measured=new HashSet<VirtualGeometryMeshAsset>();
            foreach(VirtualGeometryRenderer renderer in Object.FindObjectsByType<VirtualGeometryRenderer>().OrderBy(found=>found.name,StringComparer.Ordinal)){
                if(!renderer.isActiveAndEnabled){continue;}
                count++;
                if(renderer.MeshAsset==null){Fail(Name(renderer)+": 変換データのない有効なVGの部品がsceneに保存されています。");continue;}
                if(renderer.GetComponent<MeshFilter>().sharedMesh!=renderer.MeshAsset.ProxyMesh){Fail(Name(renderer)+": MeshFilterに代替Meshが入っていません。");continue;}
                // 半透明などMaterialがVGで描けない部品は、仕様どおり通常Proxyで描かれ(Rendering.md)、詳細化を待っても終わらないため測らない。
                // 判定はMaterialだけ(TryResolve)で行い、streaming・予算・設定によるProxyは従来どおり失敗にする。
                string unsupported=UnsupportedMaterial(renderer);
                if(unsupported!=null){skipped++;results.Add("SKIP "+Name(renderer)+": Proxy(Material): "+unsupported);continue;}
                // LODの切り替わりは変換データとstreaming設定で決まるため、同じ変換データの部品は最初の1つだけ測る。
                if(measured.Add(renderer.MeshAsset)){pending.Enqueue(renderer);}
            }
            results.Add(scenePath+": VG="+count+", measured assets="+measured.Count+", proxy by material="+skipped);
        }

        // MaterialがVGで描けない理由。描けるならnull。nullのslotはVGが白いURP Litで埋めるので対象にしない。
        private static string UnsupportedMaterial(VirtualGeometryRenderer renderer){
            // 描画前のbatchmodeではURPのRenderPipelineタグが未設定で、URPのSubShaderのpassを引けない。URPが初期化時に入れるのと同じタグを補う。
            if(string.IsNullOrEmpty(Shader.globalRenderPipeline)&&GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset){Shader.globalRenderPipeline="UniversalPipeline";}
            foreach(Material material in renderer.GetComponent<MeshRenderer>().sharedMaterials){
                if(material!=null&&!VirtualGeometryMaterials.TryResolve(material,out _,out _,out string reason)){return reason;}
            }
            return null;
        }

        // 予算の確認は保存されたままの設定(カリングを含む)で行う。
        private static void Begin(VirtualGeometryRenderer renderer){
            current=renderer;phase=Phase.Budget;Place(BudgetDistance);
        }

        private static void Place(float radiusScale){
            Bounds bounds=current.WorldBounds;float radius=Mathf.Max(bounds.extents.magnitude,0.01f);
            camera.transform.SetPositionAndRotation(bounds.center-viewDirection*radius*radiusScale,Quaternion.LookRotation(viewDirection));
            camera.farClipPlane=radius*(radiusScale+2);frames=0;settled=0;
        }

        // VGが描き、読込待ちが無い状態。予算の確認では、予算不足で詳細化できないnodeが無いことも求める。AllResidentは統計を持たない。
        // LODの比較では予算で頭打ちになっていても切り替わりは判定できるため、品質未達は問わない(待ち切れなければその時点で測る)。
        private static bool Settled(bool requireQuality){
            if(current.IsFallbackVisible||!current.IsStreamingRootReady){return false;}
            var stats=current.StreamingStatistics;
            if(stats==null){return true;}
            if(stats.Queued!=0||stats.Loading!=0||stats.Uploading!=0){return false;}
            return !requireQuality||stats.QualityTargetMisses==0;
        }

        private static string Detail(){
            var stats=current.StreamingStatistics;string detail="fallback="+current.IsFallbackVisible+", reason="+current.DiagnosticReason;
            if(stats!=null){detail+=", queued="+stats.Queued+", loading="+stats.Loading+", uploading="+stats.Uploading+", quality misses="+stats.QualityTargetMisses+", resident="+stats.Resident+", evictions="+stats.Evictions+", overflow="+stats.QueueOverflow;}
            return detail;
        }

        private static string Name(VirtualGeometryRenderer renderer){return scenePath+" / "+renderer.name;}

        private static void Fail(string message){failures.Add(message);results.Add("FAIL "+message);}

        private static void Complete(){
            foreach(string warning in warnings.Distinct()){results.Add("WARN "+warning);}
            if(errors.Count>0){Fail("エラー: "+string.Join(" | ",errors.Distinct()));}
            if(failures.Count>0){Finish(new InvalidOperationException(failures.Count+" scene audit failure(s)."));return;}
            Finish(null);
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            if(camera!=null){Object.DestroyImmediate(camera.gameObject);}
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: sample scenes switch LOD by distance with saved settings";Debug.Log(text);}
            File.WriteAllText(Output+"/Results.txt",text);
            if(error!=null){EditorApplication.Exit(1);}else{EditorApplication.Exit(0);}
        }
    }
}
