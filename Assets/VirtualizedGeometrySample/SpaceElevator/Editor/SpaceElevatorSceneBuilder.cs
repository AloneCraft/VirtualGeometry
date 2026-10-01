using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualizedGeometry.Editor;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// SpaceElevator.unity(space_elevator.fbx を参照画像「地上が、宇宙へのはじまり。」の構図・照明・色調で見る専用 scene)と、その専用 URP パイプラインを作る。
    /// メニュー:
    ///   Tools/Virtualized Geometry/Space Elevator/Use Space Elevator Pipeline … 既定の render pipeline を SpaceElevatorPipeline へ切り替える
    ///   (M0〜M5 の検証は自分のパイプラインへ切り替えるため、それらを実行した後は見る前にこれを実行する)。
    ///   Tools/Virtualized Geometry/Space Elevator/Build Scene … マテリアル更新・パイプライン切替・scene 生成をまとめて行う。
    /// batchmode: -executeMethod VirtualizedGeometry.Samples.SpaceElevator.Editor.SpaceElevatorSceneBuilder.Run
    /// </summary>
    public static class SpaceElevatorSceneBuilder{
        public const string Root=SpaceElevatorMaterialBuilder.Root;
        public const string ScenePath=Root+"/SpaceElevator.unity";
        public const string RenderingFolder=Root+"/Rendering";
        public const string RendererPath=RenderingFolder+"/SpaceElevatorRenderer.asset";
        public const string PipelinePath=RenderingFolder+"/SpaceElevatorPipeline.asset";
        public const string VolumeProfilePath=RenderingFolder+"/SpaceElevatorVolumeProfile.asset";
        public const string SkyboxPath=RenderingFolder+"/SpaceElevatorSky.mat";
        public const string LightingSettingsPath=RenderingFolder+"/SpaceElevatorLighting.lighting";
        private const string PostProcessDataPath="Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";

        /// <summary>Blender の SE_GA_Sun の進行方向(se_ground_anchor.py:1059-1066)。修正前の記録で FBX 由来の太陽と照合する。</summary>
        public static readonly Vector3 BlenderSunDirection=new Vector3(0.75f,0.45f,-0.48f);
        /// <summary>
        /// 参照画像に合わせた太陽の進行方向(Blender 座標)。Blender(左手前から、仰角約 29°)より正面寄り・低め(仰角約 20°)にして、
        /// 参照画像のように正面の壁とアバットメントを夕方の暖色で照らす。
        /// </summary>
        public static readonly Vector3 SunDirectionBlender=new Vector3(0.4f,0.85f,-0.34f);
        /// <summary>太陽の色(線形)。Blender の (1,0.9,0.78) より暖色にして参照画像の金色の光に寄せる。</summary>
        public static readonly Color SunColorLinear=new Color(1f,0.8f,0.6f);
        /// <summary>太陽の強さ。Blender の 9 W/m² を URP の単位へ(URP の拡散は 1/π を掛けないので 9/π)。</summary>
        public const float SunIntensity=9f/Mathf.PI;
        /// <summary>露出。値は Blender と同じ放射量で持ち、Volume の Post Exposure で合わせる。Blender の -2 EV では参照画像より暗いため明るくする。</summary>
        public const float PostExposure=-0.9f;
        /// <summary>空からの環境光の強さ。1 だと青い環境光が勝って参照画像より青くなるため弱める。</summary>
        public const float AmbientIntensity=0.8f;

        [MenuItem("Tools/Virtualized Geometry/Space Elevator/Use Space Elevator Pipeline")]
        public static void UsePipeline(){
            UniversalRenderPipelineAsset pipeline=EnsurePipeline();
            GraphicsSettings.defaultRenderPipeline=pipeline;
            QualitySettings.renderPipeline=pipeline;
        }

        [MenuItem("Tools/Virtualized Geometry/Space Elevator/Build Scene")]
        private static void BuildFromMenu(){
            if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            Build();
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
        }

        /// <summary>batchmode の入口。マテリアル・パイプライン・scene を作り、終了コードで結果を返す。</summary>
        public static void Run(){
            try{
                Build();
                EditorApplication.Exit(0);
            }catch(Exception error){
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>マテリアル更新 → パイプライン切替 → scene 生成と環境光の焼き込み。</summary>
        public static void Build(){
            SpaceElevatorMaterialBuilder.Apply();
            UsePipeline();
            BuildScene();
        }

        /// <summary>専用パイプライン。HDR とポストプロセスを使い、300 m の塔の影が届くよう影を 2 km・4 カスケードにする。VG が Proxy に落ちる設定(MSAA・Depth Priming・SSAO 等)は使わない。</summary>
        public static UniversalRenderPipelineAsset EnsurePipeline(){
            Directory.CreateDirectory(RenderingFolder);
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer,RendererPath);
            }
            renderer.renderingMode=RenderingMode.Forward;
            renderer.depthPrimingMode=DepthPrimingMode.Disabled;
            renderer.copyDepthMode=CopyDepthMode.AfterOpaques;
            renderer.postProcessData=AssetDatabase.LoadAssetAtPath<PostProcessData>(PostProcessDataPath);
            VirtualGeometryRendererSetup.EnsureFeature(renderer);
            EditorUtility.SetDirty(renderer);
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if(pipeline==null){
                pipeline=UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline,PipelinePath);
            }
            pipeline.supportsHDR=true;
            pipeline.msaaSampleCount=1;
            pipeline.supportsCameraDepthTexture=true;
            pipeline.colorGradingMode=ColorGradingMode.HighDynamicRange;
            pipeline.mainLightShadowmapResolution=4096;
            pipeline.shadowDistance=2000f;
            pipeline.shadowCascadeCount=4;
            pipeline.cascade4Split=new Vector3(0.025f,0.08f,0.3f);
            var fields=new SerializedObject(pipeline);
            fields.FindProperty("m_MainLightShadowsSupported").boolValue=true;
            fields.FindProperty("m_SoftShadowsSupported").boolValue=true;
            fields.FindProperty("m_SoftShadowQuality").intValue=3;
            fields.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            return pipeline;
        }

        /// <summary>SpaceElevator.unity を作り直す。FBX は原点・無回転(Unity world = Blender の (-x, z, -y))。</summary>
        public static void BuildScene(){
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            // NewScene は未使用の asset を解放するため、asset は NewScene の後に読み直す(Development.md の注記)。
            var model=AssetDatabase.LoadAssetAtPath<GameObject>(SpaceElevatorPatternSpace.ModelPath);
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);

            var sun=new GameObject("Sun").AddComponent<Light>();
            sun.type=LightType.Directional;
            sun.transform.rotation=Quaternion.LookRotation(SpaceElevatorPatternSpace.BlenderToWorld(SunDirectionBlender).normalized);
            sun.useColorTemperature=false;
            sun.color=SunColorLinear.gamma;
            sun.intensity=SunIntensity;
            sun.shadows=LightShadows.Soft;
            sun.shadowStrength=1f;
            RenderSettings.sun=sun;

            RenderSettings.skybox=EnsureSkybox();
            RenderSettings.ambientMode=AmbientMode.Skybox;
            RenderSettings.ambientIntensity=AmbientIntensity;
            RenderSettings.defaultReflectionMode=DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity=1f;
            RenderSettings.fog=false;

            var camera=new GameObject("Main Camera"){tag="MainCamera"}.AddComponent<Camera>();
            camera.gameObject.AddComponent<AudioListener>();
            SpaceElevatorCapture.Apply(camera,SpaceElevatorCapture.Hero);
            camera.clearFlags=CameraClearFlags.Skybox;
            camera.allowHDR=true;
            camera.allowMSAA=false;
            var cameraData=camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing=true;
            cameraData.antialiasing=AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality=AntialiasingQuality.High;
            cameraData.dithering=true;
            camera.gameObject.AddComponent<SpaceElevatorPlanarReflection>();
            var controller=camera.gameObject.AddComponent<VirtualGeometrySampleCameraController>();
            var controllerFields=new SerializedObject(controller);
            controllerFields.FindProperty("moveSpeed").floatValue=60f;
            controllerFields.ApplyModifiedPropertiesWithoutUndo();

            var volume=new GameObject("Global Volume").AddComponent<Volume>();
            volume.isGlobal=true;
            volume.sharedProfile=EnsureVolumeProfile();

            Lightmapping.lightingSettings=EnsureLightingSettings();
            EditorSceneManager.SaveScene(instance.scene,ScenePath);
            // 環境光(skybox の SH)と既定の反射を焼き、LightingData として scene に結び付ける。VG はこの2つだけを使う。
            if(!Lightmapping.Bake()){throw new InvalidOperationException("環境光の焼き込みに失敗しました。");}
            EditorSceneManager.SaveScene(instance.scene,ScenePath);
            AssetDatabase.SaveAssets();
        }

        // 参照画像の青空(天頂は濃く、地平線は明るく霞む)。太陽円盤は描かない(参照画像に写っていないため)。明るさは Post Exposure を見込んだ値。
        private static Material EnsureSkybox(){
            var material=AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            if(material==null){
                material=new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(material,SkyboxPath);
            }
            material.SetFloat("_SunDisk",0f);
            material.SetFloat("_AtmosphereThickness",1.1f);
            material.SetColor("_SkyTint",new Color(0.55f,0.6f,0.7f,1f));
            material.SetColor("_GroundColor",new Color(0.45f,0.42f,0.4f,1f));
            material.SetFloat("_Exposure",1.6f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static VolumeProfile EnsureVolumeProfile(){
            var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if(profile==null){
                profile=ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile,VolumeProfilePath);
            }
            var tonemapping=Component<Tonemapping>(profile);
            tonemapping.mode.Override(TonemappingMode.ACES);
            var color=Component<ColorAdjustments>(profile);
            color.postExposure.Override(PostExposure);
            color.contrast.Override(15f);
            color.saturation.Override(0f);
            // 参照画像の暖かい色調(青空は残しつつ、全体を夕方寄りに)
            var balance=Component<WhiteBalance>(profile);
            balance.temperature.Override(10f);
            var bloom=Component<Bloom>(profile);
            bloom.threshold.Override(1f);
            bloom.intensity.Override(0.5f);
            bloom.scatter.Override(0.6f);
            var split=Component<ShadowsMidtonesHighlights>(profile);
            split.shadows.Override(new Vector4(0.95f,0.98f,1.06f,0f));
            split.highlights.Override(new Vector4(1.05f,1.0f,0.94f,0f));
            var vignette=Component<Vignette>(profile);
            vignette.intensity.Override(0.15f);
            vignette.smoothness.Override(0.5f);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        private static T Component<T>(VolumeProfile profile) where T:VolumeComponent{
            if(profile.TryGet(out T existing)){return existing;}
            T created=profile.Add<T>(true);
            created.name=typeof(T).Name;
            AssetDatabase.AddObjectToAsset(created,profile);
            return created;
        }

        private static LightingSettings EnsureLightingSettings(){
            var settings=AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingSettingsPath);
            if(settings==null){
                settings=new LightingSettings{name="SpaceElevatorLighting"};
                AssetDatabase.CreateAsset(settings,LightingSettingsPath);
            }
            // static object も baked light も使わない。焼くのは環境光と既定の反射だけ。
            settings.bakedGI=false;
            settings.realtimeGI=false;
            EditorUtility.SetDirty(settings);
            return settings;
        }
    }
}
