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
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 実データの帯域より上の細部の増幅(<see cref="PlanetTerrainSource.AmplifyAmplitude"/>)の検証。月(半径 1,737.4 km)に合成の高起伏の全球の格子(画素 3.8 km、遮断波長 1.9 km)を載せ、
    /// 増幅の振幅を 0 と 40 m にして比べる(遮断波長は実データの 1/2 画素の波長)。
    /// - 粗い Cell(quad が遮断波長の半分以上。階層 ≤ 5)の頂点は、増幅の有無でビット単位で同じ(増幅は実データの帯域より上だけ)
    /// - 細かい Cell(階層 ≥ 14)は、増幅で頂点の高さが変わり、GPU の頂点が CPU の式(<see cref="PlanetHeightField.Amplified"/>)と一致する
    /// - 増幅の項を赤道に沿って並べた系列は、平均が 0 に近く、エネルギーの大半が遮断波長より短い波長にある(1 画素幅の箱形で平滑化した二乗平均の平方根 ≤ 元の 0.35 倍。gradient noise の 1 octave は帯域が広く、遮断波長の近くの octave のエネルギーの一部は長い波長に入る。1 画素ごとの平均の二乗平均の平方根 ≤ 元の 0.45 倍も見る)
    /// - 地表の高さ(<see cref="PlanetTerrainSource.TrySampleSurface(in LogicalVector3,out LogicalScalar,out LogicalScalar)"/>)が、描画の頂点と一致する(増幅の項を含む)
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.AmplificationValidation.Run(結果は TestResults/Planet/Amplification)
    /// </summary>
    public static class AmplificationValidation{
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=900;
        private const float AmplifyAmplitude=40f;
        private const float AmplifyHurst=0.87f;
        private const double MoonRadius=RealMoonData.MoonRadius;
        // 粗い Cell とみなす最大の階層(quad が遮断波長の半分 = 0.95 km 以上)。階層 L の quad は 半径·(π/2)/2^L/64 m(階層 5 で 1.3 km、6 で 0.67 km)。
        private const int CoarseLevel=5;
        private const int FineLevel=14;
        // 細かい Cell を選ぶ視点の高さ(地表から 300 m、画素 0.5 m、quad 1 m = 階層 15)。
        private const double FineAltitude=300.0;

        private static string results;
        private static Camera camera;
        private static global::Omni.Logical.Transform cameraLogical;
        private static Light sunLight;
        private static PlanetTerrainSource moon;
        private static PlanetRasterLayer globalLayer;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            results="TestResults/Planet/Amplification";
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
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("Amplification validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            globalLayer=RealMoonData.CreateSynthetic();
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var body=new GameObject("Moon");body.SetActive(false);
            body.AddComponent<global::Omni.Logical.Transform>();
            moon=body.AddComponent<PlanetTerrainSource>();
            body.AddComponent<OmniPlanetPlacement>();
            // 手続きの高さは 0(実データ + 増幅だけ)。階層は 1 quad が 1mm になる 28 まで。
            MoonBallSceneBuilder.Configure(moon,(float)MoonRadius,0f,3800f,32,32,4096,64,true);
            HeightShiftOverride.Apply(moon);
            moon.Material=AssetDatabase.LoadAssetAtPath<Material>(MoonBallSceneBuilder.MoonMaterialPath);
            moon.SetRasters(globalLayer,null);
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
            Light light=sun.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.6f;light.color=new Color(1f,0.96f,0.9f);light.shadows=LightShadows.None;
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

        // 経度・緯度の地表の上 altitude の高さに camera を置き、水平から pitch 度下を向く。
        private static void PlaceCamera(double longitudeDegrees,double latitudeDegrees,double altitude,double pitchDegrees){
            double3 d=Direction(longitudeDegrees,latitudeDegrees);
            LogicalVector3 unit=UnitLogical(d);
            if(!LogicalVector3.TryScale(in unit,moon.Selector.LogicalRadius,out LogicalVector3 onSphere)||!moon.TrySampleSurface(in onSphere,out LogicalScalar height,out LogicalScalar _)){
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
            double pitch=pitchDegrees*Math.PI/180.0;
            Vector3 forward=north*(float)Math.Cos(pitch)-up*(float)Math.Sin(pitch);
            Quaternion rotation=Quaternion.LookRotation(forward.normalized,up);
            // 太陽は、地面すれすれ(水平から約 20°)の斜めから、camera の向きと同じ方向へ(起伏の陰影が出る)。
            Vector3 travel=(-up*0.34f+north*0.94f).normalized;
            sunLight.transform.rotation=Quaternion.LookRotation(travel,up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){throw new InvalidOperationException("camera の回転を Logical にできません。");}
            cameraLogical.LocalPosition=position;cameraLogical.LocalRotation=logical;cameraLogical.SyncUnityTransform();
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

        private static Texture2D Capture(){
            return M0Validation.Capture(camera,Width,ImageHeight);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        // 選んだ Cell のうち、階層が level 以下(または以上)のもの全ての頂点を読み戻す(key ごと)。
        private static Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]> ReadCells(bool coarse){
            var result=new Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]>();
            foreach(PlanetCellKey key in moon.LastSelected){
                if(coarse&&key.Level>CoarseLevel){continue;}
                if(!coarse&&key.Level<FineLevel){continue;}
                if(!moon.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                result[key]=vertices;
            }
            return result;
        }

        private static IEnumerator Steps(){
            int waited=0;
            while(moon.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月が有効になりません。");}
                yield return null;
            }
            double cutoffWavelength=0.5*globalLayer.TexelMeters(MoonRadius);
            report.Add("amplification: amplitude="+AmplifyAmplitude+"m hurst="+AmplifyHurst+" data texel="+globalLayer.TexelMeters(MoonRadius).ToString("F0",CultureInfo.InvariantCulture)+"m cutoff wavelength="+cutoffWavelength.ToString("F0",CultureInfo.InvariantCulture)+
                "m bound="+moon.AmplifyMaximum.ToString("G4",CultureInfo.InvariantCulture)+"m (amplitude set later)");

            // 場所: 経度・緯度 0(ExtremeSites の [2]。合成の格子の最大の位置([0])ではない)。
            double[][] sites=RealMoonData.ExtremeSites(globalLayer);
            double[] site=sites[2];
            string siteName="Site"+site[0].ToString("F1",CultureInfo.InvariantCulture)+"_"+site[1].ToString("F1",CultureInfo.InvariantCulture);

            // 1. 粗い Cell(quad が遮断波長の半分 = 0.95 km 以上。階層 <= 5): 月全体の視点で、増幅の有無によらず頂点がビット単位で同じ。
            moon.AmplifyAmplitude=0f;
            PlaceCamera(site[0],site[1],MoonRadius*2.5,90.0);
            foreach(object step in Converge("WholeOff")){yield return step;}
            Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]> offCoarse=ReadCells(true);
            moon.AmplifyAmplitude=AmplifyAmplitude;moon.AmplifyHurst=AmplifyHurst;
            report.Add("amplification bound="+moon.AmplifyMaximum.ToString("G4",CultureInfo.InvariantCulture)+"m");
            PlaceCamera(site[0],site[1],MoonRadius*2.5,90.0);
            foreach(object step in Converge("WholeOn")){yield return step;}
            Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]> onCoarse=ReadCells(true);
            Save("WholeOn",Capture());
            int coarseCommon=0;int coarseDifferent=0;
            foreach(KeyValuePair<PlanetCellKey,VirtualGeometryPackedVertex[]> pair in offCoarse){
                if(!onCoarse.TryGetValue(pair.Key,out VirtualGeometryPackedVertex[] other)){continue;}
                coarseCommon++;
                if(!SameVertices(pair.Value,other)){coarseDifferent++;}
            }
            report.Add("coarse cells (level <= "+CoarseLevel+"): offSelected="+offCoarse.Count+" onSelected="+onCoarse.Count+" common="+coarseCommon+" different="+coarseDifferent);
            if(coarseCommon==0){failures.Add("増幅の有無で共通の粗い Cell がありません。");}
            if(coarseDifferent!=0){failures.Add("増幅の有無で、粗い Cell の頂点が違います("+coarseDifferent+"/"+coarseCommon+" 件)。増幅は実データの帯域より上だけに入るはずです。");}

            // 2. 細かい Cell(階層 >= 14): 地表 300 m の視点で、増幅で頂点の高さが変わる。
            moon.AmplifyAmplitude=0f;
            PlaceCamera(site[0],site[1],FineAltitude,60.0);
            foreach(object step in Converge("Off")){yield return step;}
            Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]> offFine=ReadCells(false);
            Save("Off300m",Capture());
            moon.AmplifyAmplitude=AmplifyAmplitude;
            PlaceCamera(site[0],site[1],FineAltitude,60.0);
            foreach(object step in Converge("On")){yield return step;}
            Dictionary<PlanetCellKey,VirtualGeometryPackedVertex[]> onFine=ReadCells(false);
            Save("On300m",Capture());
            int fineCommon=0;int fineChanged=0;double largestChange=0;
            foreach(KeyValuePair<PlanetCellKey,VirtualGeometryPackedVertex[]> pair in offFine){
                if(!onFine.TryGetValue(pair.Key,out VirtualGeometryPackedVertex[] other)){continue;}
                fineCommon++;
                if(!SameVertices(pair.Value,other)){fineChanged++;}
                for(int i=0;i<pair.Value.Length;i+=64){largestChange=Math.Max(largestChange,(pair.Value[i].Position-other[i].Position).magnitude);}
            }
            report.Add("fine cells (level >= "+FineLevel+"): offSelected="+offFine.Count+" onSelected="+onFine.Count+" common="+fineCommon+" changed="+fineChanged+" largestVertexChange="+largestChange.ToString("G4",CultureInfo.InvariantCulture)+"m");
            if(fineCommon==0){failures.Add("増幅の有無で共通の細かい Cell がありません。");}
            if(fineChanged==0){failures.Add("増幅しても、細かい Cell の頂点が変わりません。");}

            // GPU の頂点と CPU の式の一致(増幅あり)。camera に近い 12 Cell。
            foreach(double altitude in new[]{1000.0,10.0,0.17}){
                PlaceCamera(site[0],site[1],altitude,35.0);
                foreach(object step in Converge("On"+altitude)){yield return step;}
                CheckGpu(siteName+" altitude "+altitude+"m");
                if(altitude==10.0||altitude==0.17){Save("On"+altitude.ToString(CultureInfo.InvariantCulture)+"m",Capture());}
            }
            // 地表の高さ(CPU の TrySampleSurface)と、描画の頂点の高さの差。
            CheckSurface(siteName);

            // 増幅の項の系列: 赤道に沿って 4,096 点(画素の 1/16 刻み)。
            CheckSeries();

            report.Add("pool: used="+moon.UsedSlots+"/"+moon.PoolCapacity+" lastError="+(moon.LastError??"(none)")+" invalidBounds="+moon.InvalidBounds);
            if(moon.LastError!=null){failures.Add("設定エラー: "+moon.LastError);}
            if(moon.InvalidBounds!=0){failures.Add("非有限の頂点を持つ Cell があります("+moon.InvalidBounds+")。");}
        }

        private static bool SameVertices(VirtualGeometryPackedVertex[] a,VirtualGeometryPackedVertex[] b){
            if(a.Length!=b.Length){return false;}
            for(int i=0;i<a.Length;i++){
                // Vector3 の != は近似(差の二乗が 1e-10 未満を等しいとみなす)なので、成分を厳密に比べる。
                if(a[i].Position.x!=b[i].Position.x||a[i].Position.y!=b[i].Position.y||a[i].Position.z!=b[i].Position.z){return false;}
            }
            return true;
        }

        private static void CheckGpu(string name){
            var order=new List<KeyValuePair<float,PlanetCellKey>>();
            foreach(PlanetCellKey key in moon.LastSelected){
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TrySubtract(frame.Origin,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 relative)){continue;}
                order.Add(new KeyValuePair<float,PlanetCellKey>(((Vector3)relative).magnitude,key));
            }
            order.Sort((a,b)=>a.Key.CompareTo(b.Key));
            int side=PlanetLayerSource.CellQuads+1;
            int cells=0;int points=0;double worst=0;
            for(int n=0;n<order.Count&&n<12;n++){
                PlanetCellKey key=order[n].Value;
                if(!moon.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                cells++;
                double cellWorst=0;
                for(int j=0;j<side;j+=4){
                    for(int i=0;i<side;i+=4){
                        if(!moon.TryEvaluateVertexForDiagnostics(in key,i,j,out Vector3 expected)){continue;}
                        cellWorst=Math.Max(cellWorst,(vertices[j*side+i].Position-expected).magnitude);
                        points++;
                    }
                }
                double cellSize=moon.Selector.QuadArc(key.Level)*PlanetLayerSource.CellQuads;
                // 実データの Validation と同じ許容値(階層 14 以上: 0.3 mm + Cell の大きさの 10^-6、粗い Cell: 2 mm + Cell の大きさの 1.5×10^-5)。
                double allowed=2e-3+1.5e-5*cellSize;
                if(key.Level>=14){allowed=3e-4+1e-6*cellSize;}
                if(!(cellWorst<=allowed)){failures.Add("GPU の頂点が CPU の式と "+cellWorst+"m 違います(許す差 "+allowed+"m、"+name+"、"+key+")。");}
                worst=Math.Max(worst,cellWorst);
            }
            report.Add("GPU vs CPU "+name+": cells="+cells+" points="+points+" worst="+worst.ToString("G3",CultureInfo.InvariantCulture)+"m");
            if(cells==0||points==0){failures.Add(name+": GPU と CPU を比べる頂点がありません(Cell "+cells+"、頂点 "+points+")。");}
        }

        // 地表 0.17 m の視点の真下の Cell で、CPU の地表の高さ(増幅を含む)と、描画の頂点の高さ(|原点 + 相対位置| − R)を比べる。
        private static void CheckSurface(string name){
            PlanetCellKey nearest=default;float nearestDistance=float.MaxValue;
            foreach(PlanetCellKey key in moon.LastSelected){
                PlanetCellFrame frame=moon.Selector.Frame(in key);
                if(!LogicalVector3.TrySubtract(frame.Origin,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 relative)){continue;}
                float distance=((Vector3)relative).magnitude;
                if(distance<nearestDistance){nearestDistance=distance;nearest=key;}
            }
            if(!moon.TryReadbackCellForDiagnostics(in nearest,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame frameOfNearest)){failures.Add(name+": 最も近い Cell を読み戻せません。");return;}
            int side=PlanetLayerSource.CellQuads+1;
            double worst=0;int samples=0;
            for(int j=0;j<side;j+=8){
                for(int i=0;i<side;i+=8){
                    Vector3 position=vertices[j*side+i].Position;
                    if(!LogicalVector3.TryFromFloat3(new float3(position.x,position.y,position.z),out LogicalVector3 relativePosition)||!LogicalVector3.TryAdd(frameOfNearest.Origin,relativePosition,out LogicalVector3 vertexPoint)||
                        !LogicalVector3.TryLength(in vertexPoint,out LogicalScalar vertexDistance)||!LogicalScalar.TrySubtract(in vertexDistance,moon.Selector.LogicalRadius,out LogicalScalar vertexHeight)||
                        !moon.TrySampleSurface(in vertexPoint,out LogicalScalar surfaceHeight,out LogicalScalar _)||!LogicalScalar.TrySubtract(in vertexHeight,in surfaceHeight,out LogicalScalar heightDifference)||
                        !heightDifference.TryToDouble(out double error)){
                        continue;
                    }
                    worst=Math.Max(worst,Math.Abs(error));samples++;
                }
            }
            report.Add("surface "+name+": CPU TrySampleSurface (with amplification) vs rendered vertex height: samples="+samples+" worst="+(worst*1e6).ToString("G3",CultureInfo.InvariantCulture)+"um");
            if(samples==0||!(worst<=2e-4)){failures.Add(name+": CPU の地表の高さと描画の頂点の高さが "+worst+"m 違います(標本 "+samples+" 点、許す差 2e-4m)。");}
        }

        // 増幅の項を赤道に沿って並べた系列(画素の 1/16 刻み)の平均と、遮断波長より長い波長のエネルギーの割合。
        private static void CheckSeries(){
            const int count=4096;
            double texel=globalLayer.TexelMeters(MoonRadius);
            double step=texel/16.0;
            var series=new double[count];
            for(int i=0;i<count;i++){
                double angle=(i*step)/MoonRadius;
                // 赤道(緯度 0)の経度 angle の点。経度 0 付近から東へ。
                var direction=new double3(Math.Sin(angle),Math.Cos(angle),0.0);
                LogicalVector3 unit=UnitLogical(direction);
                if(!LogicalVector3.TryScale(in unit,moon.Selector.LogicalRadius,out LogicalVector3 point)||!moon.TryAmplifiedHeight(in point,out float value)){
                    failures.Add("増幅の項を求められません(点 "+i+")。");return;
                }
                series[i]=value;
            }
            double mean=0;double sumSquare=0;
            for(int i=0;i<count;i++){mean+=series[i];}
            mean/=count;
            for(int i=0;i<count;i++){sumSquare+=(series[i]-mean)*(series[i]-mean);}
            double rms=Math.Sqrt(sumSquare/count);
            // 2 倍幅(遮断波長の 2 倍 = 1 画素 = 16 点)の箱形で平滑化した系列(遮断波長より短い波長を落とす)の二乗平均の平方根。
            int window=16;
            double smoothSquare=0;int smoothCount=0;
            for(int i=0;i+window<=count;i+=4){
                double average=0;
                for(int k=0;k<window;k++){average+=series[i+k]-mean;}
                average/=window;
                smoothSquare+=average*average;smoothCount++;
            }
            double smoothRms=Math.Sqrt(smoothSquare/smoothCount);
            double ratio=smoothRms/rms;
            // 参考: 実データの 1 画素(16 点)ごとの平均の二乗平均の平方根 / 元(設計は「データの 1 セルごとに平均 0」。厳密には保証していない)。
            double texelSquare=0;int texelCount=0;
            for(int i=0;i+16<=count;i+=16){
                double average=0;
                for(int k=0;k<16;k++){average+=series[i+k]-mean;}
                average/=16;
                texelSquare+=average*average;texelCount++;
            }
            double texelRatio=Math.Sqrt(texelSquare/texelCount)/rms;
            report.Add("per-texel mean of the amplification: rms of 1-texel means / rms = "+texelRatio.ToString("G3",CultureInfo.InvariantCulture)+" (gate 0.45)");
            if(!(texelRatio<=0.45)){failures.Add("増幅の項の 1 画素ごとの平均が大きすぎます(1 画素ごとの平均の二乗平均の平方根 / 元 = "+texelRatio+"、許す上限 0.45)。遠景(データそのもの)と近景がずれます。");}
            report.Add("series along the equator: points="+count+" spacing="+step.ToString("F0",CultureInfo.InvariantCulture)+"m mean="+mean.ToString("G3",CultureInfo.InvariantCulture)+"m rms="+rms.ToString("G3",CultureInfo.InvariantCulture)+"m smoothedRms="+smoothRms.ToString("G3",CultureInfo.InvariantCulture)+"m ratio="+ratio.ToString("G3",CultureInfo.InvariantCulture));
            if(!(rms>0.1)){failures.Add("増幅の項の二乗平均の平方根が小さすぎます("+rms+" m)。増幅が効いていません。");}
            if(!(Math.Abs(mean)<=0.2*rms)){failures.Add("増幅の項の平均が 0 から離れています(平均 "+mean+" m、二乗平均の平方根 "+rms+" m)。");}
            if(!(ratio<=0.35)){failures.Add("増幅の項のエネルギーが、遮断波長より長い波長(実データの帯域)に入っています(平滑化後 / 元 = "+ratio+")。");}
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
