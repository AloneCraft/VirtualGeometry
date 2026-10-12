using System.Collections.Generic;
using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 仮想オブジェクトを見る camera に付ける。付けた camera の位置と画角で、ノードの画面上の大きさを求める(複数の camera では最大)。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class VirtualObjectViewer:MonoBehaviour{
        private static readonly List<VirtualObjectViewer> active=new List<VirtualObjectViewer>();
        private Camera viewerCamera;

        /// <summary>有効な viewer の一覧。</summary>
        public static IReadOnlyList<VirtualObjectViewer> Active=>active;

        public Camera Camera{
            get{
                if(this.viewerCamera==null){this.viewerCamera=this.GetComponent<Camera>();}
                return this.viewerCamera;
            }
        }

        private void OnEnable(){
            if(!active.Contains(this)){active.Add(this);}
        }

        private void OnDisable(){
            active.Remove(this);
        }
    }
}
