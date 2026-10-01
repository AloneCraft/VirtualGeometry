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

namespace VirtualizedGeometry.Editor{
    /// <summary>M4のunique archive超過、実async I/O、frame転送、fallbackと深度被覆を検証する。</summary>
    public static class M4Validation{
        private const string Root="Assets/VirtualizedGeometrySample/M4";
        private const int Width=320;
        private const int Height=240;
        private static Camera camera;
        private static VirtualGeometryRenderer streaming;
        private static VirtualGeometryRenderer reference;
        private static VirtualGeometryMeshAsset asset;
        private static VirtualGeometryStreamMetadata metadata;
        private static VirtualGeometryStreamingSettings settings;
        private static GraphicsBuffer depthProbe;
        private static float[] referenceDepth;
        private static Texture2D referenceImage;
        private static int phase;
        private static int faultStep;
        private static string faultPath;
        private static VirtualGeometryMeshAsset faultAsset;
        private static VirtualGeometryStreamMetadata faultMetadata;private static VirtualGeometryPage faultPage;private static int faultPinnedPages;
        private static VirtualGeometryStreamingStatistics cancelledStatistics;
        private static int step;
        private static int bootstrap;
        private static double started;
        private static long qualityMissFrames;
        private static long peakResident;
        private static uint coarseIndices;private static int slotCount;
        // 左右の視点で見える範囲がほとんど重ならない距離。移動後は反対側だけで使っていたpageが使われなくなり、退避と戻ったときの再要求が起きる。
        private const float Side=10;
        private static uint peakIndices;
        private static readonly List<string> checks=new List<string>();
        private static readonly StringBuilder metrics=new StringBuilder();
        private static readonly StringBuilder faultMetrics=new StringBuilder();
        private static VirtualGeometryStreamingSettings faultSettings;
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            Directory.CreateDirectory("TestResults/M4");checks.Clear();metrics.Clear();faultMetrics.Clear();errors.Clear();
            metrics.AppendLine("frame,step,rootReady,gpuAllocated,gpuResident,cpuCache,ioBytes,queued,loading,uploading,resident,failed,transfer,requests,rerequests,dedup,misses,overflow,evictions,qualityMiss,completed,retire,reuseRetire,reuseCompleted,generation,indices,missingPixels,maxDepthDelta,coveredPixels,silhouetteMismatch,depthRms,colorChangedPixels");
            faultMetrics.AppendLine("phase,step,frame,gpuAllocated,gpuBudget,cpuCache,cpuBudget,loading,ioLimit,transfer,transferLimit,resident,failed,missingPixels,depthDelta,qualityMiss,unsafeReuse");
            started=EditorApplication.timeSinceStartup;phase=0;step=-1;bootstrap=0;qualityMissFrames=0;peakResident=0;coarseIndices=0;peakIndices=0;
            Application.logMessageReceived+=RecordError;
            try{CreateSample();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }

        private static void RecordError(string message,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert||type==LogType.Warning){errors.Add(message);}
        }

        private static void CreateSample(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            Mesh generated=CreateSurface(96,64);
            asset=VirtualGeometryValidationModel.Import(generated,Root+"/Source.obj",new VirtualGeometryModelSettings{
                MaxTrianglesPerCluster=32,HierarchyFanout=4,SimplificationRatio=0.5f,PositionQuantizationBits=16
            });
            Object.DestroyImmediate(generated);
            using(var file=File.OpenRead(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath))){metadata=VirtualGeometryStreamCodec.ReadMetadata(file);}
            // 固定pageがProxyの段まで届き(準備直後から縁を固定した段で描く)、その下の2段を細分化でき、3段目は細分化に要る分の半分(片側ぶん)だけ置ける予算。
            // 1 nodeの細分化には全ての子pageが要るため、1段分の空きでは途中まで読んだ子が残って細分化が止まる。pageは大きさに応じて複数slotを使うため、
            // 細分化に要る量はslot数(子のpageのslot数の和)で数える。unique geometryより十分小さく、3段目は片側しか置けないため、視点を移すと退避と再要求が起きる。
            int reserve=VirtualGeometryStreamingController.RefinementReserveSlots(metadata);
            int slots=(int)Math.Min(VirtualGeometryValidationModel.SlotsToPinProxyLevel(metadata)+reserve+reserve/2,(metadata.UniqueGeometryBytes-1)/metadata.SlotGeometryBytes);
            if(slots<3){throw new InvalidOperationException("Validation archive must allow at least 3 slots below its unique geometry bytes.");}
            settings=new VirtualGeometryStreamingSettings{
                GpuGeometryBudgetBytes=(long)slots*metadata.SlotGeometryBytes,CpuCacheBudgetBytes=(long)metadata.MaxPageBytes*4,
                IoConcurrency=2,TransferBytesPerFrame=2048,RequestQueueCapacity=1,RequestTimeoutFrames=240,IoDelayMilliseconds=25
            };
            slotCount=VirtualGeometryStreamingController.SlotCountFor(metadata,settings);
            checks.Add("archive pages="+metadata.Pages.Length+", unique geometry="+metadata.UniqueGeometryBytes+", metadata="+metadata.MetadataBytes+
                ", root bytes="+metadata.Pages[0].ByteCount+", slot bytes="+metadata.SlotGeometryBytes+", slots="+slots+", gpu budget="+settings.GpuGeometryBudgetBytes);
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M4Renderer.asset");
            if(renderer==null){renderer=ScriptableObject.CreateInstance<UniversalRendererData>();AssetDatabase.CreateAsset(renderer,Root+"/M4Renderer.asset");}
            renderer.renderingMode=RenderingMode.Forward;renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
            VirtualGeometryRenderFeature feature=null;VirtualGeometryDepthProbeFeature probe=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){
                if(existing is VirtualGeometryRenderFeature vg){feature=vg;}
                if(existing is VirtualGeometryDepthProbeFeature found){probe=found;}
            }
            if(feature==null){feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);}
            if(probe==null){probe=ScriptableObject.CreateInstance<VirtualGeometryDepthProbeFeature>();renderer.rendererFeatures.Add(probe);AssetDatabase.AddObjectToAsset(probe,renderer);}
            var serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();probe.Create();
            EditorUtility.SetDirty(renderer);EditorUtility.SetDirty(feature);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M4Pipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/M4Pipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("M4 Camera").AddComponent<Camera>();camera.nearClipPlane=0.1f;camera.farClipPlane=200;camera.fieldOfView=55;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowMSAA=false;camera.allowHDR=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;camera.transform.position=new Vector3(-Side,0,-10);
            streaming=new GameObject("Streaming Geometry").AddComponent<VirtualGeometryRenderer>();streaming.gameObject.layer=11;streaming.SetMeshAsset(asset);
            // sceneは利用者が開いて距離でLODが切り替わる既定の設定(閾値・streaming予算)で保存する。
            // 検証用の厳しい閾値と予算、検証中だけ使う比較用objectは、保存の後に適用・作成する。
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M4.unity");AssetDatabase.SaveAssets();
            streaming.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,settings);streaming.SetLodSettings(0.2f,0);streaming.SetCullingSettings(true,true);
            reference=new GameObject("All Resident Reference").AddComponent<VirtualGeometryRenderer>();reference.gameObject.layer=10;
            reference.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,settings);
            reference.SetLodSettings(float.MaxValue,0);reference.SetCullingSettings(false,false);
            depthProbe=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Width*Height,4);
            VirtualGeometryDepthProbeFeature.Target=depthProbe;VirtualGeometryDepthProbeFeature.Width=Width;VirtualGeometryDepthProbeFeature.Height=Height;
        }

        private static void Tick(){
            try{
                if(phase>0){TickFault();return;}
                if(EditorApplication.timeSinceStartup-started>240){throw new TimeoutException("M4 validation timeout.");}
                camera.cullingMask=1<<11;
                Texture2D image=M0Validation.Capture(camera,Width,Height);float[] depth=new float[Width*Height];depthProbe.GetData(depth);
                if(!streaming.IsStreamingRootReady||(step<0&&!BootstrapDrawn())){
                    Object.DestroyImmediate(image);bootstrap++;if(bootstrap>600){throw new InvalidOperationException("Root did not finish asynchronous bootstrap.");}
                    return;
                }
                VirtualGeometryGpuSelectionSnapshot selected=streaming.ReadbackSelectionForDiagnostics();
                VirtualGeometryStreamingStatistics stats=streaming.StreamingStatistics;
                if(step<0){
                    coarseIndices=selected.Arguments[0];
                    CaptureCutReference("CoarseReference");
                    CompareDepth(depth,referenceDepth,out int initialMissing,out float initialDelta);
                    if(initialMissing!=0||initialDelta>1e-6f){throw new InvalidOperationException("Initial streamed cut differs from the CPU cut over pinned pages: missing="+initialMissing+", depth delta="+initialDelta);}
                    File.WriteAllBytes("TestResults/M4/InitialCoarse.png",image.EncodeToPNG());
                    checks.Add("initial root: indices="+coarseIndices+", missing="+initialMissing+", depth delta="+initialDelta+", bootstrap frames="+bootstrap);
                    CaptureReference(0,"AllResidentLeft");step=0;
                }
                CompareDepth(depth,referenceDepth,out int missing,out float maxDelta);
                CompareCoverage(depth,referenceDepth,out int covered,out int silhouette,out float rms);
                int colorChanged=CompareColor(image,referenceImage);
                if(missing!=0||silhouette!=0||covered==0||covered==Width*Height){throw new InvalidOperationException("Streaming depth/silhouette comparison failed at step "+step+": missing="+missing+", silhouette="+silhouette+", covered="+covered);}
                if(reference.DiagnosticGeometryAllocatedBytes!=0||stats.GpuAllocatedBytes!=streaming.DiagnosticGeometryAllocatedBytes||stats.GpuAllocatedBytes>settings.GpuGeometryBudgetBytes||
                    stats.CpuBytes>settings.CpuCacheBudgetBytes||stats.Loading>settings.IoConcurrency||stats.TransferBytesThisFrame>settings.TransferBytesPerFrame||
                    stats.UnsafeSlotReuses!=0||stats.LastReuseCompletedSubmission<stats.LastReuseRetirementSubmission){
                    throw new InvalidOperationException("Streaming budget or GPU lifetime invariant failed.");
                }
                peakResident=Math.Max(peakResident,stats.Resident);peakIndices=Math.Max(peakIndices,selected.Arguments[0]);
                if(stats.QualityTargetMisses>0){qualityMissFrames++;}
                metrics.Append(Time.frameCount).Append(',').Append(step).Append(',').Append(1).Append(',').Append(stats.GpuAllocatedBytes).Append(',').Append(stats.GpuResidentBytes)
                    .Append(',').Append(stats.CpuBytes).Append(',').Append(stats.IoBytes).Append(',').Append(stats.Queued).Append(',').Append(stats.Loading)
                    .Append(',').Append(stats.Uploading).Append(',').Append(stats.Resident).Append(',').Append(stats.Failed).Append(',').Append(stats.TransferBytesThisFrame)
                    .Append(',').Append(stats.Requests).Append(',').Append(stats.ReRequests).Append(',').Append(stats.Deduplicated).Append(',').Append(stats.Misses).Append(',').Append(stats.QueueOverflow)
                    .Append(',').Append(stats.Evictions).Append(',').Append(stats.QualityTargetMisses).Append(',').Append(stats.CompletedSubmission)
                    .Append(',').Append(stats.LastRetirementSubmission).Append(',').Append(stats.LastReuseRetirementSubmission).Append(',').Append(stats.LastReuseCompletedSubmission)
                    .Append(',').Append(stats.LastReuseGeneration).Append(',').Append(selected.Arguments[0]).Append(',').Append(missing).Append(',').Append(maxDelta.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(covered).Append(',').Append(silhouette).Append(',').Append(rms.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(colorChanged).AppendLine();
                if(step==70||step==150||step==239){File.WriteAllBytes("TestResults/M4/Streaming"+step+".png",image.EncodeToPNG());}
                Object.DestroyImmediate(image);
                step++;
                if(step==80){camera.transform.position=new Vector3(Side,0,-10);CaptureReference(0,"AllResidentRight");}
                if(step==160){camera.transform.position=new Vector3(-Side,0,-10);CaptureReference(0,"AllResidentReturn");}
                if(step<240){return;}
                File.WriteAllText("TestResults/M4/Metrics.csv",metrics.ToString());
                checks.Add("streaming peak resident pages="+peakResident+", coarse indices="+coarseIndices+", peak indices="+peakIndices+
                    ", io bytes="+stats.IoBytes+", quality miss frames="+qualityMissFrames+", overflow="+stats.QueueOverflow+", evictions="+stats.Evictions+", re-requests="+stats.ReRequests);
                checks.Add("peaks GPU resident="+stats.PeakGpuResidentBytes+", CPU cache="+stats.PeakCpuBytes+", transfer/frame="+stats.PeakTransferBytesPerFrame+
                    ", I/O concurrency="+stats.PeakIoConcurrency+", unsafe slot reuse="+stats.UnsafeSlotReuses);
                if(peakResident<=1||peakIndices<=coarseIndices||stats.IoBytes<=metadata.Pages[0].ByteCount||qualityMissFrames==0||
                    stats.QueueOverflow==0||stats.Evictions==0||stats.ReRequests==0){throw new InvalidOperationException("M4 validation did not exercise required streaming transitions.");}
                if(errors.Count>0){throw new InvalidOperationException("Unity logged errors: "+string.Join(" | ",errors));}
                File.WriteAllText("TestResults/M4/Environment.txt","UTC="+DateTime.UtcNow.ToString("O")+"\nUnity="+Application.unityVersion+
                    "\nGPU="+SystemInfo.graphicsDeviceName+"\nGraphics="+SystemInfo.graphicsDeviceVersion+"\nAPI="+SystemInfo.graphicsDeviceType+
                    "\nReversedZ="+SystemInfo.usesReversedZBuffer+"\nResolution="+Width+"x"+Height+"\nVSync="+QualitySettings.vSyncCount+
                    "\nGpuBudget="+settings.GpuGeometryBudgetBytes+"\nCpuBudget="+settings.CpuCacheBudgetBytes+"\nIoConcurrency="+settings.IoConcurrency+
                    "\nTransferBytesPerFrame="+settings.TransferBytesPerFrame+"\nGpuMetadataAndAuxBytes="+stats.GpuMetadataBytes+"\nCpuMetadataSerializedBytes="+stats.CpuMetadataBytes+"\nReferenceGpuBytesDuringStreaming="+reference.DiagnosticGeometryAllocatedBytes+"\nRequestCapacity="+settings.RequestQueueCapacity+"\nDelayMs="+settings.IoDelayMilliseconds+"\n");
                BeginFaultScenario(1);
            }catch(Exception error){Finish(error);}
        }

        private static void BeginFaultScenario(int nextPhase){
            phase=nextPhase;faultStep=-1;bootstrap=0;streaming.SetMeshAsset(null);
            if(faultAsset!=null){Object.DestroyImmediate(faultAsset);}
            string relative="VirtualGeometry/M4Validation/Fault"+phase+".vgpage";
            faultPath=VirtualGeometryPagePaths.Resolve(relative);Directory.CreateDirectory(Path.GetDirectoryName(faultPath));
            if(phase==4){File.WriteAllBytes(faultPath,VirtualGeometryStreamCodec.Write(CreateBudgetPage()));}
            else{File.Copy(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath),faultPath,true);}
            using(var file=File.OpenRead(faultPath)){faultMetadata=VirtualGeometryStreamCodec.ReadMetadata(file);}
            // 障害を入れる前の内容。障害後に常駐範囲で選ぶ形の基準を作るのに使う。
            faultPage=VirtualGeometryPageCodec.Read(File.ReadAllBytes(faultPath));
            faultAsset=Object.Instantiate(asset);faultAsset.name="M4 Fault Scenario "+phase;
            var serialized=new SerializedObject(faultAsset);
            serialized.FindProperty("pageRelativePath").stringValue=relative;serialized.FindProperty("bounds").boundsValue=faultMetadata.Bounds;
            serialized.FindProperty("streamingPageCount").intValue=faultMetadata.Pages.Length;
            serialized.FindProperty("metadataBytes").intValue=faultMetadata.MetadataBytes;serialized.FindProperty("geometryBytes").longValue=faultMetadata.UniqueGeometryBytes;
            serialized.FindProperty("slotGeometryBytes").intValue=faultMetadata.SlotGeometryBytes;serialized.ApplyModifiedPropertiesWithoutUndo();
            faultSettings=settings.Clone();faultSettings.IoDelayMilliseconds=100;
            if(phase==3){faultSettings.IoDelayMilliseconds=500;}
            if(phase==4){
                faultSettings.GpuGeometryBudgetBytes=(long)faultMetadata.MaxPageBytes*3;
                faultSettings.CpuCacheBudgetBytes=2L*faultMetadata.Pages[0].ByteCount;
                faultSettings.TransferBytesPerFrame=64;
            }
            streaming.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,faultSettings);
            streaming.SetMeshAsset(faultAsset);streaming.SetCullingSettings(false,false);streaming.SetLodSettings(float.MaxValue,0);
            reference.SetLodSettings(float.MaxValue,0);
            camera.transform.SetPositionAndRotation(new Vector3(-6,0,0),Quaternion.identity);
            CaptureReference(float.MaxValue,"Fault"+phase+"CoarseReference");
        }

        // 撮影したframeをVGが描いたか。描画判定はcameraの描画開始時に行い、rootはその後の同じframe内でGPUへ公開されるため、
        // 撮影後のIsStreamingRootReadyだけでは、代替表示のまま撮ったframeを起動完了と取り違える。
        private static bool BootstrapDrawn(){
            return streaming.IsStreamingRootReady&&!streaming.IsFallbackVisible;
        }

        private static void TickFault(){
            if(EditorApplication.timeSinceStartup-started>240){throw new TimeoutException("M4 fault validation timeout.");}
            if(phase==3&&faultStep>=1000){
                // Disposeが返った後にも数回描画し、遅れて届くGPU callbackを確認する。
                camera.cullingMask=1<<10;Texture2D final=M0Validation.Capture(camera,Width,Height);Object.DestroyImmediate(final);
                faultStep++;
                if(faultStep<1004){return;}
                if(cancelledStatistics.Cancellations==0||cancelledStatistics.Loading!=0||cancelledStatistics.CpuBytes!=0){throw new InvalidOperationException("Dispose did not cancel pending I/O.");}
                checks.Add("shutdown cancellation="+cancelledStatistics.Cancellations+", loading="+cancelledStatistics.Loading+", CPU bytes="+cancelledStatistics.CpuBytes+", delayed callbacks: PASS");
                BeginFaultScenario(4);return;
            }
            camera.cullingMask=1<<11;
            Texture2D image=M0Validation.Capture(camera,Width,Height);float[] depth=new float[Width*Height];depthProbe.GetData(depth);
            if(!streaming.IsStreamingRootReady||(faultStep<0&&!BootstrapDrawn())){
                Object.DestroyImmediate(image);if(++bootstrap>600){throw new InvalidOperationException("Fault scenario root bootstrap failed.");}return;
            }
            var stats=streaming.StreamingStatistics;
            if(faultStep<0){
                if(phase==1){using(var file=new FileStream(faultPath,FileMode.Open,FileAccess.Write,FileShare.Read)){file.SetLength(faultMetadata.Pages[0].Offset+faultMetadata.Pages[0].ByteCount);}}
                if(phase==2){
                    using(var file=new FileStream(faultPath,FileMode.Open,FileAccess.ReadWrite,FileShare.Read)){
                        for(int page=1;page<faultMetadata.Pages.Length;page++){
                            file.Position=faultMetadata.Pages[page].Offset;int value=file.ReadByte();file.Position--;file.WriteByte((byte)(value^0x40));
                        }
                    }
                }
            }
            CompareDepth(depth,referenceDepth,out int missing,out float delta);
            if(missing!=0||delta>1e-6f){
                File.WriteAllBytes("TestResults/M4/Fault"+phase+"Mismatch.png",image.EncodeToPNG());
                throw new InvalidOperationException("Fault "+phase+" did not preserve exact coarse depth: missing="+missing+", depth delta="+delta+", step="+faultStep+
                    ", fallback="+streaming.IsFallbackVisible+", reason="+streaming.DiagnosticReason+", ordinary forceOff="+streaming.Ordinary.forceRenderingOff+", resident="+stats.Resident+", failed="+stats.Failed);
            }
            if(faultStep<0){
                // 障害後は詳細pageが読めず、起動時に固定したpageだけが残る。詳細を要求させ、その常駐範囲で選ぶ形が保たれることを以後のframeで確かめる。
                streaming.SetLodSettings(0.2f,0);faultStep=0;
                int slots=VirtualGeometryStreamingController.SlotCountFor(faultMetadata,faultSettings);
                CaptureCutReference("Fault"+phase+"PinnedCutReference",faultPage,faultMetadata,slots,faultAsset.ProxyLitMaterial);
                faultPinnedPages=0;foreach(bool fixedPage in VirtualGeometryStreamingController.SelectPinnedPages(faultMetadata,slots)){if(fixedPage){faultPinnedPages++;}}
            }
            if(stats.UnsafeSlotReuses!=0||stats.GpuAllocatedBytes>faultSettings.GpuGeometryBudgetBytes||stats.CpuBytes>faultSettings.CpuCacheBudgetBytes||
                stats.Loading>faultSettings.IoConcurrency||stats.TransferBytesThisFrame>faultSettings.TransferBytesPerFrame){throw new InvalidOperationException("Fault scenario budget/lifetime invariant failed.");}
            faultMetrics.Append(phase).Append(',').Append(faultStep).Append(',').Append(Time.frameCount).Append(',').Append(stats.GpuAllocatedBytes).Append(',').Append(faultSettings.GpuGeometryBudgetBytes)
                .Append(',').Append(stats.CpuBytes).Append(',').Append(faultSettings.CpuCacheBudgetBytes).Append(',').Append(stats.Loading).Append(',').Append(faultSettings.IoConcurrency)
                .Append(',').Append(stats.TransferBytesThisFrame).Append(',').Append(faultSettings.TransferBytesPerFrame).Append(',').Append(stats.Resident).Append(',').Append(stats.Failed)
                .Append(',').Append(missing).Append(',').Append(delta.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(stats.QualityTargetMisses).Append(',').Append(stats.UnsafeSlotReuses).AppendLine();
            if(phase==3&&stats.Loading>0){
                cancelledStatistics=stats;streaming.SetMeshAsset(null);faultStep=1000;Object.DestroyImmediate(image);return;
            }
            faultStep++;
            if((phase==1||phase==2||phase==4)&&stats.Failed>0&&faultStep>15){
                File.WriteAllBytes("TestResults/M4/Fault"+phase+"Coarse.png",image.EncodeToPNG());
                checks.Add("fault "+phase+": failed="+stats.Failed+", resident="+stats.Resident+", missing pixels="+missing+
                    ", coarse depth delta="+delta+", quality misses="+stats.QualityTargetMisses+", last error="+stats.LastError);
                // 詳細pageは読めず、起動時に固定したpageだけが残る。
                if(stats.Resident!=faultPinnedPages||stats.QualityTargetMisses==0){throw new InvalidOperationException("Fault did not retain the pinned pages and report quality miss: resident="+stats.Resident+", pinned="+faultPinnedPages);}
                Object.DestroyImmediate(image);
                if(phase<4){BeginFaultScenario(phase+1);return;}
                if(errors.Count>0){throw new InvalidOperationException("Unity logged errors: "+string.Join(" | ",errors));}
                Finish(null);return;
            }
            Object.DestroyImmediate(image);
            if(faultStep>200){throw new InvalidOperationException("Fault "+phase+" did not reach expected failure/cancel state.");}
        }

        private static VirtualGeometryPage CreateBudgetPage(){
            var vertices=new List<VirtualGeometryPackedVertex>();var indices=new List<uint>();var clusters=new List<VirtualGeometryCluster>();
            var nodes=new VirtualGeometryHierarchyNode[3];
            for(int node=0;node<3;node++){
                int grid=32;float minX=-16;float maxX=16;
                if(node==0){grid=1;}else if(node==1){maxX=0;}else{minX=0;}
                int vertexStart=vertices.Count;uint firstCluster=(uint)clusters.Count;
                for(int y=0;y<=grid;y++){
                    for(int x=0;x<=grid;x++){
                        vertices.Add(new VirtualGeometryPackedVertex(new Vector3(Mathf.Lerp(minX,maxX,(float)x/grid),Mathf.Lerp(-9,9,(float)y/grid),10),Vector3.back,new Vector2((float)x/grid,(float)y/grid)));
                    }
                }
                int firstIndex=indices.Count;
                for(int y=0;y<grid;y++){for(int x=0;x<grid;x++){
                    uint a=(uint)(vertexStart+y*(grid+1)+x);uint b=a+1;uint c=a+(uint)grid+1;uint d=c+1;
                    indices.Add(a);indices.Add(c);indices.Add(b);indices.Add(b);indices.Add(c);indices.Add(d);
                }}
                for(int start=firstIndex;start<indices.Count;start+=96){
                    int count=Math.Min(96,indices.Count-start);Bounds bounds=new Bounds(vertices[(int)indices[start]].Position,Vector3.zero);
                    for(int i=1;i<count;i++){bounds.Encapsulate(vertices[(int)indices[start+i]].Position);}
                    clusters.Add(new VirtualGeometryCluster{BoundsCenter=bounds.center,BoundsRadius=bounds.extents.magnitude,FirstIndex=(uint)start,IndexCount=(uint)count,MaterialSlot=0});
                }
                Bounds nodeBounds=new Bounds(new Vector3((minX+maxX)/2,0,10),new Vector3(maxX-minX,18,0));
                nodes[node]=new VirtualGeometryHierarchyNode{BoundsCenter=nodeBounds.center,BoundsRadius=nodeBounds.extents.magnitude,FirstCluster=firstCluster,
                    ClusterCount=(uint)clusters.Count-firstCluster,LeafCount=1,LeafStart=(uint)Math.Max(0,node-1)};
            }
            nodes[0].FirstChild=1;nodes[0].ChildCount=2;nodes[0].LeafCount=2;nodes[0].Level=1;nodes[0].GeometricError=1;
            return new VirtualGeometryPage(vertices.ToArray(),indices.ToArray(),clusters.ToArray(),nodes,new uint[]{0},1,new Bounds(new Vector3(0,0,10),new Vector3(32,18,0)));
        }
        // 準備直後は起動時に固定したpageだけが常駐する。その常駐範囲でCPU選択した形を通常描画し、GPUの最初の描画と比べる。
        private static void CaptureCutReference(string name){
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath)));
            CaptureCutReference(name,page,metadata,slotCount,asset.ProxyLitMaterial);
        }

        private static void CaptureCutReference(string name,VirtualGeometryPage page,VirtualGeometryStreamMetadata archive,int slots,Material material){
            bool[] pinned=VirtualGeometryStreamingController.SelectPinnedPages(archive,slots);
            var context=new VirtualGeometryLodSelectionContext(streaming.transform.localToWorldMatrix,camera.transform.position,camera.orthographic,camera.fieldOfView,camera.orthographicSize,Height,camera.nearClipPlane,streaming.LodErrorThresholdPixels,0);
            Mesh cut=VirtualGeometryValidationModel.CutMesh(page,VirtualGeometryLodSelector.Select(page,context,id=>pinned[(int)id],null));
            var proxy=new GameObject("Pinned cut reference"){layer=10};proxy.transform.SetPositionAndRotation(streaming.transform.position,streaming.transform.rotation);proxy.transform.localScale=streaming.transform.lossyScale;
            proxy.AddComponent<MeshFilter>().sharedMesh=cut;proxy.AddComponent<MeshRenderer>().sharedMaterial=material;
            camera.cullingMask=1<<10;
            if(referenceImage!=null){Object.DestroyImmediate(referenceImage);}
            referenceImage=M0Validation.Capture(camera,Width,Height);referenceDepth=new float[Width*Height];depthProbe.GetData(referenceDepth);
            File.WriteAllBytes("TestResults/M4/"+name+".png",referenceImage.EncodeToPNG());Object.DestroyImmediate(proxy);Object.DestroyImmediate(cut);camera.cullingMask=1<<11;
        }

        private static void CaptureReference(float threshold,string name){
            reference.SetMeshAsset(phase>0?faultAsset:asset);reference.SetLodSettings(threshold,0);camera.cullingMask=1<<10;
            if(referenceImage!=null){Object.DestroyImmediate(referenceImage);}
            referenceImage=M0Validation.Capture(camera,Width,Height);referenceDepth=new float[Width*Height];depthProbe.GetData(referenceDepth);
            File.WriteAllBytes("TestResults/M4/"+name+".png",referenceImage.EncodeToPNG());reference.SetMeshAsset(null);camera.cullingMask=1<<11;
        }

        private static void CompareDepth(float[] actual,float[] expected,out int missing,out float maxDelta){
            missing=0;maxDelta=0;float background=1;if(SystemInfo.usesReversedZBuffer){background=0;}
            for(int i=0;i<actual.Length;i++){
                bool source=Mathf.Abs(expected[i]-background)>1e-7f;bool target=Mathf.Abs(actual[i]-background)>1e-7f;
                if(source&&!target){missing++;}
                if(source&&target){maxDelta=Mathf.Max(maxDelta,Mathf.Abs(EyeDepth(actual[i])-EyeDepth(expected[i])));}
            }
        }

        private static void CompareCoverage(float[] actual,float[] expected,out int covered,out int silhouette,out float rms){
            covered=0;silhouette=0;double squares=0;float background=SystemInfo.usesReversedZBuffer?0:1;
            for(int i=0;i<actual.Length;i++){
                bool a=Mathf.Abs(actual[i]-background)>1e-7f;bool e=Mathf.Abs(expected[i]-background)>1e-7f;
                if(a!=e){silhouette++;}
                if(a&&e){covered++;float delta=EyeDepth(actual[i])-EyeDepth(expected[i]);squares+=delta*delta;}
            }
            rms=covered>0?(float)Math.Sqrt(squares/covered):0;
        }

        private static int CompareColor(Texture2D actual,Texture2D expected){
            Color32[] a=actual.GetPixels32();Color32[] e=expected.GetPixels32();int changed=0;
            for(int i=0;i<a.Length;i++){if(a[i].r!=e[i].r||a[i].g!=e[i].g||a[i].b!=e[i].b){changed++;}}
            return changed;
        }

        private static float EyeDepth(float raw){
            float near=camera.nearClipPlane;float far=camera.farClipPlane;
            if(SystemInfo.usesReversedZBuffer){return near*far/(near+raw*(far-near));}
            return near*far/(far-raw*(far-near));
        }

        private static Mesh CreateSurface(int width,int height){
            var vertices=new Vector3[(width+1)*(height+1)];var normals=new Vector3[vertices.Length];var uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int i=y*(width+1)+x;float px=(float)x/width*32-16;float py=(float)y/height*18-9;
                    vertices[i]=new Vector3(px,py,10+0.35f*Mathf.Sin(px*0.5f)*Mathf.Cos(py*0.35f));normals[i]=Vector3.back;uv[i]=new Vector2((float)x/width,(float)y/height);
                }
            }
            var indices=new int[width*height*6];int write=0;
            for(int y=0;y<height;y++){for(int x=0;x<width;x++){int a=y*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;indices[write++]=a;indices[write++]=c;indices[write++]=b;indices[write++]=b;indices[write++]=c;indices[write++]=d;}}
            var mesh=new Mesh{name="M4 Unique Surface",indexFormat=IndexFormat.UInt32};mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=RecordError;
            streaming?.SetMeshAsset(null);reference?.SetMeshAsset(null);
            VirtualGeometryDepthProbeFeature.Target=null;depthProbe?.Dispose();depthProbe=null;
            if(referenceImage!=null){Object.DestroyImmediate(referenceImage);referenceImage=null;}
            File.WriteAllText("TestResults/M4/Metrics.csv",metrics.ToString());
            File.WriteAllText("TestResults/M4/FaultMetrics.csv",faultMetrics.ToString());
            string result=string.Join(Environment.NewLine,checks)+Environment.NewLine;int exitCode=0;
            if(error!=null){result+="FAIL: "+error;Debug.LogException(error);exitCode=1;}
            else{result+="PASS"+Environment.NewLine;Debug.Log("VG M4 PASS");}
            File.WriteAllText("TestResults/M4/Results.txt",result);EditorApplication.Exit(exitCode);
        }
    }
}
