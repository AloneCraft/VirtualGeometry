using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualizedGeometry.Editor;
using OmniRadialBuildPreparation=Omni.Editor.Build.OmniRadialBuildPreparation;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// BodyStack の scene(<see cref="PlanetPlayerSmoke"/> つき)を Windows の Player としてビルドして実行し、結果を確かめる。
    /// ビルドの間だけ、Graphics API を DX12 だけにする(終わったら元へ戻す)。Player は可視のウィンドウで 640×480 で動き(-nographics なし)、
    /// -planetResults の folder に PlayerReport.txt を書いて終了する(<see cref="PlanetPlayerSmoke"/>)。
    /// ビルドの出力は Builds/PlanetPlayer/(git 管理外)、結果は TestResults/Planet/PlanetPlayer*(Report.txt が合わせた結果、PlayerReport.txt が Player の結果、Player.log)。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlanetPlayerBuild.Run(Development)/ RunRelease(Development なし)
    /// </summary>
    public static class PlanetPlayerBuild{
        private const string SceneFolder="Assets/Development/PlanetWorld/PlayerSmoke";
        private const string ScenePath=SceneFolder+"/PlayerSmoke.unity";
        private const int PlayerTimeoutSeconds=900;

        public static void Run(){
            Execute(true);
        }

        public static void RunRelease(){
            Execute(false);
        }

        private static void Execute(bool development){
            string name="PlanetPlayer";
            if(!development){name="PlanetPlayerRelease";}
            string results="TestResults/Planet/"+name;
            var report=new List<string>();
            var failures=new List<string>();
            // ビルドは、URP の共有 settings と pipeline asset を書き換える(Player 向けの prefiltering の値、実行時 settings の一覧)。
            // プロジェクトの資産をビルドの前の内容に戻すため、ビルドの前の内容を覚えておく。
            Dictionary<string,byte[]> snapshots=null;
            try{
                Directory.CreateDirectory(results);
                // 前回の実行の画像が残らないようにする(今回の実行が途中で失敗しても、古い画像が今回のものに見えない)。
                foreach(string stale in Directory.GetFiles(results,"PlayerView_*.png")){File.Delete(stale);}
                snapshots=Snapshot();
                string variant="Release";
                if(development){variant="Development";}
                string folder=Path.GetFullPath("Builds/PlanetPlayer/"+variant);
                if(Directory.Exists(folder)){Directory.Delete(folder,true);}
                Directory.CreateDirectory(folder);
                string executable=Path.Combine(folder,"PlanetPlayer.exe");
                BuildScene();
                // 対数版の標準 URP Shader(Assets/Radial、git 管理外)を作る。Omni の Player Build の検査は、これが無いとビルドを止める。
                OmniRadialBuildPreparation.Prepare(new[]{ScenePath});
                BuildPlayer(development,executable,report,failures);
                if(failures.Count==0){RunPlayer(executable,Path.GetFullPath(results),report,failures);}
            }catch(Exception error){
                failures.Add(error.ToString());
            }finally{
                if(snapshots!=null){
                    try{Restore(snapshots);}catch(Exception error){failures.Add("資産を元に戻せません: "+error);}
                }
                report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
                if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
                Directory.CreateDirectory(results);
                File.WriteAllLines(results+"/Report.txt",report);
            }
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }

        // ビルドが書き換える資産(URP の共有 settings と、使う pipeline asset)の、ビルドの前の内容。
        private static Dictionary<string,byte[]> Snapshot(){
            var paths=new List<string>();
            string globalSettings=AssetDatabase.GetAssetPath(GraphicsSettings.GetSettingsForRenderPipeline<UniversalRenderPipeline>());
            if(!string.IsNullOrEmpty(globalSettings)){paths.Add(globalSettings);}
            string pipeline=AssetDatabase.GetAssetPath(MoonBallSceneBuilder.EnsurePipeline());
            if(!string.IsNullOrEmpty(pipeline)){paths.Add(pipeline);}
            var snapshots=new Dictionary<string,byte[]>();
            foreach(string path in paths){snapshots[path]=File.ReadAllBytes(path);}
            return snapshots;
        }

        private static void Restore(Dictionary<string,byte[]> snapshots){
            foreach(KeyValuePair<string,byte[]> pair in snapshots){
                if(!File.Exists(pair.Key)){continue;}
                byte[] current=File.ReadAllBytes(pair.Key);
                bool same=current.Length==pair.Value.Length;
                for(int i=0;same&&i<current.Length;i++){
                    if(current[i]!=pair.Value[i]){same=false;}
                }
                if(same){continue;}
                File.WriteAllBytes(pair.Key,pair.Value);
                // 読み込み済みの資産を、書き戻した内容で読み直す(終了時に、メモリ上の内容で書き直されないように)。
                AssetDatabase.ImportAsset(pair.Key,ImportAssetOptions.ForceUpdate);
            }
        }

        // BodyStack の scene を作り、camera に PlanetPlayerSmoke を足して保存する(元の BodyStack.unity は変えない)。
        private static void BuildScene(){
            Directory.CreateDirectory(SceneFolder);
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            BodyStackObjects objects=BodyStackSceneBuilder.Populate(BodyStackSettings.Default);
            objects.CameraObject.AddComponent<PlanetPlayerSmoke>();
            if(!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),ScenePath)){
                throw new IOException("Could not save scene: "+ScenePath);
            }
            AssetDatabase.SaveAssets();
        }

        private static void BuildPlayer(bool development,string executable,List<string> report,List<string> failures){
            // Graphics API を DX12 だけにする(ビルドの間だけ)。
            bool useDefault=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            UnityEngine.Rendering.GraphicsDeviceType[] previousApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            bool frameTiming=PlayerSettings.enableFrameTimingStats;
            // Omni の Player Build の検査は、URP の共有 resource の Camera MotionVectors が Omni 版であることを要る(Omni Manager の「Install Omni Shader」と同じ)。
            // ビルドの間だけ接続し、終わったら元の参照へ戻す(プロジェクトの URP の設定を変えたままにしない)。
            Shader previousMotionVectors=null;
            bool motionVectorsInstalled=false;
            try{
                if(GraphicsSettings.TryGetRenderPipelineSettings(out UniversalRendererResources resources)&&resources!=null){previousMotionVectors=resources.cameraMotionVector;}
                InstallOmniCameraMotionVectors();
                motionVectorsInstalled=true;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{UnityEngine.Rendering.GraphicsDeviceType.Direct3D12});
                PlayerSettings.enableFrameTimingStats=true;
                var options=new BuildPlayerOptions{
                    scenes=new[]{ScenePath},
                    locationPathName=executable,
                    target=BuildTarget.StandaloneWindows64,
                    options=BuildOptions.None,
                };
                if(development){options.options=BuildOptions.Development;}
                var timer=Stopwatch.StartNew();
                BuildReport build=VirtualGeometryPlayerBuild.Build(options);
                timer.Stop();
                BuildSummary summary=build.summary;
                report.Add("build: result="+summary.result+" development="+development+" backend="+PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)+" errors="+summary.totalErrors+" warnings="+summary.totalWarnings+
                    " size="+(summary.totalSize/(1024*1024)).ToString(CultureInfo.InvariantCulture)+"MiB time="+timer.Elapsed.TotalSeconds.ToString("F0",CultureInfo.InvariantCulture)+"s");
                if(summary.result!=BuildResult.Succeeded){
                    failures.Add("ビルドが "+summary.result+" でした(エラー "+summary.totalErrors+")。");
                    foreach(BuildStep step in build.steps){
                        foreach(BuildStepMessage message in step.messages){
                            if(message.type==LogType.Error||message.type==LogType.Exception){failures.Add("build: "+message.content);}
                        }
                    }
                }
                // ビルドの警告のメッセージを記録する(エラーは、失敗したときに上の分岐で failures へ入る)。
                foreach(BuildStep step in build.steps){
                    foreach(BuildStepMessage message in step.messages){
                        if(message.type==LogType.Warning){report.Add("build warning: "+message.content);}
                    }
                }
            }finally{
                if(motionVectorsInstalled&&GraphicsSettings.TryGetRenderPipelineSettings(out UniversalRendererResources installed)&&installed!=null){
                    installed.cameraMotionVector=previousMotionVectors;
                    AssetDatabase.SaveAssets();
                }
                PlayerSettings.enableFrameTimingStats=frameTiming;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,previousApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,useDefault);
            }
        }

        // Omni.Editor.Build.OmniUniversalRendererResourceInstaller.TryInstall(internal)を呼ぶ。
        private static void InstallOmniCameraMotionVectors(){
            Type installer=Type.GetType("Omni.Editor.Build.OmniUniversalRendererResourceInstaller, Omni.Editor");
            MethodInfo method=null;
            if(installer!=null){method=installer.GetMethod("TryInstall",BindingFlags.NonPublic|BindingFlags.Static);}
            if(method==null){throw new InvalidOperationException("Omni の OmniUniversalRendererResourceInstaller.TryInstall が見つかりません。");}
            object[] arguments={null};
            bool installed=(bool)method.Invoke(null,arguments);
            if(!installed){throw new InvalidOperationException("Omni の Camera MotionVectors を接続できません: "+arguments[0]);}
        }

        private static void RunPlayer(string executable,string results,List<string> report,List<string> failures){
            string reportPath=Path.Combine(results,"PlayerReport.txt");
            if(File.Exists(reportPath)){File.Delete(reportPath);}
            string log=Path.Combine(results,"Player.log");
            var info=new ProcessStartInfo{
                FileName=executable,
                Arguments="-force-d3d12 -screen-width 640 -screen-height 480 -screen-fullscreen 0 -logFile \""+log+"\" -planetResults \""+results+"\"",
                UseShellExecute=false,
                WorkingDirectory=Path.GetDirectoryName(executable),
            };
            var timer=Stopwatch.StartNew();
            using(Process process=Process.Start(info)){
                if(!process.WaitForExit(PlayerTimeoutSeconds*1000)){
                    try{process.Kill();}catch(Exception){}
                    failures.Add("Player が "+PlayerTimeoutSeconds+" 秒で終わりませんでした。");
                    return;
                }
                report.Add("player: exitCode="+process.ExitCode+" time="+timer.Elapsed.TotalSeconds.ToString("F0",CultureInfo.InvariantCulture)+"s");
                if(process.ExitCode!=0){failures.Add("Player の終了コードが "+process.ExitCode+" でした。");}
            }
            if(!File.Exists(reportPath)){
                failures.Add("Player が PlayerReport.txt を書きませんでした。");
                return;
            }
            string[] lines=File.ReadAllLines(reportPath);
            foreach(string line in lines){report.Add("player> "+line);}
            if(Array.IndexOf(lines,"PASSED")<0){failures.Add("Player の PlayerReport.txt が PASSED ではありません。");}
        }
    }
}
