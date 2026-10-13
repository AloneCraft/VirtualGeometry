using System;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 「単純に拡大した面」と「VG の面」に、同じボールを沈める比べのデモ(Omni の Radial 対数描画)。
    /// 左: 単位の XZ 平面を一様に S 倍した 1 つの物体(単純な拡大)。物体空間の float の座標が S·2^-25(面の原点から試験点まで D=10^6 m で約 6cm)の刻みになり、
    ///     ボールを 1mm ずつ沈めても、面との交わりが飛ぶ(誤差の内訳は未測定。Requirements/PlanetWorld.md §20)。
    /// 右: 半径 R の <see cref="PlanetTerrainSource"/>(高さ 0)の上端。Cell ごとの局所原点なので、1mm の動きが 1mm のまま画面に出る(§16、§19)。
    /// ボール(<see cref="PlanetTerrainSource"/>、半径 0.11 m)は左右とも VG で、camera は常にボールを画面の中央に置く三人称視点(Logical の周回)。
    /// 配置は <see cref="PlaneSinkLayout"/>、位置はすべて Logical で計算する。この component は camera の GameObject(Logical.Transform と RadialCamera を持つ)に付ける。
    /// 操作(Input System): ↑↓ ボールを 1mm 浮かせる/沈める(Shift で 10mm、Ctrl で 0.1mm)、WASD 水平に動く、←→ PageUp/PageDown 周回、Home/End・ホイール 距離、右ドラッグ 周回、
    ///   1 左の面 2 右の面 3 全体、[ ] 左の面の原点までの距離 D を小さく/大きく、Space 自動で沈める、R 初期化
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlaneSinkDemo:MonoBehaviour{
        public enum Area{Left,Right}

        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;
        /// <summary>ボールを試験点から水平に動かせる範囲の上限(m)。右の天体の球面の下がり(R=2×10^6 m で 2 m に 1 µm)を無視できる範囲。継ぎ目までの距離(<see cref="PlaneSinkLayout.EdgeMargin"/>)からも制限する。</summary>
        public const double HorizontalLimit=2.0;
        /// <summary>camera とボールの中心の距離の下限(m)。ボールの半径 0.11 m + 論理 near 0.03 m より大きい。</summary>
        public const float MinimumDistance=0.16f;
        private static readonly double[] Distances={1e4,1e5,1e6,1e7};
        // 左の面の mesh の細かさ(面の原点からの座標 m)。
        private const double FineHalf=32;
        private const double FineStep=0.5;
        private const double Growth=1.25;
        private const double WallHalfWidth=1000;

        [SerializeField] private PlaneSinkLayout layout=PlaneSinkLayout.Default;
        [SerializeField] private GameObject scaledPlane;
        [SerializeField] private GameObject wall;
        [SerializeField] private PlanetTerrainSource ground;
        [SerializeField] private PlanetTerrainSource ball;
        [SerializeField,Min(MinimumDistance)] private float cameraDistance=0.35f;
        [SerializeField] private float cameraYaw;
        [SerializeField] private float cameraPitch=20f;
        [SerializeField,Min(0.001f)] private float stepSeconds=0.12f;
        [SerializeField] private bool showHud=true;

        private LogicalTransform planeLogical,wallLogical,groundLogical,ballLogical,cameraLogical;
        // 左の面の material。Omni の Scene BRG は scene の材質に永続 asset の正本を要求し、実行時の複製は Radial の差し替えと
        // 明示登録の検査が食い違って Play が止まるので、複製せず scene の asset へ書く。終了時に元の値へ戻す(ownedLeftMaterial は検証用の複製)。
        private Material leftMaterial;
        private Material ownedLeftMaterial;
        private Material writtenAsset;
        private Vector4 originalGridOrigin;
        private float originalObjectScale;
        private Mesh planeMesh;
        private Mesh wallMesh;
        private bool initialized;
        private string failure;
        private Area area=Area.Left;
        // ボールの試験点からのずれ(0.1mm 単位の整数): x=+X 方向、y=上向き(隙間。負は沈み)、z=+Z 方向。
        private long offsetX,offsetY,offsetZ;
        private double remainderX,remainderZ;
        private Quaternion cameraRotation=Quaternion.identity;
        private bool autoPlay;
        private int autoStage;
        private int autoStep;
        private float autoTimer;
        private float repeatRaise,repeatSink;
        private GUIStyle style;
        private IPlaneSinkKeys keys=PlaneSinkKeyboard.Instance;

        // 自動の動き: (1 回の量(0.1mm 単位。負は沈める)、回数)。
        private static readonly (long Delta,int Count)[] AutoStages={(-10,62),(10,62)};

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        public Area CurrentArea=>this.area;
        public PlaneSinkLayout Layout=>this.layout;
        public long OffsetX=>this.offsetX;
        public long OffsetY=>this.offsetY;
        public long OffsetZ=>this.offsetZ;
        public float CameraYaw=>this.cameraYaw;
        public float CameraPitch=>this.cameraPitch;
        public float CameraDistance=>this.cameraDistance;
        public Quaternion CameraRotation=>this.cameraRotation;
        public bool AutoPlay{get=>this.autoPlay;set=>this.autoPlay=value;}
        public GameObject ScaledPlane{get=>this.scaledPlane;set=>this.scaledPlane=value;}
        public GameObject Wall{get=>this.wall;set=>this.wall=value;}
        public PlanetTerrainSource Ground{get=>this.ground;set=>this.ground=value;}
        public PlanetTerrainSource Ball{get=>this.ball;set=>this.ball=value;}
        /// <summary>左の面の material(描画に使っているもの)。</summary>
        public Material LeftMaterial=>this.leftMaterial;
        /// <summary>キーの入力元(既定は Input System。null を入れると既定に戻る)。</summary>
        public IPlaneSinkKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=PlaneSinkKeyboard.Instance;}else{this.keys=value;}
            }
        }

        private void OnEnable(){
            this.initialized=false;this.failure=null;
        }

        private void OnDestroy(){
            if(this.planeMesh!=null){DisposeObject(this.planeMesh);}
            if(this.wallMesh!=null){DisposeObject(this.wallMesh);}
            this.RestoreWrittenAsset();
            if(this.ownedLeftMaterial!=null){DisposeObject(this.ownedLeftMaterial);}
        }

        /// <summary>デモが material のアセットに書いた値(_GridOriginOS、_ObjectScale)を、元の値へ戻す。batchmode の検証は EditorApplication.Exit で終わり、OnDestroy が走らずに、検証の最後の値(D = 1e7 など)がアセットに残るので、検証の終了時に呼ぶ。</summary>
        public void RestoreWrittenAsset(){
            if(this.writtenAsset==null){return;}
            this.writtenAsset.SetVector("_GridOriginOS",this.originalGridOrigin);
            this.writtenAsset.SetFloat("_ObjectScale",this.originalObjectScale);
            this.writtenAsset=null;
        }

        private static void DisposeObject(UnityEngine.Object value){
            if(Application.isPlaying){Destroy(value);}else{DestroyImmediate(value);}
        }

        private void Update(){
            // 初期化の後に、右の天体かボールが無効になった(Selector が無くなった)ら、使えるようになるまで待って初期化し直す。
            if(this.initialized&&(this.ground==null||this.ball==null||this.ground.Selector==null||this.ball.Selector==null)){
                this.initialized=false;
                return;
            }
            if(!this.initialized){
                if(this.ground==null||this.ball==null||this.ground.Selector==null||this.ball.Selector==null){return;}
                if(!this.Initialize()){return;}
            }
            this.ReadKeys();
            this.UpdateAuto();
            this.Apply();
        }

        /// <summary>配置を適用し、左の面の mesh を作り、ボールと camera を置く。右の天体とボールの Selector が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            if(this.scaledPlane==null||this.wall==null||this.ground==null||this.ball==null){return this.Fail("左の面・壁・右の天体・ボールが必要です。");}
            this.planeLogical=this.scaledPlane.GetComponent<LogicalTransform>();
            this.wallLogical=this.wall.GetComponent<LogicalTransform>();
            this.groundLogical=this.ground.GetComponent<LogicalTransform>();
            this.ballLogical=this.ball.GetComponent<LogicalTransform>();
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.planeLogical==null||this.wallLogical==null||this.groundLogical==null||this.ballLogical==null||this.cameraLogical==null){
                return this.Fail("左の面・壁・右の天体・ボール・camera に Omni の Logical.Transform が必要です。");
            }
            if(!this.ApplyLayout()){return false;}
            this.offsetX=0;this.offsetY=0;this.offsetZ=0;this.remainderX=0;this.remainderZ=0;
            this.initialized=true;
            return this.Apply();
        }

        /// <summary>配置を変える(左の面の mesh を作り直す)。配置が不正なときは、変えずに false を返す(<see cref="Failure"/> に理由)。</summary>
        public bool SetLayout(PlaneSinkLayout value){
            if(!this.initialized){
                this.layout=value;
                return true;
            }
            if(!this.TryValidate(in value,out string reason)){return this.Fail(reason);}
            this.layout=value;
            if(!this.ApplyLayout()){return false;}
            return this.Apply();
        }

        /// <summary>左の面の material を替える(単色の検証用など)。格子の値は書き直す。</summary>
        public void SetLeftMaterial(Material material){
            this.leftMaterial=material;
            this.scaledPlane.GetComponent<MeshRenderer>().sharedMaterial=material;
            this.ApplyLeftMaterial();
        }

        // 配置の検査。右の天体の半径は、天体を作ったときの値と一致していること(中心は layout の半径で置くので、ずれると上端が y=0 から外れる)。
        private bool TryValidate(in PlaneSinkLayout value,out string reason){
            reason=null;
            if(!(value.OriginDistance>0)||!(value.EdgeMargin>0)||!(value.StepHeight>0)||!(value.RightRadius>0)){
                reason="配置は、面の原点までの距離 D、継ぎ目までの距離、段の高さ、右の天体の半径がすべて正である必要があります。";
                return false;
            }
            if(this.ground!=null&&Math.Abs(this.ground.Radius-value.RightRadius)>1e-6*value.RightRadius){
                reason="右の天体の半径("+this.ground.Radius.ToString("G7",CultureInfo.InvariantCulture)+" m)と、配置の半径("+value.RightRadius.ToString("G7",CultureInfo.InvariantCulture)+" m)が違います。";
                return false;
            }
            return true;
        }

        private bool ApplyLayout(){
            if(!this.TryValidate(in this.layout,out string reason)){return this.Fail(reason);}
            double scale=this.layout.PlaneScale;
            if(!LogicalScalar.TryFromDouble(scale,out LogicalScalar s)){return this.Fail("左の面の倍率を Logical にできません。");}
            if(!TryFromDouble3(new double3(this.layout.LeftOriginX,this.layout.StepHeight,0),out LogicalVector3 origin)){return this.Fail("左の面の位置を Logical にできません。");}
            if(!TryFromDouble3(new double3(this.layout.RightSpotX,-this.layout.RightRadius,0),out LogicalVector3 center)){return this.Fail("右の天体の位置を Logical にできません。");}
            // 右の格子の基準点は、惑星のローカル座標(惑星の中心からの位置)で渡す。上端 = (0, R, 0)。
            if(!TryFromDouble3(new double3(0,this.layout.RightRadius,0),out LogicalVector3 localTop)){return this.Fail("右の試験点を Logical にできません。");}
            this.planeLogical.LocalScale=new LogicalVector3(in s,in s,in s);
            this.planeLogical.LocalPosition=origin;
            this.planeLogical.LocalRotation=LogicalQuaternion.Identity;
            this.planeLogical.SyncUnityTransform();
            // 左の面の mesh(試験点のまわりを細かく)
            if(this.planeMesh!=null){DisposeObject(this.planeMesh);}
            this.planeMesh=ScaledPlaneMesh.Build(scale,this.layout.LeftSpotX-this.layout.LeftOriginX,0,FineHalf,FineStep,Growth);
            this.scaledPlane.GetComponent<MeshFilter>().sharedMesh=this.planeMesh;
            MeshRenderer renderer=this.scaledPlane.GetComponent<MeshRenderer>();
            if(this.leftMaterial==null){
                this.writtenAsset=renderer.sharedMaterial;
                this.originalGridOrigin=this.writtenAsset.GetVector("_GridOriginOS");
                this.originalObjectScale=this.writtenAsset.GetFloat("_ObjectScale");
                this.leftMaterial=this.writtenAsset;
            }
            this.ApplyLeftMaterial();
            // 継ぎ目の壁(世界の座標のまま、z 方向に細かく分けた mesh。倍率 1)
            if(this.wallMesh!=null){DisposeObject(this.wallMesh);}
            this.wallMesh=ScaledPlaneMesh.BuildWall(this.layout.StepHeight,WallHalfWidth,0,16,0.5,Growth);
            this.wall.GetComponent<MeshFilter>().sharedMesh=this.wallMesh;
            this.wallLogical.LocalScale=new LogicalVector3(LogicalScalar.One,LogicalScalar.One,LogicalScalar.One);
            this.wallLogical.LocalPosition=LogicalVector3.Zero;
            this.wallLogical.LocalRotation=LogicalQuaternion.Identity;
            this.wallLogical.SyncUnityTransform();
            // 右の天体(中心 (EdgeMargin, −R, 0)、回転なし)
            this.groundLogical.LocalPosition=center;
            this.groundLogical.LocalRotation=LogicalQuaternion.Identity;
            this.groundLogical.SyncUnityTransform();
            this.ground.SetReferencePoint(in localTop);
            Material groundMaterial=this.ground.Material;
            if(groundMaterial!=null){
                groundMaterial.SetVector("_GridAxisU",new Vector4(1,0,0,0));
                groundMaterial.SetVector("_GridAxisV",new Vector4(0,0,1,0));
            }
            return true;
        }

        // 左の面の shader(RadialScaledPlane)が、単純な拡大のまま(物体空間の float)で格子を描くための値。
        private void ApplyLeftMaterial(){
            if(this.leftMaterial==null){return;}
            double scale=this.layout.PlaneScale;
            float x=(float)((this.layout.LeftSpotX-this.layout.LeftOriginX)/scale);
            this.leftMaterial.SetVector("_GridOriginOS",new Vector4(x,0,0,0));
            this.leftMaterial.SetFloat("_ObjectScale",(float)scale);
        }

        /// <summary>今の試験点(惑星のローカル座標 = world 軸。Logical)。左は面の上、右は球の上端。</summary>
        public bool TryGetSpot(out LogicalVector3 spot){
            if(this.area==Area.Left){return TryFromDouble3(new double3(this.layout.LeftSpotX,this.layout.StepHeight,0),out spot);}
            return TryFromDouble3(new double3(this.layout.RightSpotX,0,0),out spot);
        }

        /// <summary>ボールの中心(Logical)= 試験点 + (x, r + y, z)。</summary>
        public bool TryGetBallCenter(out LogicalVector3 center){
            center=default;
            if(!this.initialized||!this.TryGetSpot(out LogicalVector3 spot)){return false;}
            LogicalScalar ballRadius=this.ball.Selector.LogicalRadius;
            if(!LogicalScalar.TryFromDouble(this.offsetX*UnitMeters,out LogicalScalar x)||!LogicalScalar.TryFromDouble(this.offsetY*UnitMeters,out LogicalScalar y)||
                !LogicalScalar.TryFromDouble(this.offsetZ*UnitMeters,out LogicalScalar z)||!LogicalScalar.TryAdd(in ballRadius,in y,out LogicalScalar lift)){
                return false;
            }
            var offset=new LogicalVector3(in x,in lift,in z);
            return LogicalVector3.TryAdd(in spot,in offset,out center);
        }

        /// <summary>試験点・ボールのずれ・camera を一度に決める(検証用)。</summary>
        public bool SetState(Area newArea,long x,long y,long z,float yaw,float pitch,float distance){
            this.area=newArea;
            this.offsetX=x;this.offsetY=y;this.offsetZ=z;this.remainderX=0;this.remainderZ=0;
            this.cameraYaw=yaw;this.cameraPitch=pitch;this.cameraDistance=distance;
            return this.Apply();
        }

        public bool SetOffset(long x,long y,long z){
            this.offsetX=x;this.offsetY=y;this.offsetZ=z;
            return this.Apply();
        }

        public bool SetCamera(float yaw,float pitch,float distance){
            this.cameraYaw=yaw;this.cameraPitch=pitch;this.cameraDistance=distance;
            return this.Apply();
        }

        public bool SetArea(Area newArea){
            this.area=newArea;
            return this.Apply();
        }

        /// <summary>ボールと camera を、今の状態に合わせて置く。失敗したら理由を <see cref="Failure"/> に残す。</summary>
        public bool Apply(){
            if(!this.initialized){return false;}
            this.failure=null;
            if(!this.TryGetBallCenter(out LogicalVector3 center)){return this.Fail("ボールの位置を求められません。");}
            this.ballLogical.LocalPosition=center;
            // 転がった距離 / 半径 だけ回る(+X へ動くと、軸は −Z。+Z へ動くと、軸は +X)。
            float radius=this.ball.Radius;
            float rollX=(float)(this.offsetX*UnitMeters/radius)*Mathf.Rad2Deg;
            float rollZ=(float)(this.offsetZ*UnitMeters/radius)*Mathf.Rad2Deg;
            Quaternion rotation=Quaternion.AngleAxis(rollZ,Vector3.right)*Quaternion.AngleAxis(rollX,Vector3.back);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logicalRotation)){return this.Fail("ボールの回転を Logical にできません。");}
            this.ballLogical.LocalRotation=logicalRotation;
            this.ballLogical.SyncUnityTransform();
            return this.ApplyCamera(in center);
        }

        // camera を、ボールの中心から yaw・pitch・distance の周回の位置に置き、ボールの中心を向ける(ボールは常に画面の中央)。pitch は水平から下を向く角度。
        private bool ApplyCamera(in LogicalVector3 center){
            double yaw=this.cameraYaw*Math.PI/180.0;double pitch=this.cameraPitch*Math.PI/180.0;
            var forward=new double3(Math.Sin(yaw)*Math.Cos(pitch),-Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch));
            double3 back=-forward*this.cameraDistance;
            if(!TryFromDouble3(back,out LogicalVector3 offset)||!LogicalVector3.TryAdd(in center,in offset,out LogicalVector3 position)){return this.Fail("camera の位置を Logical にできません。");}
            this.cameraRotation=Quaternion.LookRotation(new Vector3((float)forward.x,(float)forward.y,(float)forward.z),Vector3.up);
            if(!LogicalQuaternion.TryFromFloat4(this.cameraRotation.x,this.cameraRotation.y,this.cameraRotation.z,this.cameraRotation.w,out LogicalQuaternion logical)){return this.Fail("camera の回転を Logical にできません。");}
            this.cameraLogical.LocalPosition=position;
            this.cameraLogical.LocalRotation=logical;
            this.cameraLogical.SyncUnityTransform();
            return true;
        }

        private void ReadKeys(){
            IPlaneSinkKeys input=this.keys;
            long step=10;
            if(input.Held(PlaneSinkKey.Coarse)){step=100;}
            if(input.Held(PlaneSinkKey.Fine)){step=1;}
            this.NudgeKey(input,PlaneSinkKey.Raise,step,ref this.repeatRaise);
            this.NudgeKey(input,PlaneSinkKey.Sink,-step,ref this.repeatSink);
            double speed=0.3;
            if(input.Held(PlaneSinkKey.Coarse)){speed=3;}
            if(input.Held(PlaneSinkKey.Fine)){speed=0.01;}
            double yaw=this.cameraYaw*Math.PI/180.0;
            double forwardX=Math.Sin(yaw);double forwardZ=Math.Cos(yaw);
            double rightX=Math.Cos(yaw);double rightZ=-Math.Sin(yaw);
            double moveForward=0;double moveRight=0;
            if(input.Held(PlaneSinkKey.Forward)){moveForward+=1;}
            if(input.Held(PlaneSinkKey.Back)){moveForward-=1;}
            if(input.Held(PlaneSinkKey.Right)){moveRight+=1;}
            if(input.Held(PlaneSinkKey.Left)){moveRight-=1;}
            if(moveForward!=0||moveRight!=0){
                double meters=speed*Time.deltaTime;
                this.Move((forwardX*moveForward+rightX*moveRight)*meters,(forwardZ*moveForward+rightZ*moveRight)*meters);
            }
            float rate=60f*Time.deltaTime;
            if(input.Held(PlaneSinkKey.YawLeft)){this.cameraYaw-=rate;}
            if(input.Held(PlaneSinkKey.YawRight)){this.cameraYaw+=rate;}
            if(input.Held(PlaneSinkKey.PitchUp)){this.cameraPitch+=rate;}
            if(input.Held(PlaneSinkKey.PitchDown)){this.cameraPitch-=rate;}
            Vector2 look=input.LookDelta();
            this.cameraYaw+=look.x*0.2f;this.cameraPitch-=look.y*0.2f;
            float zoom=Mathf.Clamp(input.ZoomDelta(),-3f,3f);
            if(input.Held(PlaneSinkKey.ZoomIn)){zoom+=Time.deltaTime*4f;}
            if(input.Held(PlaneSinkKey.ZoomOut)){zoom-=Time.deltaTime*4f;}
            if(zoom!=0f){this.cameraDistance=Mathf.Clamp(this.cameraDistance*Mathf.Exp(-zoom*0.25f),MinimumDistance,5000f);}
            this.ClampPitch();
            if(input.PressedThisFrame(PlaneSinkKey.AreaLeft)){this.area=Area.Left;}
            if(input.PressedThisFrame(PlaneSinkKey.AreaRight)){this.area=Area.Right;}
            if(input.PressedThisFrame(PlaneSinkKey.Overview)){this.cameraPitch=35f;this.cameraDistance=12f;}
            if(input.PressedThisFrame(PlaneSinkKey.Auto)){this.autoPlay=!this.autoPlay;}
            if(input.PressedThisFrame(PlaneSinkKey.Reset)){this.ResetState();}
            if(input.PressedThisFrame(PlaneSinkKey.DistanceDown)){this.StepDistance(-1);}
            if(input.PressedThisFrame(PlaneSinkKey.DistanceUp)){this.StepDistance(1);}
        }

        // R キー: ボールと camera を初期の状態へ(領域と、左の面の D はそのまま)。
        private void ResetState(){
            this.autoPlay=false;this.autoStage=0;this.autoStep=0;
            this.offsetX=0;this.offsetY=0;this.offsetZ=0;this.remainderX=0;this.remainderZ=0;
            this.cameraYaw=0f;this.cameraPitch=20f;this.cameraDistance=0.35f;
        }

        // 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す(MoonBallDemo と同じ。キーごとに timer を持つ)。
        private void NudgeKey(IPlaneSinkKeys input,PlaneSinkKey key,long delta,ref float timer){
            if(input.PressedThisFrame(key)){
                this.offsetY+=delta;timer=0.4f;
            }else if(input.Held(key)){
                timer-=Time.deltaTime;
                if(timer<=0){this.offsetY+=delta;timer=0.06f;}
            }
        }

        /// <summary>ボールを試験点から水平に動かせる範囲(m)。<see cref="HorizontalLimit"/> と、継ぎ目までの距離 − ボールの半径 − 5cm の小さい方。</summary>
        public double HorizontalLimitMeters(){
            double fromSeam=this.layout.EdgeMargin-this.ball.Radius-0.05;
            return Math.Max(0.0,Math.Min(HorizontalLimit,fromSeam));
        }

        // 水平に dx、dz(m)動く。0.1mm 未満の端数は覚えておく。範囲に制限する。
        private void Move(double dx,double dz){
            this.remainderX+=dx/UnitMeters;this.remainderZ+=dz/UnitMeters;
            long wholeX=(long)Math.Truncate(this.remainderX);long wholeZ=(long)Math.Truncate(this.remainderZ);
            this.remainderX-=wholeX;this.remainderZ-=wholeZ;
            long limit=(long)(this.HorizontalLimitMeters()/UnitMeters);
            this.offsetX=Math.Max(-limit,Math.Min(limit,this.offsetX+wholeX));
            this.offsetZ=Math.Max(-limit,Math.Min(limit,this.offsetZ+wholeZ));
        }

        // camera が面から 5cm より下へ行かないように、pitch の下限を決める(上限は 89°。真下は LookRotation が縮退する)。
        // 必要な sin(pitch) の下限 = (5cm − ボールの中心の高さ)/距離。負なら camera は水平より下げてもよいが、−10° までにする。
        private void ClampPitch(){
            double centerHeight=this.ball.Radius+this.offsetY*UnitMeters;
            double minimumSine=(0.05-centerHeight)/this.cameraDistance;
            double minimum=Math.Asin(Math.Max(-1.0,Math.Min(1.0,minimumSine)))*180.0/Math.PI;
            minimum=Math.Min(89.0,Math.Max(-10.0,minimum));
            this.cameraPitch=Mathf.Clamp(this.cameraPitch,(float)minimum,89f);
        }

        private void StepDistance(int direction){
            int index=0;
            double best=double.MaxValue;
            for(int i=0;i<Distances.Length;i++){
                double difference=Math.Abs(Math.Log(Distances[i]/this.layout.OriginDistance));
                if(difference<best){best=difference;index=i;}
            }
            index=Math.Max(0,Math.Min(Distances.Length-1,index+direction));
            PlaneSinkLayout next=this.layout;
            next.OriginDistance=Distances[index];
            this.SetLayout(next);
        }

        private void UpdateAuto(){
            if(!this.autoPlay){return;}
            this.autoTimer+=Time.deltaTime;
            if(this.autoTimer<this.stepSeconds){return;}
            this.autoTimer=0;
            (long Delta,int Count) stage=AutoStages[this.autoStage];
            this.offsetY+=stage.Delta;
            this.autoStep++;
            if(this.autoStep>=stage.Count){
                this.autoStep=0;this.autoStage=(this.autoStage+1)%AutoStages.Length;
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
            }
            var lines=new System.Text.StringBuilder();
            if(!this.initialized){
                string reason="waiting for ground and ball (terrain roots)";
                if(this.failure!=null){reason=this.failure;}
                lines.AppendLine("PlaneSinkDemo: "+reason);
            }else{
                string side="LEFT: plain plane scaled "+this.layout.PlaneScale.ToString("N0",CultureInfo.InvariantCulture)+"x (object origin "+this.layout.OriginDistance.ToString("N0",CultureInfo.InvariantCulture)+" m away, float step about "+(this.layout.LeftQuantum()*1000.0).ToString("N1",CultureInfo.InvariantCulture)+" mm)";
                if(this.area==Area.Right){side="RIGHT: VG ground (radius "+this.layout.RightRadius.ToString("N0",CultureInfo.InvariantCulture)+" m, per-cell local origins)";}
                lines.AppendLine(side);
                lines.AppendLine("Ball sink depth "+Mm(-this.offsetY)+"    X "+Mm(this.offsetX)+"    Z "+Mm(this.offsetZ));
                lines.AppendLine("Camera yaw "+this.cameraYaw.ToString("0.0",CultureInfo.InvariantCulture)+" pitch "+this.cameraPitch.ToString("0.0",CultureInfo.InvariantCulture)+" distance "+this.cameraDistance.ToString("0.000",CultureInfo.InvariantCulture)+" m    Auto "+AutoText(this.autoPlay));
                lines.AppendLine("Up/Down raise/sink 1mm (Shift 10mm, Ctrl 0.1mm)   WASD move   Left/Right PgUp/PgDn Home/End wheel right-drag camera");
                lines.AppendLine("1 left (plain scale)   2 right (VG)   3 overview   [ ] left plane distance D   Space auto sink   R reset");
                if(this.failure!=null){lines.AppendLine("FAILED: "+this.failure);}
            }
            var rect=new Rect(12,10,1500,150);
            Color old=GUI.color;
            GUI.color=Color.black;GUI.Label(new Rect(rect.x+1,rect.y+1,rect.width,rect.height),lines.ToString(),this.style);
            GUI.color=Color.white;GUI.Label(rect,lines.ToString(),this.style);
            GUI.color=old;
        }

        private static string AutoText(bool value){
            if(value){return "ON";}
            return "OFF";
        }

        private static string Mm(long tenthMm){
            return (tenthMm*0.1).ToString("0.0",CultureInfo.InvariantCulture)+" mm";
        }
    }
}
