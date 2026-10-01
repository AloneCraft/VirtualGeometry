using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>非破壊変換、cache検証、immutable archiveとUndo可能なmetadata更新を一つのtransactionにする。</summary>
    public static class VirtualGeometryConversionService{
        public const int CurrentBuilderVersion=7;
        public static string SettingsKey(VirtualGeometryBuildSettings settings){
            string version=settings.GenerateStreamingPages&&settings.GenerateHierarchy?"v3":"v2";
            return version+"-t"+settings.MaxTrianglesPerCluster+"-h"+(settings.GenerateHierarchy?1:0)+"-f"+settings.HierarchyFanout+
                "-r"+settings.SimplificationRatio.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"-q"+settings.PositionQuantizationBits+"-b"+CurrentBuilderVersion;
        }
        public static VirtualGeometryConversionOperation Begin(Mesh source,string outputPath,VirtualGeometryBuildSettings settings,bool force=false){
            if(source==null){throw new ArgumentNullException(nameof(source));}if(settings==null){throw new ArgumentNullException(nameof(settings));}
            string assetsRoot=Path.GetFullPath("Assets")+Path.DirectorySeparatorChar;
            if(string.IsNullOrWhiteSpace(outputPath)||!outputPath.Replace('\\','/').StartsWith("Assets/",StringComparison.Ordinal)||!outputPath.EndsWith(".asset",StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(outputPath).StartsWith(assetsRoot,StringComparison.OrdinalIgnoreCase)){
                throw new ArgumentException("Output must be an .asset path below Assets.",nameof(outputPath));
            }
            if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(source))){throw new ArgumentException("Source Mesh must be a saved project asset.",nameof(source));}
            return new VirtualGeometryConversionOperation(source,outputPath.Replace('\\','/'),settings,force);
        }
        /// <summary>自動検証用の同期入口。Editor UIはBeginをpollしてブロックしない。</summary>
        public static VirtualGeometryMeshAsset Convert(Mesh source,string outputAssetPath,VirtualGeometryBuildSettings settings){
            using(var operation=Begin(source,outputAssetPath,settings)){operation.WaitUntilPrepared();return operation.Commit();}
        }
        internal static bool Matches(VirtualGeometryMeshAsset asset,string guid,long localId,string sourceHash,VirtualGeometryBuildSettings settings)=>
            asset!=null&&asset.SourceGuid==guid&&asset.SourceLocalId==localId&&asset.SourceHash==sourceHash&&asset.SettingsHash==SettingsKey(settings)&&
            asset.BuilderVersion==CurrentBuilderVersion&&asset.ProxyMesh!=null&&asset.ProxyLitMaterial!=null&&asset.ProxyLitMaterial.GetTexture("_BumpMap")!=null;

        internal static VirtualGeometryMeshAsset CommitPrepared(VirtualGeometryConversionOperation operation,VirtualGeometryConversionOperation.Prepared prepared){
            var output=prepared.Output;var metadata=prepared.Metadata;var settings=operation.Settings;
            string settingsKey=SettingsKey(settings);string relative="VirtualGeometry/"+operation.SourceGuid+"_"+operation.SourceLocalId+"_"+settingsKey+"_"+prepared.ContentHash.Substring(0,12)+".vgpage";
            string pagePath=Path.Combine(Application.streamingAssetsPath,relative),backupPath=operation.StagingPath+".previous";
            bool archiveCreated=false,archiveReplaced=false,createdAsset=false;VirtualGeometryMeshAsset asset=operation.Existing;
            Mesh generated=null,proxyBackup=null;Material litBackup=null,unlitBackup=null;string previous=asset!=null?EditorJsonUtility.ToJson(asset):null;
            var addedObjects=new List<Object>();Undo.IncrementCurrentGroup();int undoGroup=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Convert Virtual Geometry");
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(pagePath));Directory.CreateDirectory(Path.GetDirectoryName(operation.OutputPath));
                if(File.Exists(pagePath)){File.Replace(operation.StagingPath,pagePath,backupPath);archiveReplaced=true;}
                else{File.Move(operation.StagingPath,pagePath);archiveCreated=true;}
                AssetDatabase.Refresh();
                if(asset==null){asset=ScriptableObject.CreateInstance<VirtualGeometryMeshAsset>();AssetDatabase.CreateAsset(asset,operation.OutputPath);createdAsset=true;}
                else{Undo.RegisterCompleteObjectUndo(asset,"Convert Virtual Geometry");}
                generated=VirtualGeometryProxyBuilder.Create(output,out int proxyTriangles,out float proxyError);generated.name="Root Proxy";
                Mesh proxy=asset.ProxyMesh;
                if(proxy==null){proxy=generated;generated=null;AssetDatabase.AddObjectToAsset(proxy,asset);addedObjects.Add(proxy);}
                else{proxyBackup=Object.Instantiate(proxy);Undo.RegisterCompleteObjectUndo(proxy,"Convert Virtual Geometry Proxy");EditorUtility.CopySerialized(generated,proxy);EditorUtility.SetDirty(proxy);}
                Material lit=asset.ProxyLitMaterial,unlit=asset.ProxyUnlitMaterial;
                if(lit==null){lit=new Material(Shader.Find("Universal Render Pipeline/Lit")){name="Proxy Lit"};AssetDatabase.AddObjectToAsset(lit,asset);addedObjects.Add(lit);}
                else{litBackup=new Material(lit);Undo.RegisterCompleteObjectUndo(lit,"Convert Virtual Geometry Material");}
                if(unlit==null){unlit=new Material(Shader.Find("Universal Render Pipeline/Unlit")){name="Proxy Unlit"};AssetDatabase.AddObjectToAsset(unlit,asset);addedObjects.Add(unlit);}
                else{unlitBackup=new Material(unlit);Undo.RegisterCompleteObjectUndo(unlit,"Convert Virtual Geometry Material");}
                Texture2D flatNormal=null;foreach(var child in AssetDatabase.LoadAllAssetsAtPath(operation.OutputPath)){if(child is Texture2D texture&&texture.name=="Proxy Flat Normal"){flatNormal=texture;break;}}
                if(flatNormal==null){flatNormal=new Texture2D(1,1,TextureFormat.RGBAHalf,false,true){name="Proxy Flat Normal"};flatNormal.SetPixel(0,0,new Color(0.5f,0.5f,1,1));flatNormal.Apply();AssetDatabase.AddObjectToAsset(flatNormal,asset);addedObjects.Add(flatNormal);}
                lit.SetFloat("_Cull",0);unlit.SetFloat("_Cull",0);lit.SetTexture("_BumpMap",flatNormal);lit.EnableKeyword("_NORMALMAP");EditorUtility.SetDirty(lit);EditorUtility.SetDirty(unlit);
                var serialized=new SerializedObject(asset);
                serialized.FindProperty("proxyMesh").objectReferenceValue=proxy;serialized.FindProperty("proxyTriangleCount").intValue=proxyTriangles;serialized.FindProperty("proxyGeometricError").floatValue=proxyError;
                serialized.FindProperty("proxyLitMaterial").objectReferenceValue=lit;serialized.FindProperty("proxyUnlitMaterial").objectReferenceValue=unlit;
                serialized.FindProperty("sourceGuid").stringValue=operation.SourceGuid;serialized.FindProperty("sourceLocalId").longValue=operation.SourceLocalId;serialized.FindProperty("sourceHash").stringValue=operation.SourceHash;
                serialized.FindProperty("settingsHash").stringValue=settingsKey;serialized.FindProperty("contentHash").stringValue=prepared.ContentHash;serialized.FindProperty("pageRelativePath").stringValue=relative;
                serialized.FindProperty("bounds").boundsValue=output.Bounds;serialized.FindProperty("inputTriangleCount").intValue=output.InputTriangleCount;serialized.FindProperty("triangleCount").intValue=output.Indices.Length/3;
                serialized.FindProperty("leafTriangleCount").intValue=output.LeafTriangleCount;serialized.FindProperty("clusterCount").intValue=output.Clusters.Length;serialized.FindProperty("nodeCount").intValue=output.Nodes.Length;
                serialized.FindProperty("materialCount").intValue=operation.Source.subMeshCount;serialized.FindProperty("streamingPageCount").intValue=metadata?.Pages.Length??1;
                serialized.FindProperty("geometryBytes").longValue=metadata?.UniqueGeometryBytes??((long)output.Vertices.Length*32+(long)output.Indices.Length*4);serialized.FindProperty("metadataBytes").intValue=metadata?.MetadataBytes??0;
                serialized.FindProperty("slotGeometryBytes").intValue=metadata?.SlotGeometryBytes??0;serialized.FindProperty("formatVersion").intValue=metadata!=null?3:2;serialized.FindProperty("builderVersion").intValue=CurrentBuilderVersion;
                serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(asset);AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);if(File.Exists(backupPath)){File.Delete(backupPath);}
                foreach(var renderer in Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include)){if(renderer.MeshAsset==asset){renderer.RefreshAsset();}}
                return asset;
            }catch{
                if(createdAsset){AssetDatabase.DeleteAsset(operation.OutputPath);}
                else if(asset!=null){
                    EditorJsonUtility.FromJsonOverwrite(previous,asset);
                    if(proxyBackup!=null&&asset.ProxyMesh!=null){EditorUtility.CopySerialized(proxyBackup,asset.ProxyMesh);}
                    if(litBackup!=null&&asset.ProxyLitMaterial!=null){EditorUtility.CopySerialized(litBackup,asset.ProxyLitMaterial);}
                    if(unlitBackup!=null&&asset.ProxyUnlitMaterial!=null){EditorUtility.CopySerialized(unlitBackup,asset.ProxyUnlitMaterial);}
                    foreach(Object added in addedObjects){if(added!=null){Object.DestroyImmediate(added,true);}}EditorUtility.SetDirty(asset);AssetDatabase.SaveAssets();
                }
                if(archiveReplaced&&File.Exists(backupPath)){File.Replace(backupPath,pagePath,null);}else if(archiveCreated&&File.Exists(pagePath)){File.Delete(pagePath);}
                throw;
            }finally{if(generated!=null){Object.DestroyImmediate(generated);}if(proxyBackup!=null){Object.DestroyImmediate(proxyBackup);}if(litBackup!=null){Object.DestroyImmediate(litBackup);}if(unlitBackup!=null){Object.DestroyImmediate(unlitBackup);}}
        }
        /// <summary>sourceGuid/localIdから元Meshを解決する。元assetが削除・変更されていればnull。</summary>
        public static Mesh ResolveSourceMesh(VirtualGeometryMeshAsset asset){
            if(asset==null||string.IsNullOrEmpty(asset.SourceGuid)){return null;}
            string path=AssetDatabase.GUIDToAssetPath(asset.SourceGuid);
            if(string.IsNullOrEmpty(path)){return null;}
            foreach(Object candidate in AssetDatabase.LoadAllAssetsAtPath(path)){
                if(candidate is Mesh mesh&&AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,out string _,out long localId)&&localId==asset.SourceLocalId){return mesh;}
            }
            return null;
        }

        /// <summary>選択GameObjectへ専用Rendererを付けてassetを割り当てる。選択が元Meshを表示していればそのMeshRendererを無効化する。Undo 1回で戻せる。</summary>
        public static GameObject PlaceRenderer(VirtualGeometryMeshAsset asset,GameObject target){
            if(asset==null){throw new ArgumentNullException(nameof(asset));}
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Place Virtual Geometry Renderer");
            Mesh source=ResolveSourceMesh(asset);
            MeshFilter original=null;if(target!=null){original=target.GetComponent<MeshFilter>();}
            if(target==null||(original!=null&&original.sharedMesh!=source)){target=new GameObject(asset.name);Undo.RegisterCreatedObjectUndo(target,"Place Virtual Geometry Renderer");}
            var renderer=target.GetComponent<VirtualGeometryRenderer>();
            if(renderer==null){renderer=Undo.AddComponent<VirtualGeometryRenderer>(target);}else{Undo.RecordObject(renderer,"Assign Virtual Geometry");}
            renderer.SetMeshAsset(asset);
            var ordinary=target.GetComponent<MeshRenderer>();
            if(ordinary!=null){
                // 元のMeshRendererと同じMaterialを引き継ぐ。VG描画はそのshaderに対応するVirtual Geometry Shaderで描く。
                renderer.SetMaterials(ordinary.sharedMaterials);
                Undo.RecordObject(ordinary,"Replace visible source with Virtual Geometry");ordinary.enabled=false;
            }
            EditorUtility.SetDirty(renderer);Undo.CollapseUndoOperations(group);
            return target;
        }

        public static bool DeleteReferencedPage(VirtualGeometryMeshAsset asset){
            if(asset==null||string.IsNullOrWhiteSpace(asset.PageRelativePath)){return false;}
            string streamingRoot=Path.GetFullPath(Application.streamingAssetsPath);string page=Path.GetFullPath(Path.Combine(streamingRoot,asset.PageRelativePath));
            if(!page.StartsWith(streamingRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)){throw new InvalidDataException("Page path escapes StreamingAssets.");}
            if(!File.Exists(page)){return false;}bool result=AssetDatabase.DeleteAsset("Assets/StreamingAssets/"+asset.PageRelativePath.Replace('\\','/'));
            foreach(var renderer in Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include)){if(renderer.MeshAsset==asset){renderer.RefreshAsset();}}return result;
        }
    }
}