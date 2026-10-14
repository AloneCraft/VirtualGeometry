using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// ボールのドリブル(上下だけの動き)を、Unity の物理エンジン(PhysX)で動かす。
    /// 独立した物理シーンに、ボール(Rigidbody + SphereCollider、重力あり、X・Z・回転は固定)と、面の高さを表す地面(kinematic の BoxCollider)を置き、
    /// 重力・衝突・反発係数(<see cref="Bounciness"/>)は物理エンジンが解く。メインのシーンの物理(他のデモ)には影響しない。
    /// 地面の衝突面は「面の高さ − ボールの半径」に置く。ボールの中心が面の高さに着いた瞬間に衝突するので、ボールの半分が面に埋まった所から跳ね返る。
    /// 反発係数 1 でも、離散化の誤差で跳ね返るたびにエネルギーが約 0.4% 増える(1/960 秒刻みの実測)ので、跳ね返った直後の上向きの速さだけを、最高 <see cref="ApexMeters"/> になる速さへ直す。
    /// 物理シーンは自動では進まないので、<see cref="Step"/> で、細かい刻みに分けて自分で進める。
    /// </summary>
    public sealed class MoonTripleDribble:IDisposable{
        /// <summary>ボールの中心が面の高さから上がる最高の高さ(m)。</summary>
        public const double ApexMeters=1.0;
        /// <summary>反発係数(PhysicsMaterial.bounciness)。</summary>
        public const float Bounciness=1f;
        // 物理を進める刻み(s)。これより粗いと、反発のエネルギー誤差が増える(1/60 秒で最高点が 1 回ごとに約 7% 増える)。
        private const float SubstepSeconds=1f/960f;
        // 1 回の Step で進める時間の上限(s)。Editor の停止などの長い時間は、この長さだけ進める。
        private const double MaximumStepSeconds=0.25;
        private const float GroundThickness=1f;
        private const float GroundSide=4f;

        private Scene scene;
        private PhysicsScene physics;
        private bool previewScene;
        private PhysicsMaterial material;
        private Rigidbody ball;
        private Rigidbody ground;

        /// <summary>重力加速度の大きさ(m/s²)。プロジェクトの Physics.gravity の y。</summary>
        public static double Gravity=>Math.Abs(Physics.gravity.y);
        /// <summary>跳ね返る瞬間の上向きの速さ(m/s)= √(2·g·最高の高さ)。</summary>
        public static double LaunchSpeed=>Math.Sqrt(2*Gravity*ApexMeters);
        /// <summary>地面が動かないときの、1 往復の時間(s)。</summary>
        public static double Period=>2*LaunchSpeed/Gravity;

        /// <summary>ドリブル中か。</summary>
        public bool Running{get;private set;}

        /// <summary>ボールの中心の高さ(m)。始める前は 0。</summary>
        public double Height{
            get{
                if(this.ball==null){return 0;}
                return this.ball.position.y;
            }
        }

        /// <summary>ボールの上向きの速さ(m/s)。始める前は 0。</summary>
        public double Velocity{
            get{
                if(this.ball==null){return 0;}
                return this.ball.linearVelocity.y;
            }
        }

        /// <summary>物理シーンの中のボールの位置(x と z は固定で 0 のまま)。始める前は 0。</summary>
        public Vector3 Position{
            get{
                if(this.ball==null){return Vector3.zero;}
                return this.ball.position;
            }
        }

        /// <summary>地面の高さ ground から、跳ね返った直後の状態で始める(ボールの半分が面に埋まっている)。物理シーンは、最初の呼び出しで作る。</summary>
        public void Start(double ground){
            if(!this.EnsureWorld()){return;}
            this.MoveGround(ground);
            this.ball.position=new Vector3(0,(float)ground,0);
            this.ball.linearVelocity=new Vector3(0,(float)LaunchSpeed,0);
            this.ball.angularVelocity=Vector3.zero;
            this.Running=true;
        }

        /// <summary>止める(高さは今のまま)。</summary>
        public void Stop(){
            this.Running=false;
        }

        /// <summary>
        /// seconds 秒だけ進める。ground はいまの面の高さ(地面の衝突面を、この高さ − ボールの半径へ動かす)。
        /// 面が上に動いてボールに重なっても、物理エンジンが押し出す。下に動けば、ボールはそのまま落ちて、新しい高さで跳ね返る。
        /// </summary>
        public void Step(double seconds,double ground){
            if(!this.Running||!(seconds>0)||this.ball==null){return;}
            this.MoveGround(ground);
            double span=Math.Min(seconds,MaximumStepSeconds);
            int count=Math.Max(1,(int)Math.Ceiling(span/SubstepSeconds));
            float step=(float)(span/count);
            float launch=(float)LaunchSpeed;
            for(int i=0;i<count;i++){
                float before=this.ball.linearVelocity.y;
                this.physics.Simulate(step);
                float after=this.ball.linearVelocity.y;
                // 下向きから上向きに変わった = 跳ね返った。エネルギー誤差を直して、最高の高さをちょうど ApexMeters にする。
                if(before<0f&&after>0f){
                    this.ball.linearVelocity=new Vector3(0,launch,0);
                }
            }
        }

        /// <summary>物理シーンとその中の物を片付ける。</summary>
        public void Dispose(){
            this.Running=false;
            if(this.scene.IsValid()){
                if(this.previewScene){
#if UNITY_EDITOR
                    UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(this.scene);
#endif
                }else if(this.scene.isLoaded){
                    SceneManager.UnloadSceneAsync(this.scene);
                }
            }
            this.scene=default;
            this.physics=default;
            this.ball=null;
            this.ground=null;
            if(this.material!=null){
                if(Application.isPlaying){
                    UnityEngine.Object.Destroy(this.material);
                }else{
                    UnityEngine.Object.DestroyImmediate(this.material);
                }
                this.material=null;
            }
        }

        // 地面の衝突面(上面)を、面の高さ − ボールの半径に置く。
        private void MoveGround(double groundHeight){
            this.ground.position=new Vector3(0,(float)(groundHeight-MoonTripleLayout.BallRadius-GroundThickness*0.5),0);
        }

        // 物理シーン・ボール・地面を、最初の一度だけ作る。Play 中は独立した物理シーン、Edit Mode(テスト)ではプレビューシーン。
        private bool EnsureWorld(){
            if(this.ball!=null){return true;}
            // 同じ名前の scene は作れないので、この物ごとに別の名前にする(複数の Demo や、実行中のテストと重ならない)。
            string sceneName="MoonTripleDribble "+this.GetHashCode().ToString();
#if UNITY_EDITOR
            if(!Application.isPlaying){
                this.scene=UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
                this.previewScene=true;
            }else{
                this.scene=SceneManager.CreateScene(sceneName,new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                this.previewScene=false;
            }
#else
            this.scene=SceneManager.CreateScene(sceneName,new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            this.previewScene=false;
#endif
            this.physics=this.scene.GetPhysicsScene();
            if(!this.physics.IsValid()){return false;}
            this.material=new PhysicsMaterial("MoonTripleDribble"){
                bounciness=Bounciness,
                dynamicFriction=0f,
                staticFriction=0f,
                bounceCombine=PhysicsMaterialCombine.Maximum,
                frictionCombine=PhysicsMaterialCombine.Minimum,
            };
            var groundObject=new GameObject("MoonTripleDribbleGround");
            BoxCollider box=groundObject.AddComponent<BoxCollider>();
            box.size=new Vector3(GroundSide,GroundThickness,GroundSide);
            box.sharedMaterial=this.material;
            this.ground=groundObject.AddComponent<Rigidbody>();
            this.ground.isKinematic=true;
            var ballObject=new GameObject("MoonTripleDribbleBall");
            SphereCollider sphere=ballObject.AddComponent<SphereCollider>();
            sphere.radius=(float)MoonTripleLayout.BallRadius;
            sphere.sharedMaterial=this.material;
            this.ball=ballObject.AddComponent<Rigidbody>();
            this.ball.useGravity=true;
            // 上下(Y)だけ動く。X・Z の位置と、回転は固定する。
            this.ball.constraints=RigidbodyConstraints.FreezePositionX|RigidbodyConstraints.FreezePositionZ|RigidbodyConstraints.FreezeRotation;
            this.ball.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
            SceneManager.MoveGameObjectToScene(groundObject,this.scene);
            SceneManager.MoveGameObjectToScene(ballObject,this.scene);
            return true;
        }
    }
}
