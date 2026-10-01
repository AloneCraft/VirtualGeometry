using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualizedGeometry.ProjectTools{
    /// <summary>
    /// Tools/Close-UnityEditor.ps1からの要求で、開いているEditorを作業を失わずに閉じる。
    /// batchmodeの検証は、同じprojectを開いたEditorと同時に動かせないために使う。
    /// 要求はTemp/CloseEditorRequestファイル。Play中なら止め、変更のあるsceneとassetを保存してから終了する。
    /// 保存先のない新規sceneの変更や未保存のPrefab編集があるときは、失わないよう閉じずに理由をTemp/CloseEditorResult.txtへ書く。
    /// 結果の1行目は外部scriptが読む英字の理由(Windows PowerShell 5.1は日本語を正しく出力できないため)、2行目は利用者向けの説明。
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorCloseRequest{
        private const string RequestPath="Temp/CloseEditorRequest";
        private const string ResultPath="Temp/CloseEditorResult.txt";
        private static double nextPoll;

        static EditorCloseRequest(){
            if(Application.isBatchMode){return;}
            EditorApplication.update+=Poll;
        }

        private static void Poll(){
            if(EditorApplication.timeSinceStartup<nextPoll){return;}
            nextPoll=EditorApplication.timeSinceStartup+0.5;
            if(!File.Exists(RequestPath)){return;}
            // Play modeを抜けてから保存する。要求ファイルは残し、抜けた後の確認で閉じる。
            if(EditorApplication.isPlayingOrWillChangePlaymode){
                EditorApplication.isPlaying=false;
                return;
            }
            if(EditorApplication.isCompiling||EditorApplication.isUpdating){return;}
            File.Delete(RequestPath);
            string reason=Blocker();
            if(reason!=null){
                Refuse(reason);
                return;
            }
            for(int i=0;i<SceneManager.sceneCount;i++){
                Scene scene=SceneManager.GetSceneAt(i);
                if(scene.isDirty&&!EditorSceneManager.SaveScene(scene)){
                    Refuse("save-failed\nscene \""+scene.path+"\" を保存できませんでした。");
                    return;
                }
            }
            AssetDatabase.SaveAssets();
            File.WriteAllText(ResultPath,"CLOSED");
            EditorApplication.Exit(0);
        }

        private static void Refuse(string reason){
            File.WriteAllText(ResultPath,"NOT CLOSED "+reason);
            Debug.LogWarning("Editorを閉じる要求を保留しました: "+reason.Substring(reason.IndexOf('\n')+1));
        }

        // 自動では保存できない変更。閉じると失われるため、利用者に任せる。
        private static string Blocker(){
            PrefabStage stage=PrefabStageUtility.GetCurrentPrefabStage();
            if(stage!=null&&stage.scene.isDirty){return "prefab-stage\nPrefab \""+stage.assetPath+"\" の編集に未保存の変更があります。";}
            for(int i=0;i<SceneManager.sceneCount;i++){
                Scene scene=SceneManager.GetSceneAt(i);
                if(scene.isDirty&&string.IsNullOrEmpty(scene.path)){return "untitled-scene\n保存先のない新規sceneに変更があります。";}
            }
            return null;
        }
    }
}
