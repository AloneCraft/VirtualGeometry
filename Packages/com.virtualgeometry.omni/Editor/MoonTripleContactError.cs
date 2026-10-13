using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 普通の月(単純な球のメッシュ)と VG の月 A の接触の誤差を、描画せず、三角形の形から数値で求める。
    /// 普通の月の表面の高さは、実際の三角形を倍精度で補間して求める。理想の球(半径 R)との差が、メッシュの誤差(弦の落ち込み)。
    /// 接触の隙間 g(L) = (A の下面の高さ) − (普通の月の表面の高さ)、L は接触点からの横の距離(+z)。
    /// ボール(直径 2r = 0.22 m)が両側に接する横の距離は g(L) = 2r の L。理想の球と三角形で、その位置の差(ΔL)と、埋まり量を出す。
    /// 1 次元の近似(ボールの中心の横の位置で隙間の幅を測る)。MoonTripleLayout.TouchDistance と同じ考え方。
    /// 実行: -batchmode -force-d3d12 -projectPath &lt;project&gt; -executeMethod VirtualGeometry.Omni.Editor.MoonTripleContactError.Run [-plainLongitude N -plainLatitude M]
    /// 結果: TestResults/Planet/MoonTripleContactError/Report.txt
    /// </summary>
    public static class MoonTripleContactError{
        private const string Results="TestResults/Planet/MoonTripleContactError";
        // 三角形の探索の範囲(接触点からの横の距離。調べるのは 3 km 以内なので、三角形の 1 辺(21 km)を含めて 30 km)。
        private const double SearchMeters=30000;
        private const double SearchDepthMeters=300;

        private struct Triangle{
            public double3 A;
            public double3 B;
            public double3 C;
        }

        public static void Run(){
            var report=new List<string>();
            try{
                int longitude=0;int latitude=0;
                string[] args=Environment.GetCommandLineArgs();
                for(int i=0;i+1<args.Length;i++){
                    if(args[i]=="-plainLongitude"){longitude=int.Parse(args[i+1],CultureInfo.InvariantCulture);}
                    if(args[i]=="-plainLatitude"){latitude=int.Parse(args[i+1],CultureInfo.InvariantCulture);}
                }
                Mesh mesh;
                string source;
                if(longitude>0&&latitude>0){
                    mesh=MoonTripleSceneBuilder.BuildUvSphere(longitude,latitude);
                    source="UV 球 "+longitude+"x"+latitude;
                }else{
                    mesh=AssetDatabase.LoadAssetAtPath<Mesh>(MoonTripleSceneBuilder.MeshPath);
                    if(mesh==null){throw new InvalidOperationException("単位球のメッシュがありません: "+MoonTripleSceneBuilder.MeshPath);}
                    source="資産 CosmicSphere (512x256)";
                }
                report.Add("plain sphere mesh: "+source);
                Compute(mesh,report);
            }catch(Exception error){
                report.Add("FAILED");
                report.Add(error.ToString());
                Write(report);
                EditorApplication.Exit(1);
                return;
            }
            report.Add("MEASURED(数値のみ。描画は使わず、合否の判定もしない)");
            Write(report);
            EditorApplication.Exit(0);
        }

        private static void Compute(Mesh mesh,List<string> report){
            MoonTripleLayout layout=MoonTripleLayout.Default;
            double radius=layout.Radius;
            double scale=2*radius;
            double3 plainCenter=new double3(0,layout.CenterPlain,0);
            double ballDiameter=2*MoonTripleLayout.BallRadius;

            Vector3[] vertices=mesh.vertices;
            int[] indices=mesh.triangles;
            var triangles=new List<Triangle>();
            double sag=0;
            for(int i=0;i+2<indices.Length;i+=3){
                double3 a=WorldPoint(vertices[indices[i]],plainCenter,scale);
                double3 b=WorldPoint(vertices[indices[i+1]],plainCenter,scale);
                double3 c=WorldPoint(vertices[indices[i+2]],plainCenter,scale);
                // 接触点の近く(上側の面)だけを使う。横 3 km の面は、頂点が 21 km 離れていても −170 m 程度なので、高さは緩く絞る。
                if(a.y<-SearchDepthMeters||b.y<-SearchDepthMeters||c.y<-SearchDepthMeters){continue;}
                if(math.abs(a.x)>SearchMeters||math.abs(a.z)>SearchMeters||math.abs(b.z)>SearchMeters||math.abs(c.z)>SearchMeters){continue;}
                triangles.Add(new Triangle{A=a,B=b,C=c});
                double3 centroid=(a+b+c)/3;
                sag=Math.Max(sag,radius-math.length(centroid-plainCenter));
            }
            report.Add("mesh triangles total="+(indices.Length/3)+", near the contact (top side, within "+SearchMeters.ToString("F0",CultureInfo.InvariantCulture)+" m)="+triangles.Count);
            report.Add("max sag of facet centers inside the ideal sphere="+sag.ToString("F3",CultureInfo.InvariantCulture)+" m");
            report.Add("R="+radius.ToString("G8",CultureInfo.InvariantCulture)+" m, ball diameter="+ballDiameter.ToString("F2",CultureInfo.InvariantCulture)+" m");

            // 理想の球: 普通の月の表面の高さと、VG の月 A の下面の高さ(横の距離 L の位置、x = 0)。
            Func<double,double> idealPlain=l=>-radius+Math.Sqrt(radius*radius-l*l);
            Func<double,double> idealA=l=>radius-Math.Sqrt(radius*radius-l*l);
            // 三角形の表面の高さ。見つからなければ NaN。
            Func<double,double> meshPlain=l=>MeshHeight(triangles,0,l);

            double touchIdeal=layout.TouchDistance(0);
            double fitIdeal=Bisect(l=>idealA(l)-idealPlain(l)-ballDiameter,1,3000);
            double fitMesh=Bisect(l=>idealA(l)-meshPlain(l)-ballDiameter,1,3000);
            report.Add("ideal fit distance (gap = ball diameter)="+fitIdeal.ToString("F3",CultureInfo.InvariantCulture)+" m (layout TouchDistance="+touchIdeal.ToString("F3",CultureInfo.InvariantCulture)+" m)");
            report.Add("mesh fit distance (gap = ball diameter)="+fitMesh.ToString("F3",CultureInfo.InvariantCulture)+" m");
            report.Add("lateral shift of the fit point (mesh - ideal)="+(fitMesh-fitIdeal).ToString("F3",CultureInfo.InvariantCulture)+" m");

            double meshY=meshPlain(fitIdeal);
            double idealY=idealPlain(fitIdeal);
            report.Add("at the ideal fit distance: plain surface height mesh - ideal="+((meshY-idealY)*1000).ToString("F1",CultureInfo.InvariantCulture)+" mm");
            double gapMesh=idealA(fitIdeal)-meshY;
            double overlap=ballDiameter-gapMesh;
            report.Add("at the ideal fit distance: gap with mesh="+(gapMesh*1000).ToString("F1",CultureInfo.InvariantCulture)+" mm (ball "+(ballDiameter*1000).ToString("F0",CultureInfo.InvariantCulture)+" mm)");
            report.Add("overlap of the ball in the gap (positive = ball would sink)="+(overlap*1000).ToString("F1",CultureInfo.InvariantCulture)+" mm");
        }

        // 位置は、Logical の配置と同じ式(中心 + 単位球の頂点 × 直径 2R)で倍精度に直す。
        private static double3 WorldPoint(Vector3 vertex,double3 center,double scale){
            return center+new double3(vertex.x,vertex.y,vertex.z)*scale;
        }

        // 横の位置 (x, z) = (0, l) の表面の高さ。三角形の xz の投影を調べ、含む三角形の平面で補間する。
        private static double MeshHeight(List<Triangle> triangles,double x,double z){
            foreach(Triangle triangle in triangles){
                double3 a=triangle.A;double3 b=triangle.B;double3 c=triangle.C;
                double denominator=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
                if(math.abs(denominator)<1e-12){continue;}
                double wa=((b.z-c.z)*(x-c.x)+(c.x-b.x)*(z-c.z))/denominator;
                double wb=((c.z-a.z)*(x-c.x)+(a.x-c.x)*(z-c.z))/denominator;
                double wc=1-wa-wb;
                const double tolerance=1e-9;
                if(wa<-tolerance||wb<-tolerance||wc<-tolerance){continue;}
                return wa*a.y+wb*b.y+wc*c.y;
            }
            return double.NaN;
        }

        // f(lo) < 0 < f(hi) を仮定して、f = 0 の横の距離を二分法で求める(f は横の距離に対して単調増加)。
        private static double Bisect(Func<double,double> f,double lo,double hi){
            for(int i=0;i<200;i++){
                double mid=(lo+hi)*0.5;
                double value=f(mid);
                if(double.IsNaN(value)){throw new InvalidOperationException("表面の高さが見つかりません(距離 "+mid.ToString("F3",CultureInfo.InvariantCulture)+" m)。");}
                if(value<0){lo=mid;}else{hi=mid;}
            }
            return (lo+hi)*0.5;
        }

        private static void Write(List<string> report){
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            foreach(string line in report){Debug.Log("[MoonTripleContactError] "+line);}
        }
    }
}
