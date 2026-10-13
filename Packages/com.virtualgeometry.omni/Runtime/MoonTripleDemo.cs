using System;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using CosmicPlanetSurface=Omni.CosmicScale.CosmicPlanetSurface;
using RadialCamera=Omni.RadialCamera;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 3 つの月を y 軸(上向き)に積んで 1 点で接触させ、接触点にサッカーボールを置いて、面の位置の誤差(食い込み)を見るデモ(Omni の Radial 対数描画)。
    /// 3 つの月は、描き方が違う: 下から、方式 1 = 単純な巨大化(普通の月)、方式 2 = VG(VG の月 A)、方式 3 = PlanetSurface(Omni の分割した地面。月 B)。
    /// 普通の月は、単位球のメッシュ(CosmicScale の CosmicSphere)を直径 2R(R = 1,737.4 km)に拡大した 1 つの物体。物体空間の float の分解能が 0.2 m 前後になり、面の位置・深度が量子化される
    /// (Omni の COSMIC-PRECISION-001)。VG の月は <see cref="PlanetTerrainSource"/> の cube-sphere の Cell で、Cell の原点は Logical、頂点は Cell の原点からの相対位置なので、誤差は mm 以下。
    /// PlanetSurface の月は、Omni の PlanetSurfaceLod(カメラに追従して cube-sphere を分割するパッチ)で、頂点はパッチの中心からの差で持つ。
    /// 接触点では 2 つの面が接平面(y 一定)に接し、接触点から距離 d 離れると、面の間の幅は d²/R ほど開く。ボール(半径 0.11 m)は接触点では両側の面に食い込み、
    /// 幅が 0.22 m になる距離(<see cref="MoonTripleLayout.TouchDistance"/>、約 618 m)で両側の面に接する。片方の面が y 方向に δ ずれていると、この距離が変わる。
    /// camera は自由に飛べる(位置と向きを独立に持つ)。ボールは接触点からの 0.1mm 単位のずれで動き、camera はボールに付いて動かない(C でボールに向け直す)。
    /// 位置はすべて Logical のまま計算する。テレポートと隙間の変更は IMGUI のボタン(<see cref="TeleportTo"/>、<see cref="AdjustGap"/>)。
    /// この component は camera の GameObject(Logical.Transform と RadialCamera を持つ)に付ける。
    /// 操作(Input System): ↑↓ ボールを y(上の月/下の月へ)、←→ x、PageUp/PageDown z(Shift で 10 倍、Ctrl で 1/10)、
    ///   W S A D 前後左右に飛ぶ、Q E 上下、Home End とホイール 向いている方向へ、右ドラッグ 向き、C ボールに向け直す、
    ///   1 接触点 0 へ 2 接触点 1 へ(ボールが両側の面に接する距離)、3 接触点そのもの、[ ] 今の接触点の隙間を 1mm 小さく/大きく、R 初期化
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MoonTripleDemo:MonoBehaviour{
        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;
        /// <summary>テレポートと C で、ボールの中心から camera までの距離(m)。</summary>
        public const float FrameDistance=0.35f;
        /// <summary>飛ぶ速さ(m/s)。Shift で 10 倍、Ctrl で 1/10。</summary>
        public const double FlySpeed=0.3;
        // 先読みの待ち時間(frame)。これを超えたら準備ができていなくても移る。
        private const int PrefetchLimit=900;
        /// <summary>論理ニアクリップのプリセット(m)。通常(0.03)、1 mm、1 µm、1 nm、10 pm。camera の Near は 0.3 のままで、View Scale M = 0.3 ÷ 論理 near にする(<c>RadialCamera.SetViewScale</c>)。</summary>
        public static readonly double[] ScaleNearMeters={0.03,1e-3,1e-6,1e-9,1e-11};
        public static readonly string[] ScaleNames={"通常 0.03 m","1 mm","1 µm","1 nm","10 pm"};
        /// <summary>通常の論理 near(m)。飛ぶ速さ・ホイールの量は、論理 near のこの比に比例する(原子の近くでは小さな動きにする)。</summary>
        public const double DefaultNearMeters=0.03;
        /// <summary>原子へのテレポートで、原子の中心から camera までの距離(原子の半径の倍数)。</summary>
        public const double AtomFrameRadii=4;
        // Omni の CosmicPlanetLit(ボール・月)は URP のライトを使わず、_CosmicKeyLight(画面に向かう光の向き、強さ)で照らす。
        // 方向は MoonTripleSceneBuilder の太陽(travel = (0.2, -0.8, -0.55))の逆、強さは太陽の 1.3。
        private static readonly int CosmicKeyLightId=Shader.PropertyToID("_CosmicKeyLight");
        private static readonly Vector4 CosmicKeyLight=new Vector4(-0.2f,0.8f,0.55f,1.3f);

        [SerializeField] private MoonTripleLayout layout=MoonTripleLayout.Default;
        [SerializeField] private GameObject plainMoon;
        [SerializeField] private PlanetTerrainSource moonA;
        // 月 B は方式 3(PlanetSurface。Omni の分割した地面)。
        [SerializeField] private CosmicPlanetSurface moonB;
        // ボールは Omni のサッカーボール(通常の MeshRenderer、CosmicPlanetLit。VG ではない)。
        [SerializeField] private GameObject ball;
        // 原子サイズ(半径 1e-10 m)の VG の球。月 A と同じ PlanetTerrainSource の処理で描く(無ければ、原子の機能は使えない)。
        [SerializeField] private PlanetTerrainSource atom;
        [SerializeField] private bool showHud=true;

        private LogicalTransform plainLogical,aLogical,bLogical,ballLogical,cameraLogical,atomLogical;
        private RadialCamera radial;
        private Camera unityCamera;
        // 論理ニアクリップのプリセットの番号(<see cref="ScaleNearMeters"/>)。
        private int scalePreset;
        // camera を原子の周りに置いているか(C でボールではなく原子に向け直す)。
        private bool atomView;
        private bool pendingAtom;
        private bool initialized;
        private string failure;
        private int contact;
        // ボールの接触点からのずれ(0.1mm 単位の整数): x=+x 方向、y=上向き(上の月へ)、z=+z 方向。
        private long offsetX,offsetY,offsetZ;
        private long gap01Units,gap12Units;
        private float cameraYaw=180f,cameraPitch=10f;
        private Quaternion cameraRotation=Quaternion.identity;
        private double3 cameraPosition;
        // テレポートの待ち(先読みが終わるまで、今の状態のまま)。
        private bool pending;
        private int pendingFrames;
        private int pendingContact;
        private long pendingX,pendingY,pendingZ;
        private float repeatX,repeatY,repeatZ;
        private GUIStyle style;
        private GUIStyle buttonStyle;
        private IMoonTripleKeys keys=MoonTripleKeyboard.Instance;

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        public MoonTripleLayout Layout=>this.layout;
        public int ContactIndex=>this.contact;
        public long OffsetX=>this.offsetX;
        public long OffsetY=>this.offsetY;
        public long OffsetZ=>this.offsetZ;
        public long Gap01Units=>this.gap01Units;
        public long Gap12Units=>this.gap12Units;
        public float CameraYaw=>this.cameraYaw;
        public float CameraPitch=>this.cameraPitch;
        public Quaternion CameraRotation=>this.cameraRotation;
        public double3 CameraPosition=>this.cameraPosition;
        /// <summary>テレポートの先読みを待っている間は true。</summary>
        public bool IsPending=>this.pending;
        public GameObject PlainMoon{get=>this.plainMoon;set=>this.plainMoon=value;}
        public PlanetTerrainSource MoonA{get=>this.moonA;set=>this.moonA=value;}
        public CosmicPlanetSurface MoonB{get=>this.moonB;set=>this.moonB=value;}
        public GameObject Ball{get=>this.ball;set=>this.ball=value;}
        public PlanetTerrainSource Atom{get=>this.atom;set=>this.atom=value;}
        /// <summary>論理ニアクリップのプリセットの番号。</summary>
        public int ScalePreset=>this.scalePreset;
        /// <summary>camera を原子の周りに置いているか。</summary>
        public bool IsAtomView=>this.atomView;
        /// <summary>飛ぶ速さ・ホイールの量にかける倍率 = 今の論理 near ÷ 通常の論理 near。</summary>
        public double ScaleFactor=>ScaleNearMeters[this.scalePreset]/DefaultNearMeters;
        /// <summary>
        /// 論理 near を Omni から読み戻した値(m)。<see cref="RadialCamera.EffectiveNearClip"/>。読めなければ NaN。
        /// </summary>
        public double LogicalNearMeters{
            get{
                if(this.radial==null){return double.NaN;}
                if(this.radial.EffectiveNearClip.TryToFloat(out float value)){return value;}
                return double.NaN;
            }
        }
        /// <summary>View Scale M の log2 を Omni から読み戻した値。</summary>
        public float LogicalLog2ViewScale{get{if(this.radial==null){return float.NaN;}return this.radial.EffectiveLog2ViewScale;}}
        /// <summary>camera の Near(m)。論理 near ÷ M の分子で、プリセットを変えても 0.3 のまま。</summary>
        public float CameraNearPlane{get{if(this.unityCamera==null){return float.NaN;}return this.unityCamera.nearClipPlane;}}
        /// <summary>camera から原子の中心までの距離(m)。原子が無ければ NaN。</summary>
        public double DistanceToAtom{get{if(this.atom==null){return double.NaN;}return math.length(this.cameraPosition-this.layout.AtomCenter);}}
        /// <summary>キーの入力元(既定は Input System。null を入れると既定に戻る)。</summary>
        public IMoonTripleKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=MoonTripleKeyboard.Instance;}else{this.keys=value;}
            }
        }

        private void OnEnable(){
            this.initialized=false;this.failure=null;
        }

        private void Update(){
            if(this.initialized&&!this.MoonsReady()){
                this.initialized=false;
                return;
            }
            if(!this.initialized){
                if(!this.MoonsReady()){return;}
                if(!this.Initialize()){return;}
            }
            this.ReadKeys();
            this.UpdatePending();
            this.Apply();
        }

        /// <summary>方式 2(VG の月 A)の Selector と、方式 3(PlanetSurface の月 B)の分割した地面が使える状態か。</summary>
        public bool MoonsReady(){
            if(this.atom!=null&&this.atom.Selector==null){return false;}
            return this.moonA!=null&&this.moonB!=null&&this.moonA.Selector!=null&&this.moonB.Lod!=null;
        }

        /// <summary>3 つの月を置き、ボールと camera を接触点 0 へ置く。VG の月 A の Selector と PlanetSurface の月 B の分割した地面が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            if(this.plainMoon==null||this.moonA==null||this.moonB==null||this.ball==null){return this.Fail("普通の月・VG の月 A・PlanetSurface の月 B・ボールが必要です。");}
            // PlanetSurface の月 B は 1 フレーム 1 段ずつ分割する。予算が残る限り同じフレームで次の分割まで続け、要求の階層(最大 20)へ早く届かせる。
            this.moonB.Lod.FastRefinement=true;
            this.plainLogical=this.plainMoon.GetComponent<LogicalTransform>();
            this.aLogical=this.moonA.GetComponent<LogicalTransform>();
            this.bLogical=this.moonB.GetComponent<LogicalTransform>();
            this.ballLogical=this.ball.GetComponent<LogicalTransform>();
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.plainLogical==null||this.aLogical==null||this.bLogical==null||this.ballLogical==null||this.cameraLogical==null){
                return this.Fail("3 つの月・ボール・camera に Omni の Logical.Transform が必要です。");
            }
            if(this.atom!=null){
                this.atomLogical=this.atom.GetComponent<LogicalTransform>();
                if(this.atomLogical==null){return this.Fail("原子の VG の球に Omni の Logical.Transform が必要です。");}
            }
            // 論理ニアクリップの切り替えは、camera の RadialCamera に SetViewScale で行う(camera の Near は 0.3 のまま)。
            this.radial=this.GetComponent<RadialCamera>();
            this.unityCamera=this.GetComponent<Camera>();
            if(this.radial==null||this.unityCamera==null){return this.Fail("camera に RadialCamera と Camera が必要です。");}
            this.atomView=false;this.pendingAtom=false;
            this.SetScalePreset(0);
            this.gap01Units=0;this.gap12Units=0;
            this.layout.Gap01=0;this.layout.Gap12=0;
            Shader.SetGlobalVector(CosmicKeyLightId,new Vector4(CosmicKeyLight.x,CosmicKeyLight.y,CosmicKeyLight.z,CosmicKeyLight.w));
            // ボールの直径(Omni の CosmicSphere は直径 1 の単位球なので、直径 2·BallRadius へ拡大する)。
            if(!LogicalScalar.TryFromDouble(2*MoonTripleLayout.BallRadius,out LogicalScalar ballDiameter)){return this.Fail("ボールの大きさを Logical にできません。");}
            this.ballLogical.LocalScale=new LogicalVector3(in ballDiameter,in ballDiameter,in ballDiameter);
            this.ballLogical.SyncUnityTransform();
            if(!this.ApplyLayout()){return false;}
            this.initialized=true;
            this.SetState(0,0,0,(long)Math.Round(this.layout.TouchDistance(0)/UnitMeters),180f,10f,0.35f);
            return this.failure==null;
        }

        // 月の位置と、VG の月の格子の基準点・軸。接触点 0、1 の位置は隙間で変わる。
        private bool ApplyLayout(){
            this.layout.Gap01=this.gap01Units*UnitMeters;this.layout.Gap12=this.gap12Units*UnitMeters;
            double radius=this.layout.Radius;
            if(!(radius>0)){return this.Fail("月の半径は正である必要があります。");}
            if(this.moonA.Radius!=(float)radius||Math.Abs(this.moonB.RadiusMeters-radius)>1e-6*radius){
                return this.Fail("月の半径(VG の月 A "+this.moonA.Radius.ToString("G7",CultureInfo.InvariantCulture)+" m、PlanetSurface の月 B "+this.moonB.RadiusMeters.ToString("G7",CultureInfo.InvariantCulture)+" m)と、配置の半径("+radius.ToString("G7",CultureInfo.InvariantCulture)+" m)が違います。");
            }
            if(!LogicalScalar.TryFromDouble(2*radius,out LogicalScalar diameter)){return this.Fail("普通の月の倍率を Logical にできません。");}
            if(!TryFromDouble3(new double3(0,this.layout.CenterPlain,0),out LogicalVector3 plain)||!TryFromDouble3(new double3(0,this.layout.CenterA,0),out LogicalVector3 a)||
                !TryFromDouble3(new double3(0,this.layout.CenterB,0),out LogicalVector3 b)){
                return this.Fail("月の位置を Logical にできません。");
            }
            this.plainLogical.LocalScale=new LogicalVector3(in diameter,in diameter,in diameter);
            this.plainLogical.LocalPosition=plain;
            this.plainLogical.LocalRotation=LogicalQuaternion.Identity;
            this.plainLogical.SyncUnityTransform();
            this.aLogical.LocalPosition=a;this.aLogical.LocalRotation=LogicalQuaternion.Identity;this.aLogical.SyncUnityTransform();
            this.bLogical.LocalPosition=b;this.bLogical.LocalRotation=LogicalQuaternion.Identity;this.bLogical.SyncUnityTransform();
            if(this.atom!=null){
                if(this.atom.Radius!=(float)MoonTripleLayout.AtomRadius){
                    return this.Fail("原子の半径("+this.atom.Radius.ToString("G7",CultureInfo.InvariantCulture)+" m)と、配置の半径("+MoonTripleLayout.AtomRadius.ToString("G7",CultureInfo.InvariantCulture)+" m)が違います。");
                }
                // 原子の中心(x = z = 0.5 m、y = 接触点 0 の隙間の中央)。座標が 1 m 前後なので、camera の倍精度の刻み(1e-16 m)は原子の大きさ(1e-10 m)より十分小さい。
                if(!TryFromDouble3(this.layout.AtomCenter,out LogicalVector3 atomCenter)){return this.Fail("原子の位置を Logical にできません。");}
                this.atomLogical.LocalPosition=atomCenter;this.atomLogical.LocalRotation=LogicalQuaternion.Identity;this.atomLogical.SyncUnityTransform();
            }
            this.SetReferences();
            return true;
        }

        /// <summary>
        /// 論理ニアクリップのプリセットを選ぶ(<see cref="ScaleNearMeters"/>)。camera の Near(0.3 m)を論理 near で割った View Scale M を <c>RadialCamera.SetViewScale</c> に渡す。
        /// 論理 near より手前は描かれず、VG の Cell の細分化も、この論理 near を下限にして画面誤差を計算する(OmniPlanetPlacement)ので、
        /// 原子の頂点間隔(~1e-12 m)を描くには、論理 near を 1e-11 m 前後以下にする。
        /// </summary>
        public bool SetScalePreset(int index){
            if(this.radial==null||this.unityCamera==null){return false;}
            this.scalePreset=Math.Max(0,Math.Min(ScaleNearMeters.Length-1,index));
            float scale=(float)(this.unityCamera.nearClipPlane/ScaleNearMeters[this.scalePreset]);
            this.radial.SetViewScale(scale);
            return true;
        }

        // 格子の基準点は、今の接触点の、VG の月 A の面の点(月の中心からの位置)。格子の軸は接平面(x、z)。
        // PlanetSurface の月 B は、格子の基準点を持たない(Omni の分割した地面が、カメラの位置から自分で分割する)。
        private void SetReferences(){
            double radius=this.layout.Radius;
            // 接触点 0 では A の下端、接触点 1 では A の上端。
            double ay=-radius;
            if(this.contact==1){ay=radius;}
            if(TryFromDouble3(new double3(0,ay,0),out LogicalVector3 aSpot)){this.moonA.SetReferencePoint(in aSpot);}
            Material material=this.moonA.Material;
            if(material!=null){
                material.SetVector("_GridAxisU",new Vector4(1,0,0,0));
                material.SetVector("_GridAxisV",new Vector4(0,0,1,0));
            }
        }

        /// <summary>
        /// 接触点 index(0 = 普通の月(方式 1) - VG の月 A(方式 2)、1 = VG の月 A(方式 2) - PlanetSurface の月 B(方式 3))へテレポートする。touchRing が true なら、ボールが両側の面に接する距離(接触点から +z へ。camera は外側から接触点を向く)、false なら接触点そのもの。
        /// 移動先の VG の月の Cell が生成されるまで(先読み)、今の状態のまま待つ。IMGUI のボタンとキーから同じ入口を使う。
        /// </summary>
        public void TeleportTo(int index,bool touchRing){
            if(!this.initialized){return;}
            this.pendingContact=Math.Max(0,Math.Min(1,index));
            this.pendingX=0;this.pendingY=0;
            this.pendingZ=0;
            if(touchRing){this.pendingZ=(long)Math.Round(this.layout.TouchDistance(this.pendingContact)/UnitMeters);}
            this.pendingAtom=false;
            this.pending=true;this.pendingFrames=0;
        }

        /// <summary>
        /// 原子サイズの VG の球(半径 1e-10 m)へテレポートする。移動先の Cell が生成されるまで(先読み)、今の状態のまま待ち、移るときに、論理ニアクリップを最小のプリセット(10 pm)にして、
        /// camera を原子の中心から半径の 4 倍の距離に置く。ボールの側へ戻るテレポートは、論理 near を通常に戻す。
        /// </summary>
        public void TeleportToAtom(){
            if(!this.initialized||this.atom==null){return;}
            this.pendingAtom=true;
            this.pending=true;this.pendingFrames=0;
        }

        /// <summary>
        /// ボールの中心を、今の接触点の下の月(lowerMoon)または上の月の、理想の球の面に置く(今の横の位置 x、z のまま)。ボールの半分がその月に埋まる。
        /// ボールの赤道の線と、月の面の境界が一致すれば、その月の面は理想の位置にある。ずれが、面の高さの誤差になる。
        /// </summary>
        public void BuryBall(bool lowerMoon){
            if(!this.initialized){return;}
            double x=this.offsetX*UnitMeters;double z=this.offsetZ*UnitMeters;
            double lateral=Math.Sqrt(x*x+z*z);
            double surface;
            if(lowerMoon){surface=this.layout.LowerSurfaceY(this.contact,lateral);}else{surface=this.layout.UpperSurfaceY(this.contact,lateral);}
            this.offsetY=(long)Math.Round((surface-this.layout.ContactY(this.contact))/UnitMeters);
            this.Apply();
        }

        /// <summary>接触点 index の隙間を units(0.1mm 単位。正で離す、負で食い込ませる)だけ変える。ボールは接触点からのずれのまま、新しい接触点に付いて動く。</summary>
        public void AdjustGap(int index,long units){
            if(!this.initialized){return;}
            if(index==0){this.gap01Units+=units;}else{this.gap12Units+=units;}
            this.ApplyLayout();
            this.Apply();
        }

        /// <summary>接触点・ボールのずれ・camera を一度に決める(検証用)。camera はボールの周り(yaw、pitch、ボールから distance)に置く。先読みは待たない。</summary>
        public bool SetState(int contactIndex,long x,long y,long z,float yaw,float pitch,float distance){
            this.pending=false;this.pendingAtom=false;this.atomView=false;
            this.contact=Math.Max(0,Math.Min(1,contactIndex));
            this.offsetX=x;this.offsetY=y;this.offsetZ=z;
            this.SetReferences();
            this.FrameBall(yaw,pitch,distance);
            return this.Apply();
        }

        /// <summary>camera を、ボールの中心から distance だけ離れた、yaw・pitch の向きの位置に置く(ボールの中心を向く)。</summary>
        public void FrameBall(float yaw,float pitch,float distance){
            this.cameraYaw=yaw;this.cameraPitch=pitch;
            this.cameraPosition=this.BallCenterDouble()-ForwardOf(yaw,pitch)*distance;
        }

        /// <summary>camera を、原子の中心から distance(m)だけ離れた、yaw・pitch の向きの位置に置く(原子の中心を向く)。座標が 1 m 前後なので、倍精度で原子の大きさ(1e-10 m)を十分に表せる。</summary>
        public void FrameAtom(float yaw,float pitch,double distance){
            this.cameraYaw=yaw;this.cameraPitch=pitch;
            this.cameraPosition=this.layout.AtomCenter-ForwardOf(yaw,pitch)*distance;
            this.atomView=true;
        }

        /// <summary>camera の位置と向き(yaw、pitch)を直接決める(検証用。ボールの中心以外の点を狙うとき)。</summary>
        public bool PlaceCamera(double3 position,float yaw,float pitch){
            this.cameraYaw=yaw;this.cameraPitch=pitch;this.cameraPosition=position;
            return this.Apply();
        }

        // 向き(yaw、pitch)の前方。pitch は水平から下を向く角度(正で下向き)。
        private static double3 ForwardOf(float yaw,float pitch){
            double y=yaw*Math.PI/180.0;double p=pitch*Math.PI/180.0;
            return new double3(Math.Sin(y)*Math.Cos(p),-Math.Sin(p),Math.Cos(y)*Math.Cos(p));
        }

        /// <summary>隙間を決める(検証用。0.1mm 単位)。</summary>
        public bool SetGaps(long gap01,long gap12){
            this.gap01Units=gap01;this.gap12Units=gap12;
            if(!this.ApplyLayout()){return false;}
            return this.Apply();
        }

        /// <summary>ボールの中心(Logical)= 接触点 + (x, y, z)。</summary>
        public bool TryGetBallCenter(out LogicalVector3 center){
            center=default;
            if(!this.initialized){return false;}
            return TryFromDouble3(new double3(this.offsetX*UnitMeters,this.layout.ContactY(this.contact)+this.offsetY*UnitMeters,this.offsetZ*UnitMeters),out center);
        }

        /// <summary>ボールの中心(倍精度。検証の解析用)。</summary>
        public double3 BallCenterDouble(){
            return new double3(this.offsetX*UnitMeters,this.layout.ContactY(this.contact)+this.offsetY*UnitMeters,this.offsetZ*UnitMeters);
        }

        /// <summary>ボールと camera を、今の状態に合わせて置く。失敗したら理由を <see cref="Failure"/> に残す。</summary>
        public bool Apply(){
            if(!this.initialized){return false;}
            this.failure=null;
            if(!this.TryGetBallCenter(out LogicalVector3 center)){return this.Fail("ボールの位置を求められません。");}
            this.ballLogical.LocalPosition=center;
            this.ballLogical.LocalRotation=LogicalQuaternion.Identity;
            this.ballLogical.SyncUnityTransform();
            return this.ApplyCamera();
        }

        // camera を、保存している位置と向き(yaw、pitch)に置く。位置は飛んで動くので、ボールには付けない。
        private bool ApplyCamera(){
            double3 forward=ForwardOf(this.cameraYaw,this.cameraPitch);
            if(!TryFromDouble3(this.cameraPosition,out LogicalVector3 position)){
                return this.Fail("camera の位置を Logical にできません。");
            }
            this.cameraRotation=Quaternion.LookRotation(new Vector3((float)forward.x,(float)forward.y,(float)forward.z),Vector3.up);
            if(!LogicalQuaternion.TryFromFloat4(this.cameraRotation.x,this.cameraRotation.y,this.cameraRotation.z,this.cameraRotation.w,out LogicalQuaternion logical)){return this.Fail("camera の回転を Logical にできません。");}
            this.cameraLogical.LocalPosition=position;
            this.cameraLogical.LocalRotation=logical;
            this.cameraLogical.SyncUnityTransform();
            return true;
        }

        // テレポートの先読み: 移動先のボールの中心の、VG の月の面の Cell を生成させる。両方そろったら(または待ちすぎたら)移る。
        private void UpdatePending(){
            if(!this.pending){return;}
            this.pendingFrames++;
            if(this.pendingAtom){
                // 原子へのテレポート: 原子の面の点(中心から +z に半径の分)の Cell を先読みし、camera が来る前に細かい階層を用意する。
                bool readyAtom=true;
                if(TryFromDouble3(new double3(0,0,MoonTripleLayout.AtomRadius),out LogicalVector3 onAtom)){
                    readyAtom&=this.atom.Prefetch(in onAtom,(float)((AtomFrameRadii-1)*MoonTripleLayout.AtomRadius));
                }
                if(!readyAtom&&this.pendingFrames<PrefetchLimit){return;}
                this.pending=false;this.pendingAtom=false;
                this.SetScalePreset(ScaleNearMeters.Length-1);
                this.FrameAtom(180f,10f,AtomFrameRadii*MoonTripleLayout.AtomRadius);
                return;
            }
            double radius=this.layout.Radius;
            double x=this.pendingX*UnitMeters;double z=this.pendingZ*UnitMeters;
            bool ready=true;
            // 点は VG の月 A の面の上(中心からの位置)。接触点の面の点 = 中心から ±R の y 軸上、ずれ (x, z) だけ接平面に沿って動かした点(Prefetch は方向だけを使う)。
            // PlanetSurface の月 B は先読みを持たない(カメラの位置から追従して分割する)。
            if(TryFromDouble3(new double3(x,this.pendingContact==0?-radius:radius,z),out LogicalVector3 onA)){ready&=this.moonA.Prefetch(in onA,FrameDistance);}
            if(!ready&&this.pendingFrames<PrefetchLimit){return;}
            this.pending=false;
            // 原子の周りから戻るときは、論理ニアクリップを通常に戻す。
            if(this.atomView){this.atomView=false;this.SetScalePreset(0);}
            this.contact=this.pendingContact;
            this.offsetX=this.pendingX;this.offsetY=this.pendingY;this.offsetZ=this.pendingZ;
            this.SetReferences();
            this.FrameBall(180f,10f,FrameDistance);
        }

        private void ReadKeys(){
            IMoonTripleKeys input=this.keys;
            long step=10;
            if(input.Held(MoonTripleKey.Coarse)){step=100;}
            if(input.Held(MoonTripleKey.Fine)){step=1;}
            this.NudgeKey(input,MoonTripleKey.BallXMinus,-step,ref this.offsetX,ref this.repeatX);
            this.NudgeKey(input,MoonTripleKey.BallXPlus,step,ref this.offsetX,ref this.repeatX);
            this.NudgeKey(input,MoonTripleKey.BallDown,-step,ref this.offsetY,ref this.repeatY);
            this.NudgeKey(input,MoonTripleKey.BallUp,step,ref this.offsetY,ref this.repeatY);
            this.NudgeKey(input,MoonTripleKey.BallZMinus,-step,ref this.offsetZ,ref this.repeatZ);
            this.NudgeKey(input,MoonTripleKey.BallZPlus,step,ref this.offsetZ,ref this.repeatZ);
            this.Fly(input);
            if(input.PressedThisFrame(MoonTripleKey.Contact0)){this.TeleportTo(0,true);}
            if(input.PressedThisFrame(MoonTripleKey.Contact1)){this.TeleportTo(1,true);}
            if(input.PressedThisFrame(MoonTripleKey.Touch)){this.TeleportTo(this.contact,true);}
            if(input.PressedThisFrame(MoonTripleKey.Exact)){this.TeleportTo(this.contact,false);}
            if(input.PressedThisFrame(MoonTripleKey.BuryLower)){this.BuryBall(true);}
            if(input.PressedThisFrame(MoonTripleKey.BuryUpper)){this.BuryBall(false);}
            if(input.PressedThisFrame(MoonTripleKey.ScaleFiner)){this.SetScalePreset(this.scalePreset+1);}
            if(input.PressedThisFrame(MoonTripleKey.ScaleCoarser)){this.SetScalePreset(this.scalePreset-1);}
            if(input.PressedThisFrame(MoonTripleKey.TeleportAtom)){this.TeleportToAtom();}
            if(input.PressedThisFrame(MoonTripleKey.GapDown)){this.AdjustGap(this.contact,-step);}
            if(input.PressedThisFrame(MoonTripleKey.GapUp)){this.AdjustGap(this.contact,step);}
            if(input.PressedThisFrame(MoonTripleKey.Reset)){this.TeleportTo(this.contact,true);}
        }

        // 飛ぶ: W S は向いている方向の前後、A D は左右、Q E は世界の上下。Home End とホイールは向いている方向へ。右ドラッグで向きを変え、C でボールに向け直す。
        // 斜めに押したときも速さは同じ(正規化する)。Shift で 10 倍、Ctrl で 1/10。
        private void Fly(IMoonTripleKeys input){
            // 飛ぶ速さ・ホイールの量は、論理ニアクリップに比例して小さくなる(原子の近く(論理 near 10 pm)では、通常の 3e-10 倍)。
            double speed=FlySpeed*this.ScaleFactor;
            if(input.Held(MoonTripleKey.Coarse)){speed*=10;}
            if(input.Held(MoonTripleKey.Fine)){speed*=0.1;}
            double3 forward=ForwardOf(this.cameraYaw,this.cameraPitch);
            double yaw=this.cameraYaw*Math.PI/180.0;
            double3 right=new double3(Math.Cos(yaw),0,-Math.Sin(yaw));
            double3 up=new double3(0,1,0);
            double3 move=double3.zero;
            if(input.Held(MoonTripleKey.FlyForward)||input.Held(MoonTripleKey.ZoomIn)){move+=forward;}
            if(input.Held(MoonTripleKey.FlyBack)||input.Held(MoonTripleKey.ZoomOut)){move-=forward;}
            if(input.Held(MoonTripleKey.FlyRight)){move+=right;}
            if(input.Held(MoonTripleKey.FlyLeft)){move-=right;}
            if(input.Held(MoonTripleKey.FlyUp)){move+=up;}
            if(input.Held(MoonTripleKey.FlyDown)){move-=up;}
            double length=math.length(move);
            if(length>0){this.cameraPosition+=move/length*speed*Time.deltaTime;}
            // ホイールは 1 目盛りで 0.1 m(速さの倍率に比例)。
            float wheel=Mathf.Clamp(input.ZoomDelta(),-3f,3f);
            if(wheel!=0f){this.cameraPosition+=forward*(wheel*0.1*speed/FlySpeed);}
            // (speed/FlySpeed = スケールの倍率 × Shift・Ctrl の倍率)
            Vector2 look=input.LookDelta();
            this.cameraYaw+=look.x*0.2f;this.cameraPitch-=look.y*0.2f;
            this.cameraPitch=Mathf.Clamp(this.cameraPitch,-89f,89f);
            if(input.PressedThisFrame(MoonTripleKey.Frame)){
                if(this.atomView){this.FrameAtom(this.cameraYaw,this.cameraPitch,AtomFrameRadii*MoonTripleLayout.AtomRadius);}
                else{this.FrameBall(this.cameraYaw,this.cameraPitch,FrameDistance);}
            }
        }

        // 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す。同じ軸の 2 つのキーは timer を共有する。
        private void NudgeKey(IMoonTripleKeys input,MoonTripleKey key,long delta,ref long value,ref float timer){
            if(input.PressedThisFrame(key)){
                value+=delta;timer=0.4f;
            }else if(input.Held(key)){
                timer-=Time.deltaTime;
                if(timer<=0){value+=delta;timer=0.06f;}
            }
        }

        private static bool TryFromDouble3(double3 value,out LogicalVector3 result){
            result=default;
            if(!LogicalScalar.TryFromDouble(value.x,out LogicalScalar x)||!LogicalScalar.TryFromDouble(value.y,out LogicalScalar y)||!LogicalScalar.TryFromDouble(value.z,out LogicalScalar z)){return false;}
            result=new LogicalVector3(in x,in y,in z);
            return true;
        }

        private bool Fail(string reason){
            this.failure=reason;
            return false;
        }

        private void OnGUI(){
            if(!this.showHud){return;}
            if(this.style==null){
                this.style=new GUIStyle(GUI.skin.label){fontSize=15,richText=false};
                this.buttonStyle=new GUIStyle(GUI.skin.button){fontSize=15};
            }
            var lines=new System.Text.StringBuilder();
            if(!this.initialized){
                string reason="VG の月の地形のルートを待っています";
                if(this.failure!=null){reason=this.failure;}
                lines.AppendLine("MoonTripleDemo: "+reason);
            }else{
                string[] names={"普通の月(方式 1) | VG の月 A(方式 2)","VG の月 A(方式 2) | PlanetSurface の月 B(方式 3)"};
                lines.AppendLine("接触点 "+this.contact+": "+names[this.contact]+"   隙間 "+Mm(this.contact==0?this.gap01Units:this.gap12Units)+"   (普通の月 = 単位球メッシュ × 直径 "+(2*this.layout.Radius).ToString("N0",CultureInfo.InvariantCulture)+" m)");
                lines.AppendLine("接触点からのボールのずれ: x "+Mm(this.offsetX)+"  y "+Mm(this.offsetY)+"  z "+Mm(this.offsetZ)+"   両側の面に接する距離(解析) "+this.layout.TouchDistance(this.contact).ToString("F3",CultureInfo.InvariantCulture)+" m");
                lines.AppendLine("カメラ(自由飛行) x "+this.cameraPosition.x.ToString("0.000",CultureInfo.InvariantCulture)+" y "+this.cameraPosition.y.ToString("0.000",CultureInfo.InvariantCulture)+" z "+this.cameraPosition.z.ToString("0.000",CultureInfo.InvariantCulture)+
                    " m   向き yaw "+this.cameraYaw.ToString("0.0",CultureInfo.InvariantCulture)+" pitch "+this.cameraPitch.ToString("0.0",CultureInfo.InvariantCulture)+(this.pending?"   テレポート中(セルを生成しています)...":""));
                lines.AppendLine("論理ニアクリップ: プリセット "+this.scalePreset+"「"+ScaleNames[this.scalePreset]+"」  読み戻し "+this.LogicalNearMeters.ToString("G4",CultureInfo.InvariantCulture)+" m  log2 M "+this.LogicalLog2ViewScale.ToString("G4",CultureInfo.InvariantCulture)+
                    "  camera Near "+this.CameraNearPlane.ToString("G3",CultureInfo.InvariantCulture)+" m"+(this.atom!=null?"   原子(VG 半径 "+MoonTripleLayout.AtomRadius.ToString("G2",CultureInfo.InvariantCulture)+" m)までの距離 "+this.DistanceToAtom.ToString("G4",CultureInfo.InvariantCulture)+" m":""));
                if(this.moonB!=null&&this.moonB.Lod!=null){
                    var patch=this.moonB.Lod.Diagnostics;
                    lines.AppendLine("PlanetSurface の月 B(方式 3): 分割の階層 "+patch.DeepestLevel+" / 要求 "+patch.DesiredLevel+"   葉 "+patch.LeafCount+(patch.DeepestLevel<patch.DesiredLevel?"   分割中...":""));
                }
                lines.AppendLine("W S A D 前後左右に飛ぶ   Q E 下/上   Home End・ホイール 前後   右ドラッグ 向き   C ボールを見る   Shift 10 倍、Ctrl 1/10");
                lines.AppendLine("↑↓ ボールを y 方向(上の月/下の月へ)   ←→ x   PgUp/PgDn z   1 2 テレポート   3 接する距離   4 接触点ちょうど   5 下の月に半分埋める   6 上の月に半分埋める(赤い線=ボールの中心の高さ)   7 8 論理ニアクリップを細かく/粗く   9 原子へ   [ ] 隙間   R 初期化");
                if(this.failure!=null){lines.AppendLine("失敗: "+this.failure);}
            }
            var rect=new Rect(12,10,1500,100);
            Color old=GUI.color;
            GUI.color=Color.black;GUI.Label(new Rect(rect.x+1,rect.y+1,rect.width,rect.height),lines.ToString(),this.style);
            GUI.color=Color.white;GUI.Label(rect,lines.ToString(),this.style);
            GUI.color=old;
            if(!this.initialized){return;}
            // テレポートと隙間の UI。
            float x=12;float y=120;float w=330;float h=30;
            if(GUI.Button(new Rect(x,y,w,h),"テレポート: 普通の月(1) | VG の月 A(2)",this.buttonStyle)){this.TeleportTo(0,true);}
            y+=h+4;
            if(GUI.Button(new Rect(x,y,w,h),"テレポート: VG の月 A(2) | PlanetSurface の月 B(3)",this.buttonStyle)){this.TeleportTo(1,true);}
            y+=h+4;
            if(GUI.Button(new Rect(x,y,w,h),"ボールを接触点ちょうどへ",this.buttonStyle)){this.TeleportTo(this.contact,false);}
            y+=h+4;
            if(GUI.Button(new Rect(x,y,w,h),"ボールの半分を下の月に埋める",this.buttonStyle)){this.BuryBall(true);}
            y+=h+4;
            if(GUI.Button(new Rect(x,y,w,h),"ボールの半分を上の月に埋める",this.buttonStyle)){this.BuryBall(false);}
            y+=h+8;
            // 論理ニアクリップのプリセットと、原子サイズの VG の球へのテレポート。
            GUI.Label(new Rect(x,y,w,h),"論理ニアクリップ(camera の Near は 0.3 m のまま)",this.style);
            y+=h;
            for(int i=0;i<ScaleNames.Length;i++){
                float bw=w/ScaleNames.Length;
                if(GUI.Button(new Rect(x+i*bw,y,bw-3,h),i==0?"通常":ScaleNames[i],this.buttonStyle)){this.SetScalePreset(i);}
            }
            y+=h+4;
            if(this.atom!=null&&GUI.Button(new Rect(x,y,w,h),"テレポート: 原子(VG 半径 1e-10 m)",this.buttonStyle)){this.TeleportToAtom();}
            if(this.atom!=null){y+=h+8;}else{y+=4;}
            GUI.Label(new Rect(x,y,w,h),"この接触点の隙間: "+Mm(this.contact==0?this.gap01Units:this.gap12Units),this.style);
            y+=h;
            long[] amounts={-100,-10,10,100};
            string[] labels={"-10mm","-1mm","+1mm","+10mm"};
            for(int i=0;i<amounts.Length;i++){
                if(GUI.Button(new Rect(x+i*(w/4f),y,w/4f-3,h),labels[i],this.buttonStyle)){this.AdjustGap(this.contact,amounts[i]);}
            }
        }

        private static string Mm(long tenthMm){
            return (tenthMm*0.1).ToString("0.0",CultureInfo.InvariantCulture)+" mm";
        }
    }
}
