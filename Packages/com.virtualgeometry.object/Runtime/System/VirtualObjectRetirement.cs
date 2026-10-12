using System.Collections.Generic;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 表現が作った GameObject とメッシュを、描画の仕組みの決まり(<see cref="IVirtualObjectPlacement.RetireFrames"/>)の frame 数の後に破棄する。
    /// <see cref="VirtualObjectSystem"/> を止めた(<see cref="VirtualObjectSystem.Shutdown"/>)後も、残りを数え続けて破棄する
    /// (Omni の一括描画は、外した Renderer を同じ frame や直後に破棄すると止まるため)。
    /// frame は Edit Mode では Editor の更新ごと、Play 中は Update ごとに数える。Play を止めるときは、残りをすぐに破棄する。
    /// </summary>
    public static class VirtualObjectRetirement{
        private struct Entry{
            public Object Target;
            public int Remaining;
        }

        private static readonly List<Entry> entries=new List<Entry>();
        private static VirtualObjectRetirementRunner runner;
#if UNITY_EDITOR
        private static bool editorHooked;
#endif

        /// <summary>まだ破棄していない数(漏れの検査用)。</summary>
        public static int PendingCount=>entries.Count;

        /// <summary>target を frames 回の更新の後に破棄する(0 以下ならすぐに)。</summary>
        public static void Retire(Object target,int frames){
            if(target==null){return;}
            if(frames<=0){
                DestroyNow(target);
                return;
            }
            entries.Add(new Entry{Target=target,Remaining=frames});
            EnsureTicking();
        }

        /// <summary>残りを全部すぐに破棄する(Play の終わりなど、描画の仕組みも止まるとき)。</summary>
        public static void Flush(){
            for(int i=0;i<entries.Count;i++){DestroyNow(entries[i].Target);}
            entries.Clear();
        }

        /// <summary>1 回の更新を数え、期限の来たものを破棄する。</summary>
        public static void Tick(){
            for(int i=entries.Count-1;i>=0;i--){
                Entry entry=entries[i];
                entry.Remaining--;
                if(entry.Remaining>0&&entry.Target!=null){
                    entries[i]=entry;
                    continue;
                }
                DestroyNow(entry.Target);
                entries.RemoveAt(i);
            }
        }

        private static void DestroyNow(Object target){
            if(target==null){return;}
            if(Application.isPlaying){Object.Destroy(target);}else{Object.DestroyImmediate(target);}
        }

        private static void EnsureTicking(){
#if UNITY_EDITOR
            if(!editorHooked){
                editorHooked=true;
                UnityEditor.EditorApplication.update+=EditorTick;
                UnityEditor.EditorApplication.playModeStateChanged+=OnPlayModeStateChanged;
            }
#endif
            if(Application.isPlaying&&runner==null){
                var holder=new GameObject("Virtual Object Retirement");
                holder.hideFlags=HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(holder);
                runner=holder.AddComponent<VirtualObjectRetirementRunner>();
            }
        }

#if UNITY_EDITOR
        private static void EditorTick(){
            if(Application.isPlaying||entries.Count==0){return;}
            Tick();
        }

        private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange change){
            // Play の終わりと始まりで、残りを持ち越さない(Play 中に作った物は Play と一緒に消す)。
            // Play を止める途中で止まったシステムが預けた物は、Edit Mode に戻った後に消す(保存しない GameObject は Play の終わりに自動では消えない)。
            if(change==UnityEditor.PlayModeStateChange.ExitingPlayMode||change==UnityEditor.PlayModeStateChange.ExitingEditMode||change==UnityEditor.PlayModeStateChange.EnteredEditMode){Flush();}
        }
#endif

        internal static void OnRunnerDestroyed(VirtualObjectRetirementRunner destroyed){
            if(runner==destroyed){runner=null;}
        }
    }
}
