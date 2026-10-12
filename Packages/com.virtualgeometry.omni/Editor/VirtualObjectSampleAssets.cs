using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VirtualGeometry.VirtualObjects;
using VirtualizedGeometry;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 仮想オブジェクトの検証と見本に使う建物のアセット(手続きで作る高層の建物のメッシュ、その VG 変換、材質)。
    /// 置き場所は git 管理外の <see cref="Root"/>(検証のたびに同じ内容を作る。内容が同じならファイルを書き直さず、取り込み直さない)。
    /// 建物: 底面 24×24 m、高さ 72 m(18 階)、各面に 8 列の窓(枠と 0.3 m の凹み)。原点は底面の中心、上は +Y。約 1 万三角形。
    /// </summary>
    public static class VirtualObjectSampleAssets{
        public const string Root="Assets/Development/PlanetWorld/VirtualObject";
        public const string ModelPath=Root+"/Building.obj";
        public const string VirtualMaterialPath=Root+"/BuildingVirtual.mat";
        public const string LitMaterialPath=Root+"/BuildingLit.mat";
        private const string VirtualShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const string LitShaderName="Omni/RadialGlobalLit";
        // VG の変換の設定(ModelImporter.userData。core の VirtualGeometryModelSettings が JsonUtility で書く形と同じ文字列にする)。
        private const string ImportSettings="{\"virtualGeometry\":{\"enabled\":true,\"maxTrianglesPerCluster\":64,\"hierarchyFanout\":4,\"simplificationRatio\":0.5,\"positionQuantizationBits\":16}}";

        public const float Width=24f;
        public const float Depth=24f;
        public const float Height=72f;
        public const int Floors=18;
        public const int Bays=8;
        public const float Recess=0.3f;
        public static readonly Color BuildingColor=new Color(0.72f,0.68f,0.62f,1f);

        /// <summary>作ったアセット。</summary>
        public sealed class Building{
            public VirtualGeometryMeshAsset VirtualAsset;
            /// <summary>元の(細かい)メッシュ。通常の MeshRenderer で描く比べ用と、近景のプレハブに使う。</summary>
            public Mesh SourceMesh;
            /// <summary>VG の材質(RadialGlobalLit.vgshader)。</summary>
            public Material VirtualMaterial;
            /// <summary>通常の MeshRenderer の材質(Omni/RadialGlobalLit)。</summary>
            public Material LitMaterial;
        }

        /// <summary>建物のアセットを用意する(無ければ作り、内容が変わっていなければそのまま使う)。</summary>
        public static Building EnsureBuilding(){
            Directory.CreateDirectory(Root);
            Mesh mesh=BuildMesh();
            try{
                bool changed=WriteObj(mesh,ModelPath);
                if(changed){
                    AssetDatabase.ImportAsset(Path.ChangeExtension(ModelPath,".mtl"),ImportAssetOptions.ForceSynchronousImport);
                    AssetDatabase.ImportAsset(ModelPath,ImportAssetOptions.ForceSynchronousImport);
                }
            }finally{
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            var importer=AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if(importer==null){throw new InvalidOperationException(ModelPath+" is not a model.");}
            // 子の帯(MeshClusterPartition)が Play 中とビルドで元のメッシュの頂点を読むので、Read/Write を有効にする。
            if(importer.userData!=ImportSettings||!importer.isReadable){
                importer.userData=ImportSettings;
                importer.isReadable=true;
                importer.SaveAndReimport();
            }
            var result=new Building();
            Shader virtualShader=AssetDatabase.LoadAssetAtPath<Shader>(VirtualShaderPath);
            if(virtualShader==null){throw new InvalidOperationException("Shader not found: "+VirtualShaderPath);}
            Shader litShader=Shader.Find(LitShaderName);
            if(litShader==null){throw new InvalidOperationException("Shader not found: "+LitShaderName);}
            result.VirtualMaterial=EnsureMaterial(VirtualMaterialPath,virtualShader);
            result.LitMaterial=EnsureMaterial(LitMaterialPath,litShader);
            // モデルの中のアセットは、材質の作成と保存(AssetDatabase の更新)の後に読む(先に読んだ参照は更新で無効になることがある)。
            UnityEngine.Object[] assets=AssetDatabase.LoadAllAssetsAtPath(ModelPath);
            result.VirtualAsset=assets.OfType<VirtualGeometryMeshAsset>().FirstOrDefault();
            if(result.VirtualAsset==null){throw new InvalidOperationException(ModelPath+" was not converted to Virtual Geometry. See the import warnings.");}
            result.SourceMesh=assets.OfType<Mesh>().FirstOrDefault(candidate=>!candidate.name.EndsWith("(Virtual Geometry Proxy)",StringComparison.Ordinal));
            if(result.SourceMesh==null){throw new InvalidOperationException(ModelPath+" has no source mesh.");}
            return result;
        }

        /// <summary>建物の種類(仮想オブジェクトの定義と、帯ごとの表現の資源)。</summary>
        public sealed class BuildingKind{
            public Building Building;
            /// <summary>建物の種類のプレハブの VirtualObject(帯: なし / ビルボード / VG メッシュ / プレハブ / 子)。</summary>
            public VirtualObject Definition;
            public GameObject VirtualPrefab;
            public GameObject DetailPrefab;
            public Material BillboardMaterial;
            /// <summary>子の型: 外壁の部分(既定)。</summary>
            public VirtualObject FacadeTemplate;
            /// <summary>子の型: ガラスの部分(上書きの表で 1 つの番号だけに使う)。</summary>
            public VirtualObject GlassTemplate;
            /// <summary>上書きした番号。</summary>
            public int OverriddenNumber;
        }

        /// <summary>
        /// 建物の種類を用意する。diagnostic なら、全部の表現を照明の影響を受けない赤(発光のみ)にした別のアセット(画素で建物を数える検証用)。
        /// 帯: なし(0 px)/ ビルボード(2 px、常駐)/ VG メッシュ(30 px)/ プレハブ(150 px)/ 子(400 px、建物のメッシュを 512 三角形ごとの部分に分け、部分ごとに外壁の型、1 つの番号だけガラスの型)。
        /// </summary>
        public static BuildingKind EnsureBuildingKind(bool diagnostic){
            var kind=new BuildingKind{Building=EnsureBuilding(),OverriddenNumber=3};
            string folder=Root+"/Kind";
            if(diagnostic){folder=Root+"/KindDiagnostic";}
            Directory.CreateDirectory(folder);
            Shader litShader=Shader.Find(LitShaderName);
            Material virtualMaterial=kind.Building.VirtualMaterial;
            Material litMaterial=kind.Building.LitMaterial;
            Material glassMaterial=EnsureColorMaterial(folder+"/Glass.mat",litShader,new Color(0.25f,0.42f,0.55f,1f),0.8f);
            if(diagnostic){
                virtualMaterial=EnsureFlatRed(folder+"/BuildingVirtualRed.mat",virtualMaterial.shader);
                litMaterial=EnsureFlatRed(folder+"/BuildingLitRed.mat",litShader);
                glassMaterial=EnsureFlatRed(folder+"/GlassRed.mat",litShader);
                kind.BillboardMaterial=EnsureFlatRed(folder+"/BillboardRed.mat",litShader);
            }else{
                Texture2D facade=EnsureFacadeTexture(folder+"/BuildingBillboard.png");
                kind.BillboardMaterial=EnsureColorMaterial(folder+"/Billboard.mat",litShader,Color.white,0.1f);
                if(kind.BillboardMaterial.GetTexture("_BaseMap")!=facade){
                    kind.BillboardMaterial.SetTexture("_BaseMap",facade);
                    EditorUtility.SetDirty(kind.BillboardMaterial);AssetDatabase.SaveAssetIfDirty(kind.BillboardMaterial);
                }
            }
            // VG メッシュのプレハブ(代替 Mesh の影と当たり判定は無し)。
            var virtualObject=new GameObject("Building VG");
            try{
                VirtualGeometryRenderer renderer=virtualObject.AddComponent<VirtualGeometryRenderer>();
                renderer.SetMeshAsset(kind.Building.VirtualAsset);
                renderer.SetMaterials(virtualMaterial);
                renderer.SetProxyOptions(false,false);
                renderer.SetLodSettings(1f,0.1f);
                kind.VirtualPrefab=SavePrefab(virtualObject,folder+"/BuildingVirtual.prefab");
            }finally{
                UnityEngine.Object.DestroyImmediate(virtualObject);
            }
            // 近景のプレハブ(元のメッシュの MeshRenderer)。
            var detailObject=new GameObject("Building Detail");
            try{
                detailObject.AddComponent<MeshFilter>().sharedMesh=kind.Building.SourceMesh;
                MeshRenderer detail=detailObject.AddComponent<MeshRenderer>();
                detail.sharedMaterial=litMaterial;detail.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
                kind.DetailPrefab=SavePrefab(detailObject,folder+"/BuildingDetail.prefab");
            }finally{
                UnityEngine.Object.DestroyImmediate(detailObject);
            }
            // 子の型(部分のメッシュ)。
            kind.FacadeTemplate=SaveDefinition(folder+"/FacadePart.prefab","facade-part",Vector3.zero,15f,new[]{
                new RepresentationBand(VirtualObjectRepresentations.None,0f,0f),
                new RepresentationBand("partmesh",1f,0.8f,new PartMeshRepresentationSettings{Material=litMaterial}),
            },null);
            kind.GlassTemplate=SaveDefinition(folder+"/GlassPart.prefab","glass-part",Vector3.zero,15f,new[]{
                new RepresentationBand(VirtualObjectRepresentations.None,0f,0f),
                new RepresentationBand("partmesh",1f,0.8f,new PartMeshRepresentationSettings{Material=glassMaterial}),
            },null);
            // 建物の種類。
            var billboard=new RepresentationBand("billboard",2f,1.5f,new BillboardRepresentationSettings{Material=kind.BillboardMaterial,Size=new Vector2(28f,Height),VerticalOffset=Height*0.5f});
            billboard.Resident=true;
            RepresentationBand[] bands={
                new RepresentationBand(VirtualObjectRepresentations.None,0f,0f),
                billboard,
                new RepresentationBand("vgmesh",30f,24f,new VirtualGeometryRepresentationSettings{Prefab=kind.VirtualPrefab}),
                new RepresentationBand("prefab",150f,120f,new PrefabRepresentationSettings{Prefab=kind.DetailPrefab,Async=false}),
                new RepresentationBand(VirtualObjectRepresentations.Children,400f,320f),
            };
            kind.Definition=SaveDefinition(folder+"/Building.prefab","building",new Vector3(0f,Height*0.5f,0f),Mathf.Sqrt(Width*Width*0.25f+Depth*Depth*0.25f+Height*Height*0.25f),bands,definition=>{
                definition.Footprint=new Vector2(Width,Depth);
                definition.Ground=VirtualObjectGround.FootprintMinimum;
                definition.FoundationDepth=1f;
                definition.Partition=new MeshClusterPartition{Mesh=kind.Building.SourceMesh,TrianglesPerPart=512};
                definition.ChildTemplates=new[]{kind.FacadeTemplate,kind.GlassTemplate};
                definition.Assignment=new IChildAssignmentRule[]{
                    new DefaultTemplateRule{TemplateIndex=0},
                    new OverrideTableRule{Rows=new[]{new OverrideTableRule.Row{Number=kind.OverriddenNumber,TemplateIndex=1}}},
                };
            });
            string problem=RepresentationBands.Validate(kind.Definition.Bands);
            if(problem!=null){throw new InvalidOperationException("建物の帯が正しくありません: "+problem);}
            return kind;
        }

        private static GameObject SavePrefab(GameObject source,string path){
            GameObject prefab=PrefabUtility.SaveAsPrefabAsset(source,path,out bool success);
            if(!success||prefab==null){throw new InvalidOperationException("Could not save prefab: "+path);}
            return prefab;
        }

        private static VirtualObject SaveDefinition(string path,string id,Vector3 center,float radius,RepresentationBand[] bands,Action<VirtualObject> configure){
            var source=new GameObject(Path.GetFileNameWithoutExtension(path));
            try{
                VirtualObject definition=source.AddComponent<VirtualObject>();
                definition.Id=id;definition.BoundsCenter=center;definition.BoundingRadius=radius;definition.Bands=bands;
                if(configure!=null){configure(definition);}
                GameObject prefab=SavePrefab(source,path);
                return prefab.GetComponent<VirtualObject>();
            }finally{
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static Material EnsureColorMaterial(string path,Shader shader,Color color,float smoothness){
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){
                material=new Material(shader);
                AssetDatabase.CreateAsset(material,path);
            }
            material.shader=shader;
            material.SetColor("_BaseColor",color);
            material.SetFloat("_Smoothness",smoothness);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        // 純粋な赤の基本色(つや・金属・鏡面・環境の反射なし)。照明で明るさは変わるが、緑と青はほぼ 0 のままなので、
        // 赤が緑と青より十分に大きい画素を建物と数える(<see cref="IsBuildingPixel"/>)。発光は Omni の shader の一括描画で効かないことがあるので使わない。
        private static Material EnsureFlatRed(string path,Shader shader){
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){
                material=new Material(shader);
                AssetDatabase.CreateAsset(material,path);
            }
            material.shader=shader;
            material.SetColor("_BaseColor",Color.red);
            material.SetFloat("_Smoothness",0f);
            material.SetFloat("_Metallic",0f);
            material.SetColor("_EmissionColor",Color.black);
            material.DisableKeyword("_EMISSION");
            material.SetFloat("_SpecularHighlights",0f);material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetFloat("_EnvironmentReflections",0f);material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        /// <summary>検証用の赤の材質の建物の画素か(赤が 24 より大きく、緑と青の 3 倍より大きい。地面の緑、背景のマゼンタ、空は外れる)。</summary>
        public static bool IsBuildingPixel(Color32 color){
            return color.r>24&&color.r>3*color.g&&color.r>3*color.b;
        }

        // ビルボードの模様(正面から見た外壁と窓)。64×192 px、窓は各面と同じ 8 列 × 18 階。内容が同じなら書き直さない。
        private static Texture2D EnsureFacadeTexture(string path){
            const int width=64;const int height=192;
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,false);
            try{
                Color wall=BuildingColor;Color glass=new Color(0.16f,0.18f,0.22f,1f);
                var pixels=new Color32[width*height];
                for(int y=0;y<height;y++){
                    for(int x=0;x<width;x++){
                        float u=(x+0.5f)/width*Bays;float v=(y+0.5f)/height*Floors;
                        float bu=u-Mathf.Floor(u);float bv=v-Mathf.Floor(v);
                        // 窓は列の幅の 0.4 m/3 m から、階の高さの 1.0 m/4 m 〜 3.5 m/4 m(メッシュの窓と同じ割合)。
                        bool window=bu>0.4f/3f&&bu<1f-0.4f/3f&&bv>0.25f&&bv<0.875f;
                        Color color=wall;
                        if(window){color=glass;}
                        pixels[y*width+x]=color;
                    }
                }
                texture.SetPixels32(pixels);texture.Apply();
                byte[] png=texture.EncodeToPNG();
                if(!File.Exists(path)||!File.ReadAllBytes(path).SequenceEqual(png)){
                    File.WriteAllBytes(path,png);
                    AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                }
            }finally{
                UnityEngine.Object.DestroyImmediate(texture);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material EnsureMaterial(string path,Shader shader){
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created=false;
            if(material==null){
                material=new Material(shader);
                AssetDatabase.CreateAsset(material,path);
                created=true;
            }
            bool dirty=created;
            if(material.shader!=shader){material.shader=shader;dirty=true;}
            if(material.GetColor("_BaseColor")!=BuildingColor){material.SetColor("_BaseColor",BuildingColor);dirty=true;}
            if(!Mathf.Approximately(material.GetFloat("_Smoothness"),0.2f)){material.SetFloat("_Smoothness",0.2f);dirty=true;}
            if(dirty){
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
            }
            return material;
        }

        /// <summary>建物のメッシュ(Unity の座標。原点は底面の中心、上は +Y)。面ごとに頂点を分けた平らな陰影。</summary>
        public static Mesh BuildMesh(){
            var positions=new List<Vector3>();
            var normals=new List<Vector3>();
            var uvs=new List<Vector2>();
            var indices=new List<int>();
            float halfWidth=Width*0.5f;float halfDepth=Depth*0.5f;
            // 4 つの面(外向き法線、面の右方向、面の中心)。
            var faces=new[]{
                (normal:Vector3.back,right:Vector3.right,center:new Vector3(0f,0f,-halfDepth),width:Width),
                (normal:Vector3.right,right:Vector3.forward,center:new Vector3(halfWidth,0f,0f),width:Depth),
                (normal:Vector3.forward,right:Vector3.left,center:new Vector3(0f,0f,halfDepth),width:Width),
                (normal:Vector3.left,right:Vector3.back,center:new Vector3(-halfWidth,0f,0f),width:Depth),
            };
            float floorHeight=Height/Floors;
            foreach(var face in faces){
                float bayWidth=face.width/Bays;
                for(int floor=0;floor<Floors;floor++){
                    for(int bay=0;bay<Bays;bay++){
                        float u0=-face.width*0.5f+bay*bayWidth;float u1=u0+bayWidth;
                        float v0=floor*floorHeight;float v1=v0+floorHeight;
                        float wu0=u0+0.4f;float wu1=u1-0.4f;float wv0=v0+1.0f;float wv1=v1-0.5f;
                        Vector3 Point(float u,float v,float d){
                            return face.center+face.right*u+Vector3.up*v-face.normal*d;
                        }
                        // 枠(4 つの帯)
                        AddQuad(positions,normals,uvs,indices,Point(u0,v0,0f),Point(u1,v0,0f),Point(u1,wv0,0f),Point(u0,wv0,0f),face.normal);
                        AddQuad(positions,normals,uvs,indices,Point(u0,wv1,0f),Point(u1,wv1,0f),Point(u1,v1,0f),Point(u0,v1,0f),face.normal);
                        AddQuad(positions,normals,uvs,indices,Point(u0,wv0,0f),Point(wu0,wv0,0f),Point(wu0,wv1,0f),Point(u0,wv1,0f),face.normal);
                        AddQuad(positions,normals,uvs,indices,Point(wu1,wv0,0f),Point(u1,wv0,0f),Point(u1,wv1,0f),Point(wu1,wv1,0f),face.normal);
                        // 凹みの側面(下・上・左・右。法線は凹みの内側を向く)
                        AddQuad(positions,normals,uvs,indices,Point(wu0,wv0,Recess),Point(wu1,wv0,Recess),Point(wu1,wv0,0f),Point(wu0,wv0,0f),Vector3.up);
                        AddQuad(positions,normals,uvs,indices,Point(wu0,wv1,0f),Point(wu1,wv1,0f),Point(wu1,wv1,Recess),Point(wu0,wv1,Recess),Vector3.down);
                        AddQuad(positions,normals,uvs,indices,Point(wu0,wv0,0f),Point(wu0,wv1,0f),Point(wu0,wv1,Recess),Point(wu0,wv0,Recess),face.right);
                        AddQuad(positions,normals,uvs,indices,Point(wu1,wv0,Recess),Point(wu1,wv1,Recess),Point(wu1,wv1,0f),Point(wu1,wv0,0f),-face.right);
                        // 窓の奥
                        AddQuad(positions,normals,uvs,indices,Point(wu0,wv0,Recess),Point(wu1,wv0,Recess),Point(wu1,wv1,Recess),Point(wu0,wv1,Recess),face.normal);
                    }
                }
            }
            // 屋上
            AddQuad(positions,normals,uvs,indices,new Vector3(-halfWidth,Height,-halfDepth),new Vector3(-halfWidth,Height,halfDepth),new Vector3(halfWidth,Height,halfDepth),new Vector3(halfWidth,Height,-halfDepth),Vector3.up);
            var mesh=new Mesh{name="Building",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            mesh.SetVertices(positions);mesh.SetNormals(normals);mesh.SetUVs(0,uvs);mesh.SetTriangles(indices,0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // 四角形 a→b→c→d(順に周を回る 4 点)を、法線 normal の側が表になる 2 つの三角形にする。
        // Unity では三角形 (a,b,c) の表は Cross(b−a,c−a) の向きの側なので、それが normal と同じ向きなら a,b,c の順、逆なら a,c,b の順にする。
        private static void AddQuad(List<Vector3> positions,List<Vector3> normals,List<Vector2> uvs,List<int> indices,Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 normal){
            int start=positions.Count;
            positions.Add(a);positions.Add(b);positions.Add(c);positions.Add(d);
            for(int i=0;i<4;i++){normals.Add(normal);}
            uvs.Add(Uv(a,normal));uvs.Add(Uv(b,normal));uvs.Add(Uv(c,normal));uvs.Add(Uv(d,normal));
            if(Vector3.Dot(Vector3.Cross(b-a,c-a),normal)>0f){
                indices.Add(start);indices.Add(start+1);indices.Add(start+2);
                indices.Add(start);indices.Add(start+2);indices.Add(start+3);
            }else{
                indices.Add(start);indices.Add(start+2);indices.Add(start+1);
                indices.Add(start);indices.Add(start+3);indices.Add(start+2);
            }
        }

        private static Vector2 Uv(Vector3 point,Vector3 normal){
            if(Mathf.Abs(normal.y)>0.5f){return new Vector2(point.x/Width,point.z/Depth);}
            if(Mathf.Abs(normal.x)>0.5f){return new Vector2(point.z/Depth,point.y/Height);}
            return new Vector2(point.x/Width,point.y/Height);
        }

        // Unity の OBJ の取り込みは右手系として読み X を反転するので、X を反転し巻き順も逆にして書く(core の検証の書き出しと同じ規則)。
        // 内容が同じなら書き直さない(取り込み直しを起こさない)。書いたら true。
        private static bool WriteObj(Mesh mesh,string path){
            CultureInfo culture=CultureInfo.InvariantCulture;
            string library=Path.ChangeExtension(path,".mtl");
            string materials="newmtl Material0\nKd 0.8 0.8 0.8\n";
            bool changed=false;
            if(!File.Exists(library)||File.ReadAllText(library)!=materials){
                File.WriteAllText(library,materials,new UTF8Encoding(false));
                changed=true;
            }
            var text=new StringBuilder(1<<20);
            text.Append("# Building\n");
            text.Append("mtllib ").Append(Path.GetFileName(library)).Append('\n');
            Vector3[] positions=mesh.vertices;Vector3[] normals=mesh.normals;Vector2[] uvs=mesh.uv;int[] indices=mesh.triangles;
            foreach(Vector3 p in positions){text.Append("v ").Append((-p.x).ToString("R",culture)).Append(' ').Append(p.y.ToString("R",culture)).Append(' ').Append(p.z.ToString("R",culture)).Append('\n');}
            foreach(Vector2 t in uvs){text.Append("vt ").Append(t.x.ToString("R",culture)).Append(' ').Append(t.y.ToString("R",culture)).Append('\n');}
            foreach(Vector3 n in normals){text.Append("vn ").Append((-n.x).ToString("R",culture)).Append(' ').Append(n.y.ToString("R",culture)).Append(' ').Append(n.z.ToString("R",culture)).Append('\n');}
            text.Append("usemtl Material0\n");
            for(int i=0;i<indices.Length;i+=3){
                text.Append("f ").Append(Corner(indices[i])).Append(' ').Append(Corner(indices[i+2])).Append(' ').Append(Corner(indices[i+1])).Append('\n');
            }
            string content=text.ToString();
            if(!File.Exists(path)||File.ReadAllText(path)!=content){
                File.WriteAllText(path,content,new UTF8Encoding(false));
                changed=true;
            }
            return changed;
        }

        private static string Corner(int index){
            string value=(index+1).ToString(CultureInfo.InvariantCulture);
            return value+"/"+value+"/"+value;
        }
    }
}
