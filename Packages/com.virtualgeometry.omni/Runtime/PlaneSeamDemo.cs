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
    /// 「単純に拡大した平面」と「VG の平面」を、同じ高さで継ぎ目 x=0 に隣り合わせて、位置合わせの誤差を見るデモ(Omni の Radial 対数描画)。
    /// 左: 単位の XZ 平面を一様に S 倍した 1 つの物体(単純な拡大)。物体空間の float の座標が S·2^-25(面の原点から試験点まで D=10^6 m で約 6cm)の刻みになり、
    ///     継ぎ目(右端 x=0)の位置と高さがその刻みでずれうる(誤差の内訳は未測定)。
    /// 右: 一辺 <see cref="PlaneSeamLayout.RightExtent"/> の <see cref="PlanarTerrainSource"/>(高さ 0)。Cell の原点は Logical、頂点は Cell の原点からの相対位置なので、継ぎ目は x=0 に正確に置かれる。
    /// 右の面は 1mm ずつ上下に動かせる(δ)。camera は自由に動く(Logical の位置と yaw・pitch)。位置はすべて Logical で計算する。
    /// この component は camera の GameObject(Logical.Transform と RadialCamera を持つ)に付ける。
    /// 操作(Input System): W A S D 水平に動く、E Q 上下、←→ PageUp/PageDown 向き(右ドラッグも)、↑↓ 右の面を 1mm 上げる/下げる(Shift で 10mm、Ctrl で 0.1mm)、
    ///   1 継ぎ目 2 左の面 3 右の面の視点、[ ] 左の面の原点までの距離 D を小さく/大きく、R 初期化
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlaneSeamDemo:MonoBehaviour{
        /// <summary>1 単位 = 0.1mm。</summary>
        public const double UnitMeters=1e-4;
        /// <summary>camera の面からの高さの下限(m)。論理 near(0.03 m)より大きい。</summary>
        public const double MinimumHeight=0.05;
        private static readonly double[] Distances={1e4,1e5,1e6,1e7};
        // 左の面の mesh の細かさ(面の原点からの座標 m)。
        private const double FineHalf=32;
        private const double FineStep=0.5;
        private const double Growth=1.25;

        [SerializeField] private PlaneSeamLayout layout=PlaneSeamLayout.Default;
        [SerializeField] private GameObject scaledPlane;
        [SerializeField] private PlanarTerrainSource ground;
        [SerializeField] private bool showHud=true;

        private LogicalTransform planeLogical,groundLogical,cameraLogical;
        // 左の面の material。Omni の Scene BRG は scene の材質に永続 asset の正本を要求し、実行時の複製は Radial の差し替えと
        // 明示登録の検査が食い違って Play が止まるので、複製せず scene の asset へ書く。終了時に元の値へ戻す(検証では SetLeftMaterial で別の材質に替える)。
        private Material leftMaterial;
        private Material writtenAsset;
        private Vector4 originalGridOrigin;
        private float originalObjectScale;
        private Mesh planeMesh;
        private bool initialized;
        private string failure;
        // 右の面の高さの差 δ(0.1mm 単位の整数。正は左より高い)。
        private long offsetY;
        private double3 cameraPosition=new double3(-0.2,0.3,-0.8);
        private float cameraYaw,cameraPitch;
        private Quaternion cameraRotation=Quaternion.identity;
        private float repeatRaise,repeatLower;
        private GUIStyle style;
        private IPlaneSeamKeys keys=PlaneSeamKeyboard.Instance;

        public bool IsInitialized=>this.initialized;
        public string Failure=>this.failure;
        public PlaneSeamLayout Layout=>this.layout;
        /// <summary>右の面の高さの差 δ(0.1mm 単位)。</summary>
        public long OffsetY=>this.offsetY;
        public double3 CameraPosition=>this.cameraPosition;
        public float CameraYaw=>this.cameraYaw;
        public float CameraPitch=>this.cameraPitch;
        public Quaternion CameraRotation=>this.cameraRotation;
        public GameObject ScaledPlane{get=>this.scaledPlane;set=>this.scaledPlane=value;}
        public PlanarTerrainSource Ground{get=>this.ground;set=>this.ground=value;}
        /// <summary>左の面の material(描画に使っているもの)。</summary>
        public Material LeftMaterial=>this.leftMaterial;
        /// <summary>キーの入力元(既定は Input System。null を入れると既定に戻る)。</summary>
        public IPlaneSeamKeys Keys{
            get=>this.keys;
            set{
                if(value==null){this.keys=PlaneSeamKeyboard.Instance;}else{this.keys=value;}
            }
        }

        private void OnEnable(){
            this.initialized=false;this.failure=null;
        }

        private void OnDestroy(){
            if(this.planeMesh!=null){DisposeObject(this.planeMesh);}
            this.RestoreWrittenAsset();
        }

        private void RestoreWrittenAsset(){
            if(this.writtenAsset==null){return;}
            this.writtenAsset.SetVector("_GridOriginOS",this.originalGridOrigin);
            this.writtenAsset.SetFloat("_ObjectScale",this.originalObjectScale);
            this.writtenAsset=null;
        }

        private static void DisposeObject(UnityEngine.Object value){
            if(Application.isPlaying){Destroy(value);}else{DestroyImmediate(value);}
        }

        private void Update(){
            // 初期化の後に、右の面が無効になった(Selector が無くなった)ら、使えるようになるまで待って初期化し直す。
            if(this.initialized&&(this.ground==null||this.ground.Selector==null)){
                this.initialized=false;
                return;
            }
            if(!this.initialized){
                if(this.ground==null||this.ground.Selector==null){return;}
                if(!this.Initialize()){return;}
            }
            this.ReadKeys();
            this.Apply();
        }

        /// <summary>配置を適用し、左の面の mesh を作り、camera を置く。右の面の Selector が使えるようになってから呼ぶ。</summary>
        public bool Initialize(){
            this.initialized=false;
            if(this.scaledPlane==null||this.ground==null){return this.Fail("左の面と右の面が必要です。");}
            this.planeLogical=this.scaledPlane.GetComponent<LogicalTransform>();
            this.groundLogical=this.ground.GetComponent<LogicalTransform>();
            this.cameraLogical=this.GetComponent<LogicalTransform>();
            if(this.planeLogical==null||this.groundLogical==null||this.cameraLogical==null){
                return this.Fail("左の面・右の面・camera に Omni の Logical.Transform が必要です。");
            }
            if(!this.ApplyLayout()){return false;}
            this.offsetY=0;
            this.LookFrom(new double3(-0.2,0.3,-0.8),new double3(0,0,0));
            this.initialized=true;
            return this.Apply();
        }

        /// <summary>配置を変える(左の面の mesh を作り直す)。配置が不正なときは、変えずに false を返す(<see cref="Failure"/> に理由)。</summary>
        public bool SetLayout(PlaneSeamLayout value){
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

        // 配置の検査。右の面の一辺は、面を作ったときの値と一致していること(中心は layout の一辺で置くので、ずれると左端が x=0 から外れる)。
        private bool TryValidate(in PlaneSeamLayout value,out string reason){
            reason=null;
            if(!(value.OriginDistance>0)||!(value.EdgeMargin>0)||!(value.RightExtent>0)){
                reason="配置は、面の原点までの距離 D、継ぎ目までの距離、右の面の一辺がすべて正である必要があります。";
                return false;
            }
            if(this.ground!=null&&Math.Abs(this.ground.Extent-value.RightExtent)>1e-6*value.RightExtent){
                reason="右の面の一辺("+this.ground.Extent.ToString("G7",CultureInfo.InvariantCulture)+" m)と、配置の一辺("+value.RightExtent.ToString("G7",CultureInfo.InvariantCulture)+" m)が違います。";
                return false;
            }
            return true;
        }

        private bool ApplyLayout(){
            if(!this.TryValidate(in this.layout,out string reason)){return this.Fail(reason);}
            double scale=this.layout.PlaneScale;
            if(!LogicalScalar.TryFromDouble(scale,out LogicalScalar s)){return this.Fail("左の面の倍率を Logical にできません。");}
            if(!TryFromDouble3(new double3(this.layout.LeftOriginX,0,0),out LogicalVector3 origin)){return this.Fail("左の面の位置を Logical にできません。");}
            // 右の格子の基準点は、面のローカル座標(面の中心からの位置)で渡す。右の試験点 = (RightSpotX − RightCenterX, 0, 0)。
            if(!TryFromDouble3(new double3(this.layout.RightSpotX-this.layout.RightCenterX,0,0),out LogicalVector3 localSpot)){return this.Fail("右の試験点を Logical にできません。");}
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
            this.ground.SetReferencePoint(in localSpot);
            Material groundMaterial=this.ground.Material;
            if(groundMaterial!=null){
                groundMaterial.SetVector("_GridAxisU",new Vector4(1,0,0,0));
                groundMaterial.SetVector("_GridAxisV",new Vector4(0,0,1,0));
            }
            return this.ApplyGround();
        }

        // 右の面(中心 (RightCenterX, δ, 0)、回転なし)。
        private bool ApplyGround(){
            if(!TryFromDouble3(new double3(this.layout.RightCenterX,this.offsetY*UnitMeters,0),out LogicalVector3 center)){return this.Fail("右の面の位置を Logical にできません。");}
            this.groundLogical.LocalPosition=center;
            this.groundLogical.LocalRotation=LogicalQuaternion.Identity;
            this.groundLogical.SyncUnityTransform();
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

        /// <summary>camera を from に置き、target を向ける(yaw、pitch を決める)。</summary>
        public void LookFrom(double3 from,double3 target){
            this.cameraPosition=from;
            double3 d=target-from;
            this.cameraYaw=(float)(Math.Atan2(d.x,d.z)*180.0/Math.PI);
            double horizontal=Math.Sqrt(d.x*d.x+d.z*d.z);
            this.cameraPitch=(float)(Math.Atan2(-d.y,horizontal)*180.0/Math.PI);
        }

        /// <summary>右の面の高さの差 δ、camera の位置と向きを一度に決める(検証用)。</summary>
        public bool SetState(long offsetY,double3 position,float yaw,float pitch){
            this.offsetY=offsetY;this.cameraPosition=position;this.cameraYaw=yaw;this.cameraPitch=pitch;
            return this.Apply();
        }

        public bool SetOffset(long value){
            this.offsetY=value;
            return this.Apply();
        }

        /// <summary>右の面と camera を、今の状態に合わせて置く。失敗したら理由を <see cref="Failure"/> に残す。</summary>
        public bool Apply(){
            if(!this.initialized){return false;}
            this.failure=null;
            if(!this.ApplyGround()){return false;}
            return this.ApplyCamera();
        }

        // camera を cameraPosition に置き、yaw・pitch の向きにする(pitch は水平から下を向く角度)。
        private bool ApplyCamera(){
            double yaw=this.cameraYaw*Math.PI/180.0;double pitch=this.cameraPitch*Math.PI/180.0;
            var forward=new double3(Math.Sin(yaw)*Math.Cos(pitch),-Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch));
            if(!TryFromDouble3(this.cameraPosition,out LogicalVector3 position)){return this.Fail("camera の位置を Logical にできません。");}
            this.cameraRotation=Quaternion.LookRotation(new Vector3((float)forward.x,(float)forward.y,(float)forward.z),Vector3.up);
            if(!LogicalQuaternion.TryFromFloat4(this.cameraRotation.x,this.cameraRotation.y,this.cameraRotation.z,this.cameraRotation.w,out LogicalQuaternion logical)){return this.Fail("camera の回転を Logical にできません。");}
            this.cameraLogical.LocalPosition=position;
            this.cameraLogical.LocalRotation=logical;
            this.cameraLogical.SyncUnityTransform();
            return true;
        }

        private void ReadKeys(){
            IPlaneSeamKeys input=this.keys;
            long step=10;
            if(input.Held(PlaneSeamKey.Coarse)){step=100;}
            if(input.Held(PlaneSeamKey.Fine)){step=1;}
            this.NudgeKey(input,PlaneSeamKey.Raise,step,ref this.repeatRaise);
            this.NudgeKey(input,PlaneSeamKey.Lower,-step,ref this.repeatLower);
            double speed=1.0;
            if(input.Held(PlaneSeamKey.Coarse)){speed=10;}
            if(input.Held(PlaneSeamKey.Fine)){speed=0.1;}
            double yaw=this.cameraYaw*Math.PI/180.0;
            double forwardX=Math.Sin(yaw);double forwardZ=Math.Cos(yaw);
            double rightX=Math.Cos(yaw);double rightZ=-Math.Sin(yaw);
            double moveForward=0;double moveRight=0;double moveUp=0;
            if(input.Held(PlaneSeamKey.Forward)){moveForward+=1;}
            if(input.Held(PlaneSeamKey.Back)){moveForward-=1;}
            if(input.Held(PlaneSeamKey.Right)){moveRight+=1;}
            if(input.Held(PlaneSeamKey.Left)){moveRight-=1;}
            if(input.Held(PlaneSeamKey.Up)){moveUp+=1;}
            if(input.Held(PlaneSeamKey.Down)){moveUp-=1;}
            if(moveForward!=0||moveRight!=0||moveUp!=0){
                double meters=speed*Time.deltaTime;
                this.cameraPosition+=new double3((forwardX*moveForward+rightX*moveRight)*meters,moveUp*meters,(forwardZ*moveForward+rightZ*moveRight)*meters);
            }
            float rate=60f*Time.deltaTime;
            if(input.Held(PlaneSeamKey.YawLeft)){this.cameraYaw-=rate;}
            if(input.Held(PlaneSeamKey.YawRight)){this.cameraYaw+=rate;}
            if(input.Held(PlaneSeamKey.PitchUp)){this.cameraPitch-=rate;}
            if(input.Held(PlaneSeamKey.PitchDown)){this.cameraPitch+=rate;}
            Vector2 look=input.LookDelta();
            this.cameraYaw+=look.x*0.2f;this.cameraPitch+=look.y*0.2f;
            // ホイール: 向いている方向へ寄る / 離れる。
            float zoom=Mathf.Clamp(input.ZoomDelta(),-3f,3f);
            if(zoom!=0f){
                double pitch=this.cameraPitch*Math.PI/180.0;
                double3 forward=new double3(Math.Sin(yaw)*Math.Cos(pitch),-Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch));
                this.cameraPosition+=forward*zoom*0.25*Math.Max(0.1,this.cameraPosition.y);
            }
            this.cameraPitch=Mathf.Clamp(this.cameraPitch,-89f,89f);
            this.cameraPosition.y=Math.Max(MinimumHeight,this.cameraPosition.y);
            if(input.PressedThisFrame(PlaneSeamKey.ViewSeam)){this.LookFrom(new double3(-0.2,0.3,-0.8),new double3(0,0,0));}
            if(input.PressedThisFrame(PlaneSeamKey.ViewLeft)){this.LookFrom(new double3(this.layout.LeftSpotX-0.2,0.3,-0.8),new double3(this.layout.LeftSpotX,0,0));}
            if(input.PressedThisFrame(PlaneSeamKey.ViewRight)){this.LookFrom(new double3(this.layout.RightSpotX-0.2,0.3,-0.8),new double3(this.layout.RightSpotX,0,0));}
            if(input.PressedThisFrame(PlaneSeamKey.Reset)){
                this.offsetY=0;this.LookFrom(new double3(-0.2,0.3,-0.8),new double3(0,0,0));
            }
            if(input.PressedThisFrame(PlaneSeamKey.DistanceDown)){this.StepDistance(-1);}
            if(input.PressedThisFrame(PlaneSeamKey.DistanceUp)){this.StepDistance(1);}
        }

        // 押した瞬間に 1 回、押し続けると 0.4 秒後から 0.06 秒ごとに繰り返す(PlaneSinkDemo と同じ。キーごとに timer を持つ)。
        private void NudgeKey(IPlaneSeamKeys input,PlaneSeamKey key,long delta,ref float timer){
            if(input.PressedThisFrame(key)){
                this.offsetY+=delta;timer=0.4f;
            }else if(input.Held(key)){
                timer-=Time.deltaTime;
                if(timer<=0){this.offsetY+=delta;timer=0.06f;}
            }
        }

        private void StepDistance(int direction){
            int index=0;
            double best=double.MaxValue;
            for(int i=0;i<Distances.Length;i++){
                double difference=Math.Abs(Math.Log(Distances[i]/this.layout.OriginDistance));
                if(difference<best){best=difference;index=i;}
            }
            index=Math.Max(0,Math.Min(Distances.Length-1,index+direction));
            PlaneSeamLayout next=this.layout;
            next.OriginDistance=Distances[index];
            this.SetLayout(next);
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
                string reason="waiting for the VG plane (terrain root)";
                if(this.failure!=null){reason=this.failure;}
                lines.AppendLine("PlaneSeamDemo: "+reason);
            }else{
                lines.AppendLine("LEFT (x<0): plain plane scaled "+this.layout.PlaneScale.ToString("N0",CultureInfo.InvariantCulture)+"x, object origin "+this.layout.OriginDistance.ToString("N0",CultureInfo.InvariantCulture)+" m away, float step about "+(this.layout.LeftQuantum()*1000.0).ToString("N1",CultureInfo.InvariantCulture)+" mm");
                lines.AppendLine("RIGHT (x>=0): VG plane (side "+this.layout.RightExtent.ToString("N0",CultureInfo.InvariantCulture)+" m, per-cell local origins)   height diff (right - left) "+Mm(this.offsetY));
                lines.AppendLine("Camera x "+this.cameraPosition.x.ToString("0.000",CultureInfo.InvariantCulture)+" y "+this.cameraPosition.y.ToString("0.000",CultureInfo.InvariantCulture)+" z "+this.cameraPosition.z.ToString("0.000",CultureInfo.InvariantCulture)+
                    "   yaw "+this.cameraYaw.ToString("0.0",CultureInfo.InvariantCulture)+" pitch "+this.cameraPitch.ToString("0.0",CultureInfo.InvariantCulture));
                lines.AppendLine("Up/Down raise/lower the VG plane 1mm (Shift 10mm, Ctrl 0.1mm)   WASD move   E/Q up/down   Left/Right PgUp/PgDn right-drag look   wheel dolly");
                lines.AppendLine("1 seam   2 left   3 right   [ ] left plane distance D   R reset");
                if(this.failure!=null){lines.AppendLine("FAILED: "+this.failure);}
            }
            var rect=new Rect(12,10,1500,150);
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
