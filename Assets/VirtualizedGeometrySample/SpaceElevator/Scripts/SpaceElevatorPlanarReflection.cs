using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Samples.SpaceElevator{
    /// <summary>
    /// 濡れた広場に塔や灯りを映すための平面反射。Main Camera を広場の面(Unity の y=planeHeight)で鏡映した反射カメラを持ち、
    /// その描画結果を global texture(_SpaceElevatorPlanarReflection)として、SpaceElevator/Procedural の WetGround レシピへ渡す。
    /// 使用 scene: SpaceElevator.unity の Main Camera(SpaceElevatorSceneBuilder が付ける)。
    /// 反射カメラは Unity の通常の camera 順序で Main Camera より先に描かれる(depth を小さくする)。入れ子の描画はしない。
    /// VG は Game 種別の Base camera なら反射カメラでも専用描画するため、鏡映で巻き順が逆になる間だけカリングを反転する。
    /// Main Camera 以外(Scene view・反射カメラ自身)の描画では有効フラグを 0 にし、シェーダーは空の反射に戻す。
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class SpaceElevatorPlanarReflection:MonoBehaviour{
        private static readonly int TextureId=Shader.PropertyToID("_SpaceElevatorPlanarReflection");
        private static readonly int ValidId=Shader.PropertyToID("_SpaceElevatorPlanarReflectionValid");
        private static readonly int MipsId=Shader.PropertyToID("_SpaceElevatorPlanarReflectionMips");

        [SerializeField,Tooltip("反射面の高さ(Unity ワールドの y、m)。広場の上面は Blender の z=0。")] private float planeHeight=0f;
        [SerializeField,Tooltip("反射面より下を切る near 平面のずらし量(m)。広場の凹凸がちらつくなら少し上げる。")] private float clipOffset=0.05f;
        [SerializeField,Range(0.25f,1f),Tooltip("Main Camera に対する反射テクスチャの解像度の倍率。")] private float resolutionScale=0.5f;
        [SerializeField,Tooltip("反射に描く layer。")] private LayerMask reflectionLayers=~0;

        private Camera mainCamera;
        private Camera reflectionCamera;
        private RenderTexture target;

        /// <summary>反射カメラ(検証の撮影で明示的に描くときに使う)。無効な間は null。</summary>
        public Camera ReflectionCamera=>this.reflectionCamera;

        /// <summary>反射テクスチャ。無効な間は null。</summary>
        public RenderTexture Target=>this.target;

        private void OnEnable(){
            this.mainCamera=this.GetComponent<Camera>();
            var cameraObject=new GameObject("Planar Reflection Camera"){hideFlags=HideFlags.HideAndDontSave};
            this.reflectionCamera=cameraObject.AddComponent<Camera>();
            this.reflectionCamera.enabled=true;
            var data=cameraObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing=false;
            data.antialiasing=AntialiasingMode.None;
            data.requiresColorOption=CameraOverrideOption.Off;
            data.renderShadows=true;
            RenderPipelineManager.beginContextRendering+=this.BeginContext;
            RenderPipelineManager.beginCameraRendering+=this.BeginCamera;
            RenderPipelineManager.endCameraRendering+=this.EndCamera;
            Shader.SetGlobalFloat(ValidId,0f);
        }

        private void OnDisable(){
            RenderPipelineManager.beginContextRendering-=this.BeginContext;
            RenderPipelineManager.beginCameraRendering-=this.BeginCamera;
            RenderPipelineManager.endCameraRendering-=this.EndCamera;
            Shader.SetGlobalFloat(ValidId,0f);
            if(this.reflectionCamera!=null){
                this.reflectionCamera.targetTexture=null;
                DestroyImmediate(this.reflectionCamera.gameObject);
                this.reflectionCamera=null;
            }
            if(this.target!=null){
                this.target.Release();
                DestroyImmediate(this.target);
                this.target=null;
            }
        }

        // camera の描画が始まる前(同じ context の全 camera より前)に、反射カメラを Main Camera の鏡像へ合わせる。
        private void BeginContext(ScriptableRenderContext context,System.Collections.Generic.List<Camera> cameras){
            if(cameras.Contains(this.mainCamera)){
                this.UpdateReflectionCamera();
            }
        }

        private void BeginCamera(ScriptableRenderContext context,Camera camera){
            if(camera==this.reflectionCamera){
                // 鏡映した view 行列で三角形の巻き順が逆になるため、反射カメラの描画中だけカリングを反転する。
                GL.invertCulling=true;
                Shader.SetGlobalFloat(ValidId,0f);
                return;
            }
            if(camera==this.mainCamera&&this.target!=null){
                Shader.SetGlobalTexture(TextureId,this.target);
                Shader.SetGlobalFloat(MipsId,this.target.mipmapCount-1);
                Shader.SetGlobalFloat(ValidId,1f);
                return;
            }
            Shader.SetGlobalFloat(ValidId,0f);
        }

        private void EndCamera(ScriptableRenderContext context,Camera camera){
            if(camera==this.reflectionCamera){
                GL.invertCulling=false;
                return;
            }
            if(camera==this.mainCamera){
                Shader.SetGlobalFloat(ValidId,0f);
            }
        }

        /// <summary>
        /// 反射カメラを Main Camera の鏡像に合わせ、反射テクスチャを用意する。通常は描画の直前に自動で呼ばれる。
        /// 検証では Main Camera を撮る前にこれを呼び、反射カメラを明示的に描く。
        /// </summary>
        public void UpdateReflectionCamera(){
            if(this.mainCamera==null||this.reflectionCamera==null){return;}
            this.EnsureTarget();
            Camera source=this.mainCamera;
            Camera mirror=this.reflectionCamera;
            mirror.CopyFrom(source);
            mirror.cameraType=CameraType.Game;
            mirror.depth=source.depth-10f;
            mirror.targetTexture=this.target;
            mirror.cullingMask=this.reflectionLayers.value;
            mirror.allowMSAA=false;
            mirror.allowHDR=true;
            mirror.useOcclusionCulling=false;
            var plane=new Vector4(0f,1f,0f,-this.planeHeight);
            Matrix4x4 reflection=ReflectionMatrix(plane);
            mirror.worldToCameraMatrix=source.worldToCameraMatrix*reflection;
            Vector3 mirroredPosition=reflection.MultiplyPoint(source.transform.position);
            mirror.transform.position=mirroredPosition;
            Vector3 forward=Vector3.Reflect(source.transform.forward,Vector3.up);
            Vector3 up=Vector3.Reflect(source.transform.up,Vector3.up);
            mirror.transform.rotation=Quaternion.LookRotation(forward,up);
            // 反射面より下(鏡像では面の手前側)を切る斜め near 平面
            Vector4 clipPlane=CameraSpacePlane(mirror,new Vector3(0f,this.planeHeight,0f),Vector3.up,this.clipOffset);
            mirror.projectionMatrix=source.CalculateObliqueMatrix(clipPlane);
        }

        private void EnsureTarget(){
            int width=Mathf.Max(64,Mathf.RoundToInt(this.mainCamera.pixelWidth*this.resolutionScale));
            int height=Mathf.Max(64,Mathf.RoundToInt(this.mainCamera.pixelHeight*this.resolutionScale));
            if(this.target!=null&&this.target.width==width&&this.target.height==height){return;}
            if(this.target!=null){
                if(this.reflectionCamera!=null){
                    this.reflectionCamera.targetTexture=null;
                }
                this.target.Release();
                DestroyImmediate(this.target);
            }
            this.target=new RenderTexture(width,height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){
                name="SpaceElevator Planar Reflection",hideFlags=HideFlags.HideAndDontSave,useMipMap=true,autoGenerateMips=true,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear
            };
            this.target.Create();
        }

        private static Matrix4x4 ReflectionMatrix(Vector4 plane){
            var m=Matrix4x4.identity;
            m.m00=1f-2f*plane.x*plane.x;m.m01=-2f*plane.x*plane.y;m.m02=-2f*plane.x*plane.z;m.m03=-2f*plane.w*plane.x;
            m.m10=-2f*plane.y*plane.x;m.m11=1f-2f*plane.y*plane.y;m.m12=-2f*plane.y*plane.z;m.m13=-2f*plane.w*plane.y;
            m.m20=-2f*plane.z*plane.x;m.m21=-2f*plane.z*plane.y;m.m22=1f-2f*plane.z*plane.z;m.m23=-2f*plane.w*plane.z;
            return m;
        }

        private static Vector4 CameraSpacePlane(Camera camera,Vector3 position,Vector3 normal,float offset){
            Vector3 offsetPosition=position+normal*offset;
            Matrix4x4 view=camera.worldToCameraMatrix;
            Vector3 cameraPosition=view.MultiplyPoint(offsetPosition);
            Vector3 cameraNormal=view.MultiplyVector(normal).normalized;
            return new Vector4(cameraNormal.x,cameraNormal.y,cameraNormal.z,-Vector3.Dot(cameraPosition,cameraNormal));
        }
    }
}
