using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualGeometry.World{
    /// <summary>Cell の生成と描画の状態を、選択から問い合わせる口。</summary>
    public interface IPlanetCellResidency{
        /// <summary>頂点が生成済みで描けるか。</summary>
        bool IsReady(in PlanetCellKey key);
        /// <summary>生成を要求する。priority が大きいほど先に生成する。</summary>
        void Request(in PlanetCellKey key,float priority);
        /// <summary>この frame で使った(退避しない)。</summary>
        void Touch(in PlanetCellKey key);
        /// <summary>生成した頂点から求めた実際の bounds(惑星のローカル座標)。まだ無ければ false(解析的な bounds を使う)。</summary>
        bool TryGetBounds(in PlanetCellKey key,out Vector3 center,out float radius);
    }

    /// <summary>選択に使う視点。位置は惑星のローカル座標、平面は world 座標。</summary>
    public readonly struct PlanetSelectionView{
        public readonly Vector3 CameraLocal;
        public readonly Matrix4x4 LocalToWorld;
        public readonly float MaximumScale;
        /// <summary>world 座標の視錐台の6平面。null なら視錐台で落とさない。</summary>
        public readonly Plane[] WorldPlanes;
        public readonly float ProjectionScale;
        public readonly float NearPlane;
        public readonly bool Orthographic;
        public readonly bool HorizonCulling;

        public PlanetSelectionView(Vector3 cameraLocal,Matrix4x4 localToWorld,float maximumScale,Plane[] worldPlanes,float projectionScale,float nearPlane,bool orthographic,bool horizonCulling){
            this.CameraLocal=cameraLocal;this.LocalToWorld=localToWorld;this.MaximumScale=maximumScale;this.WorldPlanes=worldPlanes;
            this.ProjectionScale=projectionScale;this.NearPlane=nearPlane;this.Orthographic=orthographic;this.HorizonCulling=horizonCulling;
        }
    }

    /// <summary>
    /// カメラから見て描く Cell を選ぶ。6つの root から深さ優先で辿り、次の順に決める。
    /// 1. bounds で、視錐台と地平線の外なら描かない。bounds は、生成済みの Cell なら実際の頂点から求めたもの、
    ///    無ければ解析的なもの(半径 R±Hmax で角・辺の中点・中心を囲む球。惑星全体の高さの幅を含むので大きい)。
    /// 2. 画面上の quad の大きさ(弧長×投影÷距離)がしきい値を超え、最大階層より浅ければ細分化を望む。
    ///    しきい値は前の frame に細分化していた Cell では (1−ヒステリシス) 倍、それ以外は (1+ヒステリシス) 倍。
    /// 3. 子4つがすべて生成済みなら子へ進み、そうでなければ自分を描いて子の生成を要求する(親は子が揃うまで描き続ける)。
    /// 4. 2:1 制約: 隣が2段以上細かい Cell は細分化する(子が揃っていなければ、描いたまま要求だけ出す)。
    /// root が生成済みでなければ、その面は描かない(穴を見せない)。
    /// 座標は float(P3。Logical 精度は P4 で Cell の局所原点に置き換える)。
    /// </summary>
    public sealed class PlanetCellSelector{
        private const int BalanceIterations=4;
        private readonly Stack<PlanetCellKey> stack=new Stack<PlanetCellKey>();
        private readonly HashSet<PlanetCellKey> selected=new HashSet<PlanetCellKey>();
        private readonly List<PlanetCellKey> scratch=new List<PlanetCellKey>();

        public float Radius{get;}
        public float HeightAmplitude{get;}
        public int CellQuads{get;}
        public int MaximumLevel{get;}
        /// <summary>skirt を quad の弧長の何倍だけ下げるか。bounds に含める。</summary>
        public float SkirtQuads{get;}
        /// <summary>画面上の quad の大きさの目標(px)。</summary>
        public float TargetPixels{get;set;}=2f;
        public float Hysteresis{get;set;}=0.1f;

        public PlanetCellSelector(float radius,float heightAmplitude,int cellQuads,int maximumLevel,float skirtQuads){
            if(!(radius>0)||float.IsInfinity(radius)){throw new ArgumentOutOfRangeException(nameof(radius));}
            if(!(heightAmplitude>=0)||heightAmplitude>=radius){throw new ArgumentOutOfRangeException(nameof(heightAmplitude));}
            if(cellQuads<1){throw new ArgumentOutOfRangeException(nameof(cellQuads));}
            if(maximumLevel<0||maximumLevel>PlanetCellCoordinate.Bits){throw new ArgumentOutOfRangeException(nameof(maximumLevel));}
            this.Radius=radius;this.HeightAmplitude=heightAmplitude;this.CellQuads=cellQuads;this.MaximumLevel=maximumLevel;this.SkirtQuads=skirtQuads;
        }

        /// <summary>この階層の1 quad の弧長(面の中央の値。等角写像なので面の中でほぼ一定)。親の半分。</summary>
        public float QuadArc(int level){
            return this.Radius*(Mathf.PI*0.5f)*MathF.Pow(2f,-level)/this.CellQuads;
        }

        /// <summary>skirt の深さ。</summary>
        public float SkirtDepth(int level){
            return this.QuadArc(level)*this.SkirtQuads;
        }

        /// <summary>Cell の面の座標の範囲(s0、t0、size)。</summary>
        public static void FaceRange(in PlanetCellKey key,out float s0,out float t0,out float size){
            size=MathF.Pow(2f,-key.Level);
            s0=key.X.Fraction(key.Level);t0=key.Y.Fraction(key.Level);
        }

        /// <summary>
        /// Cell の解析的な bounds(惑星のローカル座標)。角・辺の中点・中心を、半径 R−Hmax−skirt と R+Hmax の両方で囲み、
        /// 標本点の間で球面が外へふくらむ分(sagitta)を足す。
        /// </summary>
        public void Bounds(in PlanetCellKey key,out Vector3 center,out float radius){
            FaceRange(in key,out float s0,out float t0,out float size);
            Vector3 middle=CubeSphere.Direction(key.Face,s0+size*0.5f,t0+size*0.5f);
            float inner=this.Radius-this.HeightAmplitude-this.SkirtDepth(key.Level);
            float outer=this.Radius+this.HeightAmplitude;
            center=middle*(0.5f*(inner+outer));
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
        }

        /// <summary>
        /// 描く Cell を output に入れる。refined には今回細分化した Cell が入る(次の frame の previouslyRefined に渡す)。
        /// </summary>
        public void Select(in PlanetSelectionView view,IPlanetCellResidency residency,ISet<PlanetCellKey> previouslyRefined,List<PlanetCellKey> output,ISet<PlanetCellKey> refined){
            if(residency==null){throw new ArgumentNullException(nameof(residency));}
            if(output==null){throw new ArgumentNullException(nameof(output));}
            if(refined==null){throw new ArgumentNullException(nameof(refined));}
            output.Clear();refined.Clear();this.selected.Clear();this.stack.Clear();
            for(int face=CubeSphere.FaceCount-1;face>=0;face--){this.stack.Push(PlanetCellKey.Root(face));}
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
            this.CellBounds(in key,residency,out Vector3 center,out float radius);
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
                    this.CellBounds(in key,residency,out Vector3 center,out float radius);
                    float error=this.ScreenError(in view,key.Level,center,radius);
                    if(!this.TryRefine(in key,residency,error)){continue;}
                    this.selected.Remove(key);refined.Add(key);changed=true;
                    for(int child=0;child<4;child++){
                        PlanetCellKey childKey=key.Child(child);
                        this.CellBounds(in childKey,residency,out Vector3 childCenter,out float childRadius);
                        if(this.IsCulled(in view,childCenter,childRadius)){continue;}
                        residency.Touch(in childKey);
                        this.selected.Add(childKey);
                    }
                }
                if(!changed){return;}
            }
        }

        private void CellBounds(in PlanetCellKey key,IPlanetCellResidency residency,out Vector3 center,out float radius){
            if(residency.TryGetBounds(in key,out center,out radius)){return;}
            this.Bounds(in key,out center,out radius);
        }

        private static bool HasRefinedChild(in PlanetCellKey key,ISet<PlanetCellKey> refined){
            for(int child=0;child<4;child++){
                if(refined.Contains(key.Child(child))){return true;}
            }
            return false;
        }

        /// <summary>画面上の quad の大きさ(px)。</summary>
        public float ScreenError(in PlanetSelectionView view,int level,Vector3 center,float radius){
            float arc=this.QuadArc(level);
            if(view.Orthographic){return arc*view.MaximumScale*view.ProjectionScale;}
            float distance=Mathf.Max(view.NearPlane,(center-view.CameraLocal).magnitude*view.MaximumScale-radius*view.MaximumScale);
            return arc*view.MaximumScale*view.ProjectionScale/distance;
        }

        /// <summary>視錐台の外、または惑星の地平線の向こうにあるか。</summary>
        public bool IsCulled(in PlanetSelectionView view,Vector3 center,float radius){
            if(view.WorldPlanes!=null){
                Vector3 worldCenter=view.LocalToWorld.MultiplyPoint3x4(center);
                float worldRadius=radius*view.MaximumScale;
                for(int i=0;i<view.WorldPlanes.Length;i++){
                    if(view.WorldPlanes[i].GetDistanceToPoint(worldCenter)< -worldRadius){return true;}
                }
            }
            if(view.HorizonCulling&&!view.Orthographic){
                // 遮る球は R−Hmax、地形の一番外は R+Hmax。カメラから見える最も遠い点までの距離を超える bounds は見えない。
                float occluder=this.Radius-this.HeightAmplitude;
                float outer=this.Radius+this.HeightAmplitude;
                float distance=view.CameraLocal.magnitude;
                if(distance>occluder){
                    // (d−Ro)(d+Ro) の形にして、d と Ro が近いときの桁落ちを避ける。
                    float toHorizon=Mathf.Sqrt((distance-occluder)*(distance+occluder));
                    float beyond=Mathf.Sqrt((outer-occluder)*(outer+occluder));
                    if((center-view.CameraLocal).magnitude-radius>toHorizon+beyond){return true;}
                }
            }
            return false;
        }
    }
}
