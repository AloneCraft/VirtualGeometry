using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor.Tests{
    /// <summary>
    /// SpaceElevator.unity とその専用パイプラインが、VG で描ける設定のまま参照画像の構図・照明になっていることを確かめる。
    /// 資産と scene は書き換えない(scene は追加で開いて閉じるだけ)。
    /// </summary>
    public sealed class SpaceElevatorSceneBuilderTests{
        [Test]
        public void EnsurePipeline(){
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(SpaceElevatorSceneBuilder.PipelinePath);
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(SpaceElevatorSceneBuilder.RendererPath);
            {
                //VG の専用描画に必要な設定(VG Renderer Feature・Forward・Depth Priming なし・MSAA なし)が揃っている
                Assert.That(VirtualGeometryRendererSetup.RequiredChanges(renderer,pipeline),Is.Empty);
            }
            {
                //VG 以外の RendererFeature が無い(互換を宣言しない Feature があると VG が Proxy に落ちる)
                foreach(ScriptableRendererFeature feature in renderer.rendererFeatures){
                    Assert.That(feature,Is.InstanceOf<VirtualGeometryRenderFeature>());
                }
            }
            {
                //発光とブルームのための HDR・ポストプロセス、300 m の塔の影が届く 2 km・4 カスケードの影
                Assert.That(pipeline.supportsHDR,Is.True);
                Assert.That(renderer.postProcessData,Is.Not.Null);
                Assert.That(pipeline.shadowDistance,Is.EqualTo(2000f));
                Assert.That(pipeline.shadowCascadeCount,Is.EqualTo(4));
                Assert.That(pipeline.supportsMainLightShadows,Is.True);
            }
        }

        [Test]
        public void BuildScene(){
            Scene previous=SceneManager.GetActiveScene();
            Scene scene=EditorSceneManager.OpenScene(SpaceElevatorSceneBuilder.ScenePath,OpenSceneMode.Additive);
            try{
                SceneManager.SetActiveScene(scene);
                {
                    //Main Camera は参照画像と同じ構図(Blender の SE_GA_Camera: 24 mm、地上 3.4 m、塔から 430 m)で、平面反射を持つ
                    Camera camera=Find<Camera>(scene,"Main Camera");
                    Assert.That(camera.usePhysicalProperties,Is.True);
                    Assert.That(camera.focalLength,Is.EqualTo(24f).Within(1e-4f));
                    Assert.That(Vector3.Distance(camera.transform.position,new Vector3(0f,3.4f,430f)),Is.LessThan(1e-3f));
                    Assert.That(Vector3.Angle(camera.transform.forward,Vector3.back),Is.LessThan(0.01f));
                    Assert.That(camera.GetComponent<SpaceElevatorPlanarReflection>(),Is.Not.Null);
                    Assert.That(camera.GetUniversalAdditionalCameraData().renderPostProcessing,Is.True);
                }
                {
                    //太陽は Directional で、Blender の強さ 9 を URP の単位(1/π)へ直した値
                    Light sun=RenderSettings.sun;
                    Assert.That(sun,Is.Not.Null);
                    Assert.That(sun.type,Is.EqualTo(LightType.Directional));
                    Assert.That(sun.intensity,Is.EqualTo(SpaceElevatorSceneBuilder.SunIntensity).Within(1e-4f));
                }
                {
                    //空は skybox から環境光と反射を取り、環境光は焼いてある(VG は ambientProbe と既定の反射だけを使う)
                    Assert.That(RenderSettings.skybox,Is.Not.Null);
                    Assert.That(RenderSettings.ambientMode,Is.EqualTo(AmbientMode.Skybox));
                    Assert.That(Lightmapping.lightingDataAsset,Is.Not.Null);
                }
                {
                    //FBX は原点・無回転(座標規約 SpaceElevatorPatternSpace.BlenderToWorld の前提)
                    GameObject model=null;
                    foreach(GameObject root in scene.GetRootGameObjects()){
                        if(PrefabUtility.GetCorrespondingObjectFromSource(root) is GameObject source&&AssetDatabase.GetAssetPath(source)==SpaceElevatorPatternSpace.ModelPath){model=root;}
                    }
                    Assert.That(model,Is.Not.Null);
                    Assert.That(model.transform.position,Is.EqualTo(Vector3.zero));
                    Assert.That(model.transform.rotation,Is.EqualTo(Quaternion.identity));
                }
            }finally{
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene,true);
            }
        }

        private static T Find<T>(Scene scene,string name) where T:Component{
            foreach(GameObject root in scene.GetRootGameObjects()){
                if(root.name==name&&root.GetComponent<T>() is T found){return found;}
            }
            Assert.Fail(name+" が scene にありません。");
            return null;
        }
    }
}
