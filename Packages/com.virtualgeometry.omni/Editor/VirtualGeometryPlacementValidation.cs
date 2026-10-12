using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// M1b: VG の部品(<see cref="VirtualGeometryRenderer"/>)を、配置の差し込み口(<see cref="OmniVirtualGeometryPlacement"/>)で Omni の Logical 座標に置き、
    /// Radial 対数描画で正しく描けるかを、同じ建物を通常の MeshRenderer(Omni の Scene BRG)で描いた画像と比べて確かめる。
    /// - 建物は Logical の原点から 600 km(半径 600 km の惑星の地表に相当)、Y 軸まわりに 30° 回す
    /// - 見る位置は 4 つ(近く・中くらい・遠く・足元から見上げる)。VG の LOD の許容誤差は 0.5 px
    /// - 画素の判定: 背景色(マゼンタ)でない画素を建物とし、VG と MeshRenderer の片方だけが建物の画素は、もう片方の建物の画素から 1 画素(8 近傍)以内であること
    /// - VG は代替 Mesh を描かない(ProxyFallback=false)ので、VG が描けなければ建物の画素が無く、不合格になる
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.VirtualGeometryPlacementValidation.Run
    /// </summary>
    public static class VirtualGeometryPlacementValidation{
        private const string Results="TestResults/Planet/VirtualGeometryPlacement";
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=400;
        private const double Radius=600000.0;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        // 見る位置(建物の局所の目の位置と、見る点)。
        private static readonly (string name,Vector3 eye,Vector3 target)[] Views={
            ("Near",new Vector3(40f,30f,-70f),new Vector3(0f,36f,0f)),
            ("Middle",new Vector3(-150f,60f,-260f),new Vector3(0f,36f,0f)),
            ("Far",new Vector3(900f,300f,-1300f),new Vector3(0f,36f,0f)),
            ("Below",new Vector3(18f,1.7f,-22f),new Vector3(0f,60f,0f)),
        };

        private static Camera camera;
        private static LogicalTransform cameraLogical;
        private static GameObject virtualBuilding;
        private static GameObject referenceBuilding;
        private static VirtualGeometryRenderer renderer;
        private static OmniVirtualGeometryPlacement placement;
        private static LogicalVector3 buildingPosition;
        private static Quaternion buildingRotation;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(Results);
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
                if(EditorApplication.timeSinceStartup-started>1800){throw new TimeoutException("VirtualGeometryPlacement validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            // 新しい scene を開くと使っていないアセットが解放されるので、アセットは scene を作った後に読む。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            VirtualObjectSampleAssets.Building building=VirtualObjectSampleAssets.EnsureBuilding();
            camera=new GameObject("VG Placement Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=100000f;camera.fieldOfView=60f;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1f,0f,1f,1f);camera.allowHDR=false;camera.allowMSAA=false;
            cameraLogical=camera.gameObject.AddComponent<LogicalTransform>();
            camera.gameObject.AddComponent<global::Omni.RadialCamera>().SetIdentityViewScale();
            Light sun=new GameObject("VG Placement Sun").AddComponent<Light>();
            sun.type=LightType.Directional;sun.shadows=LightShadows.None;sun.transform.rotation=Quaternion.Euler(40f,-30f,0f);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.2f,0.2f,0.22f);
            if(!LogicalVector3.TryFromFloat3(new float3(0f,(float)Radius,0f),out buildingPosition)){throw new InvalidOperationException("building position");}
            buildingRotation=Quaternion.Euler(0f,30f,0f);
            if(!LogicalQuaternion.TryFromFloat4(buildingRotation.x,buildingRotation.y,buildingRotation.z,buildingRotation.w,out LogicalQuaternion rotation)){throw new InvalidOperationException("rotation");}

            // VG の建物(代替 Mesh は描かない)。
            virtualBuilding=new GameObject("Building (Virtual Geometry)");virtualBuilding.SetActive(false);
            // 保存しない GameObject にする(Omni の Scene BRG は保存される MeshRenderer を自動で取り込むが、VG の代替 Mesh は VG が描く)。
            virtualBuilding.hideFlags=HideFlags.DontSave;
            LogicalTransform virtualLogical=virtualBuilding.AddComponent<LogicalTransform>();
            virtualLogical.LocalPosition=buildingPosition;virtualLogical.LocalRotation=rotation;virtualLogical.SyncUnityTransform();
            renderer=virtualBuilding.AddComponent<VirtualGeometryRenderer>();
            MeshRenderer ordinary=virtualBuilding.GetComponent<MeshRenderer>();
            ordinary.sharedMaterial=building.VirtualMaterial;ordinary.shadowCastingMode=ShadowCastingMode.Off;ordinary.receiveShadows=false;
            renderer.SetMeshAsset(building.VirtualAsset);
            if(renderer.MeshAsset==null){throw new InvalidOperationException("VG の部品に変換データが入りませんでした。");}
            renderer.SetLodSettings(0.5f,0.1f);
            renderer.SetProxyOptions(false,false);
            placement=virtualBuilding.AddComponent<OmniVirtualGeometryPlacement>();
            // 比べ: 同じ建物の元のメッシュを通常の MeshRenderer で(Omni の Scene BRG が描く)。
            referenceBuilding=new GameObject("Building (MeshRenderer)");referenceBuilding.SetActive(false);
            LogicalTransform referenceLogical=referenceBuilding.AddComponent<LogicalTransform>();
            referenceLogical.LocalPosition=buildingPosition;referenceLogical.LocalRotation=rotation;referenceLogical.SyncUnityTransform();
            referenceBuilding.AddComponent<MeshFilter>().sharedMesh=building.SourceMesh;
            MeshRenderer reference=referenceBuilding.AddComponent<MeshRenderer>();
            reference.sharedMaterial=building.LitMaterial;reference.shadowCastingMode=ShadowCastingMode.Off;reference.receiveShadows=false;
            reference.lightProbeUsage=LightProbeUsage.Off;reference.reflectionProbeUsage=ReflectionProbeUsage.Off;
            report.Add("building: triangles="+(building.SourceMesh.triangles.Length/3)+" vgLeafTriangles="+building.VirtualAsset.LeafTriangleCount+" position=(0,"+Radius.ToString("G7",CultureInfo.InvariantCulture)+",0) rotationY=30");
        }

        private static IEnumerator Steps(){
            var virtualImages=new Dictionary<string,Texture2D>();
            var referenceImages=new Dictionary<string,Texture2D>();
            // 1. 何も無い(背景だけ)ことを確かめる。
            PlaceCamera(Views[0].eye,Views[0].target);
            for(int i=0;i<3;i++){Capture();yield return null;}
            int empty=CountBuilding(Capture());
            report.Add("empty: buildingPixels="+empty);
            if(empty!=0){failures.Add("建物を置く前に、背景でない画素が "+empty+" ありました。");}
            // 2. VG の建物だけで、見る位置ごとに収束を待って撮る。
            virtualBuilding.SetActive(true);
            foreach(var view in Views){
                PlaceCamera(view.eye,view.target);
                Texture2D image=null;
                foreach(object step in Converge("VG "+view.name,captured=>image=captured,true)){yield return step;}
                virtualImages[view.name]=image;
                Save("VG"+view.name,image);
            }
            report.Add("vg: lastVirtualDrawFrame="+renderer.LastVirtualDrawFrame+" placementFailure="+(placement.LastFailure??"(none)")+" proxyVisible="+renderer.IsFallbackVisible);
            if(renderer.IsFallbackVisible){failures.Add("VG の代替 Mesh(通常の MeshRenderer)が描画に出ていました(ProxyFallback=false のはず)。");}
            // 負の対照: VG の建物を横へ 0.5 m ずらして近くから撮る(比べ方が 0.5 m のずれを見分けられること)。
            LogicalTransform virtualLogical=virtualBuilding.GetComponent<LogicalTransform>();
            if(!LogicalVector3.TryFromFloat3(new float3(0.5f,0f,0f),out LogicalVector3 shift)||!LogicalVector3.TryAdd(buildingPosition,shift,out LogicalVector3 shifted)){throw new InvalidOperationException("shift");}
            virtualLogical.LocalPosition=shifted;virtualLogical.SyncUnityTransform();
            PlaceCamera(Views[0].eye,Views[0].target);
            Texture2D shiftedImage=null;
            foreach(object step in Converge("VG NearShifted",captured=>shiftedImage=captured,true)){yield return step;}
            Save("VGNearShifted",shiftedImage);
            virtualLogical.LocalPosition=buildingPosition;virtualLogical.SyncUnityTransform();
            // 3. VG を外し、MeshRenderer の建物だけで撮る(Omni の Scene BRG の sidecar を同期で用意する)。
            virtualBuilding.SetActive(false);
            referenceBuilding.SetActive(true);
            string prepared=global::Omni.Editor.BatchRendering.RadialSceneBrgEditorIntegration.PrepareLoadedSceneSidecarsForAutomation();
            report.Add("brg: "+(prepared??"(null)"));
            foreach(var view in Views){
                PlaceCamera(view.eye,view.target);
                Texture2D image=null;
                foreach(object step in Converge("MeshRenderer "+view.name,captured=>image=captured,false)){yield return step;}
                referenceImages[view.name]=image;
                Save("MeshRenderer"+view.name,image);
            }
            // 4. 比べる。
            foreach(var view in Views){
                Texture2D a=virtualImages[view.name];Texture2D b=referenceImages[view.name];
                int virtualPixels=CountBuilding(a);int referencePixels=CountBuilding(b);
                int outside=CompareMasks(a,b,out int differing,out Texture2D diff);
                Save("Diff"+view.name,diff);
                UnityEngine.Object.DestroyImmediate(diff);
                report.Add(view.name+": vgPixels="+virtualPixels+" meshRendererPixels="+referencePixels+" differing="+differing+" beyondOnePixel="+outside);
                if(virtualPixels==0){failures.Add(view.name+": VG の建物が描かれていません("+(placement.LastFailure??"理由不明")+")。");}
                if(referencePixels==0){failures.Add(view.name+": MeshRenderer の建物が描かれていません。");}
                if(outside!=0){failures.Add(view.name+": VG と MeshRenderer の建物の輪郭が、1 画素を超えて違う画素が "+outside+" ありました。");}
            }
            {
                int outside=CompareMasks(shiftedImage,referenceImages[Views[0].name],out int differing,out Texture2D diff);
                Save("DiffNearShifted",diff);
                UnityEngine.Object.DestroyImmediate(diff);
                report.Add("negative control (VG shifted 0.5 m, Near): differing="+differing+" beyondOnePixel="+outside);
                if(outside==0){failures.Add("負の対照: VG の建物を 0.5 m ずらしても、1 画素を超える違いを検出できませんでした(比べ方が鈍い)。");}
            }
        }

        // 画像が 2 回続けて同じで、VG の読み込み待ちが無くなるまで撮る。
        private static IEnumerable<object> Converge(string name,Action<Texture2D> result,bool virtualGeometry){
            Texture2D previous=null;int stable=0;int frames=0;
            while(frames<ConvergeLimit){
                Texture2D image=Capture();
                bool pending=false;
                if(virtualGeometry){
                    VirtualGeometryStreamingStatistics statistics=renderer.StreamingStatistics;
                    pending=statistics!=null&&statistics.Queued+statistics.Loading+statistics.Uploading>0;
                }
                if(previous!=null&&!pending&&Same(previous,image)){stable++;}else{stable=0;}
                if(previous!=null){UnityEngine.Object.DestroyImmediate(previous);}
                previous=image;
                frames++;
                // VG は描いた frame が出るまで待つ(読み込みの開始前に、空の画像で収束したと見なさない)。
                bool drawn=!virtualGeometry||renderer.LastVirtualDrawFrame>=0;
                if(stable>=2&&frames>=5&&drawn){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で画像が収束しませんでした。");}
            string detail="";
            if(virtualGeometry){
                MeshRenderer ordinary=renderer.GetComponent<MeshRenderer>();
                VirtualGeometryStreamingStatistics statistics=renderer.StreamingStatistics;
                string streaming="(none)";
                if(statistics!=null){streaming="queued="+statistics.Queued+"/loading="+statistics.Loading+"/uploading="+statistics.Uploading+"/failed="+statistics.Failed;}
                detail=" asset="+(renderer.MeshAsset!=null)+" active="+renderer.isActiveAndEnabled+" inScene="+renderer.gameObject.scene.IsValid()+" key="+renderer.SharedResourceKey+
                    " resources="+VirtualGeometryRenderer.SharedResourceCount+" geometryBytes="+renderer.DiagnosticGeometryAllocatedBytes+
                    " reason="+(renderer.DiagnosticReason??"(none)")+" rootReady="+renderer.IsStreamingRootReady+" proxyFallback="+renderer.ProxyFallback+" forceRenderingOff="+ordinary.forceRenderingOff+
                    " lastVirtualDrawFrame="+renderer.LastVirtualDrawFrame+" frameCount="+Time.frameCount+" placementFailure="+(placement.LastFailure??"(none)")+" streaming="+streaming;
            }
            report.Add(name+": converged frames="+frames+detail);
            result(previous);
        }

        // camera を建物の局所の位置 eye に置き、建物の局所の点 target を見る(上は建物の上 = +Y)。
        private static void PlaceCamera(Vector3 eye,Vector3 target){
            Vector3 eyeWorld=buildingRotation*eye;Vector3 targetWorld=buildingRotation*target;
            if(!LogicalVector3.TryFromFloat3((float3)eyeWorld,out LogicalVector3 offset)||!LogicalVector3.TryAdd(buildingPosition,offset,out LogicalVector3 position)){throw new InvalidOperationException("camera position");}
            Quaternion rotation=Quaternion.LookRotation(targetWorld-eyeWorld,Vector3.up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion value)){throw new InvalidOperationException("camera rotation");}
            cameraLogical.LocalPosition=position;cameraLogical.LocalRotation=value;cameraLogical.SyncUnityTransform();
        }

        private static Texture2D Capture(){
            return M0Validation.Capture(camera,Width,ImageHeight);
        }

        private static bool IsBackground(Color32 color){
            return color.r==Background.r&&color.g==Background.g&&color.b==Background.b;
        }

        private static int CountBuilding(Texture2D image){
            int count=0;
            foreach(Color32 pixel in image.GetPixels32()){
                if(!IsBackground(pixel)){count++;}
            }
            return count;
        }

        private static bool Same(Texture2D a,Texture2D b){
            Color32[] x=a.GetPixels32();Color32[] y=b.GetPixels32();
            if(x.Length!=y.Length){return false;}
            for(int i=0;i<x.Length;i++){
                if(x[i].r!=y[i].r||x[i].g!=y[i].g||x[i].b!=y[i].b){return false;}
            }
            return true;
        }

        // 片方だけが建物の画素のうち、もう片方の建物の画素から 1 画素(8 近傍)を超えて離れている画素の数。
        // diff: 両方が建物 = 灰、VG だけ = 赤、MeshRenderer だけ = 青(1 画素を超えるものは明るく)、両方が背景 = 白。
        private static int CompareMasks(Texture2D a,Texture2D b,out int differing,out Texture2D diff){
            int width=a.width;int height=a.height;
            Color32[] x=a.GetPixels32();Color32[] y=b.GetPixels32();
            var ma=new bool[x.Length];var mb=new bool[y.Length];
            for(int i=0;i<x.Length;i++){ma[i]=!IsBackground(x[i]);mb[i]=!IsBackground(y[i]);}
            var colors=new Color32[x.Length];
            differing=0;int outside=0;
            for(int py=0;py<height;py++){
                for(int px=0;px<width;px++){
                    int i=py*width+px;
                    if(ma[i]==mb[i]){
                        if(ma[i]){colors[i]=new Color32(90,90,90,255);}else{colors[i]=new Color32(255,255,255,255);}
                        continue;
                    }
                    differing++;
                    bool[] other=mb;
                    if(mb[i]){other=ma;}
                    bool near=false;
                    for(int dy=-1;dy<=1&&!near;dy++){
                        for(int dx=-1;dx<=1&&!near;dx++){
                            int qx=px+dx;int qy=py+dy;
                            if(qx<0||qy<0||qx>=width||qy>=height){continue;}
                            if(other[qy*width+qx]){near=true;}
                        }
                    }
                    if(!near){outside++;}
                    if(ma[i]){
                        if(near){colors[i]=new Color32(200,120,120,255);}else{colors[i]=new Color32(255,0,0,255);}
                    }else{
                        if(near){colors[i]=new Color32(120,120,200,255);}else{colors[i]=new Color32(0,0,255,255);}
                    }
                }
            }
            diff=new Texture2D(width,height,TextureFormat.RGBA32,false);
            diff.SetPixels32(colors);diff.Apply();
            return outside;
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
