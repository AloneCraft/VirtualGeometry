using System;
using System.Collections.Generic;
using UnityEngine;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// Cell の原点を半径 R の球面から外向きに持ち上げる高さ(m)を返す。sphereFrame は持ち上げていない Cell の値。
    /// 同じ Cell には常に同じ値を返すこと(選択の bounds と、生成した頂点の基準が一致する前提)。false または 0 なら持ち上げない。
    /// </summary>
    public delegate bool PlanetHeightShiftProvider(in PlanetCellFrame sphereFrame,out float shift);

    /// <summary>Cell の生成と描画の状態を、選択から問い合わせる口。</summary>
    public interface IPlanetCellResidency{
        /// <summary>頂点が生成済みで描けるか。</summary>
        bool IsReady(in PlanetCellKey key);
        /// <summary>生成を要求する。priority が大きいほど先に生成する。</summary>
        void Request(in PlanetCellKey key,float priority);
        /// <summary>この frame で使った(退避しない)。</summary>
        void Touch(in PlanetCellKey key);
        /// <summary>
        /// 生成した頂点から求めた実際の bounds。中心は Cell の原点(<see cref="PlanetCellFrame.Origin"/>、地表の高さまで持ち上げた点)からの相対位置。
        /// まだ無ければ false(解析的な bounds を使う)。
        /// </summary>
        bool TryGetBounds(in PlanetCellKey key,out Vector3 offset,out float radius);
    }

    /// <summary>
    /// 選択に使う視点。カメラの位置は惑星のローカル座標の Logical で持ち、Cell との差を Logical で取ってから float にする
    /// (惑星が大きくても、カメラの近くの小さな Cell の判定が潰れない)。
    /// cull 空間での Cell の位置は CameraCull + LocalToCull·(Cell − カメラ)(LocalToCull は線形部分だけを使う)。
    /// </summary>
    public readonly struct PlanetSelectionView{
        public readonly LogicalVector3 CameraLocal;
        /// <summary>カメラの、惑星の半径 R からの高さ(|CameraLocal|−R を Logical で求めたもの)。</summary>
        public readonly float CameraAltitude;
        public readonly Matrix4x4 LocalToCull;
        public readonly Vector3 CameraCull;
        public readonly float MaximumScale;
        /// <summary>cull 空間の視錐台の6平面。null なら視錐台で落とさない。</summary>
        public readonly Plane[] CullPlanes;
        public readonly float ProjectionScale;
        public readonly float NearPlane;
        public readonly bool Orthographic;
        public readonly bool HorizonCulling;

        public PlanetSelectionView(in LogicalVector3 cameraLocal,in LogicalScalar radius,Matrix4x4 localToCull,Vector3 cameraCull,float maximumScale,Plane[] cullPlanes,
            float projectionScale,float nearPlane,bool orthographic,bool horizonCulling){
            this.CameraLocal=cameraLocal;this.LocalToCull=localToCull;this.CameraCull=cameraCull;this.MaximumScale=maximumScale;this.CullPlanes=cullPlanes;
            this.ProjectionScale=projectionScale;this.NearPlane=nearPlane;this.Orthographic=orthographic;this.HorizonCulling=horizonCulling;
            this.CameraAltitude=float.PositiveInfinity;
            // TryLength は最大成分で正規化してから 2 乗するので、|v|² が固定小数点の範囲を超えない。
            if(LogicalVector3.TryLength(in cameraLocal,out LogicalScalar distance)&&LogicalScalar.TrySubtract(in distance,in radius,out LogicalScalar altitude)&&altitude.TryToFloat(out float value)){
                this.CameraAltitude=value;
            }
        }
    }

    /// <summary>
    /// カメラから見て描く Cell を選ぶ。6つの root から深さ優先で辿り、次の順に決める。
    /// 1. bounds で、視錐台と地平線の外なら描かない。bounds は、生成済みの Cell なら実際の頂点から求めたもの、
    ///    無ければ解析的なもの(半径 R±Hmax で Cell を囲む球。惑星全体の高さの幅を含むので大きい)。
    /// 2. 画面上の quad の大きさ(弧長×投影÷距離)がしきい値を超え、最大階層より浅ければ細分化を望む。
    ///    しきい値は前の frame に細分化していた Cell では (1−ヒステリシス) 倍、それ以外は (1+ヒステリシス) 倍。
    /// 3. 子4つがすべて生成済みなら子へ進み、そうでなければ自分を描いて子の生成を要求する(親は子が揃うまで描き続ける)。
    /// 4. 2:1 制約: 隣が2段以上細かい Cell は細分化する(子が揃っていなければ、描いたまま要求だけ出す)。
    /// root が生成済みでなければ、その面は描かない(穴を見せない)。
    /// bounds の中心は Cell の原点(Logical)からの相対位置で持ち、カメラとの差は Logical で取る。
    /// </summary>
    public sealed class PlanetCellSelector{
        private const int BalanceIterations=4;
        /// <summary>この階層から、解析的な bounds を <see cref="FrameBounds"/> で求める(面の座標の float では角が潰れるため)。</summary>
        public const int FrameBoundsLevel=14;
        private const int FrameCacheLimit=1<<16;
        private readonly Dictionary<PlanetCellKey,PlanetCellFrame> frames=new Dictionary<PlanetCellKey,PlanetCellFrame>();
        private readonly Stack<PlanetCellKey> stack=new Stack<PlanetCellKey>();
        private readonly HashSet<PlanetCellKey> selected=new HashSet<PlanetCellKey>();
        private readonly List<PlanetCellKey> scratch=new List<PlanetCellKey>();
        private readonly LogicalScalar logicalRadius;
        private readonly PlanetHeightShiftProvider heightShift;
        private readonly bool plane;

        public float Radius{get;}
        public LogicalScalar LogicalRadius=>this.logicalRadius;
        public float HeightAmplitude{get;}
        public int CellQuads{get;}
        public int MaximumLevel{get;}
        /// <summary>skirt を quad の弧長の何倍だけ下げるか。bounds に含める。</summary>
        public float SkirtQuads{get;}
        /// <summary>画面上の quad の大きさの目標(px)。</summary>
        public float TargetPixels{get;set;}=2f;
        public float Hysteresis{get;set;}=0.1f;

        /// <param name="heightShift">Cell の原点を持ち上げる高さの供給元。null なら持ち上げない(原点は半径 R の球面上)。</param>
        public PlanetCellSelector(float radius,float heightAmplitude,int cellQuads,int maximumLevel,float skirtQuads,PlanetHeightShiftProvider heightShift=null):this(radius,heightAmplitude,cellQuads,maximumLevel,skirtQuads,heightShift,false){
        }

        // plane が true のとき、radius は平面の一辺 extent(m)。
        private PlanetCellSelector(float radius,float heightAmplitude,int cellQuads,int maximumLevel,float skirtQuads,PlanetHeightShiftProvider heightShift,bool plane){
            if(plane){radius*=0.5f;}
            if(!(radius>0)||float.IsInfinity(radius)){throw new ArgumentOutOfRangeException(nameof(radius));}
            if(!(heightAmplitude>=0)||(!plane&&heightAmplitude>=radius)){throw new ArgumentOutOfRangeException(nameof(heightAmplitude));}
            if(cellQuads<1){throw new ArgumentOutOfRangeException(nameof(cellQuads));}
            if(maximumLevel<0||maximumLevel>PlanetCellFrame.MaximumLevel){throw new ArgumentOutOfRangeException(nameof(maximumLevel));}
            if(!LogicalScalar.TryFromFloat(radius,out this.logicalRadius)){throw new ArgumentOutOfRangeException(nameof(radius));}
            this.Radius=radius;this.HeightAmplitude=heightAmplitude;this.CellQuads=cellQuads;this.MaximumLevel=maximumLevel;this.SkirtQuads=skirtQuads;
            this.heightShift=heightShift;this.plane=plane;
        }

        /// <summary>平面の root の面(<see cref="CubeSphere"/> の +Y の面と同じ向き: X が +x、Y が +z、法線 +Y)。</summary>
        public const int PlaneFace=2;

        /// <summary>
        /// 一辺 extent(m)の正方形の平面を、球の代わりに分ける選択。root は 1 つ(<see cref="PlaneFace"/>)で、局所座標の XZ の原点が正方形の中心。
        /// 球と違い、地平線で落とさず、Cell の原点は持ち上げず、1 quad の長さは extent·2^-L/N。<see cref="Radius"/> は extent の半分(正方形に外接しない、大きさの目安)。
        /// 平面の root の縁の <see cref="PlanetCellKey.Neighbor"/> は別の面の Key を返すが、その Key は選ばれないので 2:1 の均しには影響しない。
        /// </summary>
        public static PlanetCellSelector CreatePlane(float extent,float heightAmplitude,int cellQuads,int maximumLevel,float skirtQuads){
            return new PlanetCellSelector(extent,heightAmplitude,cellQuads,maximumLevel,skirtQuads,null,true);
        }

        /// <summary>平面の選択か(球の選択なら false)。</summary>
        public bool IsPlane=>this.plane;
        /// <summary>平面の一辺(m)。球では 0。</summary>
        public float Extent=>this.plane?this.Radius*2f:0f;

        /// <summary>この階層の1 quad の弧長(面の中央の値。等角写像なので面の中でほぼ一定)。親の半分。平面では 1 quad の長さ。</summary>
        public float QuadArc(int level){
            if(this.plane){return this.Radius*2f*MathF.Pow(2f,-level)/this.CellQuads;}
            return this.Radius*(Mathf.PI*0.5f)*MathF.Pow(2f,-level)/this.CellQuads;
        }

        /// <summary>skirt の深さ。</summary>
        public float SkirtDepth(int level){
            return this.QuadArc(level)*this.SkirtQuads;
        }

        /// <summary>Cell の面の座標の範囲(s0、t0、size)。float なので細かい階層では潰れる(形には使わない)。</summary>
        public static void FaceRange(in PlanetCellKey key,out float s0,out float t0,out float size){
            size=MathF.Pow(2f,-key.Level);
            s0=key.X.Fraction(key.Level);t0=key.Y.Fraction(key.Level);
        }

        /// <summary>
        /// Cell の中心の値。原点は、高さの供給元があれば地表の高さまで持ち上げた点、無ければ半径 R の球面上の点。求めた値は覚えておく。
        /// 持ち上げる高さが有限でない、または |高さ| ≥ R のときは持ち上げない。
        /// </summary>
        public PlanetCellFrame Frame(in PlanetCellKey key){
            if(this.frames.TryGetValue(key,out PlanetCellFrame frame)){return frame;}
            if(this.plane){
                if(!LogicalScalar.TryScaleByPowerOfTwo(in this.logicalRadius,1,out LogicalScalar extent)||!PlanetCellFrame.TryCreatePlane(in key,in extent,out frame)){throw new InvalidOperationException("Cell の中心を求められません: "+key);}
                if(this.frames.Count>=FrameCacheLimit){this.frames.Clear();}
                this.frames.Add(key,frame);
                return frame;
            }
            if(!PlanetCellFrame.TryCreate(in key,in this.logicalRadius,out frame)){throw new InvalidOperationException("Cell の中心を求められません: "+key);}
            if(this.heightShift!=null&&this.heightShift(in frame,out float shift)&&shift!=0f&&Mathf.Abs(shift)<this.Radius&&frame.TryWithHeightShift(shift,out PlanetCellFrame lifted)){
                frame=lifted;
            }
            if(this.frames.Count>=FrameCacheLimit){this.frames.Clear();}
            this.frames.Add(key,frame);
            return frame;
        }

        /// <summary>Cell の原点(地表の高さまで持ち上げた点)からの相対位置 offset の点の、カメラからの相対位置(惑星のローカル座標の向き)。</summary>
        public Vector3 FromCamera(in PlanetSelectionView view,in PlanetCellKey key,Vector3 offset){
            PlanetCellFrame frame=this.Frame(in key);
            if(!LogicalVector3.TrySubtract(frame.Origin,view.CameraLocal,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 relative)){
                throw new InvalidOperationException("Cell とカメラの差を求められません: "+key);
            }
            return (Vector3)relative+offset;
        }

        /// <summary>
        /// Cell の解析的な bounds。中心は Cell の原点からの相対位置。粗い階層は <see cref="SampledBounds"/>、細かい階層は <see cref="FrameBounds"/>。
        /// </summary>
        public void Bounds(in PlanetCellKey key,out Vector3 offset,out float radius){
            if(this.plane){
                this.PlaneBounds(in key,out offset,out radius);
                return;
            }
            if(key.Level>=FrameBoundsLevel){
                this.FrameBounds(in key,out offset,out radius);
            }else{
                this.SampledBounds(in key,out offset,out radius);
            }
        }

        /// <summary>
        /// 平面の Cell の bounds。Cell の原点(平面上の中心)から、高さの幅(−skirt〜+振幅)の中間を中心にして、一辺 size の正方形と高さの幅を囲む球。
        /// </summary>
        public void PlaneBounds(in PlanetCellKey key,out Vector3 offset,out float radius){
            float size=this.Radius*2f*MathF.Pow(2f,-key.Level);
            float skirt=this.SkirtDepth(key.Level);
            offset=new Vector3(0,0.5f*(this.HeightAmplitude-skirt),0);
            float half=size*0.5f;float height=0.5f*(this.HeightAmplitude+skirt);
            radius=Mathf.Sqrt(2f*half*half+height*height);
        }

        /// <summary>
        /// 面の座標(float)の角・辺の中点・中心を、半径 R−Hmax−skirt と R+Hmax の両方で囲み、標本点の間で球面が外へふくらむ分(sagitta)を足す。
        /// 粗い階層用(Cell が大きいので、惑星のローカル座標の float で原点との差を取っても誤差は Cell より十分小さい)。
        /// </summary>
        public void SampledBounds(in PlanetCellKey key,out Vector3 offset,out float radius){
            FaceRange(in key,out float s0,out float t0,out float size);
            Vector3 middle=CubeSphere.Direction(key.Face,s0+size*0.5f,t0+size*0.5f);
            float inner=this.Radius-this.HeightAmplitude-this.SkirtDepth(key.Level);
            float outer=this.Radius+this.HeightAmplitude;
            Vector3 center=middle*(0.5f*(inner+outer));
            float farthest=0;float widest=0;
            for(int j=0;j<=2;j++){
                for(int i=0;i<=2;i++){
                    Vector3 direction=CubeSphere.Direction(key.Face,s0+size*0.5f*i,t0+size*0.5f*j);
                    farthest=Mathf.Max(farthest,Mathf.Max((direction*inner-center).magnitude,(direction*outer-center).magnitude));
                    widest=Mathf.Max(widest,Vector3.Angle(middle,direction)*Mathf.Deg2Rad);
                }
            }
            // 標本点(半角 widest/2 の間隔)の間の球面のふくらみ。
            float sagitta=outer*(1f-Mathf.Cos(Mathf.Min(widest,Mathf.PI*0.5f)*0.5f));
            radius=farthest+sagitta;
            offset=center-this.Frame(in key).ApproximateOrigin;
        }

        /// <summary>
        /// 中心を Cell の原点の方向に置き、半径を角度の幅から保守的に見積もる。細かい階層用。
        /// 等角写像では、Cell の中心から角・辺の中点までの角度は面の角度の幅 (π/2)·2^-L の最大 0.816 倍(面の角の Cell。
        /// 面の中央では √2/2 倍)なので、0.85 倍で見込む。
        /// </summary>
        public void FrameBounds(in PlanetCellKey key,out Vector3 offset,out float radius){
            PlanetCellFrame frame=this.Frame(in key);
            Vector3 direction=frame.ApproximateOrigin.normalized;
            float inner=this.Radius-this.HeightAmplitude-this.SkirtDepth(key.Level);
            float outer=this.Radius+this.HeightAmplitude;
            // 中心の半径 (inner+outer)/2 = R−skirt/2 と、原点(半径 R+HeightShift の点)との差だけを足す。
            offset=direction*(-0.5f*this.SkirtDepth(key.Level)-frame.HeightShift);
            float angle=Mathf.PI*0.5f*MathF.Pow(2f,-key.Level)*0.85f;
            radius=outer*angle+0.5f*(outer-inner)+outer*(1f-Mathf.Cos(angle));
        }

        /// <summary>
        /// 描く Cell を output に入れる。refined には今回細分化した Cell が入る(次の frame の previouslyRefined に渡す)。
        /// </summary>
        public void Select(in PlanetSelectionView view,IPlanetCellResidency residency,ISet<PlanetCellKey> previouslyRefined,List<PlanetCellKey> output,ISet<PlanetCellKey> refined){
            if(residency==null){throw new ArgumentNullException(nameof(residency));}
            if(output==null){throw new ArgumentNullException(nameof(output));}
            if(refined==null){throw new ArgumentNullException(nameof(refined));}
            output.Clear();refined.Clear();this.selected.Clear();this.stack.Clear();
            if(this.plane){
                this.stack.Push(PlanetCellKey.Root(PlaneFace));
            }else{
                for(int face=CubeSphere.FaceCount-1;face>=0;face--){this.stack.Push(PlanetCellKey.Root(face));}
            }
            while(this.stack.Count>0){
                PlanetCellKey key=this.stack.Pop();
                this.Visit(in key,in view,residency,previouslyRefined,refined);
            }
            this.Balance(in view,residency,refined);
            output.AddRange(this.selected);
        }

        private void Visit(in PlanetCellKey key,in PlanetSelectionView view,IPlanetCellResidency residency,ISet<PlanetCellKey> previouslyRefined,ISet<PlanetCellKey> refined){
            if(!residency.IsReady(in key)){
                // root だけがここへ来る(子は親が「4つとも生成済み」を確かめてから積む)。
                residency.Request(in key,float.MaxValue);
                return;
            }
            this.CellBounds(in view,in key,residency,out Vector3 center,out float radius);
            if(this.IsCulled(in view,center,radius)){return;}
            residency.Touch(in key);
            float error=this.ScreenError(in view,key.Level,center,radius);
            bool wasRefined=previouslyRefined!=null&&previouslyRefined.Contains(key);
            float threshold=this.TargetPixels*(1f+this.Hysteresis);
            if(wasRefined){threshold=this.TargetPixels*(1f-this.Hysteresis);}
            if(key.Level<this.MaximumLevel&&error>threshold&&this.TryRefine(in key,residency,error)){
                refined.Add(key);
                for(int child=3;child>=0;child--){this.stack.Push(key.Child(child));}
                return;
            }
            this.selected.Add(key);
        }

        // 子4つがすべて生成済みなら true。足りない子は生成を要求する。
        private bool TryRefine(in PlanetCellKey key,IPlanetCellResidency residency,float priority){
            bool ready=true;
            for(int child=0;child<4;child++){
                PlanetCellKey childKey=key.Child(child);
                residency.Touch(in childKey);
                if(!residency.IsReady(in childKey)){
                    residency.Request(in childKey,priority);
                    ready=false;
                }
            }
            return ready;
        }

        // 隣の同じ階層の Cell が今回細分化され、その子がさらに細分化されていれば、隣は2段以上細かい。
        private void Balance(in PlanetSelectionView view,IPlanetCellResidency residency,ISet<PlanetCellKey> refined){
            for(int iteration=0;iteration<BalanceIterations;iteration++){
                this.scratch.Clear();
                foreach(PlanetCellKey key in this.selected){
                    if(key.Level>=this.MaximumLevel){continue;}
                    for(int direction=0;direction<PlanetCellKey.DirectionCount;direction++){
                        PlanetCellKey neighbor=key.Neighbor(direction);
                        if(refined.Contains(neighbor)&&HasRefinedChild(in neighbor,refined)){
                            this.scratch.Add(key);
                            break;
                        }
                    }
                }
                bool changed=false;
                foreach(PlanetCellKey key in this.scratch){
                    this.CellBounds(in view,in key,residency,out Vector3 center,out float radius);
                    float error=this.ScreenError(in view,key.Level,center,radius);
                    if(!this.TryRefine(in key,residency,error)){continue;}
                    this.selected.Remove(key);refined.Add(key);changed=true;
                    for(int child=0;child<4;child++){
                        PlanetCellKey childKey=key.Child(child);
                        this.CellBounds(in view,in childKey,residency,out Vector3 childCenter,out float childRadius);
                        if(this.IsCulled(in view,childCenter,childRadius)){continue;}
                        residency.Touch(in childKey);
                        this.selected.Add(childKey);
                    }
                }
                if(!changed){return;}
            }
        }

        // bounds の中心のカメラからの相対位置と半径。
        private void CellBounds(in PlanetSelectionView view,in PlanetCellKey key,IPlanetCellResidency residency,out Vector3 center,out float radius){
            if(!residency.TryGetBounds(in key,out Vector3 offset,out radius)){this.Bounds(in key,out offset,out radius);}
            center=this.FromCamera(in view,in key,offset);
        }

        private static bool HasRefinedChild(in PlanetCellKey key,ISet<PlanetCellKey> refined){
            for(int child=0;child<4;child++){
                if(refined.Contains(key.Child(child))){return true;}
            }
            return false;
        }

        /// <summary>画面上の quad の大きさ(px)。center はカメラからの相対位置。</summary>
        public float ScreenError(in PlanetSelectionView view,int level,Vector3 center,float radius){
            float arc=this.QuadArc(level);
            if(view.Orthographic){return arc*view.MaximumScale*view.ProjectionScale;}
            float distance=Mathf.Max(view.NearPlane,center.magnitude*view.MaximumScale-radius*view.MaximumScale);
            return arc*view.MaximumScale*view.ProjectionScale/distance;
        }

        /// <summary>視錐台の外、または惑星の地平線の向こうにあるか。center はカメラからの相対位置。</summary>
        public bool IsCulled(in PlanetSelectionView view,Vector3 center,float radius){
            if(view.CullPlanes!=null){
                Vector3 cullCenter=view.CameraCull+view.LocalToCull.MultiplyVector(center);
                float cullRadius=radius*view.MaximumScale;
                for(int i=0;i<view.CullPlanes.Length;i++){
                    if(view.CullPlanes[i].GetDistanceToPoint(cullCenter)< -cullRadius){return true;}
                }
            }
            if(view.HorizonCulling&&!view.Orthographic&&!this.plane){
                // 遮る球は R−Hmax、地形の一番外は R+Hmax。カメラから見える最も遠い点までの距離を超える bounds は見えない。
                // d−Ro はカメラの高さ(Logical で求めた値)から作り、d と Ro が近いときの桁落ちを避ける。
                float occluder=this.Radius-this.HeightAmplitude;
                float outer=this.Radius+this.HeightAmplitude;
                float aboveOccluder=view.CameraAltitude+this.HeightAmplitude;
                if(aboveOccluder>0&&!float.IsInfinity(aboveOccluder)){
                    float toHorizon=Mathf.Sqrt(aboveOccluder*(aboveOccluder+2f*occluder));
                    float beyond=Mathf.Sqrt((outer-occluder)*(outer+occluder));
                    if(center.magnitude-radius>toHorizon+beyond){return true;}
                }
            }
            return false;
        }
    }
}
