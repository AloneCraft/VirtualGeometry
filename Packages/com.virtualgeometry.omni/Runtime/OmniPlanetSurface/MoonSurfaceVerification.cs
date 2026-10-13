using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using Omni.BatchRendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using L=Omni.Logical;

namespace Omni.PlanetSurface{
    /// <summary>
    /// MoonSurfaceVerification.unityのPlanetに付き、自然地形だけを軌道・低空・地上で評価します。
    /// EarthToMoonの施設や局所上書きは使いません。生成器自体には月固有座標を持ち込みません。
    /// </summary>
    public sealed class MoonSurfaceVerification:MonoBehaviour,IRadialBatchMaterialDependencyProvider{
        [SerializeField] private PlanetSurfaceDefinition definition;
        [SerializeField] private Material material;
        [SerializeField] private L.Transform viewer;
        [SerializeField] private bool webGpuBudget;
        [SerializeField] private ComputeShader detailShader;
        private ConstrainedSurfaceEvaluator source;
        private PlanetSurfaceLod lod;
        private SurfaceQualityProfile profile;
        private double started;
        private double nextReport;
        private double maximumUpdate;
        private double peakRockWork,peakDetailWork;
        private double peakViewerWork,peakTerrainWork,peakDiagnosticWork;
        private double peakTransitionStepWork;
        private string peakTerrainStage="idle";
        private string peakTransitionStepStage="idle";
        private string peakRockStage="idle";
        private int frames;
        private double frameTime;
        private string captureDirectory;
        private int captureIndex;
        private RenderTexture captureTarget;
        private string pendingCapture;
        private bool captureDiagnostics;
        private int captureDurationSeconds=55;
        private SurfaceVerificationRenderScope renderScope;
        private readonly SurfaceDiagnosticCaptureSequence diagnosticCaptures=new();
        private SurfacePerformanceAcceptance acceptance;
        private bool acceptanceRequested,acceptanceStarted,acceptanceWritten;
        private int pendingReadbacks,requestedExitCode,quitAfterFrame;
        private bool quitRequested,captureReleaseScheduled;
        private bool captureInProgress;
        private Coroutine captureRoutine;
        private float capturePreviousDiagnosticMode;
        private Camera captureCamera;
        private bool captureCameraWasEnabled;
        private GameObject nativeShadowProbe;

        [Serializable]
        private sealed class AcceptanceFile{
            public string timestampJst,status,graphicsApi;
            public int frameCount,width,height,overBudgetFrameCount;
            public double durationSeconds,averageFramesPerSecond,maximumUpdateMilliseconds,updateBudgetMilliseconds;
            public long peakCpuBytes,peakGpuBytes,cpuBudgetBytes,gpuBudgetBytes,worstCpuBytes,worstGpuBytes;
            public bool resolutionPassed,frameRatePassed,updateBudgetPassed,memoryPassed,staticBudgetPassed;
        }

        /// <summary>実行時に作るパッチの材質を、ビルド時BRG登録へ明示します。</summary>
        public void CollectBatchMaterialDependencies(List<Material> destination){
            if(this.material!=null){destination.Add(this.material);}
        }

        /// <summary>検証用Builderから正本と視点を接続します。</summary>
        public void Configure(PlanetSurfaceDefinition definition,Material material,L.Transform viewer){
            this.definition=definition;this.material=material;this.viewer=viewer;
        }

        private void Start(){
            Application.runInBackground=true;
            string[] commandLineArguments=Application.platform==RuntimePlatform.WindowsPlayer
                ?Environment.GetCommandLineArgs()
                :Array.Empty<string>();
            if(Array.IndexOf(commandLineArguments,"--surface-backend=legacy")>=0){
                RadialSceneBatchRendererService.SetMode(RadialSceneBatchBackendMode.Legacy);
            }
            if(Application.platform==RuntimePlatform.WebGLPlayer&&Uri.TryCreate(Application.absoluteURL,UriKind.Absolute,out Uri address)){
                foreach(string option in address.Query.TrimStart('?').Split('&')){
                    if(option=="surface-backend=legacy"){
                        RadialSceneBatchRendererService.SetMode(RadialSceneBatchBackendMode.Legacy);
                    }
                }
                this.acceptanceRequested=HasQueryFlag(Application.absoluteURL,"surface-acceptance");
            }
            if(this.definition==null||this.definition.BaseHeight==null||this.material==null||this.viewer==null){
                throw new InvalidOperationException("月面検証の参照が不足しています。");
            }
            int width=this.definition.HeightWidth,height=this.definition.HeightHeight;
            // TextAssetが所有する読み取り専用ビューを捕捉し、66MBの複製を作りません。
            var values=this.definition.BaseHeight.GetData<float>();
            if(values.Length!=(long)width*height){throw new InvalidOperationException("月の標高寸法が不正です。");}
            var grid=new SphericalHeightGrid(width,height,(x,y)=>values[y*width+x],20000);
            this.source=new ConstrainedSurfaceEvaluator(grid,this.definition.RadiusMeters,this.definition.Generation);
            this.profile=SurfaceQualityProfile.Current();
            if(this.webGpuBudget||Application.platform==RuntimePlatform.WebGLPlayer){
                this.profile=SurfaceQualityProfile.WebGpu();
            }
            Application.targetFrameRate=this.profile.TargetFramesPerSecond;
            this.renderScope=new SurfaceVerificationRenderScope();
            var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            string pipelineName="none";
            float renderScale=1;
            if(pipeline!=null){pipelineName=pipeline.name;renderScale=pipeline.renderScale;}
            SurfaceVerificationLog.Info($"[PlanetSurfaceVerificationSetup] quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}, "+
                $"pipeline={pipelineName}, renderScale={renderScale:F3}, screen={Screen.width}x{Screen.height}, "+
                $"reversedZ={SystemInfo.usesReversedZBuffer}, surfaceShader={this.material.shader.name}");
            this.lod=this.gameObject.AddComponent<PlanetSurfaceLod>();
            this.lod.Initialize(this.definition,this.material,this.viewer,this.source,Array.Empty<MeshRenderer>(),null,false,this.profile,this.detailShader);
            this.started=Time.realtimeSinceStartupAsDouble;
            this.nextReport=this.started+5;
            // 自動検証Playerに明示した出力先だけへ画像を書き、検証終了後に閉じます。
            if(Application.platform==RuntimePlatform.WindowsPlayer){
                foreach(string argument in commandLineArguments){
                    if(argument=="--surface-diagnostics"){this.captureDiagnostics=true;}
                    if(argument=="--surface-long-walk"){this.captureDurationSeconds=90;}
                    if(argument=="--surface-acceptance"){this.acceptanceRequested=true;this.captureDurationSeconds=120;}
                    if(argument=="--surface-shadow-probe"){this.CreateNativeShadowProbe();}
                    const string prefix="--surface-capture-dir=";
                    if(argument.StartsWith(prefix,StringComparison.Ordinal)){
                        this.captureDirectory=argument.Substring(prefix.Length);
                        if(!Path.IsPathRooted(this.captureDirectory)){throw new ArgumentException("画像出力先は絶対パスで指定してください。");}
                        Directory.CreateDirectory(this.captureDirectory);
                        Application.runInBackground=true;
                        Application.targetFrameRate=60;
                        // URP RenderGraph の SubmitRenderRequest は出力先にもDepth/Stencilを要求する。
                        // 深度なしRTでは画像は出てもキャプチャごとに警告されるため、描画用深度を持たせる。
                        this.captureTarget=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32);
                        this.captureTarget.Create();
                    }
                }
            }
            if(this.acceptanceRequested){
                this.acceptance=new SurfacePerformanceAcceptance(this.profile,Screen.width,Screen.height,
                    this.lod.WorstCaseMemoryBudget.Fits);
            }
            this.MoveViewer(0);
        }

        /// <summary>
        /// URPがBRGのLight viewを要求する前に通常Rendererの影boundsを必要とするかを切り分けます。
        /// MoonSurfaceVerificationのWindows Player診断でだけ使い、地表より下へ置いて画像へ混ぜません。
        /// </summary>
        private void CreateNativeShadowProbe(){
            Camera camera=this.viewer!=null?this.viewer.GetComponent<Camera>():null;
            if(camera==null){throw new InvalidOperationException("影診断probeを作成できません。");}
            this.nativeShadowProbe=GameObject.CreatePrimitive(PrimitiveType.Cube);
            this.nativeShadowProbe.name="Planet Surface Native Shadow Probe";
            this.nativeShadowProbe.transform.SetParent(camera.transform,false);
            this.nativeShadowProbe.transform.localPosition=new Vector3(0,0,5);
            this.nativeShadowProbe.transform.localScale=Vector3.one;
            Collider collider=this.nativeShadowProbe.GetComponent<Collider>();
            if(collider!=null){Destroy(collider);}
            MeshRenderer renderer=this.nativeShadowProbe.GetComponent<MeshRenderer>();
            this.nativeShadowProbe.AddComponent<RadialSceneBatchRendererExclusion>();
            RadialGlobalRenderSystem.SetWarpExcluded(renderer,true);
            // 月面材質は検証Playerへ必ず収録されるため、Shader.Findによるstrip済みshader参照を避けます。
            renderer.sharedMaterial=this.material;
            renderer.shadowCastingMode=ShadowCastingMode.On;
            renderer.receiveShadows=false;
            var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var cameraData=camera.GetUniversalAdditionalCameraData();
            Light sceneLight=FindAnyObjectByType<Light>();
            SurfaceVerificationLog.Info($"[PlanetSurfaceShadowProbe] nativeCaster=True, visibleControl=True, "+
                $"localPosition=(0,0,5), size=1m, rendererEnabled={renderer.enabled}, "+
                $"shadowMode={renderer.shadowCastingMode}, shader={renderer.sharedMaterial.shader.name}, "+
                $"shadowPass={renderer.sharedMaterial.FindPass("ShadowCaster")}, cameraRenderShadows={cameraData.renderShadows}, "+
                $"systemSupportsShadows={SystemInfo.supportsShadows}, qualityShadows={QualitySettings.shadows}, "+
                $"shadowDistance={QualitySettings.shadowDistance:F3}, pipelineSupportsMain={pipeline?.supportsMainLightShadows}, "+
                $"pipelineMainMode={pipeline?.mainLightRenderingMode}, lightType={sceneLight?.type}, lightShadows={sceneLight?.shadows}");
        }

        private static bool HasQueryFlag(string url,string name){
            if(string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(name)||
                !Uri.TryCreate(url,UriKind.Absolute,out Uri uri)){return false;}
            string query=uri.Query;
            if(string.IsNullOrEmpty(query)){return false;}
            foreach(string part in query.TrimStart('?').Split(new[]{'&'},StringSplitOptions.RemoveEmptyEntries)){
                int equals=part.IndexOf('=');
                string key=Uri.UnescapeDataString(equals<0?part:part.Substring(0,equals));
                if(!string.Equals(key,name,StringComparison.Ordinal)){continue;}
                string value=equals<0?"1":Uri.UnescapeDataString(part.Substring(equals+1));
                return value=="1"||string.Equals(value,"true",StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void Update(){
            if(this.source==null){return;}
            if(this.quitRequested){this.AdvanceQuit();return;}
            double now=Time.realtimeSinceStartupAsDouble;
            this.MoveViewer(now-this.started);
            if(this.captureDirectory!=null){
                double elapsed=now-this.started;
                if(this.captureIndex<1+(this.captureDurationSeconds-8)/10&&elapsed>=8+this.captureIndex*10){
                    this.pendingCapture=Path.Combine(this.captureDirectory,$"stage-{this.captureIndex}.png");
                    this.captureIndex++;
                }
                if(elapsed>=this.captureDurationSeconds){
                    if(this.acceptanceRequested&&!this.acceptanceWritten){
                        SurfaceVerificationLog.Error($"[PlanetSurfaceAcceptance] status=FAIL, reason=timeout, "+
                            $"frames={this.acceptance?.FrameCount??0}/{SurfacePerformanceAcceptance.RequiredFrameCount}");
                        this.RequestQuit(3);
                    }else{this.RequestQuit(0);}
                    return;
                }
            }
            this.UpdateAcceptance(now-this.started);
            this.frames++;
            this.frameTime+=Time.unscaledDeltaTime;
            if(this.lod.LastUpdateMilliseconds>this.maximumUpdate){
                this.maximumUpdate=this.lod.LastUpdateMilliseconds;
                this.peakRockWork=this.lod.LastRockWorkMilliseconds;this.peakDetailWork=this.lod.LastDetailWorkMilliseconds;
                this.peakRockStage=this.lod.LastRockWorkStage;
                this.peakViewerWork=this.lod.LastViewerWorkMilliseconds;this.peakTerrainWork=this.lod.LastTerrainWorkMilliseconds;
                this.peakDiagnosticWork=this.lod.LastDiagnosticWorkMilliseconds;this.peakTerrainStage=this.lod.LastTerrainWorkStage;
                this.peakTransitionStepWork=this.lod.LastTransitionStepMilliseconds;
                this.peakTransitionStepStage=this.lod.LastTransitionStepStage;
            }
            if(now>=this.nextReport){
                var d=this.lod.Diagnostics;
                SurfaceVerificationLog.Info($"[PlanetSurfaceVerification] api={SystemInfo.graphicsDeviceType}, "+
                    $"altitude={d.ViewerAltitudeMeters:F3}m, leaves={d.LeafCount}, level={d.DeepestLevel}/{d.DesiredLevel}, "+
                    $"fps={this.frames/Math.Max(this.frameTime,1e-9):F2}, maxUpdateMs={this.maximumUpdate:F3}, "+
                    $"peakRockMs={this.peakRockWork:F3}, peakRockStage={this.peakRockStage}, peakDetailMs={this.peakDetailWork:F3}, "+
                    $"peakViewerMs={this.peakViewerWork:F3}, peakTerrainMs={this.peakTerrainWork:F3}, "+
                    $"peakTerrainStage={this.peakTerrainStage}, peakDiagnosticMs={this.peakDiagnosticWork:F3}, "+
                    $"peakTransitionStepMs={this.peakTransitionStepWork:F3}, peakTransitionStepStage={this.peakTransitionStepStage}, "+
                    $"brg={RadialSceneBatchRendererService.BackendState}, residents={RadialSceneBatchRendererService.ResidentInstanceCount}, "+
                    $"lightCulls={RadialSceneBatchRendererService.LightCullingInvocationCount}, "+
                    $"lightCameraSlot={RadialSceneBatchRendererService.LastLightCameraPacketSlot}, "+
                    $"lightBatch={RadialSceneBatchRendererService.LastLightBatchId}, "+
                    $"morphSplits={this.lod.CompletedSplitTransitions}, morphMerges={this.lod.CompletedMergeTransitions}, "+
                    $"transition={this.lod.IsTransitioning}, publishMs={this.lod.LastTransitionPublishMilliseconds:F3}, "+
                    $"pipelinedSteps={this.lod.PreparedStepsDuringMorph}, "+
                    $"detailRegions={this.lod.DetailReadyRegionCount}, detailPatches={this.lod.DetailBoundPatchCount}, "+
                    $"detailCpuBytes={this.lod.DetailCpuReservedBytes}, detailGpuBytes={this.lod.DetailGpuReservedBytes}, "+
                    $"rocks={this.lod.VisibleRockCount}, rockCapacity={this.lod.RockCapacity}, "+
                    $"rockUpdates={this.lod.CompletedRockUpdates}, rockBufferPreparations={this.lod.RockBufferPreparationCount}, "+
                    $"rockCpuReservedBytes={this.lod.RockCpuReservedBytes}, rockGpuReservedBytes={this.lod.RockGpuReservedBytes}, "+
                    $"anchorCacheBytes={this.source.AnchorCacheBytes}, "+
                    $"surfaceCpuBytes={this.lod.SurfaceCpuReservedBytes}/{this.profile.CpuBudgetBytes}, "+
                    $"surfaceGpuBytes={this.lod.SurfaceGpuReservedBytes}/{this.profile.GpuBudgetBytes}, "+
                    $"surfacePeakCpuBytes={this.lod.PeakSurfaceCpuReservedBytes}, surfacePeakGpuBytes={this.lod.PeakSurfaceGpuReservedBytes}, "+
                    $"surfaceWorstCpuBytes={this.lod.WorstCaseMemoryBudget.CpuBytes}, "+
                    $"surfaceWorstGpuBytes={this.lod.WorstCaseMemoryBudget.GpuBytes}, "+
                    $"surfaceMemoryWithinBudget={this.lod.SurfaceMemoryWithinBudget}; visual/performance acceptance pending");
                this.nextReport=now+5;this.frames=0;this.frameTime=0;this.maximumUpdate=0;
            }
        }

        private void UpdateAcceptance(double elapsed){
            if(this.acceptance==null||this.acceptanceWritten){return;}
            var diagnostics=this.lod.Diagnostics;
            if(!this.acceptanceStarted){
                if(elapsed<40||diagnostics.DeepestLevel<diagnostics.DesiredLevel||this.lod.IsTransitioning){return;}
                this.acceptanceStarted=true;
                SurfaceVerificationLog.Info($"[PlanetSurfaceAcceptance] status=START, frames={SurfacePerformanceAcceptance.RequiredFrameCount}, "+
                    $"resolution={Screen.width}x{Screen.height}, targetFps={this.profile.TargetFramesPerSecond}, "+
                    $"updateBudgetMs={this.profile.UpdateBudgetMilliseconds:F3}");
            }
            if(!this.acceptance.AddFrame(Time.unscaledDeltaTime,this.lod.LastUpdateMilliseconds,
                this.lod.SurfaceCpuReservedBytes,this.lod.SurfaceGpuReservedBytes,this.lod.SurfaceMemoryWithinBudget)){return;}
            if(!this.acceptance.TryGetResult(out SurfacePerformanceAcceptanceResult result)){
                throw new InvalidOperationException("1000フレーム完了結果を取得できませんでした。");
            }
            this.acceptanceWritten=true;
            string status=result.Passed?"PASS":"FAIL";
            SurfaceVerificationLog.Info($"[PlanetSurfaceAcceptance] status={status}, frames={result.FrameCount}, "+
                $"resolution={result.Width}x{result.Height}, durationSeconds={result.DurationSeconds:F3}, "+
                $"averageFps={result.AverageFramesPerSecond:F2}, maximumUpdateMs={result.MaximumUpdateMilliseconds:F3}, "+
                $"overBudgetFrames={result.OverBudgetFrameCount}, peakCpuBytes={result.PeakCpuBytes}/{result.CpuBudgetBytes}, "+
                $"peakGpuBytes={result.PeakGpuBytes}/{result.GpuBudgetBytes}, staticBudget={result.StaticBudgetPassed}, "+
                $"memory={result.MemoryPassed}, resolutionPassed={result.ResolutionPassed}, "+
                $"frameRatePassed={result.FrameRatePassed}, updateBudgetPassed={result.UpdateBudgetPassed}");
            if(this.captureDirectory!=null){
                var file=new AcceptanceFile{
                    timestampJst=DateTimeOffset.Now.ToString("o"),status=status,graphicsApi=SystemInfo.graphicsDeviceType.ToString(),
                    frameCount=result.FrameCount,width=result.Width,height=result.Height,
                    overBudgetFrameCount=result.OverBudgetFrameCount,durationSeconds=result.DurationSeconds,
                    averageFramesPerSecond=result.AverageFramesPerSecond,maximumUpdateMilliseconds=result.MaximumUpdateMilliseconds,
                    updateBudgetMilliseconds=result.UpdateBudgetMilliseconds,peakCpuBytes=result.PeakCpuBytes,
                    peakGpuBytes=result.PeakGpuBytes,cpuBudgetBytes=result.CpuBudgetBytes,gpuBudgetBytes=result.GpuBudgetBytes,
                    worstCpuBytes=this.lod.WorstCaseMemoryBudget.CpuBytes,worstGpuBytes=this.lod.WorstCaseMemoryBudget.GpuBytes,
                    resolutionPassed=result.ResolutionPassed,frameRatePassed=result.FrameRatePassed,
                    updateBudgetPassed=result.UpdateBudgetPassed,memoryPassed=result.MemoryPassed,
                    staticBudgetPassed=result.StaticBudgetPassed
                };
                File.WriteAllText(Path.Combine(this.captureDirectory,"acceptance.json"),JsonUtility.ToJson(file,true));
            }
            if(Application.platform==RuntimePlatform.WindowsPlayer){this.RequestQuit(result.Passed?0:2);}
        }

        private void MoveViewer(double seconds){
            // 各高度で停止した後、歩行高度で経度方向へ移動しLOD追従を確認します。
            double altitude;
            if(seconds<10){altitude=this.definition.RadiusMeters*1.5;}
            else if(seconds<20){altitude=10000;}
            else if(seconds<30){altitude=20;}
            else{altitude=1.7;}
            double walk=Math.Max(0,seconds-40)*1.2;
            double longitude=-0.3+walk/this.definition.RadiusMeters;
            const double latitude=0.1;
            var direction=new SurfaceVector(Math.Sin(longitude)*Math.Cos(latitude),
                Math.Cos(longitude)*Math.Cos(latitude),Math.Sin(latitude));
            var tangent=new SurfaceVector(Math.Cos(longitude),-Math.Sin(longitude),0);
            double ground=this.source.SampleHeightMeters(direction,0);
            SurfaceVector position=direction*(this.definition.RadiusMeters+ground+altitude);
            if(!L.Scalar.TryFromDouble(position.X,out L.Scalar x)||
                !L.Scalar.TryFromDouble(position.Y,out L.Scalar y)||
                !L.Scalar.TryFromDouble(position.Z,out L.Scalar z)){
                throw new ArithmeticException("検証カメラ座標の変換に失敗しました。");
            }
            this.viewer.LocalPosition=new L.Vector3(x,y,z);
            Vector3 forward;
            Vector3 up;
            if(altitude>100){
                forward=new Vector3((float)-direction.X,(float)-direction.Y,(float)-direction.Z);
                up=Vector3.forward;
            }else{
                SurfaceVector f=tangent-direction*0.15;
                forward=new Vector3((float)f.X,(float)f.Y,(float)f.Z);
                up=new Vector3((float)direction.X,(float)direction.Y,(float)direction.Z);
            }
            Quaternion rotation=Quaternion.LookRotation(forward,up);
            if(!L.Quaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out L.Quaternion logicalRotation)){
                throw new ArithmeticException("検証カメラ姿勢の変換に失敗しました。");
            }
            this.viewer.LocalRotation=logicalRotation;
            this.viewer.SyncUnityTransform();
        }

        private void LateUpdate(){
            if(this.captureTarget==null||this.captureInProgress){return;}
            if(this.captureDiagnostics&&this.diagnosticCaptures.IsActive){
                if(this.diagnosticCaptures.TryTake(out int mode,out string diagnosticPath)){
                    this.BeginCapture(diagnosticPath,mode);
                }
                return;
            }
            if(this.pendingCapture==null){return;}
            string path=this.pendingCapture;this.pendingCapture=null;
            this.BeginCapture(path,0);
            if(this.captureDiagnostics&&!this.diagnosticCaptures.TryBegin(path)){
                throw new InvalidOperationException("前回の診断画像列が完了する前に次のcaptureが開始されました。");
            }
        }

        private void BeginCapture(string path,int diagnosticMode){
            if(this.captureInProgress||this.captureRoutine!=null){
                throw new InvalidOperationException("前の月面captureが完了していません。");
            }
            this.captureRoutine=StartCoroutine(this.CaptureAtEndOfFrame(path,diagnosticMode));
        }

        private IEnumerator CaptureAtEndOfFrame(string path,int diagnosticMode){
            this.captureInProgress=true;
            this.capturePreviousDiagnosticMode=Shader.GetGlobalFloat("_SurfaceDiagnosticMode");
            this.captureCamera=this.viewer.GetComponent<Camera>();
            if(this.captureCamera==null){throw new InvalidOperationException("月面capture用カメラがありません。");}
            this.captureCameraWasEnabled=this.captureCamera.enabled;
            Shader.SetGlobalFloat("_SurfaceDiagnosticMode",diagnosticMode);
            try{
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=this.captureTarget};
                if(!RenderPipeline.SupportsRenderRequest(this.captureCamera,request)){
                    throw new InvalidOperationException("検証用オフスクリーン描画が未対応です。");
                }
                // 非表示でも動く明示描画を1回だけ行い、このフレームの自動描画を止めて二重送信を防ぎます。
                RenderPipeline.SubmitRenderRequest(this.captureCamera,request);
                this.captureCamera.enabled=false;
                this.QueueCapture(path);
                yield return new WaitForEndOfFrame();
            }finally{
                if(this.captureCamera!=null){this.captureCamera.enabled=this.captureCameraWasEnabled;}
                this.captureCamera=null;
                Shader.SetGlobalFloat("_SurfaceDiagnosticMode",this.capturePreviousDiagnosticMode);
                this.captureInProgress=false;this.captureRoutine=null;
            }
        }

        private void QueueCapture(string path){
            this.pendingReadbacks++;
            AsyncGPUReadback.Request(this.captureTarget,0,TextureFormat.RGBA32,result=>{
                try{
                    if(result.hasError){SurfaceVerificationLog.Error("検証画像の非同期読み戻しに失敗しました。");return;}
                    var texture=new Texture2D(1920,1080,TextureFormat.RGBA32,false);
                    try{
                        texture.LoadRawTextureData(result.GetData<byte>());
                        File.WriteAllBytes(path,texture.EncodeToPNG());
                    }finally{Destroy(texture);}
                }finally{this.pendingReadbacks--;}
            });
        }

        private void RequestQuit(int exitCode){
            if(this.quitRequested){return;}
            this.quitRequested=true;this.requestedExitCode=exitCode;this.pendingCapture=null;
            this.diagnosticCaptures.Cancel();this.AdvanceQuit();
        }

        private void AdvanceQuit(){
            if(!this.captureReleaseScheduled){
                if(this.captureInProgress||this.pendingReadbacks>0){return;}
                // 待機中にBackBufferを再利用し続けると最終Fenceが進み続けるため、先に検証カメラを止めます。
                Camera camera=this.viewer!=null?this.viewer.GetComponent<Camera>():null;
                if(camera!=null){camera.enabled=false;}
                if(this.captureTarget!=null){
                    this.captureTarget.Release();Destroy(this.captureTarget);this.captureTarget=null;
                }
                // RenderRequestの一時RTをDestroyした後もGPU/PlayerConnectionが参照を排出できる余裕を取ります。
                this.captureReleaseScheduled=true;this.quitAfterFrame=Time.frameCount+16;return;
            }
            if(Time.frameCount>=this.quitAfterFrame){Application.Quit(this.requestedExitCode);}
        }

        private void OnDestroy(){
            this.diagnosticCaptures.Cancel();
            if(this.captureRoutine!=null){StopCoroutine(this.captureRoutine);this.captureRoutine=null;}
            if(this.captureInProgress){
                if(this.captureCamera!=null){this.captureCamera.enabled=this.captureCameraWasEnabled;}
                this.captureCamera=null;
                Shader.SetGlobalFloat("_SurfaceDiagnosticMode",this.capturePreviousDiagnosticMode);
                this.captureInProgress=false;
            }
            this.renderScope?.Dispose();this.renderScope=null;
            if(this.nativeShadowProbe!=null){
                MeshRenderer renderer=this.nativeShadowProbe.GetComponent<MeshRenderer>();
                if(renderer!=null){RadialGlobalRenderSystem.SetWarpExcluded(renderer,false);}
                Destroy(this.nativeShadowProbe);this.nativeShadowProbe=null;
            }
            if(this.captureTarget!=null){this.captureTarget.Release();Destroy(this.captureTarget);}
        }
    }
}
