using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>UPM metadata、sample導線、文書とEditor native dependency境界を検証する。</summary>
    public static class M5PackageValidation{
        public static void Run(){
            const string root="Packages/com.virtualgeometry.core/";var lines=new List<string>();Directory.CreateDirectory("TestResults/M5/Package");
            try{
                var package=UnityEditor.PackageManager.PackageInfo.FindForAssetPath(root+"Runtime/VirtualizedGeometry.Runtime.asmdef");
                if(package==null||package.name!="com.virtualgeometry.core"||package.version!="0.1.0"){throw new InvalidOperationException("UPM package metadata mismatch.");}
                lines.Add("UPM="+package.name+"@"+package.version+", source="+package.source);
                string[] required={"LICENSE.md","Third Party Notices.md","README.md","Documentation~/SupportMatrix.md","Documentation~/Conversion.md","Documentation~/Rendering.md","Documentation~/Culling.md","Documentation~/Streaming.md","Documentation~/Proxy.md","Documentation~/Debugger.md","Documentation~/Benchmark.md","Documentation~/PlayerBuild.md","Samples~/Practical/README.md"};
                foreach(string path in required){if(!File.Exists(root+path)||new FileInfo(root+path).Length==0){throw new FileNotFoundException("Required package document missing.",path);}}
                lines.Add("Required package/license/sample documents="+required.Length+": PASS");
                const string native=root+"Editor/Plugins/x86_64/VirtualGeometryMeshOptimizer.dll";
                var importer=AssetImporter.GetAtPath(native) as PluginImporter;if(importer==null){throw new InvalidOperationException("Native PluginImporter missing.");}
                bool player=importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64);
                lines.Add("meshoptimizer ABI="+MeshOptimizerSimplifier.Version+", editor="+importer.GetCompatibleWithEditor()+", anyPlatform="+importer.GetCompatibleWithAnyPlatform()+", WindowsPlayer="+player);
                bool included=false;foreach(var candidate in PluginImporter.GetImporters(BuildTarget.StandaloneWindows64)){if(candidate.assetPath==native){included=true;}}
                lines.Add("Native present in Windows Player importer list="+included);
                if(MeshOptimizerSimplifier.Version!=1010||included){throw new InvalidOperationException("Editor native library would enter the Player.");}
                lines.Add("Native upstream policy=RETAINED_UPSTREAM_ORIGINAL; optional future cleanup; Player native/source exclusion verified.");
                File.WriteAllText("TestResults/M5/Package/Results.txt",string.Join(Environment.NewLine,lines)+"\nPASS: v0.1 package boundaries; upstream original retained");Debug.Log("M5 package boundaries PASS");EditorApplication.Exit(0);
            }catch(Exception error){File.WriteAllText("TestResults/M5/Package/Results.txt",string.Join(Environment.NewLine,lines)+"\nFAIL: "+error);Debug.LogException(error);EditorApplication.Exit(1);}
        }
    }
}