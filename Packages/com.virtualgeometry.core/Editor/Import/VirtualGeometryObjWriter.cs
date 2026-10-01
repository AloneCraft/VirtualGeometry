using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 検証用の元MeshをOBJモデルとして書き出し、利用者と同じModelImporter経由でVirtual Geometryへ変換できるようにする。
    /// UnityのOBJインポーターは右手系として読みX軸を反転するため、Xを反転し巻き順も逆にして書き、インポート結果を元Meshと一致させる。
    /// .mtlでMaterialを定義しないとsubmeshが1つにまとめられるため、submeshごとのMaterialを同名の.mtlへ書く。
    /// </summary>
    internal static class VirtualGeometryObjWriter{
        public static void Write(Mesh mesh,string path){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            // インポート済みモデルのMesh(読み書き無効)も書き出せるよう、変換器と同じ読み出しを使う。
            VirtualGeometryMeshInput input=VirtualGeometryMeshInput.FromMesh(mesh);
            Vector3[] positions=input.Positions;Vector3[] normals=input.Normals;Vector2[] uv=input.UV;
            bool hasNormals=normals.Length==positions.Length;bool hasUv=uv.Length==positions.Length;
            CultureInfo culture=CultureInfo.InvariantCulture;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string library=Path.ChangeExtension(path,".mtl");
            var materials=new StringBuilder();
            for(int submesh=0;submesh<input.SubmeshIndices.Length;submesh++){materials.Append("newmtl Material").Append(submesh).Append("\nKd 0.8 0.8 0.8\n");}
            // OBJのインポートは.mtlに依存する。内容が同じ.mtlを書き直すと、AssetDatabaseが取り込み直す前のOBJインポートが版の不一致で失敗するため、変わったときだけ書く。
            if(!File.Exists(library)||File.ReadAllText(library)!=materials.ToString()){File.WriteAllText(library,materials.ToString(),new UTF8Encoding(false));}
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(false),1<<20)){
                writer.NewLine="\n";
                writer.WriteLine("# "+mesh.name);
                writer.WriteLine("mtllib "+Path.GetFileName(library));
                foreach(Vector3 p in positions){writer.WriteLine("v "+(-p.x).ToString("R",culture)+" "+p.y.ToString("R",culture)+" "+p.z.ToString("R",culture));}
                if(hasUv){
                    foreach(Vector2 t in uv){writer.WriteLine("vt "+t.x.ToString("R",culture)+" "+t.y.ToString("R",culture));}
                }
                if(hasNormals){
                    foreach(Vector3 n in normals){writer.WriteLine("vn "+(-n.x).ToString("R",culture)+" "+n.y.ToString("R",culture)+" "+n.z.ToString("R",culture));}
                }
                for(int submesh=0;submesh<input.SubmeshIndices.Length;submesh++){
                    // submeshごとにMaterialを分け、インポート後も同じsubmesh構成にする。
                    writer.WriteLine("usemtl Material"+submesh);
                    int[] indices=input.SubmeshIndices[submesh];
                    for(int i=0;i<indices.Length;i+=3){
                        writer.Write("f ");writer.Write(Corner(indices[i],hasUv,hasNormals));
                        writer.Write(' ');writer.Write(Corner(indices[i+2],hasUv,hasNormals));
                        writer.Write(' ');writer.WriteLine(Corner(indices[i+1],hasUv,hasNormals));
                    }
                }
            }
        }

        private static string Corner(int index,bool hasUv,bool hasNormals){
            string value=(index+1).ToString(CultureInfo.InvariantCulture);
            if(hasUv&&hasNormals){return value+"/"+value+"/"+value;}
            if(hasNormals){return value+"//"+value;}
            if(hasUv){return value+"/"+value;}
            return value;
        }
    }
}
