using System;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// GameObject で見せる表現の基底。準備で、作り置きの非表示の親(<see cref="VirtualObjectContext.Pool"/>)の下に GameObject を作り、
    /// 表示で placement(<see cref="IVirtualObjectPlacement"/>)がノードの位置に置いて登録する。隠すと非表示の親へ戻し、解放で placement の決まりの後に破棄する。
    /// 見えたことは、表示した frame の次の frame に分かる(<see cref="IVirtualObjectRepresentation.IsVisible"/>)。
    /// </summary>
    public abstract class GameObjectRepresentation:VirtualObjectRepresentationBase{
        private int activatedFrame=-1;
        private bool visible;

        protected GameObjectRepresentation(in RepresentationRequest request):base(in request){
        }

        /// <summary>この表現の GameObject(準備が済むまで null)。</summary>
        public GameObject Instance{get;protected set;}

        protected override void OnActivate(VirtualObjectContext context){
            if(this.Instance==null){throw new InvalidOperationException("GameObject がありません。");}
            this.Place(context,this.Node.EffectiveRotation);
            context.Placement.OnShown(this.Instance);
            this.activatedFrame=context.Frame;this.visible=false;
        }

        /// <summary>GameObject をノードの位置に、向き rotation(ルートの局所)で置く。</summary>
        protected void Place(VirtualObjectContext context,Quaternion rotation){
            context.Placement.Place(context.Root,this.Instance,this.Node.Origin,rotation,this.Node.Scale);
        }

        protected override bool OnIsVisible(){
            return this.visible;
        }

        protected override void OnUpdatePose(VirtualObjectContext context){
            if(context.Frame>this.activatedFrame){this.visible=true;}
        }

        protected override void OnDeactivate(VirtualObjectContext context){
            this.visible=false;
            if(this.Instance==null){return;}
            context.Placement.OnHidden(this.Instance);
            this.Instance.transform.SetParent(context.Pool,false);
        }

        protected override void OnRelease(VirtualObjectContext context){
            if(this.Instance!=null){
                this.Instance.transform.SetParent(context.Pool,false);
                context.Retire(this.Instance);
                this.Instance=null;
            }
        }
    }

    /// <summary>プレハブの表現の設定。</summary>
    [Serializable]
    public sealed class PrefabRepresentationSettings:IRepresentationSettings{
        public GameObject Prefab;
        [Tooltip("GPU の memory の見積もり(byte)。")]
        public long GpuBytes=1024*1024;
        [Tooltip("CPU の memory の見積もり(byte)。")]
        public long CpuBytes=256*1024;
        [Tooltip("Play 中は InstantiateAsync で作る(main thread を止めない)。")]
        public bool Async=true;
    }

    /// <summary>
    /// プレハブを置く表現(近距離の詳細、当たり判定)。Play 中は <see cref="UnityEngine.Object.InstantiateAsync{T}(T,Transform)"/> で非同期に作る。
    /// プレハブの Renderer の材質は、描画の仕組みの決まり(Omni では Radial 対応の shader)に従うこと(Validator が検査する)。
    /// </summary>
    [VirtualObjectRepresentation("prefab",typeof(PrefabRepresentationSettings))]
    public sealed class PrefabRepresentation:GameObjectRepresentation{
        private AsyncInstantiateOperation<GameObject> operation;

        public PrefabRepresentation(in RepresentationRequest request):base(in request){
        }

        private PrefabRepresentationSettings Settings=>this.Band.Settings as PrefabRepresentationSettings;

        public override RepresentationCost EstimateCost(){
            PrefabRepresentationSettings settings=this.Settings;
            if(settings==null){return default;}
            return new RepresentationCost(settings.GpuBytes,settings.CpuBytes,0.05f,0.5f);
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,CancellationToken cancellation){
            PrefabRepresentationSettings settings=this.Settings;
            if(settings==null||settings.Prefab==null){throw new InvalidOperationException("プレハブの表現にプレハブがありません。");}
            if(Application.isPlaying&&settings.Async){
                this.operation=UnityEngine.Object.InstantiateAsync(settings.Prefab,context.Pool);
                return;
            }
            this.Instance=UnityEngine.Object.Instantiate(settings.Prefab,context.Pool,false);
            this.Instance.hideFlags=HideFlags.DontSave;
        }

        protected override bool OnPoll(VirtualObjectContext context){
            if(this.Instance!=null){return true;}
            if(this.operation==null||!this.operation.isDone){return false;}
            if(this.operation.Result==null||this.operation.Result.Length==0){throw new InvalidOperationException("プレハブを作れませんでした。");}
            this.Instance=this.operation.Result[0];
            this.Instance.hideFlags=HideFlags.DontSave;
            this.operation=null;
            return true;
        }

        protected override void OnRelease(VirtualObjectContext context){
            if(this.operation!=null){
                if(this.operation.isDone){
                    if(this.operation.Result!=null){
                        foreach(GameObject created in this.operation.Result){
                            if(created!=null){context.Retire(created);}
                        }
                    }
                }else{
                    this.operation.Cancel();
                }
                this.operation=null;
            }
            base.OnRelease(context);
        }
    }

    /// <summary>ビルボードの表現の設定。</summary>
    [Serializable]
    public sealed class BillboardRepresentationSettings:IRepresentationSettings{
        public Material Material;
        [Tooltip("板の大きさ(m、局所)。0 なら境界の球の直径。")]
        public Vector2 Size;
        [Tooltip("板の中心の、原点からの高さ(m、局所)。")]
        public float VerticalOffset;
    }

    /// <summary>
    /// 1 枚の板を、最も近い camera に向けて見せる表現(遠景)。板は上向き(ノードの上向き)の周りだけ回す。
    /// 常駐(<see cref="RepresentationBand.Resident"/>)にして、粗い代替の最後の砦に使う。
    /// </summary>
    [VirtualObjectRepresentation("billboard",typeof(BillboardRepresentationSettings))]
    public sealed class BillboardRepresentation:GameObjectRepresentation{
        private static Mesh quad;

        public BillboardRepresentation(in RepresentationRequest request):base(in request){
        }

        private BillboardRepresentationSettings Settings=>this.Band.Settings as BillboardRepresentationSettings;

        public override RepresentationCost EstimateCost(){
            return new RepresentationCost(1024,1024,0f,0.1f);
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,CancellationToken cancellation){
            BillboardRepresentationSettings settings=this.Settings;
            if(settings==null||settings.Material==null){throw new InvalidOperationException("ビルボードの表現に材質がありません。");}
            Vector2 size=settings.Size;
            float diameter=this.Node.Definition.BoundingRadius*2f;
            if(!(size.x>0f)){size.x=diameter;}
            if(!(size.y>0f)){size.y=diameter;}
            var root=new GameObject("Billboard "+this.Node.Path);
            root.hideFlags=HideFlags.DontSave;
            root.transform.SetParent(context.Pool,false);
            var board=new GameObject("Quad");
            board.hideFlags=HideFlags.DontSave;
            board.transform.SetParent(root.transform,false);
            board.transform.localPosition=new Vector3(0f,settings.VerticalOffset,0f);
            board.transform.localScale=new Vector3(size.x,size.y,1f);
            board.AddComponent<MeshFilter>().sharedMesh=Quad();
            var renderer=board.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=settings.Material;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            this.Instance=root;
        }

        protected override void OnActivate(VirtualObjectContext context){
            base.OnActivate(context);
            this.Face(context);
        }

        protected override void OnUpdatePose(VirtualObjectContext context){
            base.OnUpdatePose(context);
            this.Face(context);
        }

        // 最も近い camera の方へ、上向きの周りだけ回す。
        private void Face(VirtualObjectContext context){
            Quaternion rotation=this.Node.EffectiveRotation;
            if(context.TryGetPrimaryViewer(this.Node.Origin,out VirtualObjectViewerState viewer)&&
                LogicalVector3.TrySubtract(viewer.Position,this.Node.Origin,out LogicalVector3 difference)&&difference.TryToFloat3(out float3 offset)){
                Vector3 up=rotation*Vector3.up;
                Vector3 toViewer=Vector3.ProjectOnPlane((Vector3)offset,up);
                if(toViewer.sqrMagnitude>1e-8f){rotation=Quaternion.LookRotation(-toViewer.normalized,up);}
            }
            this.Place(context,rotation);
        }

        // 原点を中心とする 1×1 の板。−Z の側から見て表(法線は −Z)。
        private static Mesh Quad(){
            if(quad!=null){return quad;}
            quad=new Mesh{name="Virtual Object Billboard",hideFlags=HideFlags.DontSave};
            quad.vertices=new[]{new Vector3(-0.5f,-0.5f,0f),new Vector3(0.5f,-0.5f,0f),new Vector3(0.5f,0.5f,0f),new Vector3(-0.5f,0.5f,0f)};
            quad.uv=new[]{new Vector2(0f,0f),new Vector2(1f,0f),new Vector2(1f,1f),new Vector2(0f,1f)};
            quad.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
            quad.triangles=new[]{0,3,2,0,2,1};
            quad.RecalculateBounds();
            return quad;
        }
    }
}
