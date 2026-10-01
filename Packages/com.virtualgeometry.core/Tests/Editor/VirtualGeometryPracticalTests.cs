using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    /// <summary>M5の共有寿命・fallback排他・material・変換取消・統計・Player収録境界を先に固定する。</summary>
    public sealed class VirtualGeometryPracticalTests{
        private sealed class Resource:IDisposable{public int Disposals;public void Dispose(){this.Disposals++;}}

        [Test]
        public void SharedLeaseDisposesExactlyOnceAndOldLeaseCannotReleaseNewGeneration(){
            using(var cache=new VirtualGeometryReferenceCache<Resource>()){
                var first=cache.Acquire("asset/settings",()=>new Resource());
                var second=cache.Acquire("asset/settings",()=>new Resource());
                Assert.That(second.Value,Is.SameAs(first.Value));Assert.That(cache.ResourceCount,Is.EqualTo(1));
                Resource old=first.Value;first.Dispose();Assert.That(old.Disposals,Is.Zero);
                cache.Reset();Assert.That(old.Disposals,Is.EqualTo(1));Assert.That(second.IsValid,Is.False);
                var next=cache.Acquire("asset/settings",()=>new Resource());second.Dispose();
                Assert.That(next.IsValid,Is.True);Assert.That(next.Value.Disposals,Is.Zero);
                Resource current=next.Value;next.Dispose();Assert.That(current.Disposals,Is.EqualTo(1));
                Assert.That(cache.ResourceCount,Is.Zero);
            }
            Assert.That(new VirtualGeometrySelectionKey(1,10),Is.Not.EqualTo(new VirtualGeometrySelectionKey(2,10)));
            Assert.That(new VirtualGeometrySelectionKey(1,10),Is.Not.EqualTo(new VirtualGeometrySelectionKey(1,20)));
        }

        [Test]
        public void ProxyBootstrapAndUnsupportedPathsAreExclusiveWithVirtualDrawing(){
            foreach(bool ready in new[]{false,true}){
                foreach(bool forced in new[]{false,true}){
                    foreach(string unsupported in new[]{null,"Compute unsupported"}){
                        VirtualGeometryRenderDecision decision=VirtualGeometryRenderDecision.Resolve(unsupported,ready,forced,false);
                        Assert.That(decision.UseProxy^decision.UseVirtual,Is.True);
                        Assert.That(decision.UseVirtual,Is.EqualTo(ready&&!forced&&unsupported==null));
                    }
                }
            }
            var rejected=VirtualGeometryRenderDecision.Resolve(null,false,false,true);
            Assert.That(rejected.UseProxy,Is.True);Assert.That(rejected.Reason,Is.Not.Empty);
        }

        [UnityTest]
        public IEnumerator SerializedRendererSettingsRebuildProxyOutsideOnValidate(){
            var source=GameObject.CreatePrimitive(PrimitiveType.Cube);var mesh=source.GetComponent<MeshFilter>().sharedMesh;
            var asset=ScriptableObject.CreateInstance<VirtualGeometryMeshAsset>();var lit=new Material(Shader.Find("Universal Render Pipeline/Lit"));var unlit=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            var normal=new Texture2D(1,1);lit.SetTexture("_BumpMap",normal);
            var metadata=new SerializedObject(asset);metadata.FindProperty("proxyMesh").objectReferenceValue=mesh;metadata.FindProperty("proxyLitMaterial").objectReferenceValue=lit;metadata.FindProperty("proxyUnlitMaterial").objectReferenceValue=unlit;metadata.FindProperty("materialCount").intValue=1;metadata.ApplyModifiedPropertiesWithoutUndo();
            var go=new GameObject("Serialized Proxy lifecycle");var renderer=go.AddComponent<VirtualGeometryRenderer>();
            var other=ScriptableObject.CreateInstance<VirtualGeometryMeshAsset>();var otherMetadata=new SerializedObject(other);
            otherMetadata.FindProperty("proxyMesh").objectReferenceValue=mesh;otherMetadata.FindProperty("proxyLitMaterial").objectReferenceValue=lit;otherMetadata.FindProperty("materialCount").intValue=1;otherMetadata.ApplyModifiedPropertiesWithoutUndo();
            try{
                renderer.SetMeshAsset(asset);
                // 部品を付けるとMeshFilter/MeshRendererが揃い、MeshFilterには代替Mesh、空きslotには変換assetのMaterialが入る
                Assert.That(go.GetComponent<MeshFilter>().sharedMesh,Is.SameAs(mesh));
                Assert.That(go.GetComponent<MeshRenderer>().sharedMaterial,Is.SameAs(lit));
                VirtualGeometryProxyOwner first=renderer.GetComponentInChildren<VirtualGeometryProxyOwner>(true);
                for(int update=0;update<3;update++){
                    // MeshRendererのMaterialとLOD閾値をInspectorと同じserialized経路で変えても、影専用の子は作り直さずMaterialだけを反映する。
                    var ordinary=new SerializedObject(go.GetComponent<MeshRenderer>());var materials=ordinary.FindProperty("m_Materials");materials.arraySize=1;
                    Material assigned=unlit;if(update%2==0){assigned=lit;}
                    materials.GetArrayElementAtIndex(0).objectReferenceValue=assigned;ordinary.ApplyModifiedPropertiesWithoutUndo();
                    var settings=new SerializedObject(renderer);settings.FindProperty("lodErrorThresholdPixels").floatValue=2+update;settings.ApplyModifiedPropertiesWithoutUndo();
                    yield return null;EditorApplication.QueuePlayerLoopUpdate();yield return null;
                    VirtualGeometryProxyOwner[] owners=renderer.GetComponentsInChildren<VirtualGeometryProxyOwner>(true);
                    Assert.That(owners.Length,Is.EqualTo(1));Assert.That(owners[0],Is.SameAs(first));
                    Material expectedShadow=assigned;if(assigned.FindPass("ShadowCaster")<0){expectedShadow=lit;}
                    Assert.That(owners[0].GetComponent<MeshRenderer>().sharedMaterial,Is.SameAs(expectedShadow));
                }
                {
                    //Mesh Assetを変えたときはProxyを作り直す
                    var settings=new SerializedObject(renderer);settings.FindProperty("meshAsset").objectReferenceValue=other;settings.ApplyModifiedPropertiesWithoutUndo();
                    yield return null;EditorApplication.QueuePlayerLoopUpdate();yield return null;
                    VirtualGeometryProxyOwner[] owners=renderer.GetComponentsInChildren<VirtualGeometryProxyOwner>(true);
                    Assert.That(owners.Length,Is.EqualTo(1));Assert.That(owners[0],Is.Not.SameAs(first));
                }
                // 部品を止めると影専用の子は消え、通常MeshRendererは止めたまま残らない
                renderer.enabled=false;Assert.That(go.GetComponentsInChildren<VirtualGeometryProxyOwner>(true),Is.Empty);
                Assert.That(go.GetComponent<MeshRenderer>().forceRenderingOff,Is.False);
                {
                    //変換データを外すと、部品が入れた代替MeshはMeshFilterから外れ、通常のMeshとして見え続けない
                    renderer.SetMeshAsset(asset);renderer.SetMeshAsset(null);
                    Assert.That(go.GetComponent<MeshFilter>().sharedMesh,Is.Null);
                }
                {
                    //利用者が後から入れたMeshは、変換データを外しても残す
                    var custom=new Mesh();
                    try{
                        renderer.SetMeshAsset(asset);go.GetComponent<MeshFilter>().sharedMesh=custom;renderer.SetMeshAsset(null);
                        Assert.That(go.GetComponent<MeshFilter>().sharedMesh,Is.SameAs(custom));
                    }finally{UnityEngine.Object.DestroyImmediate(custom);}
                }
            }finally{UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(source);UnityEngine.Object.DestroyImmediate(asset);UnityEngine.Object.DestroyImmediate(other);UnityEngine.Object.DestroyImmediate(lit);UnityEngine.Object.DestroyImmediate(unlit);UnityEngine.Object.DestroyImmediate(normal);}
        }

        [Test]
        public void DerivativeNormalFrameHandlesMirroredUvAndRejectsDegenerateUv(){
            Assert.That(VirtualGeometryNormalMapping.TryBuildFrame(Vector3.forward,Vector3.right,Vector3.up,Vector2.right,Vector2.up,out Vector3 tangent,out Vector3 bitangent),Is.True);
            Assert.That(Vector3.Dot(tangent,Vector3.right),Is.GreaterThan(0.999f));
            Assert.That(Vector3.Dot(bitangent,Vector3.up),Is.GreaterThan(0.999f));
            Assert.That(VirtualGeometryNormalMapping.TryBuildFrame(Vector3.forward,Vector3.right,Vector3.up,Vector2.left,Vector2.up,out tangent,out bitangent),Is.True);
            Assert.That(Vector3.Dot(tangent,Vector3.left),Is.GreaterThan(0.999f));
            Assert.That(VirtualGeometryNormalMapping.TryBuildFrame(Vector3.forward,Vector3.right,Vector3.up,Vector2.zero,Vector2.zero,out tangent,out bitangent),Is.False);
        }

        [Test]
        public void SettingsKeyTracksBuildSettings(){
            // 変換設定が変われば変換データの識別キーも変わる(詳細表示と再変換の判別に使う)
            var settings=new VirtualGeometryBuildSettings();
            string original=VirtualGeometryAssetBuild.SettingsKey(settings);
            settings.HierarchyFanout=8;
            Assert.That(VirtualGeometryAssetBuild.SettingsKey(settings),Is.Not.EqualTo(original));
        }

        [Test]
        public void DebugMetricsCountSharedGeometryOnceAndInstancesSeparately(){
            var rows=new[]{
                new VirtualGeometryInstanceMetrics{ResourceKey="same",InputTriangles=100,UniqueTriangles=80,DrawnTriangles=20,SelectedClusters=2,GpuGeometryBytes=500,DrawCalls=1},
                new VirtualGeometryInstanceMetrics{ResourceKey="same",InputTriangles=100,UniqueTriangles=80,DrawnTriangles=30,SelectedClusters=3,GpuGeometryBytes=500,DrawCalls=1}};
            var total=VirtualGeometryDebugMetrics.Aggregate(rows);
            Assert.That(total.UniqueTriangles,Is.EqualTo(80));Assert.That(total.InstanceTriangles,Is.EqualTo(160));
            Assert.That(total.DrawnTriangles,Is.EqualTo(50));Assert.That(total.GpuGeometryBytes,Is.EqualTo(500));
            Assert.That(total.SelectedClusters,Is.EqualTo(5));Assert.That(total.DrawCalls,Is.EqualTo(2));
        }

        [Test]
        public void PlayerArchiveInclusionDeduplicatesPathsAndExcludesEditorNativeCode(){
            var result=VirtualGeometryBuildInclusion.DistinctArchives(new[]{"VirtualGeometry/a.vgpage","VirtualGeometry/a.vgpage","VirtualGeometry/b.vgpage"});
            Assert.That(result.Length,Is.EqualTo(2));
            Assert.That(VirtualGeometryBuildInclusion.IsForbiddenPlayerPath("Plugins/x86_64/meshoptimizer.dll"),Is.True);
            Assert.That(VirtualGeometryBuildInclusion.IsForbiddenPlayerPath("Native~/meshoptimizer/src/simplifier.cpp"),Is.True);
        }

        [Test]
        public void SparseSimplificationAllocatesForReferencedVerticesAndPreservesGlobalIndices(){
            var vertices=new VirtualGeometryPackedVertex[100000];var locks=new byte[vertices.Length];
            uint[] indices={90000,90002,90001,90001,90002,90003};
            vertices[90000].Position=Vector3.zero;vertices[90001].Position=Vector3.right;vertices[90002].Position=Vector3.up;vertices[90003].Position=Vector3.one;
            foreach(uint index in indices){vertices[index].Normal=Vector3.forward;locks[index]=MeshOptimizerSimplifier.VertexLock;}
            _=MeshOptimizerSimplifier.Version;
            var result=MeshOptimizerSimplifier.Simplify(vertices,indices,locks,3);
            Assert.That(result.Indices.Length,Is.EqualTo(indices.Length));foreach(uint index in result.Indices){Assert.That(index,Is.InRange(90000u,90003u));}
            Assert.That(result.ScratchVertexCount,Is.EqualTo(4),"A small hierarchy group must not copy all source positions/attributes.");
        }
        [Test]
        public void TransformCompatibilityKeepsNegativeTrsAndRejectsShearSingularAndNonfinite(){
            Assert.That(VirtualGeometryTransformSupport.GetUnsupportedReason(Matrix4x4.TRS(Vector3.one,Quaternion.Euler(20,40,60),new Vector3(-2,3,.5f))),Is.Null);
            Matrix4x4 shear=Matrix4x4.identity;shear.m01=.4f;Assert.That(VirtualGeometryTransformSupport.GetUnsupportedReason(shear),Is.Not.Empty);
            Assert.That(VirtualGeometryTransformSupport.GetUnsupportedReason(Matrix4x4.Scale(new Vector3(1,0,1))),Is.Not.Empty);
            Matrix4x4 invalid=Matrix4x4.identity;invalid.m03=float.NaN;Assert.That(VirtualGeometryTransformSupport.GetUnsupportedReason(invalid),Is.Not.Empty);
        }
    }
}
