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
using LogicalPosition3=Omni.Logical.Position3;
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
    /// </summary>
    public static class MoonBallValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static MoonBallSettings settings;
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
            settings=MoonBallSettings.Moon;results="TestResults/Planet/MoonBall";
            Start();
        }

        public static void RunGiant(){
            settings=MoonBallSettings.Giant;results="TestResults/Planet/GiantBall";
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

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Capture(320,240);
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
        private static Vector3 Ray(int x,int y,int width,int height){
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            return (camera.transform.rotation*local).normalized;
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
        private static int CountHoles(Texture2D image,double altitude){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            double radius=moon.Radius;
            double ratio=(radius-settings.MoonHeight)/(radius+altitude);
            double dip=Math.Asin(Math.Max(-1.0,Math.Min(1.0,ratio)));
            double pixelAngle=camera.fieldOfView*Mathf.Deg2Rad/height;
            int holes=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    if(Classify(pixels[y*width+x])!=0){continue;}
                    if(NadirAngle(Ray(x,y,width,height))<dip-2*pixelAngle){holes++;}
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

        // ボールと月の、描いている頂点(メッシュ)の隙間。
        // 接点からの相対位置(Logical で引いてから float)で、ボールの下側の頂点と、その真下の月のメッシュの三角形の高さを比べる。
        private static void ContactMeshGap(){
            demo.SetOffset(0,0,0);
            LogicalVector3 contactWorld;
            if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,demo.Contact,out contactWorld)){failures.Add("接点を求められません。");return;}
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
            var triangles=new List<Vector3>();
            float nearest=float.MaxValue;int nearestLevel=-1;
            foreach(PlanetCellKey key in moon.LastSelected){
                float cellSize=moon.Selector.QuadArc(key.Level)*PlanetLayerSource.CellQuads;
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TryAdd(moonLogical.LocalPosition,frame.Origin,out LogicalVector3 originWorld)||!LogicalVector3.TrySubtract(in originWorld,in contactWorld,out LogicalVector3 d)||!d.TryToFloat3(out float3 o)){continue;}
                // Cell の原点は半径 R の球面上の点で、高さは頂点の位置に入っている。接点との距離は、接平面の向き(u、v)だけで測る。
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
            report.Add("contact mesh: moonCells="+moon.LastSelected.Count+" nearestCellOriginLateral="+nearest.ToString("G4",CultureInfo.InvariantCulture)+"m (level "+nearestLevel+")");
            if(ballPoints.Count==0||triangles.Count==0){failures.Add("接触の隙間: ボールまたは月のメッシュを読み戻せません(ボール "+ballPoints.Count+"、月の三角形 "+triangles.Count/3+")。");return;}
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
            }
            report.Add("contact mesh gap: ballVertices="+ballPoints.Count+" moonTriangles="+triangles.Count/3+" lowestVertexGap="+(lowest*1e6f).ToString("G4",CultureInfo.InvariantCulture)+"um minimumGap(64 lowest)="+
                (minimum*1e6f).ToString("G4",CultureInfo.InvariantCulture)+"um (checked "+found+")");
            if(found==0){failures.Add("接触の隙間: ボールの下の月のメッシュが見つかりません。");}
            else if(Mathf.Abs(minimum)>1e-4f||Mathf.Abs(lowest)>1e-3f){failures.Add("接触の隙間が大きすぎます(最小 "+minimum*1e6f+"um、最下の頂点 "+lowest*1e6f+"um)。");}
        }

        // 接点の接平面の座標 (qu, qv) の真上下にある、月のメッシュの高さ(up 方向)。
        private static bool MeshHeightAt(List<Vector3> triangles,Vector3 u,Vector3 v,Vector3 up,float qu,float qv,out float height){
            height=0;
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
                height=w0*Vector3.Dot(a,up)+w1*Vector3.Dot(b,up)+w2*Vector3.Dot(c,up);
                return true;
            }
            return false;
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
