using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// space_elevator.fbx の質感調整の検証。batchmode(-force-d3d12)で実行し、結果を TestResults/SpaceElevator へ書く。sceneは保存しない。
    /// Run: 修正後の確認。SpaceElevator.unity を参照画像と同じ構図で撮り、参照画像と横並びにする。あわせて
    ///   (1) 全部品が Main と平面反射の両カメラで VG 描画されること、(2) 平面反射が広場に効いていること、
    ///   (3) VG と元 Mesh の通常描画で手続き模様が一致すること(オブジェクト空間の一致)を確かめる。
    /// 参照画像は repo に入れず、-referenceImage &lt;path&gt; で渡す(既定は下の DefaultReference。無ければ横並びを省く)。
    /// </summary>
    public static class SpaceElevatorValidation{
        private const string Output="TestResults/SpaceElevator";
        private const string ImportScene="Assets/VirtualizedGeometrySample/Import.unity";
        private const string DefaultReference=@"G:\Blender\AI\OpenAI\起動エレベーター_地球駅・月駅\references\Ground_Station_Reference.png";
        // VG の起動完了(root 準備・読込待ちなし)が続いた frame 数。Development.md の注記どおり IsStreamingRootReady と IsFallbackVisible の両方で判定する。
        private const int SettleFrames=3,MaxFrames=900;
        // VG と元 Mesh の比較に使う一時 layer(sceneは保存しない)
        private const int VirtualLayer=30,ReferenceLayer=31;

        private sealed class Shot{
            public string Name;
            public Camera Camera;
            public int Width,Height;
            public bool Reflection;
            public Action Before;
            public Action<Texture2D> After;
            public bool RequireSettled=true;
        }

        private static readonly List<string> results=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly Queue<Shot> shots=new Queue<Shot>();
        private static List<VirtualGeometryRenderer> renderers;
        private static HashSet<VirtualGeometryRenderer> materialProxies;
        private static SpaceElevatorPlanarReflection reflection;
        private static RenderTexture mainTarget;
        private static Shot current;
        private static int frames,settled;
        private static double started;
        private static string resultFile;
        private static string previousRenderPipeline;
        private static Texture2D heroImage,noReflectionImage,virtualImage,referenceMeshImage;

        // ---- 修正後 ---------------------------------------------------------------------------------------------------

        /// <summary>修正後の確認(SpaceElevator.unity)。</summary>
        public static void Run(){
            Begin("Results.txt");
            try{
                Directory.CreateDirectory(Output+"/after");
                SpaceElevatorSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(SpaceElevatorSceneBuilder.ScenePath,OpenSceneMode.Single);
                results.Add("pipeline="+GraphicsSettings.currentRenderPipeline?.name+", ambientProbe[0,0]="+RenderSettings.ambientProbe[0,0].ToString("R",CultureInfo.InvariantCulture)+", skybox="+NameOf(RenderSettings.skybox));
                if(Mathf.Abs(RenderSettings.ambientProbe[0,0])<1e-6f){failures.Add("環境光(ambientProbe)が焼かれていません。");}
                CollectRenderers();
                Camera main=Camera.main;
                if(main==null){throw new InvalidOperationException("SpaceElevator.unity に Main Camera がありません。");}
                reflection=main.GetComponent<SpaceElevatorPlanarReflection>();
                if(reflection==null){throw new InvalidOperationException("Main Camera に SpaceElevatorPlanarReflection がありません。");}
                Light sun=RenderSettings.sun;
                SpaceElevatorCapture.BlenderCamera hero=SpaceElevatorCapture.Hero,aerial=SpaceElevatorCapture.Aerial;
                shots.Enqueue(new Shot{Name="Hero",Camera=main,Width=hero.Width,Height=hero.Height,Reflection=true,Before=()=>{reflection.enabled=true;SpaceElevatorCapture.Apply(main,hero);},
                    After=image=>{SpaceElevatorCapture.SavePng(image,Output+"/after/SE_GA_Camera.png");heroImage=Copy(image);}});
                shots.Enqueue(new Shot{Name="HeroNoReflection",Camera=main,Width=hero.Width,Height=hero.Height,Reflection=false,Before=()=>{reflection.enabled=false;},
                    After=image=>{SpaceElevatorCapture.SavePng(image,Output+"/after/SE_GA_Camera_NoReflection.png");noReflectionImage=Copy(image);}});
                shots.Enqueue(new Shot{Name="Aerial",Camera=main,Width=aerial.Width,Height=aerial.Height,Reflection=true,Before=()=>{reflection.enabled=true;SpaceElevatorCapture.Apply(main,aerial);},
                    After=image=>SpaceElevatorCapture.SavePng(image,Output+"/after/SE_GA_Camera_Aerial.png")});
                EnqueueConsistency(sun);
                EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }

        // 正面ゲート(SE_GA_GateBlock)を 30 m 程度の距離から、VG(最も細かい段・全常駐)と元 Mesh の通常描画で撮り比べる。影は Proxy から落ちるので切る。
        private static void EnqueueConsistency(Light sun){
            VirtualGeometryRenderer gate=renderers.Find(found=>found!=null&&found.name=="SE_GA_GateBlock");
            if(gate==null){failures.Add("SE_GA_GateBlock の VG 部品が見つかりません。");return;}
            Mesh source=SpaceElevatorPatternSpace.SourceMesh("SE_GA_GateBlock");
            var camera=new GameObject("Consistency Camera").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(SpaceElevatorPatternSpace.BlenderToWorld(new Vector3(20f,-290f,20f)),Quaternion.LookRotation(Vector3.back,Vector3.up));
            camera.fieldOfView=50f;camera.nearClipPlane=0.5f;camera.farClipPlane=3000f;camera.allowHDR=false;camera.allowMSAA=false;camera.clearFlags=CameraClearFlags.Skybox;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing=false;
            GameObject reference=null;
            LightShadows shadows=LightShadows.None;
            if(sun!=null){shadows=sun.shadows;}
            shots.Enqueue(new Shot{Name="Virtual",Camera=camera,Width=960,Height=720,Before=()=>{
                    reflection.enabled=false;
                    if(sun!=null){sun.shadows=LightShadows.None;}
                    gate.gameObject.layer=VirtualLayer;
                    gate.SetLodSettings(0f,0f);
                    gate.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());
                    reference=new GameObject("GateBlock source mesh"){layer=ReferenceLayer};
                    reference.transform.SetPositionAndRotation(gate.transform.position,gate.transform.rotation);
                    reference.transform.localScale=gate.transform.lossyScale;
                    reference.AddComponent<MeshFilter>().sharedMesh=source;
                    var visual=reference.AddComponent<MeshRenderer>();
                    visual.sharedMaterials=gate.GetComponent<MeshRenderer>().sharedMaterials;
                    visual.shadowCastingMode=ShadowCastingMode.Off;
                    camera.cullingMask=~(1<<ReferenceLayer);
                },After=image=>{virtualImage=Copy(image);SpaceElevatorCapture.SavePng(image,Output+"/after/GateVirtual.png");}});
            shots.Enqueue(new Shot{Name="SourceMesh",Camera=camera,Width=960,Height=720,RequireSettled=false,Before=()=>{camera.cullingMask=~(1<<VirtualLayer);},
                After=image=>{
                    referenceMeshImage=Copy(image);SpaceElevatorCapture.SavePng(image,Output+"/after/GateSourceMesh.png");
                    if(sun!=null){sun.shadows=shadows;}
                    if(reference!=null){Object.DestroyImmediate(reference);}
                }});
        }

        // ---- 共通 -----------------------------------------------------------------------------------------------------

        private static void Begin(string file){
            results.Clear();errors.Clear();failures.Clear();shots.Clear();current=null;resultFile=file;started=EditorApplication.timeSinceStartup;
            heroImage=null;noReflectionImage=null;virtualImage=null;referenceMeshImage=null;
            // URP の SubShader(RenderPipeline タグ)で Material の pass を引くため、描画前でも URP と同じタグを入れておく(VirtualGeometryMaterialsTests と同じ)。
            previousRenderPipeline=Shader.globalRenderPipeline;Shader.globalRenderPipeline="UniversalPipeline";
            Application.logMessageReceived+=Log;
        }

        private static void Log(string message,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}
        }

        // 半透明など Material が VG で描けない部品は、仕様どおり Proxy で描かれるので判定から外す(M5SceneAuditValidation と同じ規則)。
        private static void CollectRenderers(){
            renderers=new List<VirtualGeometryRenderer>(Object.FindObjectsByType<VirtualGeometryRenderer>());
            materialProxies=new HashSet<VirtualGeometryRenderer>();
            foreach(VirtualGeometryRenderer renderer in renderers){
                foreach(Material material in renderer.GetComponent<MeshRenderer>().sharedMaterials){
                    if(material!=null&&!VirtualGeometryMaterials.TryResolve(material,out _,out _,out string reason)){
                        materialProxies.Add(renderer);
                        results.Add("Proxy(Material): "+renderer.name+": "+reason);
                        break;
                    }
                }
            }
            results.Add("VG renderers="+renderers.Count+", proxy by material="+materialProxies.Count);
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>2400){throw new TimeoutException("SpaceElevator validation timeout.");}
                if(current==null){
                    if(shots.Count==0){Complete();return;}
                    current=shots.Dequeue();frames=0;settled=0;
                    current.Before?.Invoke();
                }
                bool ready=true;
                if(current.Reflection&&reflection!=null&&reflection.isActiveAndEnabled){ready&=RenderReflection(current);}
                Texture2D image=SpaceElevatorCapture.CaptureSrgb(current.Camera,current.Width,current.Height);
                ready&=Settled(current.Camera,out string pending);
                frames++;
                if(ready||!current.RequireSettled){settled++;}else{settled=0;}
                if(settled<SettleFrames&&frames<MaxFrames){Object.DestroyImmediate(image);EditorApplication.QueuePlayerLoopUpdate();return;}
                if(settled<SettleFrames){failures.Add(current.Name+": "+MaxFrames+" frame 待っても VG が揃いません("+pending+")。");}
                string line=current.Name+": captured after "+frames+" frames, VG drawn triangles="+DrawnTriangles(current.Camera);
                if(current.Reflection&&reflection!=null&&reflection.ReflectionCamera!=null){
                    long mirrored=DrawnTriangles(reflection.ReflectionCamera);
                    line+=", reflection camera VG drawn triangles="+mirrored;
                    if(mirrored<=0){failures.Add(current.Name+": 平面反射カメラで VG が描かれていません。");}
                }
                results.Add(line);
                current.After?.Invoke(image);
                Object.DestroyImmediate(image);
                current=null;
            }catch(Exception error){Finish(error);}
        }

        // 平面反射カメラを Main Camera の鏡像へ合わせ、Main Camera と同じ解像度の比率で先に描く。
        private static bool RenderReflection(Shot shot){
            if(mainTarget==null||mainTarget.width!=shot.Width||mainTarget.height!=shot.Height){
                if(mainTarget!=null){mainTarget.Release();Object.DestroyImmediate(mainTarget);}
                mainTarget=new RenderTexture(shot.Width,shot.Height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            }
            RenderTexture previous=shot.Camera.targetTexture;
            shot.Camera.targetTexture=mainTarget;
            reflection.UpdateReflectionCamera();
            shot.Camera.targetTexture=previous;
            Camera mirror=reflection.ReflectionCamera;
            var request=new RenderPipeline.StandardRequest{destination=reflection.Target};
            RenderPipeline.SubmitRenderRequest(mirror,request);
            return Settled(mirror,out _);
        }

        private static bool Settled(Camera camera,out string pending){
            var text=new StringBuilder();int count=0;
            foreach(VirtualGeometryRenderer renderer in renderers){
                if(renderer==null||!renderer.isActiveAndEnabled||materialProxies.Contains(renderer)){continue;}
                if((camera.cullingMask&(1<<renderer.gameObject.layer))==0){continue;}
                bool busy=renderer.IsFallbackVisible||!renderer.IsStreamingRootReady;
                var stats=renderer.StreamingStatistics;
                if(!busy&&stats!=null&&(stats.Queued!=0||stats.Loading!=0||stats.Uploading!=0)){busy=true;}
                if(busy){
                    count++;
                    if(count<=4){text.Append(renderer.name).Append('[').Append(renderer.DiagnosticReason).Append("] ");}
                }
            }
            pending=count+" pending "+text;
            return count==0;
        }

        private static long DrawnTriangles(Camera camera){
            long total=0;
            foreach(VirtualGeometryRenderer renderer in renderers){
                if(renderer==null||!renderer.isActiveAndEnabled||materialProxies.Contains(renderer)){continue;}
                total+=renderer.ReadMetricsForDiagnostics(camera).DrawnTriangles;
            }
            return total;
        }

        private static void Complete(){
            if(heroImage!=null&&noReflectionImage!=null){
                // 平面反射: 画面下 1/4(広場)で、反射あり/なしの差が出ていること
                Difference(heroImage,noReflectionImage,0,heroImage.height/4,out double mean,out int changed);
                results.Add("planar reflection effect on plaza: mean RGB difference="+mean.ToString("F5",CultureInfo.InvariantCulture)+", changed pixels="+changed);
                if(changed<1000){failures.Add("平面反射が広場に効いていません(変化画素 "+changed+")。");}
            }
            if(virtualImage!=null&&referenceMeshImage!=null){
                Difference(virtualImage,referenceMeshImage,0,virtualImage.height,out double mean,out int changed);
                results.Add("VG vs source mesh (GateBlock 30 m): mean RGB error="+mean.ToString("F5",CultureInfo.InvariantCulture)+", changed pixels="+changed+" / "+(virtualImage.width*virtualImage.height));
                if(mean>0.01||changed>virtualImage.width*virtualImage.height/50){failures.Add("VG と元 Mesh で手続き模様が一致しません(mean="+mean+", changed="+changed+")。");}
            }
            if(heroImage!=null){CompareWithReference(heroImage);}
            if(errors.Count>0){failures.Add("Console error: "+string.Join(" | ",errors));}
            if(failures.Count>0){Finish(new InvalidOperationException(failures.Count+" failure(s): "+string.Join(" / ",failures)));return;}
            Finish(null);
        }

        // 参照画像と横並びにし、領域ごとの平均色(sRGB 0-255)を並べて記録する。参照画像の注記・挿絵(左上の3枚、右上の図、下端の文字)は領域から外す。
        private static void CompareWithReference(Texture2D image){
            string path=DefaultReference;
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i+1<arguments.Length;i++){if(arguments[i]=="-referenceImage"){path=arguments[i+1];}}
            if(!File.Exists(path)){results.Add("reference image not found: "+path+" (side-by-side skipped)");return;}
            var reference=new Texture2D(2,2,TextureFormat.RGBA32,false,false);
            reference.LoadImage(File.ReadAllBytes(path));
            if(reference.width!=image.width||reference.height!=image.height){results.Add("reference size "+reference.width+"x"+reference.height+" differs (side-by-side skipped)");Object.DestroyImmediate(reference);return;}
            var pair=new Texture2D(image.width*2,image.height,TextureFormat.RGBA32,false,false);
            pair.SetPixels32(0,0,image.width,image.height,image.GetPixels32());
            pair.SetPixels32(image.width,0,image.width,image.height,reference.GetPixels32());
            pair.Apply();
            SpaceElevatorCapture.SavePng(pair,Output+"/after/SE_GA_Camera_vs_Reference.png");
            Object.DestroyImmediate(pair);
            // 領域は画像の割合(左下原点)。参照画像の文字・挿絵にかからない場所を選ぶ。
            Region(image,reference,"sky (left of tether)",0.36f,0.72f,0.46f,0.88f);
            Region(image,reference,"tower center",0.45f,0.35f,0.55f,0.65f);
            Region(image,reference,"A1 abutment",0.12f,0.17f,0.22f,0.25f);
            Region(image,reference,"plaza near",0.22f,0.11f,0.40f,0.17f);
            Region(image,reference,"plaza mid",0.30f,0.17f,0.70f,0.21f);
            Object.DestroyImmediate(reference);
        }

        private static void Region(Texture2D image,Texture2D reference,string name,float x0,float y0,float x1,float y1){
            Color a=Mean(image,x0,y0,x1,y1),b=Mean(reference,x0,y0,x1,y1);
            results.Add(string.Format(CultureInfo.InvariantCulture,"region {0}: unity=({1:0},{2:0},{3:0}) reference=({4:0},{5:0},{6:0})",name,a.r*255,a.g*255,a.b*255,b.r*255,b.g*255,b.b*255));
        }

        private static Color Mean(Texture2D image,float x0,float y0,float x1,float y1){
            int ax=(int)(x0*image.width),ay=(int)(y0*image.height),bx=(int)(x1*image.width),by=(int)(y1*image.height);
            double r=0,g=0,b=0;int count=0;
            for(int y=ay;y<by;y++){for(int x=ax;x<bx;x++){Color c=image.GetPixel(x,y);r+=c.r;g+=c.g;b+=c.b;count++;}}
            if(count==0){return Color.black;}
            return new Color((float)(r/count),(float)(g/count),(float)(b/count),1f);
        }

        private static void Difference(Texture2D a,Texture2D b,int y0,int y1,out double mean,out int changed){
            Color32[] pa=a.GetPixels32(),pb=b.GetPixels32();long sum=0;changed=0;int count=0;
            for(int y=y0;y<y1;y++){
                for(int x=0;x<a.width;x++){
                    int i=y*a.width+x;
                    int dr=Math.Abs(pa[i].r-pb[i].r),dg=Math.Abs(pa[i].g-pb[i].g),db=Math.Abs(pa[i].b-pb[i].b);
                    sum+=dr+dg+db;if(dr+dg+db>24){changed++;}count++;
                }
            }
            mean=0;
            if(count>0){mean=(double)sum/(count*3*255);}
        }

        private static Texture2D Copy(Texture2D image){
            var copy=new Texture2D(image.width,image.height,TextureFormat.RGBA32,false,false);
            copy.SetPixels32(image.GetPixels32());copy.Apply();
            return copy;
        }

        private static string NameOf(Object value){
            if(value==null){return "null";}
            return value.name;
        }


        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            Shader.globalRenderPipeline=previousRenderPipeline;
            if(mainTarget!=null){mainTarget.Release();Object.DestroyImmediate(mainTarget);mainTarget=null;}
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error.Message;Debug.LogException(error);}else{text+="PASS";}
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output+"/"+resultFile,text);
            if(Application.isBatchMode){
                int code=1;
                if(error==null){code=0;}
                EditorApplication.Exit(code);
            }
        }
    }
}
