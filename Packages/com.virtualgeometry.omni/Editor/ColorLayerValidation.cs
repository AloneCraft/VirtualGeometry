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
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 色(アルベド)の格子(<see cref="PlanetColorLayer"/>、<see cref="PlanetTerrainSource.ColorLayer"/>)の検証。月(半径 1,737.4 km)に合成の高起伏と、色の texture を載せ、
    /// RadialMoonGrid.shader の _ColorOnly(照明も格子もなしで、色の texture の色を線形のまま出す)で撮った画素を、CPU の双線形補間(<see cref="PlanetColorLayer.SampleDirection"/>)と比べる。
    /// - 地上(高度 2 m、真下を向く)の画像の中央が、真下の方向の色に一致する(経度 0、東経 90°、経度 ±180° の継ぎ目、緯度 ±89.99° の極の近く)
    /// - 月全体の視点(高度 2.5R)の中央と、中央から離れた画素が、光線と球の交点の方向の色に一致する(経度 0 と、継ぎ目 180°)
    /// texture は、合成(滑らかな関数、1024×512)と、LocalData に実データ(LROC のモザイク 4096×2048。git 管理外)があれば、その実データの両方。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.ColorLayerValidation.Run(結果は TestResults/Planet/ColorLayer)
    /// </summary>
    public static class ColorLayerValidation{
        private const int Width=320;
        private const int ImageHeight=240;
        private const int ConvergeLimit=900;
        private const double MoonRadius=RealMoonData.MoonRadius;
        // 8 bit の texture と出力の丸め、GPU の補間の重みの精度(8 bit)による誤差の許容(線形色、0〜1)。
        private const float GroundTolerance=2.5f/255f;
        // 月全体の視点は、地形の高さ(最大 9 km、約 1 画素)による交点の方向のずれが加わる。
        private const float OrbitTolerance=8f/255f;
        // 実データの texture は、月全体の視点で mip(1 画素 7.6 km に対して texel 2.7 km なので約 1.5 段上)で引くので、CPU の mip 0 の補間とは、コントラストの高い所で 10/255 以上違いうる。
        private const float OrbitRealTolerance=30f/255f;
        private const string LrocFile="lroc-global-4096x2048.png";

        private static string results;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static Light sunLight;
        private static PlanetTerrainSource moon;
        private static PlanetRasterLayer heightLayer;
        private static Material colorOnlyMaterial;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            results="TestResults/Planet/ColorLayer";
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
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("ColorLayer validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            heightLayer=RealMoonData.CreateSynthetic();
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var body=new GameObject("Moon");body.SetActive(false);
            body.AddComponent<global::Omni.Logical.Transform>();
            moon=body.AddComponent<PlanetTerrainSource>();
            body.AddComponent<OmniPlanetPlacement>();
            MoonBallSceneBuilder.Configure(moon,(float)MoonRadius,0f,3800f,1,32,4096,64,true);
            HeightShiftOverride.Apply(moon);
            colorOnlyMaterial=new Material(AssetDatabase.LoadAssetAtPath<Material>(MoonBallSceneBuilder.MoonMaterialPath));
            colorOnlyMaterial.SetFloat("_ColorOnly",1);colorOnlyMaterial.SetFloat("_ColorGain",1);
            moon.Material=colorOnlyMaterial;
            moon.SetRasters(heightLayer,null);
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
            Light light=sun.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.3f;light.shadows=LightShadows.None;
            sunLight=light;
            RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.03f,0.03f,0.04f);
            RenderSettings.skybox=null;
        }

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

        // 経度・緯度の地表の上 altitude の高さに camera を置き、真下を向く(pitch 90°)。地表の高さ(m)を返す。
        private static double PlaceCameraDown(double longitudeDegrees,double latitudeDegrees,double altitude){
            double3 d=Direction(longitudeDegrees,latitudeDegrees);
            LogicalVector3 unit=UnitLogical(d);
            if(!LogicalVector3.TryScale(in unit,moon.Selector.LogicalRadius,out LogicalVector3 onSphere)||!moon.TrySampleSurface(in onSphere,out LogicalScalar height,out LogicalScalar _)||!height.TryToDouble(out double groundHeight)){
                throw new InvalidOperationException("地表の高さを求められません。");
            }
            if(!LogicalScalar.TryFromDouble(altitude,out LogicalScalar above)||
                !LogicalScalar.TryAdd(moon.Selector.LogicalRadius,in height,out LogicalScalar a)||!LogicalScalar.TryAdd(in a,in above,out LogicalScalar distance)||
                !LogicalVector3.TryScale(in unit,in distance,out LogicalVector3 position)){
                throw new InvalidOperationException("camera の位置を Logical にできません。");
            }
            double lon=longitudeDegrees*Math.PI/180.0;double lat=latitudeDegrees*Math.PI/180.0;
            Vector3 up=new Vector3((float)d.x,(float)d.y,(float)d.z);
            Vector3 north=new Vector3((float)(-Math.Sin(lon)*Math.Sin(lat)),(float)(-Math.Cos(lon)*Math.Sin(lat)),(float)Math.Cos(lat));
            Quaternion rotation=Quaternion.LookRotation(-up,north);
            sunLight.transform.rotation=Quaternion.LookRotation((-up*0.34f+north*0.94f).normalized,up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){throw new InvalidOperationException("camera の回転を Logical にできません。");}
            cameraLogical.LocalPosition=position;cameraLogical.LocalRotation=logical;cameraLogical.SyncUnityTransform();
            return groundHeight;
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                UnityEngine.Object.DestroyImmediate(M0Validation.Capture(camera,Width,ImageHeight));
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

        // 合成の色の texture(滑らかな関数。sRGB の 8 bit、mip 付き。経度は Repeat、緯度は Clamp)。
        private static Texture2D CreateSyntheticTexture(int width,int height){
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,true,false){name="SyntheticColor",wrapModeU=TextureWrapMode.Repeat,wrapModeV=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear};
            var pixels=new Color32[width*height];
            for(int row=0;row<height;row++){
                // 北が上(行 0 が北)。texture の行は下が 0。
                double lat=(0.5-(row+0.5)/height)*Math.PI;
                for(int column=0;column<width;column++){
                    double lon=((column+0.5)/width-0.5)*2.0*Math.PI;
                    float r=(float)(0.5+0.4*Math.Sin(2.0*lon+0.3)*Math.Cos(lat));
                    float g=(float)(0.5+0.4*Math.Sin(3.0*lat+lon));
                    float b=(float)(0.5+0.4*Math.Cos(3.0*lon));
                    pixels[(height-1-row)*width+column]=new Color32((byte)Mathf.RoundToInt(r*255f),(byte)Mathf.RoundToInt(g*255f),(byte)Mathf.RoundToInt(b*255f),255);
                }
            }
            texture.SetPixels32(pixels);texture.Apply(true,false);
            return texture;
        }

        private static Texture2D LoadLroc(){
            string path=Path.Combine(Path.GetDirectoryName(Application.dataPath),RealMoonData.DataFolder,LrocFile);
            if(!File.Exists(path)){return null;}
            var texture=new Texture2D(2,2,TextureFormat.RGBA32,true,false){name="LrocGlobal",wrapModeU=TextureWrapMode.Repeat,wrapModeV=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear};
            if(!ImageConversion.LoadImage(texture,File.ReadAllBytes(path),false)){return null;}
            texture.wrapModeU=TextureWrapMode.Repeat;texture.wrapModeV=TextureWrapMode.Clamp;
            texture.Apply(true,false);
            return texture;
        }

        private static IEnumerator Steps(){
            int waited=0;
            while(moon.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月が有効になりません。");}
                yield return null;
            }
            var textures=new List<KeyValuePair<string,Texture2D>>();
            textures.Add(new KeyValuePair<string,Texture2D>("synthetic",CreateSyntheticTexture(1024,512)));
            Texture2D lroc=LoadLroc();
            if(lroc!=null){textures.Add(new KeyValuePair<string,Texture2D>("lroc",lroc));}
            else{report.Add("lroc: "+RealMoonData.DataFolder+"/"+LrocFile+" が無いので、実データの色は確認しません。");}

            foreach(KeyValuePair<string,Texture2D> entry in textures){
                var layer=new PlanetColorLayer(entry.Value);
                moon.ColorLayer=layer;
                report.Add("texture "+entry.Key+": "+layer.Width+"x"+layer.Height+" texel="+layer.TexelMeters(MoonRadius).ToString("F0",CultureInfo.InvariantCulture)+"m");

                // 1. 地上(高度 2 m、真下): 画像の中央 5×5 画素の平均が、真下の方向の色に一致する。
                var groundSites=new[]{
                    new[]{0.0,0.0},new[]{90.0,0.0},new[]{45.0,30.0},new[]{180.0,0.0},new[]{-179.9,-20.0},new[]{179.9,60.0},new[]{10.0,89.99},new[]{-120.0,-89.99},new[]{-30.0,-75.0},
                };
                foreach(double[] site in groundSites){
                    string name=entry.Key+" ground lon="+site[0].ToString("F2",CultureInfo.InvariantCulture)+" lat="+site[1].ToString("F2",CultureInfo.InvariantCulture);
                    PlaceCameraDown(site[0],site[1],2.0);
                    foreach(object step in Converge(name)){yield return step;}
                    Texture2D image=M0Validation.Capture(camera,Width,ImageHeight);
                    double3 d=Direction(site[0],site[1]);
                    CompareCenter(name,image,layer.SampleDirection(d.x,d.y,d.z),GroundTolerance);
                    if(site[0]==0.0&&site[1]==0.0||site[1]>89.0||site[0]==180.0){File.WriteAllBytes(results+"/"+entry.Key+"_ground_"+site[0].ToString("F0",CultureInfo.InvariantCulture)+"_"+site[1].ToString("F0",CultureInfo.InvariantCulture)+".png",image.EncodeToPNG());}
                    UnityEngine.Object.DestroyImmediate(image);
                }

                // 2. 月全体の視点(高度 2.5R、真下): 中央と、中央から離れた画素が、光線と球の交点の方向の色に一致する。
                foreach(double[] site in new[]{new[]{0.0,0.0},new[]{180.0,0.0},new[]{-60.0,40.0}}){
                    string name=entry.Key+" orbit lon="+site[0].ToString("F1",CultureInfo.InvariantCulture)+" lat="+site[1].ToString("F1",CultureInfo.InvariantCulture);
                    double altitude=MoonRadius*2.5;
                    double ground=PlaceCameraDown(site[0],site[1],altitude);
                    foreach(object step in Converge(name)){yield return step;}
                    Texture2D image=M0Validation.Capture(camera,Width,ImageHeight);
                    File.WriteAllBytes(results+"/"+entry.Key+"_orbit_"+site[0].ToString("F0",CultureInfo.InvariantCulture)+"_"+site[1].ToString("F0",CultureInfo.InvariantCulture)+".png",image.EncodeToPNG());
                    double3 d=Direction(site[0],site[1]);
                    float orbitTolerance=OrbitTolerance;
                    if(entry.Key=="lroc"){orbitTolerance=OrbitRealTolerance;}
                    CompareCenter(name,image,layer.SampleDirection(d.x,d.y,d.z),orbitTolerance);
                    CompareOffCenter(name,image,layer,site,MoonRadius+ground+altitude,orbitTolerance);
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
            report.Add("pool: used="+moon.UsedSlots+"/"+moon.PoolCapacity+" lastError="+(moon.LastError??"(none)")+" invalidBounds="+moon.InvalidBounds);
            if(moon.LastError!=null){failures.Add("設定エラー: "+moon.LastError);}
            if(moon.InvalidBounds!=0){failures.Add("非有限の頂点を持つ Cell があります("+moon.InvalidBounds+")。");}
        }

        // 画像の中央 5×5 画素の平均(線形色の値 0〜1)。
        private static Vector3 CenterMean(Texture2D image){
            Vector3 sum=Vector3.zero;int count=0;
            for(int y=-2;y<=2;y++){
                for(int x=-2;x<=2;x++){
                    Color32 pixel=image.GetPixel(image.width/2+x,image.height/2+y);
                    sum+=new Vector3(pixel.r,pixel.g,pixel.b)/255f;count++;
                }
            }
            return sum/count;
        }

        private static bool IsBackground(Color32 pixel){
            return pixel.r>200&&pixel.g<60&&pixel.b>200;
        }

        private static void CompareCenter(string name,Texture2D image,Vector3 expected,float tolerance){
            Vector3 actual=CenterMean(image);
            Color32 centerPixel=image.GetPixel(image.width/2,image.height/2);
            if(IsBackground(centerPixel)){failures.Add(name+": 画像の中央が背景です(月が描かれていません)。");return;}
            float error=Mathf.Max(Mathf.Abs(actual.x-expected.x),Mathf.Max(Mathf.Abs(actual.y-expected.y),Mathf.Abs(actual.z-expected.z)));
            report.Add(name+": center rendered=("+actual.x.ToString("F4",CultureInfo.InvariantCulture)+","+actual.y.ToString("F4",CultureInfo.InvariantCulture)+","+actual.z.ToString("F4",CultureInfo.InvariantCulture)+") expected=("+
                expected.x.ToString("F4",CultureInfo.InvariantCulture)+","+expected.y.ToString("F4",CultureInfo.InvariantCulture)+","+expected.z.ToString("F4",CultureInfo.InvariantCulture)+") maxError="+(error*255f).ToString("F2",CultureInfo.InvariantCulture)+"/255");
            if(!(error<=tolerance)){failures.Add(name+": 中央の色が CPU の色と "+(error*255f).ToString("F2",CultureInfo.InvariantCulture)+"/255 違います(許す差 "+(tolerance*255f).ToString("F1",CultureInfo.InvariantCulture)+"/255)。");}
        }

        // 月全体の視点で、中央から離れた画素(水平・垂直に ±1/4、±1/8 画面)の光線と、半径 radius(地表の高さを入れた球)の交点の方向の色を、描かれた色と比べる。
        private static void CompareOffCenter(string name,Texture2D image,PlanetColorLayer layer,double[] site,double cameraDistance,float tolerance){
            double3 up=Direction(site[0],site[1]);
            double3 cameraPosition=up*cameraDistance;
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)image.width/image.height;
            int[][] offsets={new[]{-image.width/4,0},new[]{image.width/4,0},new[]{0,-image.height/4},new[]{0,image.height/4},new[]{image.width/8,image.height/8},new[]{-image.width/8,-image.height/8}};
            float worst=0;int compared=0;
            foreach(int[] offset in offsets){
                int px=image.width/2+offset[0];int py=image.height/2+offset[1];
                Vector3 local=new Vector3(((px+0.5f)/image.width*2-1)*tangent*aspect,((py+0.5f)/image.height*2-1)*tangent,1);
                Vector3 direction=(camera.transform.rotation*local).normalized;
                // 光線と、半径 MoonRadius の球の交点(手前)。
                double3 d=new double3(direction.x,direction.y,direction.z);
                double b=math.dot(cameraPosition,d);
                double c=math.dot(cameraPosition,cameraPosition)-MoonRadius*MoonRadius;
                double discriminant=b*b-c;
                if(discriminant<0){continue;}
                double t=-b-Math.Sqrt(discriminant);
                double3 hit=cameraPosition+d*t;
                Color32 pixel=image.GetPixel(px,py);
                if(IsBackground(pixel)){continue;}
                Vector3 expected=layer.SampleDirection(hit.x,hit.y,hit.z);
                Vector3 actual=new Vector3(pixel.r,pixel.g,pixel.b)/255f;
                float error=Mathf.Max(Mathf.Abs(actual.x-expected.x),Mathf.Max(Mathf.Abs(actual.y-expected.y),Mathf.Abs(actual.z-expected.z)));
                worst=Mathf.Max(worst,error);compared++;
            }
            report.Add(name+": offCenterPixels compared="+compared+" maxError="+(worst*255f).ToString("F2",CultureInfo.InvariantCulture)+"/255");
            if(compared<3){failures.Add(name+": 中央から離れた画素で比べられたのが "+compared+" 個だけです。");}
            if(!(worst<=tolerance)){failures.Add(name+": 中央から離れた画素の色が CPU の色と "+(worst*255f).ToString("F2",CultureInfo.InvariantCulture)+"/255 違います(許す差 "+(tolerance*255f).ToString("F1",CultureInfo.InvariantCulture)+"/255)。");}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
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
