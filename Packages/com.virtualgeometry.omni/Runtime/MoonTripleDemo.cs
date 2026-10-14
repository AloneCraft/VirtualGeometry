using System;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using RadialCamera=Omni.RadialCamera;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalScalarField=Omni.Logical.ScalarField;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 4 枚の平面(角が原点で接する)に、サッカーボールを置いて、面の高さのずれを見るデモ(Omni の Radial 対数描画)。
    /// 4 枚は、VG の面(<see cref="PlanarTerrainSource"/>、一辺 1000 km)、Standard100Km(10 m の Plane を 10^4 倍)、Standard1000Km(10 m の Plane を 10^5 倍)、
    /// 手動分割 1000m(10 m の Plane を 100×100 枚並べた面)。すべて y=0 に置き、角(x=0, z=0)で接する(<see cref="MoonTripleLayout"/>)。
    /// 操作: 面を選び(2)、その面の高さを 1mm ずつ動かす([ ])。ボールは 1(原点へ)、5(選んだ面の高さに置く = 半分埋まる)。
    /// 位置はすべて Logical で計算する。この component は camera の GameObject(Logical.Transform と RadialCamera を持つ)に付ける。
    /// </summary>
    // [ExecuteAlways]: Play を止めた Edit Mode でも、Play 開始と同じ絵(ボールと camera の既定の視点)を Game View に出す。
    // Edit Mode では、初期化と配置(Logical の位置・倍率・camera)だけを行い、キー・ボタンの操作は Play のときだけ(Update の先頭と OnGUI で止める)。
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class MoonTripleDemo:MonoBehaviour{
        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;
        /// <summary>テレポートと C で、ボールの中心から camera までの距離(m)。</summary>
        public const float FrameDistance=0.35f;
        /// <summary>飛ぶ速さ(m/s)。Shift で 10 倍、Ctrl で 1/10。</summary>
        public const double FlySpeed=0.3;
        /// <summary>論理ニアクリップのプリセット(m)。通常(0.03)、1 mm、1 µm、1 nm、10 pm。camera の Near は 0.3 のままで、View Scale M = 0.3 ÷ 論理 near にする。</summary>
        public static readonly double[] ScaleNearMeters={0.03,1e-3,1e-6,1e-9,1e-11};
        public static readonly string[] ScaleNames={"通常 0.03 m","1 mm","1 µm","1 nm","10 pm"};
        /// <summary>面の呼び名(<see cref="MoonTripleFace"/> の順)。</summary>
        public static readonly string[] FaceNames={"VG の面","Standard100Km","Standard1000Km","手動分割 1000m"};
        /// <summary>
        /// 面の色(材質の _BaseColor。MoonTripleSceneBuilder がシーン生成時に保存する)。画面の見出しも同じ色相にする。赤系にしないのは、ボールの赤い線の検出(赤さ = r − (g+b)/2)に、面の色が混ざらないようにするため。
        /// </summary>
        public static readonly Color[] FaceColors={new Color(0.30f,0.60f,0.25f,1),new Color(0.25f,0.40f,0.75f,1),new Color(0.55f,0.55f,0.55f,1),new Color(0.62f,0.36f,0.72f,1)};
        /// <summary>ボールの点光源の強さ(Omni の面は <c>_CosmicBallLight</c>、VG の面は <c>_PlanetBallLight</c> の w)。減衰は 1 / (1 + (d / 0.4 m)²)。0 で消す。</summary>
        public const float BallLightIntensity=0.35f;
        private static readonly int CosmicBallLightId=Shader.PropertyToID("_CosmicBallLight");
        private static readonly int PlanetBallLightId=Shader.PropertyToID("_PlanetBallLight");
        /// <summary>
        /// VG の面の細かさ(<see cref="PlanarTerrainSource.TargetPixels"/>。大きいほど粗く、選ぶ Cell が減る)。Editor で、カメラを水平に向けたときの実測(Game View 最大化): 2 = 約 500 個の Cell で VG の面だけで約 14 ms、
        /// 6 = 270 個で 57 fps、12 = 144 個で 76 fps、24 = 83 個で 88 fps。面は平らなので、粗くしても形は変わらない(最大階層 17 は変えない)。
        /// MoonTripleSceneBuilder も、この値を VG の面の設定に書く。
        /// </summary>
        public const float VgTargetPixels=16f;
        /// <summary>通常の論理 near(m)。飛ぶ速さ・ホイールの量は、論理 near のこの比に比例する。</summary>
        public const double DefaultNearMeters=0.03;
        /// <summary>camera の Near の設定値(m)。論理 near を変えても 0.3 のまま(View Scale M = この値 ÷ 論理 near)。</summary>
        public const float DefaultCameraNearMeters=0.3f;
        /// <summary>面の高さを 1 回で動かす量(0.1mm 単位。10 = 1 mm)。</summary>
        private const long FaceStepUnits=10;
        // Omni の CosmicPlanetLit(ボール)は URP のライトを使わず、_CosmicKeyLight(画面に向かう光の向き、強さ)で照らす。
        // 向きは MoonTripleSceneBuilder の太陽(travel = (-0.7, -0.55, -0.25))の逆、強さは太陽の 1.3。VG の面にも同じ値を渡す(_PlanetKeyLight)。
        private static readonly int CosmicKeyLightId=Shader.PropertyToID("_CosmicKeyLight");
        private static readonly int PlanetKeyLightId=Shader.PropertyToID("_PlanetKeyLight");
        private static readonly Vector4 KeyLight=new Vector4(0.7f,0.55f,0.25f,1.3f);

        [SerializeField] private MoonTripleLayout layout=MoonTripleLayout.Default;
        // 4 枚の面の根(MoonTripleFace の順)。VG と Standard は、この物体が Logical の面。手動分割は、子の 10,000 枚が Logical の面(子の順が TileCenter の (i, j))。
        [SerializeField] private GameObject[] faces=new GameObject[4];
        // ボール(Omni のサッカーボール。通常の MeshRenderer、CosmicPlanetLit)。
        [SerializeField] private GameObject ball;
        [SerializeField] private bool showHud=true;

        private PlanarTerrainSource vg;
        private LogicalTransform ballLogical,cameraLogical;
        // 面ごとの Logical の物体(手動分割は 10,000 枚)。Initialize で集める。
        private LogicalTransform[][] faceLogical;
        private RadialCamera radial;
        private Camera unityCamera;
        // 論理ニアクリップのプリセットの番号(<see cref="ScaleNearMeters"/>)。
        private int scalePreset;
        // 選んでいる面(<see cref="MoonTripleFace"/> の番号)と、各面の高さのずれ(0.1mm 単位)。
        private int selectedFace;
        private readonly long[] faceOffsetUnits=new long[4];
        // camera の Near の実測を、描画の外(Update)で見た結果(OnGUI で読むと、Omni が描画中に書き換えた値が読めて、誤警告になる)。
        private bool cameraNearSampled;
        private bool cameraNearWarned;
        private bool initialized;
        private string failure;
        // ボールの中心の、原点からのずれ(0.1mm 単位の整数): x=+x 方向、y=上向き、z=+z 方向。
        private long offsetX,offsetY,offsetZ;
        private float cameraYaw=180f,cameraPitch=10f;
        private Quaternion cameraRotation=Quaternion.identity;
        private double3 cameraPosition;
        private float repeatX,repeatY,repeatZ;
        private float repeatFace;
        // 見ている点へ埋めたときの結果(<see cref="BuryAtAim"/>)。
        private string aimNote;
        // ドリブル(ボールの高さの動き)。中は、ボールの前後左右をカメラの前 FrameDistance に固定する。
        // ドリブルは Unity の物理エンジン(独立した物理シーン)で動かす。物理シーンは、最初に始めたときに作る。
        private readonly MoonTripleDribble dribble=new MoonTripleDribble();
        // ドリブルの速さの倍率(1 = 普通。小さいほどゆっくり)。
        private float dribbleSpeed=1f;
        // ボタンと説明の背景の高さ(前の描画で求めた値)。
        private float controlsHeight=560f;
        // 操作パネル(HUD とボタン)を出しているか(キー H)。
        private bool showPanel=true;
        // HUD の文字(0.1 秒ごとに作り直す)と、その高さ・幅・次に作り直す時刻。
        private const float HudRefreshSeconds=0.1f;
        private string hudText;
        private float hudHeight;
        private float hudWidthUsed;
        private float hudRefreshTime;
        private GUIStyle style;
        private GUIStyle noteStyle;
        private GUIStyle buttonStyle;
        private IMoonTripleKeys keys=MoonTripleKeyboard.Instance;

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        public MoonTripleLayout Layout=>this.layout;
        public int SelectedFace=>this.selectedFace;
        public long OffsetX=>this.offsetX;
        public long OffsetY=>this.offsetY;
        public long OffsetZ=>this.offsetZ;
        /// <summary>面の高さのずれ(0.1mm 単位)。</summary>
        public long FaceOffsetUnits(int face){return this.faceOffsetUnits[face];}
        public float CameraYaw=>this.cameraYaw;
        public float CameraPitch=>this.cameraPitch;
        public Quaternion CameraRotation=>this.cameraRotation;
        public double3 CameraPosition=>this.cameraPosition;
        /// <summary>ドリブル中か。</summary>
        public bool IsDribbling=>this.dribble.Running;
        /// <summary>ドリブルの速さの下限(倍率)。</summary>
        public const float MinimumDribbleSpeed=0.05f;
        /// <summary>ドリブルの速さの倍率(1 = 普通、小さいほどゆっくり)。時間の進みだけを変えるので、最高の高さ 1 m は変わらない。<see cref="MinimumDribbleSpeed"/> 〜 1 に収める。</summary>
        public float DribbleSpeed{
            get=>this.dribbleSpeed;
            set=>this.dribbleSpeed=Mathf.Clamp(value,MinimumDribbleSpeed,1f);
        }
        public GameObject[] Faces{get=>this.faces;set=>this.faces=value;}
        public GameObject Ball{get=>this.ball;set=>this.ball=value;}
        /// <summary>論理ニアクリップのプリセットの番号。</summary>
        public int ScalePreset=>this.scalePreset;
        /// <summary>飛ぶ速さ・ホイールの量にかける倍率 = 今の論理 near ÷ 通常の論理 near。</summary>
        public double ScaleFactor=>ScaleNearMeters[this.scalePreset]/DefaultNearMeters;
        /// <summary>論理 near を Omni から読み戻した値(m)。読めなければ NaN。</summary>
        public double LogicalNearMeters{
            get{
                if(this.radial==null){return double.NaN;}
                if(this.radial.EffectiveNearClip.TryToFloat(out float value)){return value;}
                return double.NaN;
            }
        }
        /// <summary>View Scale M の log2 を Omni から読み戻した値。</summary>
        public float LogicalLog2ViewScale{get{if(this.radial==null){return float.NaN;}return this.radial.EffectiveLog2ViewScale;}}
        /// <summary>
        /// 今の <c>Camera.nearClipPlane</c>(m)。Omni は描画中にこの値を写像後の値(log2(1+0.3) ≒ 0.378)へ書き換えて、描画後に戻すので、
        /// 描画の外で読めば <see cref="DefaultCameraNearMeters"/> のはず。違うときは、戻し漏れ(描画の例外など)の手がかり。
        /// </summary>
        public float CameraNearPlane{get{if(this.unityCamera==null){return float.NaN;}return this.unityCamera.nearClipPlane;}}
        /// <summary><see cref="CameraNearPlane"/> が設定の Near と違うか(描画の途中の値が残っている)。</summary>
        public bool CameraNearRewritten{get{return !float.IsNaN(this.CameraNearPlane)&&Mathf.Abs(this.CameraNearPlane-DefaultCameraNearMeters)>1e-4f;}}
        /// <summary>キーの入力元(既定は Input System。null を入れると既定に戻る)。</summary>
        public IMoonTripleKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=MoonTripleKeyboard.Instance;}else{this.keys=value;}
            }
        }

        // ドリブルの物理シーンを片付ける。
        private void OnDestroy(){
            this.dribble.Dispose();
        }

        private void OnEnable(){
            this.initialized=false;this.failure=null;
#if UNITY_EDITOR
            if(!Application.isPlaying){UnityEditor.EditorApplication.update+=this.EditModeTick;}
#endif
        }

#if UNITY_EDITOR
        private void OnDisable(){
            UnityEditor.EditorApplication.update-=this.EditModeTick;
        }

        // Edit Mode の更新(EditorApplication.update): 初期化(配置、_CosmicKeyLight)と、camera・ボールの配置を、1 回ずつ行う。
        // その後は、VG の Cell の生成が進むよう、数 tick に 1 回、プレイヤーループと全 View の再描画を要求する(Edit Mode では Time.frameCount が進まず、生成が止まりやすい)。
        // シーンの dirty は、配置の書き込み(Logical.Transform の SyncUnityTransform)で立つので、Edit Mode の更新のたびに元へ戻す(開いただけで「保存しますか」と聞かれないように)。
        private int editModeTicks;

        private void EditModeTick(){
            if(this==null||Application.isPlaying){return;}
            bool wasDirty=this.gameObject.scene.IsValid()&&this.gameObject.scene.isDirty;
            if(!this.initialized){
                if(!this.MoonsReady()){return;}
                if(!this.Initialize()){return;}
            }else if(!this.MoonsReady()){
                this.initialized=false;return;
            }
            this.editModeTicks++;
            if(this.editModeTicks%4==0){
                this.Apply();
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
            this.KeepSceneClean(wasDirty);
        }

        // CosmicPlanetSurface.KeepSceneClean と同じ(EditorSceneManager.ClearSceneDirtiness をリフレクションで呼ぶ)。
        private void KeepSceneClean(bool wasDirty){
            UnityEngine.SceneManagement.Scene scene=this.gameObject.scene;
            if(wasDirty||!scene.IsValid()||!scene.isDirty){return;}
            System.Reflection.MethodInfo clear=typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod("ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            clear?.Invoke(null,new object[]{scene});
        }
#endif

        private void Update(){
            // Edit Mode(ExecuteAlways)の Update では、キー入力を動かさない(初期化と配置は EditModeTick が行う)。
            if(!Application.isPlaying){return;}
            this.SampleCameraNear();
            if(this.initialized&&!this.MoonsReady()){
                this.initialized=false;
                return;
            }
            if(!this.initialized){
                if(!this.MoonsReady()){return;}
                if(!this.Initialize()){return;}
            }
            this.ReadKeys();
            this.UpdateDribble(Time.deltaTime);
            this.Apply();
        }

        // Camera.nearClipPlane を、描画の外(Update)で見る。OnGUI で読むと、Omni が描画中に書き換えた値が読めて、誤警告になる。
        // Update でも設定の 0.3 と違えば、Omni の書き換えが戻っていない(描画の例外など)ので、初回だけ Console に警告を出す。
        private void SampleCameraNear(){
            if(this.unityCamera==null){return;}
            this.cameraNearSampled=this.CameraNearRewritten;
            if(this.cameraNearSampled&&!this.cameraNearWarned){
                this.cameraNearWarned=true;
                Debug.LogWarning("[MoonTriple] Camera.nearClipPlane が "+this.CameraNearPlane.ToString("G4",CultureInfo.InvariantCulture)+" です(設定は "+DefaultCameraNearMeters+")。Omni が描画中に書き換えた値が、描画の後に戻っていない可能性があります。");
            }
        }

        /// <summary>VG の面の Selector が使える状態か(面の物体が揃っているか)。</summary>
        public bool MoonsReady(){
            if(this.faces==null||this.faces.Length!=4){return false;}
            for(int i=0;i<this.faces.Length;i++){
                if(this.faces[i]==null){return false;}
            }
            PlanarTerrainSource source=this.faces[(int)MoonTripleFace.Vg].GetComponent<PlanarTerrainSource>();
            return source!=null&&source.Selector!=null;
        }

        /// <summary>4 枚の面とボールと camera を置く。VG の面の Selector が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            if(!this.MoonsReady()||this.ball==null){return this.Fail("4 枚の面とボールが必要です。");}
            this.vg=this.faces[(int)MoonTripleFace.Vg].GetComponent<PlanarTerrainSource>();
            this.vg.TargetPixels=VgTargetPixels;
            this.ballLogical=this.ball.GetComponent<LogicalTransform>();
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.ballLogical==null||this.cameraLogical==null){return this.Fail("ボールと camera に Omni の Logical.Transform が必要です。");}
            // 面ごとの Logical を集める。手動分割は子の 10,000 枚(子の順が (i, j))、それ以外は根の物体。
            this.faceLogical=new LogicalTransform[4][];
            for(int f=0;f<4;f++){
                if(f==(int)MoonTripleFace.Manual){
                    int count=this.faces[f].transform.childCount;
                    this.faceLogical[f]=new LogicalTransform[count];
                    for(int k=0;k<count;k++){
                        this.faceLogical[f][k]=this.faces[f].transform.GetChild(k).GetComponent<LogicalTransform>();
                        if(this.faceLogical[f][k]==null){return this.Fail("手動分割の 1 枚に Omni の Logical.Transform が必要です。");}
                    }
                }else{
                    this.faceLogical[f]=new[]{this.faces[f].GetComponent<LogicalTransform>()};
                    if(this.faceLogical[f][0]==null){return this.Fail(FaceNames[f]+" に Omni の Logical.Transform が必要です。");}
                }
            }
            // 論理ニアクリップの切り替えは、camera の RadialCamera に SetViewScale で行う(camera の Near は 0.3 のまま)。
            this.radial=this.GetComponent<RadialCamera>();
            this.unityCamera=this.GetComponent<Camera>();
            if(this.radial==null||this.unityCamera==null){return this.Fail("camera に RadialCamera と Camera が必要です。");}
            this.selectedFace=0;
            this.dribble.Stop();
            Array.Clear(this.faceOffsetUnits,0,this.faceOffsetUnits.Length);
            this.SetScalePreset(0);
            this.WriteKeyLight();
            // ボールの直径(Omni の CosmicSphere は直径 1 の単位球なので、直径 2·BallRadius へ拡大する)。
            if(!LogicalScalar.TryFromDouble(2*MoonTripleLayout.BallRadius,out LogicalScalar ballDiameter)){return this.Fail("ボールの大きさを Logical にできません。");}
            this.ballLogical.LocalScale=new LogicalVector3(in ballDiameter,in ballDiameter,in ballDiameter);
            this.ballLogical.SyncUnityTransform();
            this.ApplyFaces();
            this.initialized=true;
            this.offsetX=0;this.offsetY=0;this.offsetZ=0;
            this.FrameBall(180f,10f,FrameDistance);
            return this.Apply()&&this.failure==null;
        }

        /// <summary>
        /// 4 枚の面を、大きさと位置(角を原点に接する)へ置く。Standard は一辺 N 倍(Logical の倍率)、VG と手動分割は倍率 1。
        /// 面の高さのずれ(<see cref="faceOffsetUnits"/>)は、位置の y に足す。面の色と格子の値は MoonTripleSceneBuilder が材質に書いている。
        /// </summary>
        private bool ApplyFaces(){
            for(int f=0;f<4;f++){
                var face=(MoonTripleFace)f;
                double y=this.faceOffsetUnits[f]*UnitMeters;
                double standardScale=this.layout.StandardScale(face);
                double scale=standardScale>0?standardScale:1;
                if(!LogicalScalar.TryFromDouble(scale,out LogicalScalar s)){return this.Fail(FaceNames[f]+" の倍率を Logical にできません。");}
                LogicalTransform[] list=this.faceLogical[f];
                for(int k=0;k<list.Length;k++){
                    double3 center=face==MoonTripleFace.Manual?this.layout.TileCenter(k%this.layout.TilesPerSide,k/this.layout.TilesPerSide):this.layout.FaceCenter(face);
                    if(!TryFromDouble3(new double3(center.x,center.y+y,center.z),out LogicalVector3 position)){return this.Fail(FaceNames[f]+" の位置を Logical にできません。");}
                    list[k].LocalScale=new LogicalVector3(in s,in s,in s);
                    list[k].LocalPosition=position;
                    list[k].LocalRotation=LogicalQuaternion.Identity;
                    list[k].SyncUnityTransform();
                }
            }
            return true;
        }

        /// <summary>論理ニアクリップのプリセットを選ぶ(<see cref="ScaleNearMeters"/>)。camera の Near(0.3 m)を論理 near で割った View Scale M を、論理 near を直接代入して決める。</summary>
        public bool SetScalePreset(int index){
            if(this.radial==null||this.unityCamera==null){return false;}
            this.scalePreset=Math.Max(0,Math.Min(ScaleNearMeters.Length-1,index));
            // 論理 near を直接代入する(SetViewScale は camera.nearClipPlane を読むが、Omni は描画中にこの値を写像後の値(約 0.378)へ書き換えて描画後に戻すので、
            // 描画の途中の値を読むと M がずれる)。論理 near を決めれば M は Omni が求める。
            if(!LogicalScalar.TryFromDouble(ScaleNearMeters[this.scalePreset],out LogicalScalar near)){return false;}
            this.radial.nearClip=(LogicalScalarField)near;
            return true;
        }

        /// <summary>ドリブルを止め、ボールを 4 枚の角(原点)へ置き、camera を既定の向きで置く。</summary>
        public void TeleportToCorner(){
            if(!this.initialized){return;}
            this.dribble.Stop();
            this.offsetX=0;this.offsetY=0;this.offsetZ=0;
            this.FrameBall(180f,10f,FrameDistance);
        }

        /// <summary>
        /// ボールの中心の (x, z)(m)の下にある面の高さ(m)。面の境界(x=0 または z=0)の上では、いま操作している面。
        /// 面の広がりは見ない(手動分割は一辺 1 km なので、その外では面が無いが、象限の面の高さを使う)。
        /// </summary>
        public double GroundHeight(double x,double z){
            MoonTripleFace face=MoonTripleLayout.FaceAt(x,z,(MoonTripleFace)this.selectedFace);
            return this.faceOffsetUnits[(int)face]*UnitMeters;
        }

        /// <summary>
        /// ドリブル中の、ボールからカメラまでの水平の距離(m)。2.5 m(最高 1 m の跳ねが全部画面に入る)の 1/4。
        /// 近いので、カメラの高さ <see cref="DribbleCameraHeight"/> を中心に、上下 ±0.29 m(視野角 50°)の外は画面に入らない(Q E でカメラの高さを変えられる)。
        /// </summary>
        public const double DribbleDistance=0.625;
        /// <summary>ドリブルを始めるときの、カメラの高さ(地面から。m)。水平に向けて、地面から 1 m の高さまでが画面に入る。</summary>
        public const double DribbleCameraHeight=0.6;

        /// <summary>
        /// ドリブルを始める。ボールは、いまの (x, z) の下の面の高さ(ボールの半分が埋まった高さ)から跳ね始める。
        /// 跳ねるボールが画面に入るよう、カメラを、いまの向き(yaw)のまま、ボールから <see cref="DribbleDistance"/> 後ろ、地面から <see cref="DribbleCameraHeight"/> の高さへ置き、水平に向ける。
        /// </summary>
        public void StartDribble(){
            if(!this.initialized){return;}
            double x=this.offsetX*UnitMeters;
            double z=this.offsetZ*UnitMeters;
            double ground=this.GroundHeight(x,z);
            this.dribble.Start(ground);
            this.offsetY=(long)Math.Round(this.dribble.Height/UnitMeters);
            this.aimNote=null;
            double yaw=this.cameraYaw*Math.PI/180.0;
            this.cameraPitch=0f;
            this.cameraPosition=new double3(x-Math.Sin(yaw)*DribbleDistance,ground+DribbleCameraHeight,z-Math.Cos(yaw)*DribbleDistance);
        }

        /// <summary>ドリブルを止める(ボールはその場の高さで止まる)。</summary>
        public void StopDribble(){
            this.dribble.Stop();
        }

        /// <summary>ドリブルの開始 / 停止を切り替える。</summary>
        public void ToggleDribble(){
            if(this.dribble.Running){
                this.StopDribble();
            }else{
                this.StartDribble();
            }
        }

        // ドリブル中だけ: ボールの (x, z) をカメラの前 DribbleDistance(向いている水平の方向)に置き、高さ y をドリブルで進める。
        private void UpdateDribble(double seconds){
            if(!this.dribble.Running){return;}
            double yaw=this.cameraYaw*Math.PI/180.0;
            double x=this.cameraPosition.x+Math.Sin(yaw)*DribbleDistance;
            double z=this.cameraPosition.z+Math.Cos(yaw)*DribbleDistance;
            this.dribble.Step(seconds*this.dribbleSpeed,this.GroundHeight(x,z));
            this.offsetX=(long)Math.Round(x/UnitMeters);
            this.offsetY=(long)Math.Round(this.dribble.Height/UnitMeters);
            this.offsetZ=(long)Math.Round(z/UnitMeters);
        }

        /// <summary>ボールの中心を、選んだ面の高さに置く(今の横の位置 x、z のまま)。ボールの半分がその面に埋まり、赤い線(ボールの中心の高さ)と面の境界のずれが、面の高さの誤差になる。</summary>
        public void BuryBall(){
            if(!this.initialized){return;}
            this.offsetY=this.faceOffsetUnits[this.selectedFace];
            this.FrameBall(180f,15f,FrameDistance);
        }

        /// <summary>
        /// 見ている点へ埋める: camera の中心線と、選んだ面の高さの平面との交点へ、ボールの中心を置く(x、z は交点、y は選んだ面の高さ)。
        /// camera は動かさないので、ボールは画面の中央に出る。下を見ていない、交点が camera の前に無い、交点が選んだ面の外のときは置かず、理由を <see cref="AimNote"/> に残す。
        /// </summary>
        public bool BuryAtAim(){
            if(!this.initialized){return false;}
            double3 forward=ForwardOf(this.cameraYaw,this.cameraPitch);
            if(forward.y>-1e-9){
                this.aimNote="下を見ていないので、面との交点がありません(見下ろしてから押してください)";
                return false;
            }
            long height=this.faceOffsetUnits[this.selectedFace];
            double t=(height*UnitMeters-this.cameraPosition.y)/forward.y;
            if(t<=0){
                this.aimNote="面との交点が camera の前にありません";
                return false;
            }
            double3 hit=this.cameraPosition+forward*t;
            var face=(MoonTripleFace)this.selectedFace;
            double3 center=this.layout.FaceCenter(face);
            double half=this.layout.FaceSize(face)*0.5;
            if(Math.Abs(hit.x-center.x)>half||Math.Abs(hit.z-center.z)>half){
                this.aimNote="見ている点が「"+FaceNames[this.selectedFace]+"」の外です(面の内側を見てください)";
                return false;
            }
            this.offsetX=(long)Math.Round(hit.x/UnitMeters);
            this.offsetY=height;
            this.offsetZ=(long)Math.Round(hit.z/UnitMeters);
            this.aimNote="見ている点("+hit.x.ToString("0.000",CultureInfo.InvariantCulture)+", "+hit.z.ToString("0.000",CultureInfo.InvariantCulture)+" m)へ、ボールを「"+FaceNames[this.selectedFace]+"」の高さに置きました";
            return this.Apply();
        }

        /// <summary>見ている点へ埋めたときの結果(HUD に出す 1 行。成功・失敗の理由)。まだ押していなければ null。</summary>
        public string AimNote=>this.aimNote;

        /// <summary>選ぶ面を次の面へ(VG → Standard100Km → Standard1000Km → 手動分割 → VG)。</summary>
        public void SelectNextFace(){
            this.selectedFace=(this.selectedFace+1)%4;
        }

        /// <summary>選んだ面の高さを units(0.1mm 単位。正で上げる)だけ変える。ボールは動かさない(面だけが動く)。</summary>
        public void AdjustFace(int face,long units){
            if(!this.initialized){return;}
            this.faceOffsetUnits[face]+=units;
            this.ApplyFaces();
            this.WriteKeyLight();
        }

        /// <summary>camera を、ボールの中心から distance だけ離れた、yaw・pitch の向きの位置に置く(ボールの中心を向く)。</summary>
        public void FrameBall(float yaw,float pitch,float distance){
            this.cameraYaw=yaw;this.cameraPitch=pitch;
            this.cameraPosition=this.BallCenterDouble()-ForwardOf(yaw,pitch)*distance;
        }

        /// <summary>camera の位置と向き(yaw、pitch)を直接決める(検証用)。</summary>
        public bool PlaceCamera(double3 position,float yaw,float pitch){
            this.cameraYaw=yaw;this.cameraPitch=pitch;this.cameraPosition=position;
            return this.Apply();
        }

        /// <summary>ボールの位置を直接決める(検証用。0.1mm 単位。原点からのずれ)。</summary>
        public bool SetBallOffset(long x,long y,long z){
            this.offsetX=x;this.offsetY=y;this.offsetZ=z;
            return this.Apply();
        }

        // 向き(yaw、pitch)の前方。pitch は水平から下を向く角度(正で下向き)。
        private static double3 ForwardOf(float yaw,float pitch){
            double y=yaw*Math.PI/180.0;double p=pitch*Math.PI/180.0;
            return new double3(Math.Sin(y)*Math.Cos(p),-Math.Sin(p),Math.Cos(y)*Math.Cos(p));
        }

        /// <summary>ボールの中心(倍精度。原点からのずれ)。</summary>
        public double3 BallCenterDouble(){
            return new double3(this.offsetX*UnitMeters,this.offsetY*UnitMeters,this.offsetZ*UnitMeters);
        }

        /// <summary>ボールと camera を、今の状態に合わせて置く。失敗したら理由を <see cref="Failure"/> に残す。</summary>
        public bool Apply(){
            if(!this.initialized){return false;}
            this.failure=null;
            double3 center=this.BallCenterDouble();
            if(!TryFromDouble3(center,out LogicalVector3 position)){return this.Fail("ボールの位置を Logical にできません。");}
            this.ballLogical.LocalPosition=position;
            this.ballLogical.LocalRotation=LogicalQuaternion.Identity;
            this.ballLogical.SyncUnityTransform();
            bool applied=this.ApplyCamera();
            this.SetBallLight();
            return applied;
        }

        // 主光源の向き・強さを、Omni の面(_CosmicKeyLight)と VG の面(_PlanetKeyLight)に書く。光は常に上から。
        private void WriteKeyLight(){
            Shader.SetGlobalVector(CosmicKeyLightId,KeyLight);
            Shader.SetGlobalVector(PlanetKeyLightId,KeyLight);
        }

        // ボールの点光源の位置を、面のシェーダーへ渡す(Shader.SetGlobalVector)。
        // Omni の面: ボールの中心のカメラからの相対位置。VG の面: ボールの中心 − VG の面の高さ(VG の面の基準点は、VG の角の y)。
        // どちらも倍精度で引いてから float にする。原子の周りはもう無いので、強さは常に BallLightIntensity。
        private void SetBallLight(){
            double3 ball=this.BallCenterDouble();
            double3 relative=ball-this.cameraPosition;
            Shader.SetGlobalVector(CosmicBallLightId,new Vector4((float)relative.x,(float)relative.y,(float)relative.z,BallLightIntensity));
            double reference=this.faceOffsetUnits[(int)MoonTripleFace.Vg]*UnitMeters;
            Shader.SetGlobalVector(PlanetBallLightId,new Vector4((float)ball.x,(float)(ball.y-reference),(float)ball.z,BallLightIntensity));
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

        private void ReadKeys(){
            IMoonTripleKeys input=this.keys;
            long step=10;
            if(input.Held(MoonTripleKey.Coarse)){step=100;}
            if(input.Held(MoonTripleKey.Fine)){step=1;}
            // ドリブル中は、ボールの位置をドリブルが決めるので、矢印キーと埋める操作は効かない。
            if(!this.dribble.Running){
                this.NudgeKey(input,MoonTripleKey.BallXMinus,-step,ref this.offsetX,ref this.repeatX);
                this.NudgeKey(input,MoonTripleKey.BallXPlus,step,ref this.offsetX,ref this.repeatX);
                this.NudgeKey(input,MoonTripleKey.BallDown,-step,ref this.offsetY,ref this.repeatY);
                this.NudgeKey(input,MoonTripleKey.BallUp,step,ref this.offsetY,ref this.repeatY);
                this.NudgeKey(input,MoonTripleKey.BallZMinus,-step,ref this.offsetZ,ref this.repeatZ);
                this.NudgeKey(input,MoonTripleKey.BallZPlus,step,ref this.offsetZ,ref this.repeatZ);
            }
            this.Fly(input);
            if(input.PressedThisFrame(MoonTripleKey.Corner)){this.TeleportToCorner();}
            if(input.PressedThisFrame(MoonTripleKey.SelectFace)){this.SelectNextFace();}
            if(input.PressedThisFrame(MoonTripleKey.Dribble)){this.ToggleDribble();}
            if(input.PressedThisFrame(MoonTripleKey.Panel)){this.showPanel=!this.showPanel;}
            if(!this.dribble.Running){
                if(input.PressedThisFrame(MoonTripleKey.Bury)){this.BuryBall();}
                if(input.PressedThisFrame(MoonTripleKey.BuryAim)){this.BuryAtAim();}
            }
            if(input.PressedThisFrame(MoonTripleKey.ScaleFiner)){this.SetScalePreset(this.scalePreset+1);}
            if(input.PressedThisFrame(MoonTripleKey.ScaleCoarser)){this.SetScalePreset(this.scalePreset-1);}
            // 選んだ面の高さ: 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す(面を動かすのは、ここだけ)。
            long faceStep=FaceStepUnits;
            if(input.Held(MoonTripleKey.Coarse)){faceStep=100;}
            if(input.Held(MoonTripleKey.Fine)){faceStep=1;}
            this.FaceKey(input,MoonTripleKey.FaceDown,-faceStep);
            this.FaceKey(input,MoonTripleKey.FaceUp,faceStep);
            if(input.PressedThisFrame(MoonTripleKey.Reset)){this.ResetDemo();}
        }

        /// <summary>初期化: ボールを原点へ、面の高さのずれを 0 へ、論理 near を通常へ。</summary>
        public void ResetDemo(){
            if(!this.initialized){return;}
            this.SetScalePreset(0);
            this.selectedFace=0;
            this.dribbleSpeed=1f;
            Array.Clear(this.faceOffsetUnits,0,this.faceOffsetUnits.Length);
            this.ApplyFaces();
            this.WriteKeyLight();
            this.TeleportToCorner();
        }

        // 飛ぶ: W S は向いている方向の前後、A D は左右、Q E は世界の上下。Home End とホイールは向いている方向へ。右ドラッグで向きを変え、C でボールに向け直す。
        // 斜めに押したときも速さは同じ(正規化する)。Shift で 10 倍、Ctrl で 1/10。
        private void Fly(IMoonTripleKeys input){
            // 飛ぶ速さ・ホイールの量は、論理ニアクリップに比例して小さくなる(論理 near 10 pm では、通常の 3e-10 倍)。
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
                // ドリブル中は、ボールが付いてくる距離(DribbleDistance)に合わせる。
                float distance=FrameDistance;
                if(this.dribble.Running){distance=(float)DribbleDistance;}
                this.FrameBall(this.cameraYaw,this.cameraPitch,distance);
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

        // 選んだ面の高さを、押した瞬間と繰り返しで動かす(NudgeKey と同じ間隔)。
        private void FaceKey(IMoonTripleKeys input,MoonTripleKey key,long delta){
            if(input.PressedThisFrame(key)){
                this.AdjustFace(this.selectedFace,delta);this.repeatFace=0.4f;
            }else if(input.Held(key)){
                this.repeatFace-=Time.deltaTime;
                if(this.repeatFace<=0){this.AdjustFace(this.selectedFace,delta);this.repeatFace=0.06f;}
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
                this.style=new GUIStyle(GUI.skin.label){fontSize=15,richText=true,wordWrap=true};
                this.noteStyle=new GUIStyle(GUI.skin.label){fontSize=13,richText=true,wordWrap=true};
                this.noteStyle.normal.textColor=new Color(0.78f,0.78f,0.78f,1f);
                this.buttonStyle=new GUIStyle(GUI.skin.button){fontSize=15,richText=true};
            }
            // 操作パネル(HUD とボタン)を隠す・出すボタン(キー H)。隠すと、ボールの高さとドリブルの状態の 1 行だけが残る(パネルがボールを覆うので)。
            float top=10;
            if(Application.isPlaying){
                string panelLabel="[H] 操作パネルを隠す";
                if(!this.showPanel){panelLabel="[H] 操作パネルを出す";}
                if(GUI.Button(new Rect(12,top,240,26),panelLabel,this.buttonStyle)){this.showPanel=!this.showPanel;}
                if(!this.showPanel){
                    if(this.initialized){
                        string dribbleState="止まっている";
                        if(this.dribble.Running){dribbleState="中";}
                        Color before=GUI.color;
                        GUI.color=new Color(0,0,0,0.6f);GUI.DrawTexture(new Rect(258,top,420,26),Texture2D.whiteTexture);GUI.color=Color.white;
                        GUI.Label(new Rect(264,top+2,410,24),"ボールの高さ "+Height(this.offsetY)+"   ドリブル: "+dribbleState,this.style);
                        GUI.color=before;
                    }
                    return;
                }
                top+=32;
            }
            // HUD の文字と高さは、0.1 秒ごとにだけ作り直す。ボールの高さが毎フレーム変わると、11 行の文字の組み直し(影の分も入れて 1 フレームに何度も)が重く、fps が約 3 割下がった(Editor で実測)。
            // 組み直しは Layout イベントだけで行い、同じフレームの Layout と Repaint で中身を変えない。
            float hudWidth=Mathf.Min(1500f,Screen.width-24f);
            if(this.hudText==null||this.hudWidthUsed!=hudWidth||(Event.current.type==EventType.Layout&&Time.unscaledTime>=this.hudRefreshTime)){
                this.hudText=this.BuildHudText();
                this.hudHeight=this.style.CalcHeight(new GUIContent(this.hudText),hudWidth);
                this.hudWidthUsed=hudWidth;
                this.hudRefreshTime=Time.unscaledTime+HudRefreshSeconds;
            }
            var rect=new Rect(12,top,hudWidth,this.hudHeight);
            Color old=GUI.color;
            // 文字の後ろに暗い背景を敷く(面の模様の上でも読めるように。影の文字は、組み直しが増えるので使わない)。
            GUI.color=new Color(0,0,0,0.6f);GUI.DrawTexture(new Rect(rect.x-6,rect.y-4,rect.width+12,rect.height+8),Texture2D.whiteTexture);
            GUI.color=Color.white;GUI.Label(rect,this.hudText,this.style);
            GUI.color=old;
            if(!this.initialized){return;}
            // Edit Mode では HUD の表示だけで、ボタンは Play のときだけ。
            if(!Application.isPlaying){return;}
            // 1 ボタン = 1 機能。見出し、灰色の説明、ボタンの順に並べる。キーの番号は [n] でラベルの先頭に付ける。
            float x=12;float y=rect.yMax+12;float w=700;float h=28;
            bool dribbling=this.dribble.Running;
            float panelTop=y;
            GUI.color=new Color(0,0,0,0.6f);GUI.DrawTexture(new Rect(x-6,panelTop-4,w+12,this.controlsHeight+8),Texture2D.whiteTexture);GUI.color=old;
            GUI.Label(new Rect(x,y,w,h),"ボールの操作",this.style);
            y+=h-4;
            y+=this.Note(x,y,w,"「角」は、4 枚の面が集まる中央の点です。ドリブルを始めると、カメラがボールの後ろ "+DribbleDistance.ToString("0.###",CultureInfo.InvariantCulture)+" m に下がり、ボールはカメラの前 "+DribbleDistance.ToString("0.###",CultureInfo.InvariantCulture)+" m に付いてきます(カメラを動かして進みます)。")+4;
            if(GUI.Button(new Rect(x,y,w,h),"[1] ボールを角へ戻す",this.buttonStyle)){this.TeleportToCorner();}
            y+=h+2;
            string dribbleLabel="[4] ドリブルを始める(ボールが 1 m の高さまで跳ねます)";
            if(dribbling){dribbleLabel="[4] ドリブルを止める(いま: ドリブル中)";}
            if(this.ToggleButton(new Rect(x,y,w,h),dribbleLabel,dribbling)){this.ToggleDribble();}
            y+=h+2;
            // ドリブルの速さのゲージ: 左ほどゆっくり(最高の 1 m は変わらない)。ドリブル中でも動かせる。
            GUI.Label(new Rect(x,y,300,h),"ドリブルの速さ "+this.dribbleSpeed.ToString("0.00",CultureInfo.InvariantCulture)+" 倍(左ほどゆっくり)",this.style);
            this.DribbleSpeed=GUI.HorizontalSlider(new Rect(x+310,y+8,w-310,18),this.dribbleSpeed,MinimumDribbleSpeed,1f);
            y+=h+2;
            y+=this.Note(x,y,w,"物理エンジンで動かしています(重力 "+MoonTripleDribble.Gravity.ToString("0.00",CultureInfo.InvariantCulture)+" m/s²、反発係数 "+MoonTripleDribble.Bounciness.ToString("0.0",CultureInfo.InvariantCulture)+")。ボールは上下だけ動きます。ボールの半分が面に埋まったところで跳ね返ります。")+4;
            // ドリブル中は、ボールの位置をドリブルが決めるので、埋める操作は使えない(灰色)。
            bool enabled=GUI.enabled;
            GUI.enabled=!dribbling;
            if(GUI.Button(new Rect(x,y,w,h),"[3] 画面の中央にボールを置く(下を向いて押す)",this.buttonStyle)){this.BuryAtAim();}
            y+=h+2;
            if(GUI.Button(new Rect(x,y,w,h),"[5] ボールを今の場所のまま、操作する面に半分埋める",this.buttonStyle)){this.BuryBall();}
            y+=h+2;
            GUI.enabled=enabled;
            y+=this.Note(x,y,w,"[3] [5] はどちらも、ボールの中心が「操作する面」の高さになり、ボールの半分が面に埋まります。ボールの赤い線が、ボールの中心の高さです。");
            y+=8;
            GUI.Label(new Rect(x,y,w,h),"面の高さを動かす",this.style);
            y+=h-4;
            y+=this.Note(x,y,w,"「操作する面」だけが上下に動きます(ボールは動きません)。面の境目とボールの赤い線のずれで、高さの違いが見えます。")+4;
            if(GUI.Button(new Rect(x,y,w,h),"[2] 操作する面を切り替える(いま: "+Tint(this.selectedFace)+")",this.buttonStyle)){this.SelectNextFace();}
            y+=h+2;
            long[] amounts={-100,-10,10,100};
            string[] labels={"下げる 10mm","下げる 1mm","上げる 1mm","上げる 10mm"};
            for(int i=0;i<amounts.Length;i++){
                if(GUI.Button(new Rect(x+i*(w/4f),y,w/4f-3,h),labels[i],this.buttonStyle)){this.AdjustFace(this.selectedFace,amounts[i]);}
            }
            y+=h+8;
            // カメラの近づける限界(論理ニアクリップ)のプリセット。
            GUI.Label(new Rect(x,y,w,h),"カメラの近づける限界(ニアクリップ。キー 7・8)",this.style);
            y+=h-4;
            y+=this.Note(x,y,w,"カメラが映す一番近い距離です。小さくすると、1 mm や 10 pm のような極端に近いものまで映せます(ボールの直径は 22 cm)。通常は 0.03 m です。")+4;
            for(int i=0;i<ScaleNames.Length;i++){
                float bw=w/ScaleNames.Length;
                string presetLabel=ScaleNames[i];
                if(i==0){presetLabel="通常";}
                if(this.PresetButton(new Rect(x+i*bw,y,bw-3,h),presetLabel,i)){this.SetScalePreset(i);}
            }
            // 次の描画の背景の高さ(ボタンと説明の並びが変わっても合わせる)。
            this.controlsHeight=y+h-panelTop;
        }

        // 灰色の説明の文字(幅で折り返す)。使った高さを返す。
        private float Note(float x,float y,float width,string text){
            float height=this.noteStyle.CalcHeight(new GUIContent(text),width);
            GUI.Label(new Rect(x,y,width,height),text,this.noteStyle);
            return height;
        }

        // 入り切りのボタン: 入っているときは色を変えて、「いまここ」と分かるようにする。
        private bool ToggleButton(Rect rect,string label,bool on){
            Color old=GUI.backgroundColor;
            if(on){GUI.backgroundColor=new Color(0.6f,1f,0.6f,1f);}
            bool pressed=GUI.Button(rect,label,this.buttonStyle);
            GUI.backgroundColor=old;
            return pressed;
        }

        // 今のプリセットのボタンは、色を変えて、「いまここ」と分かるようにする。
        private bool PresetButton(Rect rect,string label,int index){
            return this.ToggleButton(rect,label,this.scalePreset==index);
        }

        /// <summary>
        /// HUD に出す文字。OnGUI から切り出した関数(検証が、呼び名と内容を確かめる)。
        /// </summary>
        public string BuildHudText(){
            var lines=new System.Text.StringBuilder();
            if(!this.initialized){
                string reason="4 枚の面の地形を待っています";
                if(this.failure!=null){reason=this.failure;}
                lines.AppendLine("MoonTripleDemo: "+reason);
                return lines.ToString();
            }
            string dribbleText="止まっている";
            if(this.dribble.Running){dribbleText="中";}
            lines.AppendLine("ボールの高さ "+Height(this.offsetY)+"(0 mm = 4 枚の面が集まる角の高さ)   ドリブル: "+dribbleText+"   操作する面: "+Tint(this.selectedFace));
            lines.AppendLine("ボールの場所(角から) x "+Mm(this.offsetX)+"  z "+Mm(this.offsetZ));
            for(int f=0;f<4;f++){
                string detail=this.FaceDetail((MoonTripleFace)f);
                string marker="";
                if(f==this.selectedFace){marker="   ◀ 操作する面";}
                lines.AppendLine(Tint(f)+": "+detail+"   高さ "+Mm(this.faceOffsetUnits[f])+marker);
            }
            lines.AppendLine("カメラの位置 x "+this.cameraPosition.x.ToString("0.000",CultureInfo.InvariantCulture)+" y "+this.cameraPosition.y.ToString("0.000",CultureInfo.InvariantCulture)+" z "+this.cameraPosition.z.ToString("0.000",CultureInfo.InvariantCulture)+" m");
            string warning="";
            if(this.cameraNearSampled){warning="   [警告: カメラの Near の値が設定と違います(Omni が描画中に書き換えた値が戻っていない)]";}
            lines.AppendLine("カメラの近づける限界: "+ScaleNames[this.scalePreset]+warning);
            lines.AppendLine("カメラ: W S A D = 前後左右 / Q E = 下・上 / 右ドラッグ = 向き / ホイール = 前後 / C = ボールを見る / Shift = 速く / Ctrl = ゆっくり");
            lines.AppendLine("ボール: ← → = x 方向 / PageUp PageDown = z 方向 / ↑ ↓ = 上下 / 4 = ドリブル / 1 = 角へ戻す / R = ぜんぶ最初に戻す");
            lines.AppendLine("面: 2 = 操作する面を切り替える / [ ] = 操作する面を下げる・上げる / 7 8 = カメラの近づける限界 / H = この操作パネルを隠す・出す");
            if(this.aimNote!=null){lines.AppendLine("画面の中央に置いた結果: "+this.aimNote);}
            if(this.failure!=null){lines.AppendLine("失敗: "+this.failure);}
            return lines.ToString();
        }

        // 面ごとの説明(HUD の 1 行)。
        private string FaceDetail(MoonTripleFace face){
            double size=this.layout.FaceSize(face);
            string sizeText;
            if(size>=1000){
                sizeText=(size/1000).ToString("0",CultureInfo.InvariantCulture)+" km";
            }else{
                sizeText=size.ToString("0",CultureInfo.InvariantCulture)+" m";
            }
            switch(face){
                case MoonTripleFace.Vg:
                    return "一辺 "+sizeText+"。VG の面(カメラの近くだけ細かく描く。いちばん細かい升目は "+MoonTripleLayout.TargetCellMeters.ToString("0",CultureInfo.InvariantCulture)+" m 以下、最大階層 "+this.layout.VgMaximumLevel+")";
                case MoonTripleFace.Manual:
                    return "一辺 "+sizeText+"。"+this.layout.BaseMeters.ToString("0",CultureInfo.InvariantCulture)+" m の板を "+this.layout.TilesPerSide+"×"+this.layout.TilesPerSide+" 枚並べた面(手で分割)";
                default:
                    return "一辺 "+sizeText+"。"+this.layout.BaseMeters.ToString("0",CultureInfo.InvariantCulture)+" m の板を "+this.layout.StandardScale(face).ToString("N0",CultureInfo.InvariantCulture)+" 倍に拡大しただけの面(分割しない)";
            }
        }

        private string Tint(int face){
            return "<color=#"+ColorUtility.ToHtmlStringRGB(UiColor(face))+">"+FaceNames[face]+"</color>";
        }

        // 画面の文字に使う色(面の色は暗いので、白へ寄せる)。
        private static Color UiColor(int face){
            return Color.Lerp(FaceColors[face],Color.white,0.4f);
        }

        // ボールの高さ: 1 m 未満は mm、それ以上は m で出す(ドリブルの最高点は 1 m)。
        private static string Height(long tenthMm){
            if(Math.Abs(tenthMm)>=10000){
                return (tenthMm*UnitMeters).ToString("0.000",CultureInfo.InvariantCulture)+" m";
            }
            return Mm(tenthMm);
        }

        private static string Mm(long tenthMm){
            return (tenthMm*0.1).ToString("0.0",CultureInfo.InvariantCulture)+" mm";
        }
    }
}
