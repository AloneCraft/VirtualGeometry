using System;
using System.Threading;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 表現の基底。状態の遷移(Missing → Preparing → Ready ⇄ Active → Released)、取り消し、例外の扱いを持つ。
    /// 派生クラスは On... を実装する。On... で例外が出ると Failed になり、その表現は使われない(粗い表現のまま見せる)。
    /// </summary>
    public abstract class VirtualObjectRepresentationBase:IVirtualObjectRepresentation{
        private CancellationToken token;

        protected VirtualObjectRepresentationBase(in RepresentationRequest request){
            this.Node=request.Node;this.Band=request.Band;this.BandIndex=request.BandIndex;
            this.State=RepresentationState.Missing;
        }

        public VirtualObjectNode Node{get;}
        public RepresentationBand Band{get;}
        public int BandIndex{get;}
        public RepresentationState State{get;private set;}
        /// <summary>Failed になった理由。</summary>
        public string FailureReason{get;private set;}

        public virtual RepresentationCost EstimateCost(){
            return default;
        }

        public void BeginPrepare(VirtualObjectContext context,CancellationToken cancellation){
            if(this.State!=RepresentationState.Missing){return;}
            this.token=cancellation;
            this.State=RepresentationState.Preparing;
            try{
                this.OnBeginPrepare(context,cancellation);
            }catch(Exception error){
                this.Fail(context,error);
            }
        }

        public bool Poll(VirtualObjectContext context){
            if(this.State==RepresentationState.Preparing){
                if(this.token.IsCancellationRequested){
                    this.Release(context);
                    return false;
                }
                try{
                    if(this.OnPoll(context)){this.State=RepresentationState.Ready;}
                }catch(Exception error){
                    this.Fail(context,error);
                }
            }
            return this.State==RepresentationState.Ready||this.State==RepresentationState.Active;
        }

        public void Activate(VirtualObjectContext context){
            if(this.State!=RepresentationState.Ready){return;}
            try{
                this.OnActivate(context);
                this.State=RepresentationState.Active;
            }catch(Exception error){
                this.Fail(context,error);
            }
        }

        public bool IsVisible{
            get{
                if(this.State!=RepresentationState.Active){return false;}
                return this.OnIsVisible();
            }
        }

        public void UpdatePose(VirtualObjectContext context){
            if(this.State!=RepresentationState.Active){return;}
            try{
                this.OnUpdatePose(context);
            }catch(Exception error){
                this.Fail(context,error);
            }
        }

        public void Deactivate(VirtualObjectContext context){
            if(this.State!=RepresentationState.Active){return;}
            try{
                this.OnDeactivate(context);
                this.State=RepresentationState.Ready;
            }catch(Exception error){
                this.Fail(context,error);
            }
        }

        public void Release(VirtualObjectContext context){
            if(this.State==RepresentationState.Released){return;}
            try{
                if(this.State==RepresentationState.Active){this.OnDeactivate(context);}
                this.OnRelease(context);
            }catch(Exception error){
                this.FailureReason=error.Message;
                context?.ReportError(this,error);
            }
            this.State=RepresentationState.Released;
        }

        private void Fail(VirtualObjectContext context,Exception error){
            this.FailureReason=error.Message;
            context?.ReportError(this,error);
            try{
                this.OnRelease(context);
            }catch(Exception){
                // 解放の失敗は、最初の失敗の理由を上書きしない。
            }
            this.State=RepresentationState.Failed;
        }

        /// <summary>準備を始める。重い処理は非同期に始め、完了は <see cref="OnPoll"/> で返す。</summary>
        protected abstract void OnBeginPrepare(VirtualObjectContext context,CancellationToken cancellation);
        /// <summary>準備が終わったら true。既定は true(BeginPrepare で終わる表現)。</summary>
        protected virtual bool OnPoll(VirtualObjectContext context){
            return true;
        }
        protected abstract void OnActivate(VirtualObjectContext context);
        protected virtual bool OnIsVisible(){
            return true;
        }
        protected virtual void OnUpdatePose(VirtualObjectContext context){
        }
        protected abstract void OnDeactivate(VirtualObjectContext context);
        protected abstract void OnRelease(VirtualObjectContext context);
    }
}
