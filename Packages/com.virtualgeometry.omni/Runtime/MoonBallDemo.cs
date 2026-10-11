using System;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using LogicalMatrix3x3=Omni.Logical.Matrix3x3;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalRenderPose=Omni.Logical.RenderPose;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// 月とサッカーボールのデモ。ボールを月の表面にぎりぎり接して置き、1mm(Ctrl で 0.1mm)単位で動かす。
    /// 月もボールも <see cref="PlanetTerrainSource"/>(cube-sphere の Cell に分けた実行時生成 cluster)で、位置は Omni の Logical(Q95.96 固定小数点)。
    /// 位置は float にせず Logical のまま計算するので、半径が 10^6 m でも、ボールの 1mm の動きは 1mm のまま画面に出る。
    /// この component は camera の GameObject(Logical.Transform と RadialCamera を持つ)に付ける。
    /// 月は惑星のローカル座標 = world 軸(回転なし)で、Logical の原点に置く前提。
    /// 操作(Input System):
    ///   ↑↓ ボールを月の面から 1mm 離す/近づける、←→ 面に沿って 1mm 転がす、PageUp/PageDown 手前/奥 1mm(Shift で 10mm、Ctrl で 0.1mm)
    ///   Space 自動の動き、1 接触の視点(Omni の COSMIC-PRECISION-001 と同じ配置)、2 月全体、3 月全体から接触の視点までのズーム、R 初期位置
    ///   WASD / QE 飛行、右ドラッグで向き(<see cref="PlanetDescentCamera"/>。月の面より下へは行かない)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MoonBallDemo:MonoBehaviour{
        /// <summary>接点の方向 up の接平面の、画面の横になる軸 u(v = up × u)。</summary>
        public static Vector3 TangentU(Vector3 up){
            Vector3 axis=Vector3.Cross(up,Vector3.forward);
            if(axis.sqrMagnitude<1e-6f){axis=Vector3.Cross(up,Vector3.right);}
            return axis.normalized;
        }

        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;

        [SerializeField] private PlanetTerrainSource moon;
        [SerializeField] private PlanetTerrainSource ball;
        [SerializeField] private PlanetDescentCamera descent;
        /// <summary>接点の方向(月の中心から外向き、惑星のローカル座標)。</summary>
        [SerializeField] private Vector3 contactDirection=new Vector3(0.3f,1f,0.2f);
        /// <summary>接触の視点: camera の月の面からの高さと、ボールの中心までの距離(Omni の COSMIC-PRECISION-001: 面から約 0.17m、ボールから 0.28m)。</summary>
        [SerializeField,Min(0)] private float viewHeight=0.17f;
        [SerializeField,Min(0)] private float viewDistance=0.28f;
        [SerializeField,Min(0.001f)] private float stepSeconds=0.12f;
        [SerializeField,Min(1)] private float zoomSeconds=25f;
        [SerializeField] private bool showHud=true;

        private LogicalTransform moonLogical;
        private LogicalTransform ballLogical;
        private LogicalTransform cameraLogical;
        private bool initialized;
        private string failure;
        // 接点の座標系(惑星のローカル座標)。up は外向き、u と v は接平面の単位ベクトル(v = up × u)。
        private LogicalVector3 upL,uL,vL;
        private Vector3 up,u,v;
        private LogicalVector3 contact;
        private float surfaceHeight;
        // ボールの接触の位置からのずれ(0.1mm 単位の整数): x=u 方向(転がす)、y=up 方向(隙間)、z=v 方向。
        private long offsetX,offsetY,offsetZ;
        private enum ViewMode{Free,Contact,Orbit,Zoom}
        private ViewMode viewMode=ViewMode.Free;
        private float zoomTime;
        private bool autoPlay;
        private int autoStage;
        private int autoStep;
        private float autoTimer;
        private float repeatTimer;
        private GUIStyle style;
        private IMoonBallKeys keys=MoonBallKeyboard.Instance;

        // 自動の動き: (軸 0=x、1=y、2=z、1 回の量(0.1mm 単位)、回数)。
        private static readonly (int Axis,long Delta,int Count)[] AutoStages={
            (1,10,20),(1,-10,20),(0,10,30),(0,-10,30),(2,10,20),(2,-10,20),
        };

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        /// <summary>接点(月の面の点、惑星のローカル座標、Logical)。</summary>
        public LogicalVector3 Contact=>this.contact;
        public Vector3 Up=>this.up;
        public Vector3 AxisU=>this.u;
        public Vector3 AxisV=>this.v;
        /// <summary>接点の高さ(月の半径 R からの高さ、CPU の高さの Field。float にした表示・検証用の値で、接点の位置は Logical の高さで決める)。</summary>
        public float SurfaceHeight=>this.surfaceHeight;
        public long OffsetX=>this.offsetX;
        public long OffsetY=>this.offsetY;
        public long OffsetZ=>this.offsetZ;
        public bool AutoPlay{get=>this.autoPlay;set=>this.autoPlay=value;}
        /// <summary>今の視点(Free、Contact、Orbit、Zoom)。</summary>
        public string ViewName=>this.viewMode.ToString();
        /// <summary>キーの入力元(既定は Input System のキーボード。null を入れると既定に戻る)。</summary>
        public IMoonBallKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=MoonBallKeyboard.Instance;}else{this.keys=value;}
            }
        }
        public PlanetTerrainSource Moon{get=>this.moon;set=>this.moon=value;}
        public PlanetTerrainSource Ball{get=>this.ball;set=>this.ball=value;}
        public PlanetDescentCamera Descent{get=>this.descent;set=>this.descent=value;}
        public float ViewHeight=>this.viewHeight;
        public float ViewDistance=>this.viewDistance;

        private void OnEnable(){
            this.initialized=false;this.failure=null;
        }

        private void Update(){
            if(!this.initialized){
                if(this.moon==null||this.ball==null||this.moon.Selector==null||this.ball.Selector==null){return;}
                if(!this.Initialize()){return;}
                this.ShowContactView();
            }
            this.ReadKeys();
            this.UpdateAuto();
            this.UpdateZoom();
        }

        /// <summary>接点を求め、ボールを置き、基準点と格子の軸を月の shader に渡す。月とボールの Selector が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            this.moonLogical=this.moon.GetComponent<LogicalTransform>();
            this.ballLogical=this.ball.GetComponent<LogicalTransform>();
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.moonLogical==null||this.ballLogical==null||this.cameraLogical==null){return this.Fail("月・ボール・camera に Omni の Logical.Transform が必要です。");}
            Vector3 direction=this.contactDirection.normalized;
            if(direction==Vector3.zero){return this.Fail("接点の方向が 0 です。");}
            // 外向きの単位ベクトルは Logical で正規化する(float の方向のままだと、半径が大きいとき球から外れる)。
            if(!LogicalVector3.TryFromFloat3(direction,out LogicalVector3 rough)||!LogicalVector3.TryNormalize(rough,out this.upL)||!this.upL.TryToFloat3(out float3 upFloat)){
                return this.Fail("接点の方向を Logical にできません。");
            }
            this.up=upFloat;
            this.u=TangentU(this.up);this.v=Vector3.Cross(this.up,this.u).normalized;
            if(!LogicalVector3.TryFromFloat3(this.u,out this.uL)||!LogicalVector3.TryFromFloat3(this.v,out this.vL)){return this.Fail("接平面の軸を Logical にできません。");}
            LogicalScalar radius=this.moon.Selector.LogicalRadius;
            // 接点の高さは Logical のまま使う(float にすると、|高さ| が数 km の地点で刻みが 0.5〜1 mm になる)。
            if(!LogicalVector3.TryScale(in this.upL,in radius,out LogicalVector3 onSphere)||!this.moon.TrySampleSurface(in onSphere,out LogicalScalar height,out LogicalScalar _)||!height.TryToFloat(out this.surfaceHeight)){
                return this.Fail("接点の地表の高さを求められません。");
            }
            if(!LogicalScalar.TryAdd(in radius,in height,out LogicalScalar distance)||!LogicalVector3.TryScale(in this.upL,in distance,out this.contact)){
                return this.Fail("接点を Logical にできません。");
            }
            this.moon.SetReferencePoint(in this.contact);
            Material material=this.moon.Material;
            if(material!=null){
                material.SetVector("_GridAxisU",new Vector4(this.u.x,this.u.y,this.u.z,0));
                material.SetVector("_GridAxisV",new Vector4(this.v.x,this.v.y,this.v.z,0));
            }
            this.offsetX=0;this.offsetY=0;this.offsetZ=0;
            this.initialized=true;
            return this.ApplyBall();
        }

        /// <summary>ボールを、接触の位置から (x, y, z)(0.1mm 単位)ずらした位置に置く。</summary>
        public bool SetOffset(long x,long y,long z){
            this.offsetX=x;this.offsetY=y;this.offsetZ=z;
            return this.ApplyBall();
        }

        public bool Nudge(int axis,long delta){
            if(axis==0){this.offsetX+=delta;}
            if(axis==1){this.offsetY+=delta;}
            if(axis==2){this.offsetZ+=delta;}
            return this.ApplyBall();
        }

        /// <summary>ボールの中心(惑星のローカル座標、Logical)= 接点 + up·(r + y) + u·x + v·z。</summary>
        public bool TryGetBallCenterLocal(out LogicalVector3 center){
            center=default;
            if(!this.initialized){return false;}
            LogicalScalar ballRadius=this.ball.Selector.LogicalRadius;
            if(!LogicalScalar.TryFromDouble(this.offsetY*UnitMeters,out LogicalScalar y)||!LogicalScalar.TryFromDouble(this.offsetX*UnitMeters,out LogicalScalar x)||
                !LogicalScalar.TryFromDouble(this.offsetZ*UnitMeters,out LogicalScalar z)||!LogicalScalar.TryAdd(in ballRadius,in y,out LogicalScalar lift)||
                !LogicalVector3.TryScale(in this.upL,in lift,out LogicalVector3 alongUp)||!LogicalVector3.TryScale(in this.uL,in x,out LogicalVector3 alongU)||
                !LogicalVector3.TryScale(in this.vL,in z,out LogicalVector3 alongV)||!LogicalVector3.TryAdd(in this.contact,in alongUp,out LogicalVector3 a)||
                !LogicalVector3.TryAdd(in a,in alongU,out LogicalVector3 b)||!LogicalVector3.TryAdd(in b,in alongV,out center)){
                return false;
            }
            return true;
        }

        private bool ApplyBall(){
            if(!this.TryGetBallCenterLocal(out LogicalVector3 center)){return this.Fail("ボールの位置を求められません。");}
            if(!LogicalVector3.TryAdd(this.moonLogical.LocalPosition,in center,out LogicalVector3 world)){return this.Fail("ボールの位置を Logical にできません。");}
            this.ballLogical.LocalPosition=world;
            // 面に沿って x 動くと、ボールは x/r ラジアン転がる(軸は up × u = v)。
            float angle=(float)(this.offsetX*UnitMeters/this.ball.Radius)*Mathf.Rad2Deg;
            Quaternion rotation=Quaternion.AngleAxis(angle,this.v);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){return this.Fail("ボールの回転を Logical にできません。");}
            this.ballLogical.LocalRotation=logical;
            this.ballLogical.SyncUnityTransform();
            return true;
        }

        /// <summary>
        /// camera の位置を、接点の上の高さ altitude、ボールから +v 側へ水平に horizontal の所に置き、ボールの中心を向ける。
        /// −v 向きに見るので、画面の右が +u(ボールを +u へ動かすと右へ動く)、+v は camera に近づく向きになる。
        /// </summary>
        public bool PlaceCamera(double altitude,double horizontal,bool lookAtContact=false){
            if(!this.initialized||!this.TryGetBallCenterLocal(out LogicalVector3 center)){return false;}
            if(!LogicalScalar.TryFromDouble(altitude,out LogicalScalar a)||!LogicalScalar.TryFromDouble(horizontal,out LogicalScalar h)||
                !LogicalVector3.TryScale(in this.upL,in a,out LogicalVector3 alongUp)||!LogicalVector3.TryScale(in this.vL,in h,out LogicalVector3 alongV)||
                !LogicalVector3.TryAdd(in this.contact,in alongUp,out LogicalVector3 p)||!LogicalVector3.TryAdd(in p,in alongV,out LogicalVector3 local)||
                !LogicalVector3.TryAdd(this.moonLogical.LocalPosition,in local,out LogicalVector3 world)){
                return this.Fail("camera の位置を Logical にできません。");
            }
            // 向きは、camera からボールの中心(lookAtContact なら接点)への差(Logical で引いてから float)。
            LogicalVector3 target=center;
            if(lookAtContact){target=this.contact;}
            if(!LogicalVector3.TrySubtract(in target,in local,out LogicalVector3 toBall)||!toBall.TryToFloat3(out float3 look)){return this.Fail("camera の向きを求められません。");}
            Vector3 forward=look;
            if(forward.sqrMagnitude<1e-20f){forward=-this.up;}
            Quaternion rotation=Quaternion.LookRotation(forward.normalized,this.up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){return this.Fail("camera の回転を Logical にできません。");}
            this.cameraLogical.LocalPosition=world;
            this.cameraLogical.LocalRotation=logical;
            this.cameraLogical.SyncUnityTransform();
            return true;
        }

        /// <summary>接触の視点の、ボールの中心からの水平距離(面からの高さ viewHeight、ボールの中心までの距離 viewDistance から)。</summary>
        public double ContactHorizontal(){
            double lift=this.viewHeight-this.ball.Radius;
            double square=(double)this.viewDistance*this.viewDistance-lift*lift;
            if(square<0){square=0;}
            return Math.Sqrt(square);
        }

        public void ShowContactView(){
            this.viewMode=ViewMode.Contact;
            this.SetFly(true);
            this.PlaceCamera(this.viewHeight,this.ContactHorizontal());
        }

        public void ShowOrbitView(){
            this.viewMode=ViewMode.Orbit;
            this.SetFly(true);
            this.PlaceCamera(this.moon.Radius*2.5,this.ContactHorizontal());
        }

        public void StartZoom(){
            this.viewMode=ViewMode.Zoom;this.zoomTime=0;
            this.SetFly(false);
        }

        private void SetFly(bool enabled){
            if(this.descent!=null){this.descent.ReadInput=enabled;}
        }

        private void UpdateZoom(){
            if(this.viewMode!=ViewMode.Zoom){return;}
            this.zoomTime+=Time.deltaTime;
            float t=Mathf.Clamp01(this.zoomTime/this.zoomSeconds);
            // 高さを対数で、月全体(半径の 2.5 倍)から接触の視点まで縮める。
            double far=this.moon.Radius*2.5;
            double altitude=this.viewHeight*Math.Pow(far/this.viewHeight,1.0-t);
            double horizontal=this.ContactHorizontal();
            this.PlaceCamera(altitude,horizontal);
            if(t>=1f){this.viewMode=ViewMode.Contact;this.SetFly(true);}
        }

        private void ReadKeys(){
            IMoonBallKeys input=this.keys;
            long step=10;
            if(input.Held(MoonBallKey.Shift)){step=100;}
            if(input.Held(MoonBallKey.Ctrl)){step=1;}
            this.NudgeKey(input,MoonBallKey.Up,1,step);
            this.NudgeKey(input,MoonBallKey.Down,1,-step);
            this.NudgeKey(input,MoonBallKey.Right,0,step);
            this.NudgeKey(input,MoonBallKey.Left,0,-step);
            this.NudgeKey(input,MoonBallKey.PageUp,2,step);
            this.NudgeKey(input,MoonBallKey.PageDown,2,-step);
            if(input.PressedThisFrame(MoonBallKey.Space)){this.autoPlay=!this.autoPlay;}
            if(input.PressedThisFrame(MoonBallKey.Digit1)){this.ShowContactView();}
            if(input.PressedThisFrame(MoonBallKey.Digit2)){this.ShowOrbitView();}
            if(input.PressedThisFrame(MoonBallKey.Digit3)){this.StartZoom();}
            if(input.PressedThisFrame(MoonBallKey.Reset)){this.autoPlay=false;this.SetOffset(0,0,0);this.ShowContactView();}
        }

        // 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す。
        private void NudgeKey(IMoonBallKeys input,MoonBallKey key,int axis,long delta){
            if(input.PressedThisFrame(key)){
                this.Nudge(axis,delta);this.repeatTimer=0.4f;
            }else if(input.Held(key)){
                this.repeatTimer-=Time.deltaTime;
                if(this.repeatTimer<=0){this.Nudge(axis,delta);this.repeatTimer=0.06f;}
            }
        }

        private void UpdateAuto(){
            if(!this.autoPlay){return;}
            this.autoTimer+=Time.deltaTime;
            if(this.autoTimer<this.stepSeconds){return;}
            this.autoTimer=0;
            (int Axis,long Delta,int Count) stage=AutoStages[this.autoStage];
            this.Nudge(stage.Axis,stage.Delta);
            this.autoStep++;
            if(this.autoStep>=stage.Count){
                this.autoStep=0;this.autoStage=(this.autoStage+1)%AutoStages.Length;
            }
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
                lines.AppendLine("MoonBallDemo: "+(this.failure??"waiting for moon and ball (terrain roots)"));
            }else{
                lines.AppendLine("Moon radius "+this.moon.Radius.ToString("N1",CultureInfo.InvariantCulture)+" m    Ball radius "+this.ball.Radius.ToString("0.000",CultureInfo.InvariantCulture)+" m");
                lines.AppendLine("Gap above moon surface "+Mm(this.offsetY)+"    Roll (slide) "+Mm(this.offsetX)+"    Depth "+Mm(this.offsetZ));
                if(this.TryGetBallCenterLocal(out LogicalVector3 center)&&LogicalVector3.TryLength(in center,out LogicalScalar distance)){
                    lines.AppendLine("Ball center from moon center "+Truncate(distance.ToString(),6)+" m (Logical Q95.96)");
                }
                lines.AppendLine("Auto "+(this.autoPlay?"ON  stage "+(this.autoStage+1)+"/"+AutoStages.Length+"  step "+(this.autoStep+1)+"/"+AutoStages[this.autoStage].Count:"OFF"));
                lines.AppendLine("Up/Down gap +-1mm   Left/Right roll   PgUp/PgDn toward camera   (Shift 10mm, Ctrl 0.1mm)   Space auto");
                lines.AppendLine("1 contact view   2 whole moon   3 zoom from orbit   R reset   WASD/QE fly, right-drag look");
            }
            var rect=new Rect(12,10,1400,160);
            Color old=GUI.color;
            GUI.color=Color.black;GUI.Label(new Rect(rect.x+1,rect.y+1,rect.width,rect.height),lines.ToString(),this.style);
            GUI.color=Color.white;GUI.Label(rect,lines.ToString(),this.style);
            GUI.color=old;
        }

        private static string Mm(long tenthMm){
            return (tenthMm*0.1).ToString("0.0",CultureInfo.InvariantCulture)+" mm";
        }

        private static string Truncate(string number,int decimals){
            int dot=number.IndexOf('.');
            if(dot<0||number.Length<=dot+1+decimals){return number;}
            return number.Substring(0,dot+1+decimals);
        }
    }
}
