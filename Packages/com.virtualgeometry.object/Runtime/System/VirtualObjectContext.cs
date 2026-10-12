using System;
using System.Collections.Generic;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>見る camera の、この frame の状態(ルートの局所)。</summary>
    public struct VirtualObjectViewerState{
        public Camera Camera;
        public LogicalVector3 Position;
        public Quaternion Rotation;
        public float ProjectionScale;
        /// <summary>速度(m/s、ルートの局所。平滑)。</summary>
        public Vector3 Velocity;
    }

    /// <summary>表現に渡す、システムの状態と道具。</summary>
    public sealed class VirtualObjectContext{
        private readonly List<VirtualObjectViewerState> viewers=new List<VirtualObjectViewerState>();

        public VirtualObjectContext(VirtualObjectSystem system){
            this.System=system;
        }

        public VirtualObjectSystem System{get;}
        public Transform Root=>this.System.transform;
        public IVirtualObjectPlacement Placement=>this.System.Placement;
        public IVirtualObjectGround Ground=>this.System.Ground;
        public VirtualObjectBudget Budget=>this.System.Budget;
        /// <summary>システムの frame の番号(Step のたびに 1 増える)。</summary>
        public int Frame{get;internal set;}
        /// <summary>システムの時刻(秒。Step の dt の和)。</summary>
        public float Time{get;internal set;}
        public IReadOnlyList<VirtualObjectViewerState> Viewers=>this.viewers;
        /// <summary>表現を作り置きする、非表示の親(この下の GameObject は描かれない)。</summary>
        public Transform Pool=>this.System.PoolTransform;
        /// <summary>
        /// システムを止めている最中か(<see cref="VirtualObjectSystem.Shutdown"/>)。止める途中は、親の GameObject を無効にしている最中のことがあり、
        /// GameObject の親を付け替えられないので、表現は付け替えずに破棄の予定に載せる。
        /// </summary>
        public bool ShuttingDown{get;internal set;}

        internal List<VirtualObjectViewerState> MutableViewers=>this.viewers;

        /// <summary>最も近い camera(無ければ false)。</summary>
        public bool TryGetPrimaryViewer(in LogicalVector3 position,out VirtualObjectViewerState viewer){
            viewer=default;
            float best=float.MaxValue;
            bool found=false;
            for(int i=0;i<this.viewers.Count;i++){
                if(!LogicalVector3.TrySubtract(position,this.viewers[i].Position,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 offset)){continue;}
                float distance=Unity.Mathematics.math.lengthsq(offset);
                if(distance<best){best=distance;viewer=this.viewers[i];found=true;}
            }
            return found;
        }

        /// <summary>GameObject を、placement の決まり(<see cref="IVirtualObjectPlacement.RetireFrames"/>)の後に破棄する。</summary>
        public void Retire(GameObject instance){
            this.System.Retire(instance);
        }

        /// <summary>表現が作ったメッシュを、GameObject と同じく placement の決まりの後に破棄する(描画から外した frame に消さない)。</summary>
        public void RetireMesh(Mesh mesh){
            this.System.Retire(mesh);
        }

        public void ReportError(IVirtualObjectRepresentation representation,Exception error){
            this.System.ReportError(representation,error);
        }
    }
}
