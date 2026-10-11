using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalVector3=Omni.Logical.Vector3;
using Debug=UnityEngine.Debug;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 4 つの天体(巨大な天体 10^20 m、月 1,737.4 km、球 1 m、ボール 0.11 m)を積んだデモ(<see cref="BodyStackDemo"/>)の検証。
    /// - 接点 1・2・3 ごとに、描いている頂点(メッシュ)の隙間と、1mm ずつ離したときの隙間の変化
    /// - 画面に描かれた天体が、Logical の位置から倍精度の光線で求めた球・平面と 1 画素の帯の内で一致すること(接点 2・3 は小さい天体の位置、接点 1 と全体は全天体)
    /// - 距離を変えたときの一致(深度の分解能の目安)、描画の時間
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.BodyStackValidation.Run
    /// </summary>
    public static class BodyStackValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private const int Count=BodyStackDemo.BodyCount;
        // 接点の近くの Cell だけを隙間の計算に使う(粗い Cell は float の桁が足りず、位置がずれる)。
        private const float NearCellLimit=5f;

        private const int Background=0;
        private const int Ball=1;
        private const int Sphere=2;
        private const int Moon=3;
        private const int Ground=4;
        private const int Other=5;

        private static string results;
        private static bool timingOnly;
        private static bool cellsOnly;
        // 半径 1.8e19 m(float の二乗が 3.4e38 を超える)の前後を含める。
        private static readonly double[] CellRadii={1.0,1e3,1e6,1e9,1e12,1e15,1e18,1e19,3e19,6e19,1e20};
        private static BodyStackObjects objects;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static BodyStackDemo demo;
        private static PlanetTerrainSource[] sources;
        private static Material[] flat;
        private static Material[] shaded;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            timingOnly=false;cellsOnly=false;
            Begin("TestResults/Planet/BodyStack");
        }

        /// <summary>描画の時間だけを測る(結果は TestResults/Planet/BodyStackTiming)。</summary>
        public static void RunTiming(){
            timingOnly=true;cellsOnly=false;
            Begin("TestResults/Planet/BodyStackTiming");
        }

        /// <summary>地上に立ったときの Cell 数の表だけを作る(結果は TestResults/Planet/BodyStackCells)。</summary>
        public static void RunCells(){
            timingOnly=false;cellsOnly=true;
            Begin("TestResults/Planet/BodyStackCells");
        }

        private static void Begin(string output){
            results=output;
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
                if(EditorApplication.timeSinceStartup-started>3400){throw new TimeoutException("BodyStack validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            objects=BodyStackSceneBuilder.Populate(BodyStackSettings.Default);
            HeightShiftOverride.ApplyToScene();
            camera=objects.Camera;camera.backgroundColor=new Color(1,0,1,1);
            cameraLogical=objects.CameraObject.GetComponent<global::Omni.Logical.Transform>();
            demo=objects.Demo;sources=objects.Sources;
            // 単色(照明・模様なし)の material: 巨大な天体は青、月は灰色、球は緑、ボールは赤。見た目の material は別に持つ。
            Color[] tints={new Color(0.1f,0.1f,1f,1),new Color(0.5f,0.5f,0.5f,1),new Color(0.1f,1f,0.1f,1),new Color(1f,0.1f,0.1f,1)};
            flat=new Material[Count];shaded=new Material[Count];
            for(int i=0;i<Count;i++){
                shaded[i]=new Material(objects.Materials[i]);
                flat[i]=new Material(objects.Materials[i]);
                flat[i].SetFloat("_FlatColor",1);flat[i].SetColor("_FlatTint",tints[i]);
            }
            // 検証の間は Update を回さない(操作の入力を読まない)。
            demo.enabled=false;
            SetFlat(true);
        }

        private static void SetFlat(bool value){
            for(int i=0;i<Count;i++){
                if(value){sources[i].Material=flat[i];}else{sources[i].Material=shaded[i];}
            }
            // 格子の軸は Initialize が material に書くので、見た目の material には書き直す。
            if(demo.IsInitialized){
                Vector3 u=demo.AxisU;Vector3 v=demo.AxisV;
                for(int i=0;i<2;i++){
                    shaded[i].SetVector("_GridAxisU",new Vector4(u.x,u.y,u.z,0));shaded[i].SetVector("_GridAxisV",new Vector4(v.x,v.y,v.z,0));
                    flat[i].SetVector("_GridAxisU",new Vector4(u.x,u.y,u.z,0));flat[i].SetVector("_GridAxisV",new Vector4(v.x,v.y,v.z,0));
                }
            }
        }

        private static IEnumerator Steps(){
            report.Add("bodies: radius ground="+sources[0].Radius.ToString("G6",CultureInfo.InvariantCulture)+" moon="+sources[1].Radius.ToString("G6",CultureInfo.InvariantCulture)+
                " sphere="+sources[2].Radius.ToString("G6",CultureInfo.InvariantCulture)+" ball="+sources[3].Radius.ToString("G6",CultureInfo.InvariantCulture)+" camera near logical="+0.3f/10f+"m");
            int waited=0;
            while(true){
                bool ready=true;
                for(int i=0;i<Count;i++){if(sources[i].Selector==null){ready=false;}}
                if(ready){break;}
                if(waited++>100){throw new InvalidOperationException("天体が有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            SetFlat(true);
            report.Add("frame: up="+demo.Up.ToString("F6")+" topHeight(m) ground="+demo.TopHeight(0).ToString("G6",CultureInfo.InvariantCulture)+" moon="+demo.TopHeight(1).ToString("G6",CultureInfo.InvariantCulture)+
                " moonBottom="+demo.BottomHeight(1).ToString("G6",CultureInfo.InvariantCulture));

            if(timingOnly){
                foreach(object step in Timing()){yield return step;}
                yield break;
            }
            if(cellsOnly){
                foreach(object step in CellTable(CellRadii)){yield return step;}
                yield break;
            }

            // 接点 3(球とボール)、2(月と球)、1(巨大な天体と月)。
            for(int contact=3;contact>=1;contact--){
                demo.ShowContactView(contact);
                foreach(object step in Converge("Contact"+contact)){yield return step;}
                Texture2D image=Capture();
                Save("Contact"+contact+"Flat",image);
                if(contact>=2){CompareView("Contact"+contact+"Flat",image,contact);}else{ReportCounts("Contact1Flat",image);}
                Destroy(image);
                if(contact==1){
                    // 診断: 月を外すと、画面の月の画素(細い線)は無くなるか(月の中から見た月の skirt か)。
                    sources[1].enabled=false;
                    for(int i=0;i<20;i++){Destroy(Capture(320,240));yield return null;}
                    Texture2D without=Capture();
                    ReportCounts("Contact1Flat without moon",without);
                    Destroy(without);
                    sources[1].enabled=true;
                    foreach(object step in Converge("Contact1 moon restored")){yield return step;}
                }
                SetFlat(false);
                foreach(object step in Converge("Contact"+contact+"Shaded")){yield return step;}
                image=Capture();Save("Contact"+contact+"Shaded",image);Destroy(image);
                SetFlat(true);

                // 隙間を 1mm ずつ離す(接点の近くの視点で、メッシュの隙間と、画像の一致)。
                if(contact>=2){
                    int[] gapSteps={1,2,3,12};
                    foreach(int k in gapSteps){
                        demo.SetOffset(contact,0,10*k,0);
                        foreach(object step in Converge("Contact"+contact+"Gap"+k)){yield return step;}
                        image=Capture();
                        if(k==1||k==12){Save("Contact"+contact+"Gap"+k+"mm",image);}
                        CompareView("Contact"+contact+"Gap"+k+"mm",image,contact);
                        Destroy(image);
                    }
                    int[] rollSteps={1,8};
                    foreach(int k in rollSteps){
                        demo.SetOffset(contact,10*k,0,0);
                        foreach(object step in Converge("Contact"+contact+"Roll"+k)){yield return step;}
                        image=Capture();
                        CompareView("Contact"+contact+"Roll"+k+"mm",image,contact);
                        Destroy(image);
                        demo.SetOffset(contact,0,0,10*k);
                        foreach(object step in Converge("Contact"+contact+"Depth"+k)){yield return step;}
                        image=Capture();
                        CompareView("Contact"+contact+"Depth"+k+"mm",image,contact);
                        Destroy(image);
                    }
                }
                demo.ResetOffset(contact);
                demo.SetOffset(contact,0,0,0);

                // 接触の隙間(メッシュ)。接点が見える近接の視点で、0〜3mm を測る。
                PlaceNearContact(contact);
                foreach(object step in Converge("Near"+contact)){yield return step;}
                float gap0=0;
                for(int k=0;k<=3;k++){
                    demo.SetOffset(contact,0,10*k,0);
                    foreach(object step in Converge("Near"+contact+"Gap"+k)){yield return step;}
                    if(!MeshGap(contact,out float atContact,out float lowest,out string detail)){failures.Add("接点 "+contact+" の隙間 "+k+"mm: メッシュを求められません。"+detail);continue;}
                    if(k==0){gap0=atContact;}
                    float change=atContact-gap0;
                    report.Add("contact "+contact+" mesh gap at +"+k+"mm: atContact="+(atContact*1e6f).ToString("G5",CultureInfo.InvariantCulture)+"um change from 0="+(change*1e3f).ToString("G5",CultureInfo.InvariantCulture)+"mm ("+detail+")");
                    if(k==0&&Mathf.Abs(atContact)>1e-4f){failures.Add("接点 "+contact+": 隙間 0 のときの接点の位置の隙間が "+atContact*1e6f+"um(100um 以内であること)。");}
                    if(Mathf.Abs(change-k*1e-3f)>1e-4f){failures.Add("接点 "+contact+": "+k+"mm 離したときの隙間の変化が "+change*1e3f+"mm(誤差 0.1mm 以内であること)。");}
                }
                demo.SetOffset(contact,0,0,0);
                foreach(object step in Converge("Reset"+contact)){yield return step;}
            }

            // 全体
            demo.ShowOverview();
            foreach(object step in Converge("Overview")){yield return step;}
            {
                Texture2D image=Capture();
                Save("OverviewFlat",image);
                CompareView("OverviewFlat",image,0);
                Destroy(image);
                SetFlat(false);
                foreach(object step in Converge("OverviewShaded")){yield return step;}
                image=Capture();Save("OverviewShaded",image);Destroy(image);
                SetFlat(true);
            }

            // 距離を変える(深度の分解能の目安): 球とボールを、遠ざかりながら見る。画像の一致が崩れ始める距離を記録する。
            foreach(object step in DistanceSweep(2,new[]{3.5,10,30,100,300})){yield return step;}
            foreach(object step in DistanceSweep(3,new[]{0.28,1,3,10,30})){yield return step;}

            // Logical Near(Camera の Near ÷ View Scale M)を小さくしていく。
            foreach(object step in ViewScaleSweep(new[]{10f,100f,300f,1000f,3000f})){yield return step;}

            // 描画の時間
            foreach(object step in Timing()){yield return step;}

            // 地上に立ったときの Cell 数(天体の半径ごと)
            foreach(object step in CellTable(CellRadii)){yield return step;}

            for(int i=0;i<Count;i++){
                report.Add("pool body "+i+": used="+sources[i].UsedSlots+"/"+sources[i].PoolCapacity+" selected="+sources[i].LastSelected.Count+" pending="+sources[i].PendingGeneration+" evictions="+sources[i].Evictions+
                    " starved="+sources[i].StarvedRequests+" lastError="+(sources[i].LastError??"(none)"));
                if(sources[i].LastError!=null){failures.Add("設定エラー(天体 "+i+"): "+sources[i].LastError);}
            }
        }

        // 選択は撮影と同じ解像度で収束させる(別の解像度だと、画面誤差が変わり、撮影の frame で新しい Cell の生成が始まる)。
        private static IEnumerable<object> Converge(string name,int width=320,int height=240){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Destroy(Capture(width,height));
                int signature=0;
                int pending=0;
                for(int i=0;i<Count;i++){
                    signature=signature*31+sources[i].LastSelected.Count;
                    foreach(PlanetCellKey key in sources[i].LastSelected){signature^=key.GetHashCode()*(i+1);}
                    pending+=sources[i].PendingGeneration;
                }
                if(pending==0&&signature==previous){stable++;}else{stable=0;}
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

        private static void Destroy(Texture2D image){
            if(image!=null){UnityEngine.Object.DestroyImmediate(image);}
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        // 0: 背景(マゼンタ)、1: ボール(赤)、2: 球(緑)、3: 月(灰色。単色の出力は 0.5 が 56 ほどになる)、4: 巨大な天体(青)、5: それ以外。
        private static int Classify(Color32 pixel){
            if(pixel.r>200&&pixel.g<60&&pixel.b>200){return Background;}
            if(pixel.r>200&&pixel.g<90&&pixel.b<90){return Ball;}
            if(pixel.g>200&&pixel.r<90&&pixel.b<90){return Sphere;}
            if(pixel.b>200&&pixel.r<90&&pixel.g<90){return Ground;}
            if(Mathf.Abs(pixel.r-pixel.g)<12&&Mathf.Abs(pixel.g-pixel.b)<12&&pixel.r>20&&pixel.r<200){return Moon;}
            return Other;
        }

        // 画素ごとの光線(camera の向き、画角)。
        private static double3 Ray(int x,int y,int width,int height){
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            Vector3 direction=(camera.transform.rotation*local).normalized;
            return new double3(direction.x,direction.y,direction.z);
        }

        // world の Logical の点の、camera からの差(Logical で引いてから倍精度)。
        private static bool Relative(in LogicalVector3 world,out double3 value){
            value=default;
            if(!LogicalVector3.TrySubtract(in world,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.X.TryToDouble(out double x)||
                !difference.Y.TryToDouble(out double y)||!difference.Z.TryToDouble(out double z)){return false;}
            value=new double3(x,y,z);
            return true;
        }

        private struct AnalyticBody{
            public double3 Center;
            public double Radius;
            public int Class;
        }

        // 光線が最初に当たるものの種類。球は外側から入る向きだけ(裏面は描かない)。平面(巨大な天体)は、接点 1 を通る up を法線とする面。
        private static int AnalyticClass(double3 direction,AnalyticBody[] spheres,bool plane,double3 planePoint,double3 planeNormal){
            double best=double.MaxValue;int result=Background;
            for(int i=0;i<spheres.Length;i++){
                double3 center=spheres[i].Center;
                double b=math.dot(direction,center);
                double c=math.dot(center,center)-spheres[i].Radius*spheres[i].Radius;
                double discriminant=b*b-c;
                if(c>0&&b>0&&discriminant>=0){
                    double t=b-math.sqrt(discriminant);
                    if(t<best){best=t;result=spheres[i].Class;}
                }
            }
            if(plane){
                double denominator=math.dot(direction,planeNormal);
                double numerator=math.dot(planePoint,planeNormal);
                if(denominator<0&&numerator<0){
                    double t=numerator/denominator;
                    if(t<best){best=t;result=Ground;}
                }
            }
            return result;
        }

        // 見ている接点(contact = 1..3)または全体(0)の、描かれた画像と解析の比較。
        // 接点 2・3 は小さい天体(ボール、球)だけ、接点 1 と全体は全ての天体と平面を比べる。解析の領域の境から 1 画素の帯の外で違う画素を数える。
        private static void CompareView(string name,Texture2D image,int contact){
            var spheres=new List<AnalyticBody>();
            bool plane=false;double3 planePoint=default;double3 planeNormal=new double3(demo.Up.x,demo.Up.y,demo.Up.z);
            bool[] compare=new bool[6];
            int[] classes={Ball,Sphere,Moon};
            for(int i=3;i>=1;i--){
                if(!Relative(demo.Center(i),out double3 center)){failures.Add(name+": 天体 "+i+" の位置を求められません。");return;}
                spheres.Add(new AnalyticBody{Center=center,Radius=sources[i].Radius,Class=classes[3-i]});
            }
            if(contact==2||contact==3){
                compare[Ball]=true;compare[Sphere]=true;
            }else{
                compare[Background]=true;compare[Moon]=true;compare[Ground]=true;
                if(!Relative(demo.Contact(1),out planePoint)){failures.Add(name+": 接点 1 を求められません。");return;}
                plane=true;
            }
            AnalyticBody[] bodies=spheres.ToArray();
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            var analytic=new int[pixels.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    analytic[y*width+x]=AnalyticClass(Ray(x,y,width,height),bodies,plane,planePoint,planeNormal);
                }
            }
            var counts=new int[6];var analyticCounts=new int[6];
            int anomaly=0;
            var mismatch=new int[6];
            var rendered=new int[pixels.Length];
            for(int i=0;i<pixels.Length;i++){
                rendered[i]=Classify(pixels[i]);
                counts[rendered[i]]++;analyticCounts[analytic[i]]++;
                if(rendered[i]==Other){anomaly++;}
            }
            for(int cls=0;cls<6;cls++){
                if(!compare[cls]){continue;}
                var mask=new bool[pixels.Length];
                for(int i=0;i<pixels.Length;i++){mask[i]=analytic[i]==cls;}
                bool[] band=Band(mask,width,height);
                for(int i=0;i<pixels.Length;i++){
                    bool isClass=rendered[i]==cls;
                    if(isClass!=mask[i]&&!band[i]){mismatch[cls]++;}
                }
            }
            string text=name+": rendered bg/ball/sphere/moon/ground/other="+counts[0]+"/"+counts[1]+"/"+counts[2]+"/"+counts[3]+"/"+counts[4]+"/"+counts[5]+
                " analytic="+analyticCounts[0]+"/"+analyticCounts[1]+"/"+analyticCounts[2]+"/"+analyticCounts[3]+"/"+analyticCounts[4]+
                " mismatchOutside1px="+mismatch[0]+"/"+mismatch[1]+"/"+mismatch[2]+"/"+mismatch[3]+"/"+mismatch[4]+" anomaly="+anomaly;
            report.Add(text);
            for(int cls=0;cls<6;cls++){
                if(mismatch[cls]!=0){failures.Add(name+": 種類 "+cls+" の画素が、解析と 1 画素の帯の外で "+mismatch[cls]+" 違います。");}
            }
            if(anomaly!=0){failures.Add(name+": どの種類でもない画素が "+anomaly+" ありました。");}
            if((contact==2||contact==3)&&analyticCounts[contact==3?Ball:Sphere]==0){failures.Add(name+": 解析上、上の天体が画面にありません。");}
        }

        // 接点 1 の視点は、巨大な天体の面の上 0.17m(月の中)。月は内側から見るので描かれない(裏面)。画素の種類の数だけ出す。
        private static void ReportCounts(string name,Texture2D image){
            var counts=new int[6];
            foreach(Color32 pixel in image.GetPixels32()){counts[Classify(pixel)]++;}
            report.Add(name+": rendered bg/ball/sphere/moon/ground/other="+counts[0]+"/"+counts[1]+"/"+counts[2]+"/"+counts[3]+"/"+counts[4]+"/"+counts[5]+" (camera is inside the moon)");
            if(counts[Other]!=0){failures.Add(name+": どの種類でもない画素が "+counts[Other]+" ありました。");}
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

        // 接点が見える近接の視点(真横の視点では、上の天体の最下の Cell が地平線の向こうとして選ばれないことがある)。
        private static void PlaceNearContact(int contact){
            if(contact==3){demo.PlaceCamera(3,0.01,0.25,true);}
            else if(contact==2){demo.PlaceCamera(2,0.02,0.5,true);}
            else{demo.PlaceCamera(1,0.05,0.2,true);}
        }

        // 上の天体(contact)と下の天体(contact-1)の、描いている頂点(メッシュ)の隙間。
        // 接点からの相対位置(Logical で引いてから float)で、接点の真上下(接平面の座標 (0, 0))での、上の天体のメッシュの下面と下の天体のメッシュの上面の高さの差を求める
        // (起伏のある月が上のとき、最下の頂点は接点とは別の所にあるので、接点の位置で比べる)。
        // 滑らかな球が上のときは、下側 64 頂点の隙間(最下と最小)も出す。
        private static bool MeshGap(int contact,out float atContact,out float lowest,out string detail){
            atContact=0;lowest=0;detail="";
            PlanetTerrainSource upper=sources[contact];PlanetTerrainSource lower=sources[contact-1];
            LogicalVector3 contactWorld=demo.Contact(contact);
            Vector3 up=demo.Up;Vector3 u=demo.AxisU;Vector3 v=demo.AxisV;
            int side=PlanetLayerSource.CellQuads+1;
            int grid=side*side;
            int nonFinite=0;
            var upperPoints=new List<Vector3>();
            var upperTriangles=new List<Vector3>();
            int upperCells=ReadCells(upper,contactWorld,u,v,side,grid,upperPoints,upperTriangles,ref nonFinite);
            var lowerTriangles=new List<Vector3>();
            int lowerCells=ReadCells(lower,contactWorld,u,v,side,grid,null,lowerTriangles,ref nonFinite);
            detail="upperCells="+upperCells+" lowerCells="+lowerCells+" upperTriangles="+upperTriangles.Count/3+" lowerTriangles="+lowerTriangles.Count/3+" nonFinite="+nonFinite;
            if(nonFinite!=0){failures.Add("接点 "+contact+": 読み戻した頂点に inf または NaN が "+nonFinite+" 個ありました。");}
            if(!MeshHeightAt(upperTriangles,u,v,up,0,0,out float upperHeight)||!MeshHeightAt(lowerTriangles,u,v,up,0,0,out float lowerHeight)){return false;}
            atContact=upperHeight-lowerHeight;
            // 上の天体の頂点のうち、up 方向に最も低い 64 個について、真下の下の天体のメッシュの高さとの差(滑らかな球で意味がある)。
            upperPoints.Sort((a,b)=>Vector3.Dot(a,up).CompareTo(Vector3.Dot(b,up)));
            float minimum=float.MaxValue;int found=0;
            for(int i=0;i<Mathf.Min(64,upperPoints.Count);i++){
                Vector3 p=upperPoints[i];
                if(!MeshHeightAt(lowerTriangles,u,v,up,Vector3.Dot(p,u),Vector3.Dot(p,v),out float height)){continue;}
                float gap=Vector3.Dot(p,up)-height;
                if(found==0){lowest=gap;}
                found++;
                if(gap<minimum){minimum=gap;}
            }
            detail+=" lowestVertexGap="+(lowest*1e6f).ToString("G5",CultureInfo.InvariantCulture)+"um minimumOf64="+(minimum*1e6f).ToString("G5",CultureInfo.InvariantCulture)+"um checked="+found;
            return true;
        }

        // 接点の近くの Cell を読み戻し、三角形(接点からの相対位置)と、必要なら頂点を集める。読み戻した Cell の数を返す。
        private static int ReadCells(PlanetTerrainSource source,LogicalVector3 contactWorld,Vector3 u,Vector3 v,int side,int grid,List<Vector3> points,List<Vector3> triangles,ref int nonFinite){
            int cells=0;
            global::Omni.Logical.Transform transform=source.GetComponent<global::Omni.Logical.Transform>();
            foreach(PlanetCellKey key in source.LastSelected){
                float cellSize=source.Selector.QuadArc(key.Level)*PlanetLayerSource.CellQuads;
                if(cellSize>NearCellLimit){continue;}
                if(!source.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frame)){continue;}
                if(!LogicalVector3.TryAdd(transform.LocalPosition,frame.Origin,out LogicalVector3 originWorld)||
                    !LogicalVector3.TrySubtract(in originWorld,in contactWorld,out LogicalVector3 d)||!d.TryToFloat3(out float3 o)){continue;}
                // Cell の原点は地表の高さまで持ち上げた点(持ち上げない Cell は半径 R の球面上の点)で、起伏は頂点の位置に入っている。接点との距離は、接平面の向き(u、v)だけで測る。
                float distance=new Vector2(Vector3.Dot(o,u),Vector3.Dot(o,v)).magnitude;
                if(distance>0.3f+cellSize){continue;}
                cells++;
                if(points!=null){
                    for(int i=0;i<grid;i++){
                        Vector3 p=(Vector3)o+vertices[i].Position;
                        if(!IsFinite(p)){nonFinite++;continue;}
                        points.Add(p);
                    }
                }
                for(int y=0;y<side-1;y++){
                    for(int x=0;x<side-1;x++){
                        Vector3 a=(Vector3)o+vertices[y*side+x].Position;Vector3 b=(Vector3)o+vertices[y*side+x+1].Position;
                        Vector3 c=(Vector3)o+vertices[(y+1)*side+x].Position;Vector3 e=(Vector3)o+vertices[(y+1)*side+x+1].Position;
                        if(!IsFinite(a)||!IsFinite(b)||!IsFinite(c)||!IsFinite(e)){nonFinite++;continue;}
                        triangles.Add(a);triangles.Add(c);triangles.Add(b);
                        triangles.Add(b);triangles.Add(c);triangles.Add(e);
                    }
                }
            }
            return cells;
        }

        private static bool IsFinite(Vector3 p){
            return !(float.IsNaN(p.x)||float.IsInfinity(p.x)||float.IsNaN(p.y)||float.IsInfinity(p.y)||float.IsNaN(p.z)||float.IsInfinity(p.z));
        }

        // 接点の接平面の座標 (qu, qv) の真上下にある、メッシュの高さ(up 方向)。
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

        // 接点 contact の上の天体を、下の天体の面に沿って遠ざかりながら見る(隙間 0)。距離ごとに解析との一致を数える。
        private static IEnumerable<object> DistanceSweep(int contact,double[] distances){
            demo.SetOffset(contact,0,0,0);
            double lift=0.5;
            if(contact==3){lift=0.17;}
            foreach(double distance in distances){
                if(contact==2){
                    // 月の起伏は 100m で数十 cm 動くので、camera は月の地表から lift の高さに置く(接点の接平面から lift では、地面の中に入りうる)。
                    if(!PlaceCameraAboveMoon(contact,distance,lift)){failures.Add("距離 "+distance+"m: camera を月の地表の上に置けません。");}
                }else{
                    demo.PlaceCamera(contact,lift,distance);
                }
                foreach(object step in Converge("Distance"+contact+"_"+distance)){yield return step;}
                Texture2D image=Capture();
                CompareView("Distance contact "+contact+" at "+distance.ToString("G4",CultureInfo.InvariantCulture)+"m",image,contact);
                if(distance==distances[distances.Length-1]){Save("Distance"+contact+"Far",image);}
                Destroy(image);
            }
        }

        // View Scale M(論理 near = Camera の Near ÷ M)を変えて、ボールを近くで見る(接点 3 の視点)。解析との一致が崩れる M を記録する。
        private static IEnumerable<object> ViewScaleSweep(float[] scales){
            global::Omni.RadialCamera radial=objects.CameraObject.GetComponent<global::Omni.RadialCamera>();
            demo.SetOffset(3,0,0,0);
            foreach(float scale in scales){
                radial.SetViewScale(scale);
                demo.ShowContactView(3);
                foreach(object step in Converge("Scale"+scale)){yield return step;}
                Texture2D image=Capture();
                CompareView("ViewScale M="+scale.ToString("G6",CultureInfo.InvariantCulture)+" (logical near "+(camera.nearClipPlane/scale*1000f).ToString("G4",CultureInfo.InvariantCulture)+"mm)",image,3);
                if(scale==scales[scales.Length-1]){Save("ViewScaleLast",image);}
                Destroy(image);
            }
            radial.SetViewScale(10f);
        }

        // 巨大な天体を半径 R に作り直し、地表から 1.7m の目の高さ(水平から 10 度下)で立ったときに描く Cell の数と、描かれた画像を調べる。他の天体は外す。
        // 半径 10^19 m 以上でも、地面が欠けないこと(背景・地面以外の画素が無く、地面の画素数が半径 10^9 m と同じ)、隣り合う Cell の共有辺が 1mm 以内で合うこと。
        private static IEnumerable<object> CellTable(double[] radii){
            for(int i=1;i<Count;i++){sources[i].enabled=false;}
            PlanetTerrainSource ground=sources[0];
            if(!LogicalVector3.TryFromFloat3(new float3(0.3f,1f,0.2f),out LogicalVector3 rough)||!LogicalVector3.TryNormalize(rough,out LogicalVector3 upLogical)){failures.Add("向きを Logical にできません。");yield break;}
            Vector3 up=demo.Up;Vector3 v=demo.AxisV;
            int referenceGround=-1;
            foreach(double radius in radii){
                ground.Configure((float)radius,0f,90,2f);
                int waited=0;
                while(ground.Selector==null){if(waited++>100){failures.Add("半径 "+radius+": 天体が有効になりません。");yield break;}yield return null;}
                // 高さは、天体の半径(float の値)の Logical に 1.7m を足す(double の radius+1.7 では、半径が 10^20 m のとき float の半径と合わず、地下になる)。
                if(!global::Omni.Logical.Scalar.TryFromDouble(1.7,out global::Omni.Logical.Scalar eyeHeight)||!global::Omni.Logical.Scalar.TryAdd(ground.Selector.LogicalRadius,in eyeHeight,out global::Omni.Logical.Scalar eye)||
                    !LogicalVector3.TryScale(in upLogical,in eye,out LogicalVector3 position)){failures.Add("半径 "+radius+": camera の位置を Logical にできません。");continue;}
                cameraLogical.LocalPosition=position;
                // 水平から 10 度下。半径が小さく、水平線が大きく下がるときは、水平線の下がりから 15 度上を向く(球が画面に入るように)。
                double dip=Math.Acos(radius/(radius+1.7))*180.0/Math.PI;
                float pitch=(float)Math.Max(10.0,dip-15.0)*Mathf.Deg2Rad;
                Vector3 forward=(-v*Mathf.Cos(pitch)-up*Mathf.Sin(pitch)).normalized;
                Quaternion rotation=Quaternion.LookRotation(forward,up);
                if(global::Omni.Logical.Quaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out global::Omni.Logical.Quaternion logical)){cameraLogical.LocalRotation=logical;}
                cameraLogical.SyncUnityTransform();
                foreach(object step in Converge("CellTable"+radius,Width,ImageHeight)){yield return step;}
                Texture2D image=Capture();
                var counts=new int[6];
                foreach(Color32 pixel in image.GetPixels32()){counts[Classify(pixel)]++;}
                Save("Standing"+radius.ToString("G3",CultureInfo.InvariantCulture),image);
                Destroy(image);
                var levels=new SortedDictionary<int,int>();
                foreach(PlanetCellKey key in ground.LastSelected){levels.TryGetValue(key.Level,out int n);levels[key.Level]=n+1;}
                string levelText="";
                foreach(KeyValuePair<int,int> pair in levels){levelText+=pair.Key+":"+pair.Value+" ";}
                report.Add("cells standing on radius "+radius.ToString("G3",CultureInfo.InvariantCulture)+"m (eye 1.7m, quad<=2px): selected="+ground.LastSelected.Count+" levels="+levels.Count+" used="+ground.UsedSlots+" bg/ground/other="+counts[0]+"/"+counts[4]+"/"+counts[5]+" notReady="+ground.LastNotReadyCells+" describeFailed="+ground.LastDescribeFailedCells+" levelCounts: "+levelText);
                if(counts[Other]!=0){failures.Add("半径 "+radius+": どの種類でもない画素が "+counts[Other]+" ありました。");}
                if(ground.LastError!=null){failures.Add("半径 "+radius+": "+ground.LastError);}
                if(ground.LastNotReadyCells!=0||ground.LastDescribeFailedCells!=0){failures.Add("半径 "+radius+": 選んだ Cell のうち描けなかったものがあります(未生成 "+ground.LastNotReadyCells+"、配置の失敗 "+ground.LastDescribeFailedCells+")。");}
                // 半径 10^9 m 以上は、地面の画素数が同じになる(水平線の位置は半径にほとんど依らない)。欠けがあれば減る。
                if(radius>=1e9){
                    if(referenceGround<0){referenceGround=counts[4];}
                    else if(Mathf.Abs(counts[4]-referenceGround)>referenceGround/200){failures.Add("半径 "+radius+": 地面の画素が "+counts[4]+"(半径 10^9 m では "+referenceGround+")。地面が欠けています。");}
                }
                CheckSeams(ground,radius);
            }
        }

        // 隣り合う Cell(同じ階層の +u、+v の隣)の共有辺の頂点(読み戻し)の位置の差が 1mm 以内。ずれていれば、Cell の間に隙間や重なりができる。
        private static void CheckSeams(PlanetTerrainSource source,double radius){
            var selected=new HashSet<PlanetCellKey>(source.LastSelected);
            int side=PlanetLayerSource.CellQuads+1;
            float worst=0;int pairs=0;int bad=0;PlanetCellKey worstKey=default;
            foreach(PlanetCellKey key in source.LastSelected){
                PlanetCellFrame frame=source.Selector.Frame(in key);
                if(!LogicalVector3.TrySubtract(frame.Origin,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 relative)){continue;}
                if(((Vector3)relative).magnitude>60f){continue;}
                for(int direction=1;direction<=3;direction+=2){
                    PlanetCellKey neighbor=key.Neighbor(direction);
                    // 面をまたぐ隣は、辺の向きが回っているので比べない(同じ面の中だけ)。
                    if(neighbor.Face!=key.Face||!selected.Contains(neighbor)){continue;}
                    if(!source.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] a,out PlanetCellFrame _)||!source.TryReadbackCellForDiagnostics(in neighbor,out VirtualGeometryPackedVertex[] b,out PlanetCellFrame neighborFrame)){continue;}
                    if(!LogicalVector3.TrySubtract(neighborFrame.Origin,cameraLogical.LocalPosition,out LogicalVector3 neighborDifference)||!neighborDifference.TryToFloat3(out float3 neighborRelative)){continue;}
                    pairs++;
                    float pairWorst=0;
                    for(int j=0;j<side;j++){
                        Vector3 pa;Vector3 pb;
                        if(direction==1){
                            // +x の隣: A の最後の列と B の最初の列
                            pa=(Vector3)relative+a[j*side+(side-1)].Position;
                            pb=(Vector3)neighborRelative+b[j*side].Position;
                        }else{
                            // +y の隣: A の最後の行と B の最初の行
                            pa=(Vector3)relative+a[(side-1)*side+j].Position;
                            pb=(Vector3)neighborRelative+b[j].Position;
                        }
                        pairWorst=Mathf.Max(pairWorst,(pa-pb).magnitude);
                    }
                    if(pairWorst>worst){worst=pairWorst;worstKey=key;}
                    if(pairWorst>1e-3f){bad++;}
                }
            }
            string where="";
            if(bad>0){where=" at "+worstKey;}
            report.Add("seams radius "+radius.ToString("G3",CultureInfo.InvariantCulture)+": adjacentPairs="+pairs+" pairsOffBy>1mm="+bad+" worst="+worst.ToString("G3",CultureInfo.InvariantCulture)+"m"+where);
            if(bad!=0){failures.Add("半径 "+radius+": 隣り合う Cell の共有辺が 1mm を超えてずれた組が "+bad+" あります(最大 "+worst+"m、"+worstKey+")。");}
        }

        // camera を、接点 contact の +v 側へ水平に distance の所に、月の地表から clearance の高さに置く(月の地表の高さを CPU の Field で求めて合わせる)。
        private static bool PlaceCameraAboveMoon(int contact,double distance,double clearance){
            double altitude=clearance;
            for(int i=0;i<4;i++){
                if(!demo.PlaceCamera(contact,altitude,distance)){return false;}
                if(!LogicalVector3.TrySubtract(cameraLogical.LocalPosition,demo.Center(1),out LogicalVector3 local)||!sources[1].TrySampleSurface(in local,out float _,out float above)){return false;}
                altitude+=clearance-above;
            }
            return demo.PlaceCamera(contact,altitude,distance);
        }

        // 描画の時間: 画像を読み戻さずに N frame 続けて描き、CPU の提出時間と、まとめて待った全体の時間(GPU が律速なら GPU の frame 時間の目安)を測る。
        private static IEnumerable<object> Timing(){
            const int frames=60;
            string[] names={"contact3","contact2","contact1","overview"};
            for(int view=0;view<4;view++){
                if(view==0){demo.ShowContactView(3);}
                else if(view==1){demo.ShowContactView(2);}
                else if(view==2){demo.ShowContactView(1);}
                else{demo.ShowOverview();}
                foreach(object step in Converge("Timing"+names[view],Width,ImageHeight)){yield return step;}
                RenderTexture target=RenderTexture.GetTemporary(Width,ImageHeight,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear,1);
                RenderTexture oldTarget=camera.targetTexture;
                try{
                    camera.targetTexture=target;
                    var request=new RenderPipeline.StandardRequest{destination=target};
                    // 温める
                    for(int i=0;i<5;i++){RenderPipeline.SubmitRenderRequest(camera,request);}
                    SyncGpu(target);
                    var total=Stopwatch.StartNew();
                    double submit=0;
                    var select=new double[Count];var flush=new double[Count];var prepare=new double[Count];var describe=new double[Count];var properties=new double[Count];
                    var evictionsBefore=new int[Count];var starvedBefore=new int[Count];
                    for(int body=0;body<Count;body++){evictionsBefore[body]=sources[body].Evictions;starvedBefore[body]=sources[body].StarvedRequests;}
                    for(int i=0;i<frames;i++){
                        var one=Stopwatch.StartNew();
                        RenderPipeline.SubmitRenderRequest(camera,request);
                        one.Stop();
                        submit+=one.Elapsed.TotalMilliseconds;
                        for(int body=0;body<Count;body++){
                            select[body]+=sources[body].LastSelectMilliseconds;flush[body]+=sources[body].LastFlushMilliseconds;prepare[body]+=sources[body].LastPrepareMilliseconds;
                            describe[body]+=sources[body].LastDescribeMilliseconds;properties[body]+=sources[body].LastPropertiesMilliseconds;
                        }
                    }
                    SyncGpu(target);
                    total.Stop();
                    report.Add("timing "+names[view]+" "+Width+"x"+ImageHeight+": cpuSubmit="+(submit/frames).ToString("F2",CultureInfo.InvariantCulture)+"ms/frame total(with GPU wait)="+
                        (total.Elapsed.TotalMilliseconds/frames).ToString("F2",CultureInfo.InvariantCulture)+"ms/frame cells="+sources[0].LastSelected.Count+"/"+sources[1].LastSelected.Count+"/"+sources[2].LastSelected.Count+"/"+sources[3].LastSelected.Count+
                        " pending="+sources[0].PendingGeneration+"/"+sources[1].PendingGeneration+"/"+sources[2].PendingGeneration+"/"+sources[3].PendingGeneration);
                    string parts="";
                    for(int body=0;body<Count;body++){
                        parts+=" body"+body+"(select/flush/prepare[describe+properties])="+(select[body]/frames).ToString("F2",CultureInfo.InvariantCulture)+"/"+(flush[body]/frames).ToString("F2",CultureInfo.InvariantCulture)+"/"+
                            (prepare[body]/frames).ToString("F2",CultureInfo.InvariantCulture)+"["+(describe[body]/frames).ToString("F2",CultureInfo.InvariantCulture)+"+"+(properties[body]/frames).ToString("F2",CultureInfo.InvariantCulture)+"]"+
                            " evictions+="+(sources[body].Evictions-evictionsBefore[body])+" starved+="+(sources[body].StarvedRequests-starvedBefore[body]);
                    }
                    report.Add("timing "+names[view]+" CPU parts (ms/frame):"+parts);
                }finally{
                    camera.targetTexture=oldTarget;
                    RenderTexture.ReleaseTemporary(target);
                }
            }
        }

        private static void SyncGpu(RenderTexture target){
            AsyncGPUReadbackRequest request=AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32);
            request.WaitForCompletion();
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
