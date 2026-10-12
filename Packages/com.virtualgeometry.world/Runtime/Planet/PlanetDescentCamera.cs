using UnityEngine;
using UnityEngine.InputSystem;
using LogicalMatrix3x3=Omni.Logical.Matrix3x3;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalRenderPose=Omni.Logical.RenderPose;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 惑星へ降りるカメラ。Omni の Logical.Transform(親を持たない)を動かす。
    /// 速度は地表からの高さに比例し、地表(CPU の高さの Field、<see cref="PlanetTerrainSource.TrySampleSurface"/>)から eyeHeight より下へは行かない。
    /// 操作(Input System): WASD で前後左右(カメラの向き)、E/Q で上下(惑星の中心から外向き)、右ドラッグで向き、ホイールで速度の倍率。
    /// 検証などからは <see cref="Move"/> と <see cref="Teleport"/> で動かす。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlanetDescentCamera:MonoBehaviour{
        [SerializeField] private PlanetTerrainSource planet;
        /// <summary>海(任意)。有効なら、海面から波の高さの上限より下へも行かない。</summary>
        [SerializeField] private PlanetOceanSource ocean;
        [SerializeField,Min(0)] private float eyeHeight=1.7f;
        /// <summary>1 秒に、地表からの高さの何倍だけ進むか。</summary>
        [SerializeField,Min(0)] private float speedPerAltitude=1f;
        [SerializeField,Min(0)] private float minimumSpeed=1f;
        /// <summary>右ドラッグの 1 画素で回る角度(度)。</summary>
        [SerializeField,Min(0)] private float lookDegreesPerPixel=0.15f;
        [SerializeField] private bool readInput=true;

        private LogicalTransform logical;
        private float speedMultiplier=1f;

        public PlanetTerrainSource Planet{get=>this.planet;set=>this.planet=value;}
        public PlanetOceanSource Ocean{get=>this.ocean;set=>this.ocean=value;}
        public float EyeHeight{get=>this.eyeHeight;set=>this.eyeHeight=Mathf.Max(0,value);}
        public bool ReadInput{get=>this.readInput;set=>this.readInput=value;}
        /// <summary>直近の地表からの高さ(CPU の高さの Field)。</summary>
        public float Altitude{get;private set;}
        /// <summary>直近の移動で、地表より下へ行こうとして押し戻したか。</summary>
        public bool Clamped{get;private set;}
        /// <summary>直近の失敗の理由(成功した frame では null)。</summary>
        public string LastFailure{get;private set;}
        /// <summary>
        /// 速さの上限(m/s)。既定は正の無限大(上限なし)。ゲームが、読み込みの追いつく速さ(仮想オブジェクトの安全速度など)を任意で渡す。
        /// </summary>
        public float SpeedLimit{get;set;}=float.PositiveInfinity;

        private void OnEnable(){
            this.logical=this.GetComponent<LogicalTransform>();
        }

        private void Update(){
            if(!this.readInput){return;}
            Keyboard keyboard=Keyboard.current;Mouse mouse=Mouse.current;
            Vector3 move=Vector3.zero;Vector2 look=Vector2.zero;
            if(keyboard!=null){
                move.x=Axis(keyboard.dKey.isPressed,keyboard.aKey.isPressed);
                move.y=Axis(keyboard.eKey.isPressed,keyboard.qKey.isPressed);
                move.z=Axis(keyboard.wKey.isPressed,keyboard.sKey.isPressed);
            }
            if(mouse!=null){
                if(mouse.rightButton.isPressed){look=mouse.delta.ReadValue()*this.lookDegreesPerPixel;}
                float scroll=mouse.scroll.ReadValue().y;
                if(scroll>0){this.speedMultiplier*=1.25f;}
                if(scroll<0){this.speedMultiplier/=1.25f;}
                this.speedMultiplier=Mathf.Clamp(this.speedMultiplier,1e-3f,1e3f);
            }
            this.Move(move,look,Time.deltaTime);
        }

        private static float Axis(bool positive,bool negative){
            float value=0;
            if(positive){value+=1;}
            if(negative){value-=1;}
            return value;
        }

        /// <summary>
        /// 動かす。move は (右, 上, 前) で −1〜1。右と前はカメラの向き、上は惑星の中心から外向き。look は (右回り, 上向き) の角度(度)。
        /// 向きは惑星の中心からの上方向を保って回す。移動の後に地表より下なら押し戻す。
        /// </summary>
        public bool Move(Vector3 move,Vector2 look,float deltaTime){
            if(!this.TryBegin(out LogicalRenderPose pose,out LogicalVector3 local,out Vector3 up)){return false;}
            if(!this.TrySurface(in local,out float _,out float altitude)){return this.Fail("地表の高さを求められない。");}
            Quaternion rotation=this.transform.rotation;
            if(look!=Vector2.zero){
                rotation=Quaternion.AngleAxis(look.x,up)*rotation;
                rotation=Quaternion.AngleAxis(-look.y,rotation*Vector3.right)*rotation;
                if(!this.TrySetRotation(rotation)){return this.Fail("回転を Logical にできない。");}
            }
            float speed=Mathf.Max(this.minimumSpeed,altitude)*this.speedPerAltitude*this.speedMultiplier;
            if(speed>this.SpeedLimit){speed=Mathf.Max(0f,this.SpeedLimit);}
            Vector3 delta=(rotation*new Vector3(move.x,0,move.z)+up*move.y)*(speed*deltaTime);
            if(delta!=Vector3.zero){
                if(!LogicalVector3.TryFromFloat3(delta,out LogicalVector3 worldDelta)||!this.logical.TranslateWorld(in worldDelta)){return this.Fail("移動を Logical にできない。");}
            }
            return this.Clamp(in pose);
        }

        /// <summary>
        /// 瞬間移動の準備。移動先の真下の Cell を先に生成させる(<see cref="PlanetTerrainSource.Prefetch"/>)。準備ができたら true。
        /// true になってから <see cref="Teleport"/> すると、生成が追いつかない間の粗い面がカメラより上に描かれない。
        /// </summary>
        public bool PrepareTeleport(Vector3 direction,float altitude){
            if(!this.TryBegin(out LogicalRenderPose _,out LogicalVector3 _,out Vector3 _)){return false;}
            if(!this.TryTarget(direction,altitude,out LogicalVector3 target)){return false;}
            this.LastFailure=null;
            return this.planet.Prefetch(in target,this.eyeHeight);
        }

        /// <summary>惑星のローカル座標の方向 direction の、地表から altitude の高さへ移る。向きはそのまま。</summary>
        public bool Teleport(Vector3 direction,float altitude){
            if(!this.TryBegin(out LogicalRenderPose pose,out LogicalVector3 _,out Vector3 _)){return false;}
            if(!this.TryTarget(direction,altitude,out LogicalVector3 target)){return false;}
            if(!LogicalMatrix3x3.TryMultiplyVector(in pose.Basis,in target,out LogicalVector3 offset)||
                !LogicalPosition3.TryAdd(pose.Position,offset,out LogicalPosition3 world)){
                return this.Fail("移動先を Logical にできない。");
            }
            this.logical.LocalPosition=world.Value;
            return this.Clamp(in pose);
        }

        // 方向 direction の地表から altitude の点(惑星のローカル座標、Logical)。地表の高さは半径 R の点で求める。
        private bool TryTarget(Vector3 direction,float altitude,out LogicalVector3 target){
            target=default;
            direction=direction.normalized;
            if(!LogicalVector3.TryFromFloat3(direction,out LogicalVector3 unit)||!LogicalVector3.TryScale(in unit,this.planet.Selector.LogicalRadius,out LogicalVector3 surface)||
                !this.TrySurface(in surface,out float height,out float _)){
                return this.Fail("移動先の地表の高さを求められない。");
            }
            if(!LogicalScalar.TryFromFloat(height+altitude,out LogicalScalar lift)||
                !LogicalScalar.TryAdd(this.planet.Selector.LogicalRadius,in lift,out LogicalScalar distance)||
                !LogicalVector3.TryScale(in unit,in distance,out target)){
                return this.Fail("移動先を Logical にできない。");
            }
            return true;
        }

        // 地表から eyeHeight より下なら、惑星の中心からの方向へ押し戻す(押し戻すと真下の点が変わるので 2 回まで繰り返す)。
        private bool Clamp(in LogicalRenderPose pose){
            this.Clamped=false;
            for(int iteration=0;iteration<2;iteration++){
                if(!this.TryLocal(in pose,out LogicalVector3 local,out Vector3 up)||!this.TrySurface(in local,out float _,out float altitude)){
                    return this.Fail("地表の高さを求められない。");
                }
                this.Altitude=altitude;
                if(altitude>=this.eyeHeight){break;}
                this.Clamped=true;
                if(!LogicalVector3.TryFromFloat3(up*(this.eyeHeight-altitude),out LogicalVector3 push)||!this.logical.TranslateWorld(in push)){return this.Fail("押し戻しを Logical にできない。");}
            }
            this.logical.SyncUnityTransform();
            this.LastFailure=null;
            return true;
        }

        // 地表(地形と、海があれば波の高さの上限の海面の高い方)の高さと、point のそこからの高さ。
        private bool TrySurface(in LogicalVector3 point,out float height,out float altitude){
            if(!this.planet.TrySampleSurface(in point,out height,out altitude)){return false;}
            if(this.ocean!=null&&this.ocean.isActiveAndEnabled){
                float sea=this.ocean.WaveAmplitude;
                if(height<sea){
                    altitude-=sea-height;
                    height=sea;
                }
            }
            return true;
        }

        private bool TryBegin(out LogicalRenderPose pose,out LogicalVector3 local,out Vector3 up){
            pose=default;local=default;up=default;
            if(this.logical==null){this.logical=this.GetComponent<LogicalTransform>();}
            if(this.logical==null){return this.Fail("Omni の Logical.Transform が無い。");}
            if(this.planet==null||this.planet.Selector==null){return this.Fail("惑星が無いか、まだ有効になっていない。");}
            if(!LogicalRenderPose.TryResolve(this.planet.transform,out pose)){return this.Fail("惑星の Logical 姿勢を解決できない。");}
            if(!this.TryLocal(in pose,out local,out up)){return this.Fail("カメラの惑星ローカル位置を求められない。");}
            return true;
        }

        // カメラの惑星ローカル位置(Logical)と、world の上方向(惑星の中心から外向き)。
        private bool TryLocal(in LogicalRenderPose pose,out LogicalVector3 local,out Vector3 up){
            local=default;up=default;
            if(!this.logical.TryGetWorldAffine(out LogicalPosition3 position,out LogicalMatrix3x3 _)||
                !LogicalPosition3.TryDifference(in position,pose.Position,out LogicalVector3 fromPlanet)||
                !pose.Basis.TryInverse(out LogicalMatrix3x3 inverse)||
                !LogicalMatrix3x3.TryMultiplyVector(in inverse,in fromPlanet,out local)||
                !fromPlanet.TryToFloat3(out Unity.Mathematics.float3 world)){
                return false;
            }
            up=((Vector3)world).normalized;
            return up!=Vector3.zero;
        }

        private bool TrySetRotation(Quaternion rotation){
            rotation=rotation.normalized;
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion value)){return false;}
            // Unity の Transform は Omni の SyncUnityTransform だけが書く(Clamp の最後で同期する)。
            this.logical.LocalRotation=value;
            return true;
        }

        private bool Fail(string reason){
            this.LastFailure=reason;
            return false;
        }
    }
}
