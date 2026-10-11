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
    /// 大きさの違う 4 つの天体を、同じ垂直線の上に積んだデモ(下から、巨大な天体 → 月 → 球 → ボール)。
    /// 隣り合う天体は面で接し(接点 1: 巨大な天体と月、2: 月と球、3: 球とボール)、接点ごとに 1mm(Ctrl で 0.1mm)単位で動かせる。
    /// すべての天体は <see cref="PlanetTerrainSource"/>(cube-sphere の Cell に分けた実行時生成 cluster)で、位置は Omni の Logical(Q95.96 固定小数点)。
    /// 天体の中心は、下の天体の中心 + 上向き·(下の天体の上の面の距離 + 上の天体の下の面の距離 + 隙間) として Logical のまま計算する。
    /// 接点 i を動かすと、その上に積んだ天体も一緒に動く(上の天体どうしの隙間は変わらない)。
    /// 操作: 1 2 3 接点を選んで camera をそこへ、4 全体、↑↓ 隙間 ±1mm、←→ 面に沿って 1mm、PageUp/PageDown 手前/奥 1mm(Shift で 10mm、Ctrl で 0.1mm)、R 初期位置
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BodyStackDemo:MonoBehaviour{
        /// <summary>積む天体の数(下から 0 = 巨大な天体、1 = 月、2 = 球、3 = ボール)。</summary>
        public const int BodyCount=4;
        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;

        [SerializeField] private PlanetTerrainSource[] bodies=new PlanetTerrainSource[BodyCount];
        /// <summary>接点の方向(巨大な天体の中心から外向き、惑星のローカル座標)。</summary>
        [SerializeField] private Vector3 contactDirection=new Vector3(0.3f,1f,0.2f);
        [SerializeField] private bool showHud=true;

        private readonly LogicalTransform[] transforms=new LogicalTransform[BodyCount];
        private readonly LogicalVector3[] centers=new LogicalVector3[BodyCount];
        // contacts[i] = 下の天体 i-1 の上の面の点(接点 i、world の Logical)。i = 1..3。
        private readonly LogicalVector3[] contacts=new LogicalVector3[BodyCount];
        private readonly float[] topHeights=new float[BodyCount];
        private readonly float[] bottomHeights=new float[BodyCount];
        private readonly long[] offsetX=new long[BodyCount];
        private readonly long[] offsetY=new long[BodyCount];
        private readonly long[] offsetZ=new long[BodyCount];
        private LogicalTransform cameraLogical;
        private LogicalVector3 upL,uL,vL;
        private Vector3 up,u,v;
        private bool initialized;
        private string failure;
        private int selected=3;
        private string viewName="None";
        private float repeatTimer;
        private IMoonBallKeys keys=MoonBallKeyboard.Instance;
        private GUIStyle style;

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        public int Selected=>this.selected;
        public string ViewName=>this.viewName;
        public Vector3 Up=>this.up;
        public Vector3 AxisU=>this.u;
        public Vector3 AxisV=>this.v;
        public PlanetTerrainSource[] Bodies=>this.bodies;
        /// <summary>天体 i の中心(world、Logical)。</summary>
        public LogicalVector3 Center(int body)=>this.centers[body];
        /// <summary>接点 i(下の天体 i-1 の上の面の点、world、Logical)。</summary>
        public LogicalVector3 Contact(int contact)=>this.contacts[contact];
        /// <summary>天体 i の、中心から上の面までの距離のうち、高さの Field の分(CPU)。</summary>
        public float TopHeight(int body)=>this.topHeights[body];
        public float BottomHeight(int body)=>this.bottomHeights[body];
        public long OffsetX(int contact)=>this.offsetX[contact];
        public long OffsetY(int contact)=>this.offsetY[contact];
        public long OffsetZ(int contact)=>this.offsetZ[contact];

        /// <summary>キーの入力元(既定は Input System のキーボード。null を入れると既定に戻る)。</summary>
        public IMoonBallKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=MoonBallKeyboard.Instance;}else{this.keys=value;}
            }
        }

        private void OnEnable(){
            this.initialized=false;this.failure=null;
        }

        private void Update(){
            if(!this.initialized){
                for(int i=0;i<BodyCount;i++){
                    if(this.bodies[i]==null||this.bodies[i].Selector==null){return;}
                }
                if(!this.Initialize()){return;}
                this.ShowContactView(3);
            }
            this.ReadKeys();
        }

        /// <summary>各天体の上下の面の高さを CPU で求め、基準点と格子の軸を材質に渡し、積み上げる。全天体の Selector が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.cameraLogical==null){return this.Fail("camera に Omni の Logical.Transform が必要です。");}
            for(int i=0;i<BodyCount;i++){
                if(this.bodies[i]==null){return this.Fail("天体 "+i+" がありません。");}
                this.transforms[i]=this.bodies[i].GetComponent<LogicalTransform>();
                if(this.transforms[i]==null){return this.Fail("天体 "+i+" に Omni の Logical.Transform が必要です。");}
                // 回転しない(天体のローカル座標 = world 軸)。
                if(!LogicalQuaternion.TryFromFloat4(0,0,0,1,out LogicalQuaternion identity)){return this.Fail("回転を Logical にできません。");}
                this.transforms[i].LocalRotation=identity;
            }
            Vector3 direction=this.contactDirection.normalized;
            if(direction==Vector3.zero){return this.Fail("接点の方向が 0 です。");}
            // 外向きの単位ベクトルは Logical で正規化する(float の方向のままだと、半径が大きいとき球から外れる)。
            if(!LogicalVector3.TryFromFloat3(direction,out LogicalVector3 rough)||!LogicalVector3.TryNormalize(rough,out this.upL)||!this.upL.TryToFloat3(out float3 upFloat)){
                return this.Fail("接点の方向を Logical にできません。");
            }
            this.up=upFloat;
            this.u=MoonBallDemo.TangentU(this.up);this.v=Vector3.Cross(this.up,this.u).normalized;
            if(!LogicalVector3.TryFromFloat3(this.u,out this.uL)||!LogicalVector3.TryFromFloat3(this.v,out this.vL)){return this.Fail("接平面の軸を Logical にできません。");}
            if(!LogicalVector3.TryNegate(in this.upL,out LogicalVector3 down)){return this.Fail("下向きを求められません。");}
            for(int i=0;i<BodyCount;i++){
                LogicalScalar radius=this.bodies[i].Selector.LogicalRadius;
                if(!this.TrySurfaceHeight(i,in this.upL,in radius,out this.topHeights[i])||!this.TrySurfaceHeight(i,in down,in radius,out this.bottomHeights[i])){
                    return this.Fail("天体 "+i+" の上下の面の高さを求められません。");
                }
            }
            for(int i=0;i<BodyCount;i++){
                this.ResetOffset(i);
            }
            this.initialized=true;
            if(!this.Apply()){return false;}
            // 格子の基準点 = 巨大な天体と月の、上の面の接点(天体のローカル座標)。
            for(int i=0;i<2;i++){
                if(!this.TryTopPointLocal(i,out LogicalVector3 point)){return this.Fail("基準点を求められません。");}
                this.bodies[i].SetReferencePoint(in point);
                Material material=this.bodies[i].Material;
                if(material!=null){
                    material.SetVector("_GridAxisU",new Vector4(this.u.x,this.u.y,this.u.z,0));
                    material.SetVector("_GridAxisV",new Vector4(this.v.x,this.v.y,this.v.z,0));
                }
            }
            return true;
        }

        private bool TrySurfaceHeight(int body,in LogicalVector3 direction,in LogicalScalar radius,out float height){
            height=0;
            if(!LogicalVector3.TryScale(in direction,in radius,out LogicalVector3 onSphere)){return false;}
            return this.bodies[body].TrySampleSurface(in onSphere,out height,out float _);
        }

        // 天体 i の上の面の点(天体のローカル座標)= 上向き·(R + 上の高さ)。
        private bool TryTopPointLocal(int body,out LogicalVector3 point){
            point=default;
            LogicalScalar radius=this.bodies[body].Selector.LogicalRadius;
            if(!LogicalScalar.TryFromFloat(this.topHeights[body],out LogicalScalar height)||!LogicalScalar.TryAdd(in radius,in height,out LogicalScalar distance)){return false;}
            return LogicalVector3.TryScale(in this.upL,in distance,out point);
        }

        public void ResetOffset(int contact){
            this.offsetX[contact]=0;this.offsetY[contact]=0;this.offsetZ[contact]=0;
        }

        /// <summary>接点 contact(1..3)の上の天体を、接触の位置から (x, y, z)(0.1mm 単位)ずらす。x = 面に沿って(u)、y = 隙間(up)、z = 手前奥(v)。</summary>
        public bool SetOffset(int contact,long x,long y,long z){
            this.offsetX[contact]=x;this.offsetY[contact]=y;this.offsetZ[contact]=z;
            return this.Apply();
        }

        public bool Nudge(int contact,int axis,long delta){
            if(axis==0){this.offsetX[contact]+=delta;}
            if(axis==1){this.offsetY[contact]+=delta;}
            if(axis==2){this.offsetZ[contact]+=delta;}
            return this.Apply();
        }

        // 下から順に、天体の中心 = 下の天体の上の面の点(接点) + 上向き·(上の天体の下の面の距離 + 隙間) + u·x + v·z。
        private bool Apply(){
            if(!this.initialized){return false;}
            LogicalVector3 center=this.transforms[0].LocalPosition;
            this.centers[0]=center;
            for(int i=1;i<BodyCount;i++){
                // 下の天体の上の面の点(world)
                if(!this.TryTopPointLocal(i-1,out LogicalVector3 topLocal)||!LogicalVector3.TryAdd(in center,in topLocal,out LogicalVector3 contact)){return this.Fail("接点 "+i+" を求められません。");}
                this.contacts[i]=contact;
                LogicalScalar radius=this.bodies[i].Selector.LogicalRadius;
                if(!LogicalScalar.TryFromFloat(this.bottomHeights[i],out LogicalScalar bottomHeight)||!LogicalScalar.TryAdd(in radius,in bottomHeight,out LogicalScalar bottom)||
                    !LogicalScalar.TryFromDouble(this.offsetY[i]*UnitMeters,out LogicalScalar y)||!LogicalScalar.TryAdd(in bottom,in y,out LogicalScalar lift)||
                    !LogicalScalar.TryFromDouble(this.offsetX[i]*UnitMeters,out LogicalScalar x)||!LogicalScalar.TryFromDouble(this.offsetZ[i]*UnitMeters,out LogicalScalar z)||
                    !LogicalVector3.TryScale(in this.upL,in lift,out LogicalVector3 alongUp)||!LogicalVector3.TryScale(in this.uL,in x,out LogicalVector3 alongU)||
                    !LogicalVector3.TryScale(in this.vL,in z,out LogicalVector3 alongV)||!LogicalVector3.TryAdd(in contact,in alongUp,out LogicalVector3 a)||
                    !LogicalVector3.TryAdd(in a,in alongU,out LogicalVector3 b)||!LogicalVector3.TryAdd(in b,in alongV,out center)){
                    return this.Fail("天体 "+i+" の位置を求められません。");
                }
                this.centers[i]=center;
                this.transforms[i].LocalPosition=center;
                // 面に沿って x 動くと、球は x/r ラジアン転がる(軸は up × u = v)。
                float angle=(float)(this.offsetX[i]*UnitMeters/this.bodies[i].Radius)*Mathf.Rad2Deg;
                Quaternion rotation=Quaternion.AngleAxis(angle,this.v);
                if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){return this.Fail("天体 "+i+" の回転を Logical にできません。");}
                this.transforms[i].LocalRotation=logical;
                this.transforms[i].SyncUnityTransform();
            }
            return true;
        }

        /// <summary>
        /// camera の位置を、接点 contact の上の高さ altitude、そこから +v 側へ水平に horizontal の所に置き、上の天体の中心(lookAtContact なら接点)を向ける。
        /// −v 向きに見るので、画面の右が +u(上の天体を +u へ動かすと右へ動く)になる。
        /// </summary>
        public bool PlaceCamera(int contact,double altitude,double horizontal,bool lookAtContact=false){
            if(!this.initialized){return false;}
            if(!LogicalScalar.TryFromDouble(altitude,out LogicalScalar a)||!LogicalScalar.TryFromDouble(horizontal,out LogicalScalar h)||
                !LogicalVector3.TryScale(in this.upL,in a,out LogicalVector3 alongUp)||!LogicalVector3.TryScale(in this.vL,in h,out LogicalVector3 alongV)||
                !LogicalVector3.TryAdd(in this.contacts[contact],in alongUp,out LogicalVector3 p)||!LogicalVector3.TryAdd(in p,in alongV,out LogicalVector3 position)){
                return this.Fail("camera の位置を Logical にできません。");
            }
            // 向きは、camera から対象への差(Logical で引いてから float)。
            LogicalVector3 target=this.centers[contact];
            if(lookAtContact){target=this.contacts[contact];}
            if(!LogicalVector3.TrySubtract(in target,in position,out LogicalVector3 toTarget)||!toTarget.TryToFloat3(out float3 look)){return this.Fail("camera の向きを求められません。");}
            Vector3 forward=look;
            if(forward.sqrMagnitude<1e-20f){forward=-this.up;}
            Quaternion rotation=Quaternion.LookRotation(forward.normalized,this.up);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logical)){return this.Fail("camera の回転を Logical にできません。");}
            this.cameraLogical.LocalPosition=position;
            this.cameraLogical.LocalRotation=logical;
            this.cameraLogical.SyncUnityTransform();
            return true;
        }

        /// <summary>接点 contact の既定の視点。1: 巨大な天体の面から 0.17m(月の中)で接点を見る、2: 球から 3.5m、3: ボールの中心から 0.28m。</summary>
        public void ShowContactView(int contact){
            this.selected=contact;
            this.viewName="Contact"+contact;
            if(contact==1){
                this.PlaceCamera(1,0.17,0.3,true);
            }else if(contact==2){
                this.PlaceCamera(2,0.5,Math.Sqrt(3.5*3.5-0.5*0.5));
            }else{
                // ボールの半径 r の中心は接点の上 r にある。面から 0.17m、中心から 0.28m(Omni の COSMIC-PRECISION-001 と同じ)。
                double r=this.bodies[3].Radius;
                double lift=0.17-r;
                this.PlaceCamera(3,0.17,Math.Sqrt(0.28*0.28-lift*lift));
            }
        }

        /// <summary>全体: 巨大な天体の面の上、月の半径の 2 倍ほどの高さから月を見る(球とボールは画素より小さい)。</summary>
        public void ShowOverview(){
            this.viewName="Overview";
            double moonRadius=this.bodies[1].Radius;
            this.PlaceCamera(1,moonRadius*2.0,moonRadius*3.0);
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
            if(input.PressedThisFrame(MoonBallKey.Digit1)){this.ShowContactView(1);}
            if(input.PressedThisFrame(MoonBallKey.Digit2)){this.ShowContactView(2);}
            if(input.PressedThisFrame(MoonBallKey.Digit3)){this.ShowContactView(3);}
            if(input.PressedThisFrame(MoonBallKey.Digit4)){this.ShowOverview();}
            if(input.PressedThisFrame(MoonBallKey.Reset)){
                for(int i=1;i<BodyCount;i++){this.ResetOffset(i);}
                this.Apply();
                if(this.viewName=="Overview"){this.ShowOverview();}else{this.ShowContactView(this.selected);}
            }
        }

        // 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す。
        private void NudgeKey(IMoonBallKeys input,MoonBallKey key,int axis,long delta){
            if(input.PressedThisFrame(key)){
                this.Nudge(this.selected,axis,delta);this.repeatTimer=0.4f;
            }else if(input.Held(key)){
                this.repeatTimer-=Time.deltaTime;
                if(this.repeatTimer<=0){this.Nudge(this.selected,axis,delta);this.repeatTimer=0.06f;}
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
                lines.AppendLine("BodyStackDemo: "+(this.failure??"waiting for the four bodies"));
            }else{
                lines.AppendLine("Radii (m): ground "+this.bodies[0].Radius.ToString("G6",CultureInfo.InvariantCulture)+"  moon "+this.bodies[1].Radius.ToString("G6",CultureInfo.InvariantCulture)+
                    "  sphere "+this.bodies[2].Radius.ToString("G6",CultureInfo.InvariantCulture)+"  ball "+this.bodies[3].Radius.ToString("G6",CultureInfo.InvariantCulture));
                for(int i=1;i<BodyCount;i++){
                    string mark="  ";
                    if(i==this.selected){mark="> ";}
                    lines.AppendLine(mark+"Contact "+i+" (body "+i+" on body "+(i-1)+"): gap "+Mm(this.offsetY[i])+"  slide "+Mm(this.offsetX[i])+"  depth "+Mm(this.offsetZ[i]));
                }
                lines.AppendLine("View "+this.viewName);
                lines.AppendLine("1 2 3 contact view   4 overview   Up/Down gap +-1mm   Left/Right slide   PgUp/PgDn depth   (Shift 10mm, Ctrl 0.1mm)   R reset");
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
    }
}
