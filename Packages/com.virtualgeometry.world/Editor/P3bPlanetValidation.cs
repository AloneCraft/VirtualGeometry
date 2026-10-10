using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.World.Editor{
    /// <summary>
    /// P3b: 手続き生成の惑星(半径 10km、Hmax 300m、float 座標)を、宇宙から地上まで撮って次を確かめる。
    /// - 穴・継ぎ目 0: 内側の球(半径 R−Hmax)に当たる光線の画素が背景色でない。左右または上下を地形に挟まれた背景の画素が無い
    /// - LOD 遷移 1px 未満: 目標誤差を 1/4 にした細かい描画と比べ、輪郭の差が基準の輪郭から 1px より離れない
    /// - pool の上限: 使用 slot が容量以下で、設定エラーが無い
    /// - 生成予算を超えても穴が無い: 予算 1 で遠くから地上近くへ瞬間移動しても、各 frame で穴が無い
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.World.Editor.P3bPlanetValidation.Run
    /// </summary>
    public static class P3bPlanetValidation{
        private const string Root="Assets/Development/PlanetWorld/P3b";
        private const string Results="TestResults/Planet/P3b";
        private const float Radius=10000;
        private const float Height=300;
        private const float Target=2;
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=600;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static Camera camera;
        private static PlanetTerrainSource planet;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static int maximumUsedSlots;

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();maximumUsedSlots=0;started=EditorApplication.timeSinceStartup;
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
                if(EditorApplication.timeSinceStartup-started>1800){throw new TimeoutException("P3b validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/P3bRenderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.ForwardPlus;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/P3bRenderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry P3b";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            var serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/P3bPipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/P3bPipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            AssetDatabase.SaveAssets();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("P3b Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000;camera.fieldOfView=60;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1,0,1,1);camera.allowHDR=false;camera.allowMSAA=false;
            Light sun=new GameObject("P3b Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(40,-30,0);
            Material material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",new Color(0.35f,0.55f,0.3f,1));
            GameObject holder=new GameObject("P3b Planet");holder.SetActive(false);
            planet=holder.AddComponent<PlanetTerrainSource>();
            var settings=new SerializedObject(planet);
            settings.FindProperty("poolCapacity").intValue=2048;
            settings.FindProperty("maximumDrawSlots").intValue=2048;
            settings.FindProperty("generationBudget").intValue=32;
            settings.ApplyModifiedPropertiesWithoutUndo();
            planet.Material=material;
            planet.Configure(Radius,Height,16,Target);
            holder.SetActive(true);
        }

        private static IEnumerator Steps(){
            report.Add("planet: radius="+Radius+" height="+Height+" target="+Target+"px pool="+planet.PoolCapacity);
            // 固定の4視点。
            var views=new (string Name,float Altitude,float Pitch)[]{("Orbit30km",30000,90),("High5km",5000,35),("Low500m",500,20),("Ground10m",10,8)};
            foreach(var view in views){
                Place(view.Altitude,view.Pitch);
                foreach(object step in Converge(view.Name)){yield return step;}
                Texture2D image=Capture();
                Save(view.Name,image);
                Measure(view.Name,image,true);
                // LOD 遷移: 目標誤差を 1/4 にした細かい描画と輪郭を比べる。
                planet.TargetPixels=Target*0.25f;
                foreach(object step in Converge(view.Name+"Fine")){yield return step;}
                Texture2D fine=Capture();
                Save(view.Name+"Fine",fine);
                CompareSilhouettes(view.Name,image,fine);
                planet.TargetPixels=Target;
                foreach(object step in Converge(view.Name+"Restore")){yield return step;}
            }
            // 宇宙から地上までの経路。毎 frame 穴が無いこと(生成が追いつかない間は親が描かれる)。
            int pathHoles=0;int savedPathHoles=0;
            for(int frame=0;frame<=120;frame++){
                float altitude=Mathf.Exp(Mathf.Lerp(Mathf.Log(30000),Mathf.Log(10),frame/120f));
                Place(altitude,Mathf.Lerp(90,8,frame/120f));
                Texture2D image=Capture(320,240);
                var marked=new Color32[image.width*image.height];
                int holes=CountHoles(image,marked);
                pathHoles+=holes;
                if(holes>0&&savedPathHoles<6){
                    savedPathHoles++;
                    Save("PathHole"+frame,image);
                    var mask=new Texture2D(image.width,image.height,TextureFormat.RGBA32,false);mask.SetPixels32(marked);mask.Apply();
                    Save("PathHoleMask"+frame,mask);
                    UnityEngine.Object.DestroyImmediate(mask);
                    report.Add("path hole frame="+frame+" altitude="+altitude.ToString("0.0",CultureInfo.InvariantCulture)+" holes="+holes+" cells="+planet.LastSelected.Count+" pending="+planet.PendingGeneration+" used="+planet.UsedSlots+" levels="+Levels());
                }
                Track();
                yield return null;
            }
            report.Add("path frames=121 holes="+pathHoles+" usedSlotsMax="+maximumUsedSlots+" evictions="+planet.Evictions+" starved="+planet.StarvedRequests);
            if(pathHoles!=0){failures.Add("経路で穴が "+pathHoles+" 画素ありました。");}
            // 生成予算 1 で、遠くから地上近くへ瞬間移動する。
            planet.GenerationBudget=1;
            Place(30000,90);
            foreach(object step in Converge("BudgetStart")){yield return step;}
            Place(10,8);
            int budgetHoles=0;
            for(int frame=0;frame<60;frame++){
                Texture2D image=Capture(320,240);
                int holes=CountHoles(image);
                budgetHoles+=holes;
                if(holes>0&&frame%10==0){Save("BudgetFrame"+frame,image);}
                Track();
                yield return null;
            }
            report.Add("budget=1 teleport frames=60 holes="+budgetHoles+" pending="+planet.PendingGeneration);
            if(budgetHoles!=0){failures.Add("生成予算 1 の瞬間移動で穴が "+budgetHoles+" 画素ありました。");}
            planet.GenerationBudget=32;
            report.Add("pool: usedSlotsMax="+maximumUsedSlots+" capacity="+planet.PoolCapacity+" lastError="+(planet.LastError??"(none)"));
            if(maximumUsedSlots>planet.PoolCapacity){failures.Add("使用 slot が容量を超えました。");}
            if(planet.LastError!=null){failures.Add("設定エラー: "+planet.LastError);}
        }

        // 撮影と同じ解像度で、生成待ちが無くなり選択が2 frame 続けて変わらなくなるまで描き続ける(解像度で画面誤差が変わるため)。
        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Capture();
                Track();
                int signature=Signature();
                if(planet.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
            report.Add(name+": converged frames="+frames+" cells="+planet.LastSelected.Count+" levels="+Levels()+" used="+planet.UsedSlots);
        }

        private static int Signature(){
            int hash=planet.LastSelected.Count;
            foreach(PlanetCellKey key in planet.LastSelected){hash^=key.GetHashCode();}
            return hash;
        }

        private static string Levels(){
            var counts=new SortedDictionary<int,int>();
            foreach(PlanetCellKey key in planet.LastSelected){
                counts.TryGetValue(key.Level,out int count);counts[key.Level]=count+1;
            }
            var parts=new List<string>();
            foreach(var pair in counts){parts.Add("L"+pair.Key+":"+pair.Value);}
            return string.Join(" ",parts);
        }

        private static void Track(){
            maximumUsedSlots=Mathf.Max(maximumUsedSlots,planet.UsedSlots);
        }

        // 方向 d0 の地表の真上(R+Hmax+altitude)に置き、地平線の方向から pitch 度下を見る。
        private static void Place(float altitude,float pitch){
            Vector3 up=new Vector3(0.3f,1f,0.2f).normalized;
            Vector3 forward=Vector3.ProjectOnPlane(Vector3.forward,up).normalized;
            Vector3 right=Vector3.Cross(up,forward);
            Vector3 look=Quaternion.AngleAxis(pitch,right)*forward;
            camera.transform.SetPositionAndRotation(up*(Radius+Height+altitude),Quaternion.LookRotation(look,up));
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static bool IsBackground(Color32 color){
            return color.r==Background.r&&color.g==Background.g&&color.b==Background.b;
        }

        // 内側の球に当たる光線の画素は、必ず地形を通る。そこが背景色なら穴。
        // 内側の球の半径は R−Hmax から、描いている最も粗い階層の三角形の弦が球面より沈む分(辺の長さ²÷8R)を引く。
        // 辺の長さは quad の弧長の2倍(対角線と等角写像の伸びを見込む)。引かないと、縁で高さが下限の場所を穴と取り違える。
        private static int CountHoles(Texture2D image){
            return CountHoles(image,null);
        }

        // marked が null でなければ、穴の画素を赤、地形を暗い灰色にした画像を書く(穴の場所を確かめるため)。
        private static int CountHoles(Texture2D image,Color32[] marked){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            Vector3 center=planet.transform.position;
            int coarsest=planet.MaximumLevel;
            foreach(PlanetCellKey key in planet.LastSelected){coarsest=Mathf.Min(coarsest,key.Level);}
            float edge=planet.Selector.QuadArc(coarsest)*2f;
            float inner=Radius-Height-edge*edge/(8f*(Radius-Height));
            // 1画素の角度。縁では、地形の輪郭と内側の球の輪郭が画素未満しか離れないので、1画素分内側の光線だけを数える。
            float pixelAngle=camera.fieldOfView*Mathf.Deg2Rad/height;
            int holes=0;
            if(marked!=null){
                for(int i=0;i<pixels.Length;i++){
                    if(IsBackground(pixels[i])){marked[i]=new Color32(255,255,255,255);}else{marked[i]=new Color32(60,60,60,255);}
                }
            }
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    if(!IsBackground(pixels[y*width+x])){continue;}
                    Ray ray=camera.ViewportPointToRay(new Vector3((x+0.5f)/width,(y+0.5f)/height,0));
                    if(HitsSphere(ray,center,inner,camera.nearClipPlane,pixelAngle)){
                        holes++;
                        if(marked!=null){marked[y*width+x]=new Color32(255,0,0,255);}
                    }
                }
            }
            return holes;
        }

        // 光線が球に、縁から1画素分(接線の長さ×1画素の角度)より深く入るか。
        private static bool HitsSphere(Ray ray,Vector3 center,float radius,float near,float pixelAngle){
            Vector3 offset=ray.origin-center;
            float b=Vector3.Dot(offset,ray.direction);
            // (|o|−r)(|o|+r) の形で、カメラが球の近くにあるときの桁落ちを避ける。
            float distance=offset.magnitude;
            float c=(distance-radius)*(distance+radius);
            float discriminant=b*b-c;
            if(discriminant<0){return false;}
            float t=-b-Mathf.Sqrt(discriminant);
            if(t<=near){return false;}
            if(c<=0){return true;}
            float closest=(offset+ray.direction*(-b)).magnitude;
            float tangent=Mathf.Sqrt(c);
            return radius-closest>tangent*pixelAngle;
        }

        private static void Measure(string name,Texture2D image,bool fail){
            int holes=CountHoles(image);
            int cracks=CountThinGaps(image);
            uint[] statistics=planet.ReadbackStatisticsForDiagnostics(camera);
            report.Add(name+": holes="+holes+" thinGaps="+cracks+" cull(considered,frustum,hzb,visible)="+string.Join(",",statistics));
            if(fail&&holes!=0){failures.Add(name+": 穴が "+holes+" 画素ありました。");}
            if(fail&&cracks!=0){failures.Add(name+": 地形に挟まれた細い背景(継ぎ目の候補)が "+cracks+" 画素ありました。");}
        }

        // 左右または上下を地形に挟まれた、幅1〜2画素の背景。
        private static int CountThinGaps(Texture2D image){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            int count=0;
            for(int y=2;y<height-2;y++){
                for(int x=2;x<width-2;x++){
                    if(!IsBackground(pixels[y*width+x])){continue;}
                    bool horizontal=Terrain(pixels,width,x-1,y)&&(Terrain(pixels,width,x+1,y)||Terrain(pixels,width,x+2,y));
                    bool vertical=Terrain(pixels,width,x,y-1)&&(Terrain(pixels,width,x,y+1)||Terrain(pixels,width,x,y+2));
                    if(horizontal||vertical){count++;}
                }
            }
            return count;
        }

        private static bool Terrain(Color32[] pixels,int width,int x,int y){
            return !IsBackground(pixels[y*width+x]);
        }

        // 基準(細かい描画)の輪郭から1画素の帯に入らない輪郭の差を数える。
        private static void CompareSilhouettes(string name,Texture2D image,Texture2D reference){
            Color32[] a=image.GetPixels32();Color32[] b=reference.GetPixels32();
            int width=image.width;int height=image.height;
            var mask=new bool[a.Length];
            for(int i=0;i<b.Length;i++){mask[i]=!IsBackground(b[i]);}
            var band=new bool[a.Length];
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
            int different=0;int outside=0;
            var difference=new Color32[a.Length];
            for(int i=0;i<a.Length;i++){
                bool terrain=!IsBackground(a[i]);
                if(terrain==mask[i]){difference[i]=new Color32(0,0,0,255);continue;}
                different++;
                if(band[i]){difference[i]=new Color32(255,255,0,255);continue;}
                outside++;difference[i]=new Color32(255,0,0,255);
            }
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,false);texture.SetPixels32(difference);texture.Apply();
            Save(name+"SilhouetteDifference",texture);
            UnityEngine.Object.DestroyImmediate(texture);
            report.Add(name+": silhouette differing="+different+" outside1px="+outside);
            if(outside!=0){failures.Add(name+": LOD 遷移で、輪郭が細かい基準から1画素より離れた画素が "+outside+" ありました。");}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
