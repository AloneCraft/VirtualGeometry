using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 海の fragment(RadialOcean.shader)が使う式 OceanLabel(不動点反復で札を求める)と OceanShortWaves(帯域制限で落とした波の傾きと分散)を、
    /// 倍精度の C# の参照と数値で比べる(半径 600km、風速 10m/s、24 成分)。GPU の評価は PlanetOceanProbe.compute(PlanetOcean.hlsl をそのまま使う)。
    /// 点は、Cell の格子の中の乱数の位置で、頂点の生成と同じ式の変位(帯域制限で残る波)を倍精度で足した、変位した海面の上の点。
    /// 比べるもの:
    ///  (a) 実装の一致: GPU の札と傾きが、同じ式(同じ反復回数)を倍精度で評価した値と、float の丸めの範囲で一致する
    ///  (b) 近似の精度(分けて測る):
    ///      - 不動点反復(3 回)の札の誤差(m)と、それが傾きに与える誤差
    ///      - 傾きの式(Gerstner の水平成分を無視する一次近似)の誤差: 正確な札で、帯域制限で落とした波の変位した面の法線(中心差分、尖りも含む)から求めた傾きと比べる
    ///      - 合計: GPU の傾きと、正確な傾きの差(傾きの差 ≈ 法線の角度の差 rad)
    /// 点は、変位した海面の上にあるので、頂点の間の補間(chord)の誤差は含まない。波は水深に依らないので、外洋と海岸の区別はしない。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.OceanProbeValidation.Run
    /// </summary>
    public static class OceanProbeValidation{
        private const string Results="TestResults/Planet/OceanProbe";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const string OceanShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialOcean.vgshader";
        private const string ProbePath="Packages/com.virtualgeometry.world/Editor/PlanetOceanProbe.compute";
        private const float Radius=600000;
        private const float Height=3000;
        // 比べる反復回数。既定は RadialOcean.shader の OCEAN_LABEL_ITERATIONS(shader の文字列から読む)。
        // -oceanProbeIterations N で変えられる(負の対照: 1 回にすると近似の精度の検査が不合格になる)。
        private static int shaderIterations=3;
        private const int PointsPerCell=40;
        private const int ConvergedIterations=200;

        // (a) 実装の一致の許容値。float の丸め(Cell の中の位置 数 km を最短波長 0.5m で割った位相の float の刻み 約 1.5e-3 rad、傾き ≤ 0.3 を掛ける)の範囲。
        private const double ImplementationLabelTolerance=5e-4;
        private const double ImplementationGradientTolerance=1e-3;
        // (b) 近似の精度の許容値(傾きの差。法線の角度の差とほぼ同じ rad)。測定値(最大 0.023、平均 0.0042)の 2 倍強。反復 1 回(最大 0.08)は不合格になる。
        private const double TotalGradientMeanTolerance=0.008;
        private const double TotalGradientMaximumTolerance=0.05;
        // 札の誤差による、解像される最短の波の位相の誤差の許容値(rad)。測定値は 0.064。反復 1 回(約 3 rad)は不合格になる。
        private const double LabelPhaseTolerance=0.15;

        private static readonly float[] Footprints={0.01f,0.1f,1f,10f};
        private static readonly int[] Levels={8,10,12,14,16};
        private static readonly double[] Times={0.0,12.3,1000.0};
        private static readonly Vector3[] Directions={
            new Vector3(0.3f,0.5f,0.8f),new Vector3(-0.7f,0.2f,0.4f),new Vector3(0.1f,-0.9f,0.3f),
            new Vector3(-0.4f,-0.3f,-0.85f),new Vector3(0.9f,0.1f,-0.2f),new Vector3(-0.2f,0.8f,-0.5f),
        };

        private static PlanetTerrainSource terrain;
        private static PlanetOceanSource ocean;
        private static ComputeShader probe;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        // 1 つの (Cell、時刻) の波の値(倍精度)。
        private sealed class WaveSet{
            public int Count;
            public double3[] Direction;
            public double[] Wavelength;
            public double[] Amplitude;
            public double[] Sharpness;
            public double[] Fraction;
            public double[] TimePhase;
            public double3 Origin;
            public double Radius;
            public double Quad;
        }

        // (レベル、フットプリント)ごとの統計。
        private sealed class Statistics{
            public int Count;
            public double ImplementationLabel;
            public double ImplementationGradient;
            public double ImplementationVariance;
            public double LabelIteration;
            public double GradientIteration;
            public double GradientFirstOrder;
            public double TotalMaximum;
            public double TotalSum;
            public double TotalSquare;
            public double SignalSquare;
            public double ResolvedLabelPhase;
        }

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                shaderIterations=ReadShaderIterations();
                Setup();
                steps=Steps();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        // RadialOcean.shader の "#define OCEAN_LABEL_ITERATIONS N" の N(-oceanProbeIterations N があればそれ)。
        private static int ReadShaderIterations(){
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]=="-oceanProbeIterations"){
                    return int.Parse(arguments[i+1],CultureInfo.InvariantCulture);
                }
            }
            string text=File.ReadAllText("Packages/com.virtualgeometry.omni/Shaders/RadialOcean.shader");
            System.Text.RegularExpressions.Match match=System.Text.RegularExpressions.Regex.Match(text,@"#define\s+OCEAN_LABEL_ITERATIONS\s+(\d+)");
            if(!match.Success){throw new InvalidOperationException("RadialOcean.shader に OCEAN_LABEL_ITERATIONS の定義がありません。");}
            return int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
        }

        private static void Record(string condition,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                if(condition.Contains("[OmniConversion] ValidatedOnly")||condition.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition);
            }
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1200){throw new TimeoutException("OceanProbe validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            probe=AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbePath);
            if(probe==null){throw new InvalidOperationException("Compute shader not found: "+ProbePath);}
            Shader oceanShader=AssetDatabase.LoadAssetAtPath<Shader>(OceanShaderPath);
            if(oceanShader==null){throw new InvalidOperationException("Shader not found: "+OceanShaderPath);}
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            GameObject holder=new GameObject("OceanProbe Planet");holder.SetActive(false);
            terrain=holder.AddComponent<PlanetTerrainSource>();
            ocean=holder.AddComponent<PlanetOceanSource>();
            // 描かないので、pool は小さくてよい。
            var terrainSettings=new SerializedObject(terrain);
            terrainSettings.FindProperty("poolCapacity").intValue=64;
            terrainSettings.FindProperty("maximumDrawSlots").intValue=64;
            terrainSettings.FindProperty("generationBudget").intValue=4;
            terrainSettings.FindProperty("baseWavelength").floatValue=240000;
            terrainSettings.FindProperty("octaves").intValue=26;
            terrainSettings.ApplyModifiedPropertiesWithoutUndo();
            Shader landShader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(landShader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}
            terrain.Material=new Material(landShader);
            terrain.Configure(Radius,Height,PlanetLayerSource.GpuMaximumLevel,2f);
            var oceanSettings=new SerializedObject(ocean);
            oceanSettings.FindProperty("poolCapacity").intValue=64;
            oceanSettings.FindProperty("maximumDrawSlots").intValue=64;
            oceanSettings.FindProperty("generationBudget").intValue=4;
            oceanSettings.ApplyModifiedPropertiesWithoutUndo();
            ocean.Terrain=terrain;ocean.Material=new Material(oceanShader);ocean.AdvanceTime=false;
            ocean.Configure(10f,24,0.5f,PlanetLayerSource.GpuMaximumLevel,2f);
            holder.SetActive(true);
        }

        private static IEnumerator Steps(){
            // Selector が使えるまで待つ。
            int waited=0;
            while(ocean.Selector==null){
                if(waited++>100){throw new InvalidOperationException("海の Selector が有効になりません。");}
                yield return null;
            }
            double contraction=0;
            for(int i=0;i<ocean.WaveCount;i++){
                PlanetOceanSource.WaveInfo wave=ocean.GetWave(i);
                contraction+=wave.Amplitude*2.0*Math.PI/wave.Wavelength*(1.0+wave.Sharpness);
            }
            report.Add("ocean: waves="+ocean.WaveCount+" amplitude="+ocean.WaveAmplitude.ToString("G4",CultureInfo.InvariantCulture)+"m longest="+ocean.LongestWavelength.ToString("G4",CultureInfo.InvariantCulture)+
                "m contraction sum(A·k·(1+Q))="+contraction.ToString("G4",CultureInfo.InvariantCulture)+" shaderIterations="+shaderIterations);
            int kernel=probe.FindKernel("Probe");
            var statistics=new Dictionary<string,Statistics>();
            // 反復回数ごとの札の誤差(倍精度の反復どうし。GPU を使わない)の最大。
            var iterationLabelError=new double[7];
            var iterationGradientError=new double[7];
            double worstConvergence=0;
            int unconverged=0;
            int totalProbes=0;
            foreach(int level in Levels){
                float quad=ocean.Selector.QuadArc(level);
                foreach(double time in Times){
                    ocean.WaveTime=time;
                    for(int c=0;c<Directions.Length;c++){
                        PlanetCellKey key=PlanetCellGeometry.KeyAt(Directions[c].normalized,level);
                        PlanetCellFrame frame=ocean.Selector.Frame(in key);
                        WaveSet waves=BuildWaveSet(in frame,quad);
                        var random=new System.Random(1000*level+10*c+(int)(time*10)%97);
                        // 点: 変位した海面の上(頂点の生成と同じ式の、帯域制限で残る波の変位を倍精度で足したもの)。
                        var positions=new List<Vector4>();
                        var origins=new List<Vector4>();
                        var parameters=new List<Vector4>();
                        foreach(float footprint in Footprints){
                            var pointRandom=new System.Random(random.Next());
                            for(int p=0;p<PointsPerCell;p++){
                                float a=(float)(pointRandom.NextDouble()*PlanetLayerSource.CellQuads);
                                float b=(float)(pointRandom.NextDouble()*PlanetLayerSource.CellQuads);
                                Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,PlanetLayerSource.CellQuads,a,b,Radius,out Vector3 unused);
                                double3 s=new double3(sphere.x,sphere.y,sphere.z);
                                double3 direction=math.normalize(waves.Origin*waves.Radius+s);
                                double3 displaced=s+Offset(waves,s,direction,false,0);
                                positions.Add(new Vector4((float)displaced.x,(float)displaced.y,(float)displaced.z,quad));
                                origins.Add(new Vector4((float)waves.Origin.x,(float)waves.Origin.y,(float)waves.Origin.z,Radius));
                                parameters.Add(new Vector4(footprint,shaderIterations,0,0));
                            }
                        }
                        int count=positions.Count;
                        Vector4[] labels;Vector4[] gradients;
                        if(!Evaluate(kernel,in key,positions,origins,parameters,out labels,out gradients)){throw new InvalidOperationException("海の波を compute へ書けません。");}
                        for(int n=0;n<count;n++){
                            float footprint=parameters[n].x;
                            double3 position=new double3(positions[n].x,positions[n].y,positions[n].z);
                            double3 labelN=LabelIterate(waves,position,shaderIterations);
                            double3 labelExact=LabelIterate(waves,position,ConvergedIterations);
                            double3 labelNext=LabelIterate(waves,position,ConvergedIterations+1);
                            double convergence=math.length(labelNext-labelExact);
                            worstConvergence=Math.Max(worstConvergence,convergence);
                            if(convergence>1e-7){unconverged++;}
                            double3 onSphere=ProjectOnSphere(waves,labelExact);
                            double3 gradientN=Formula(waves,labelN,footprint,out double varianceN);
                            double3 gradientAtExact=Formula(waves,onSphere,footprint,out double unusedVariance);
                            double3 gradientTrue=ExactGradient(waves,onSphere,footprint);
                            double3 gpuLabel=new double3(labels[n].x,labels[n].y,labels[n].z);
                            double3 gpuGradient=new double3(gradients[n].x,gradients[n].y,gradients[n].z);
                            double gpuVariance=labels[n].w;
                            string group="level"+level+" footprint"+footprint.ToString("G3",CultureInfo.InvariantCulture)+"m";
                            if(!statistics.TryGetValue(group,out Statistics stat)){stat=new Statistics();statistics.Add(group,stat);}
                            stat.Count++;
                            stat.ImplementationLabel=Math.Max(stat.ImplementationLabel,math.length(gpuLabel-labelN));
                            stat.ImplementationGradient=Math.Max(stat.ImplementationGradient,math.length(gpuGradient-gradientN));
                            if(varianceN>1e-10){stat.ImplementationVariance=Math.Max(stat.ImplementationVariance,Math.Abs(gpuVariance-varianceN)/varianceN);}
                            double labelError=math.length(labelN-labelExact);
                            stat.LabelIteration=Math.Max(stat.LabelIteration,labelError);
                            stat.GradientIteration=Math.Max(stat.GradientIteration,math.length(gradientN-gradientAtExact));
                            stat.GradientFirstOrder=Math.Max(stat.GradientFirstOrder,math.length(gradientAtExact-gradientTrue));
                            double total=math.length(gpuGradient-gradientTrue);
                            stat.TotalMaximum=Math.Max(stat.TotalMaximum,total);
                            stat.TotalSum+=total;stat.TotalSquare+=total*total;
                            stat.SignalSquare+=math.lengthsq(gradientTrue);
                            // 解像される最短の波(波長が画素の 2 倍以上、帯域制限で落ちる波)の位相の誤差(rad)。
                            double shortest=ResolvedShortestWavelength(waves,footprint);
                            if(shortest>0){stat.ResolvedLabelPhase=Math.Max(stat.ResolvedLabelPhase,2.0*Math.PI*labelError/shortest);}
                            // 反復回数ごとの誤差(GPU を使わない)。
                            if(footprint==Footprints[0]||footprint==Footprints[2]){
                                for(int iterations=1;iterations<=6;iterations++){
                                    double3 label=LabelIterate(waves,position,iterations);
                                    iterationLabelError[iterations]=Math.Max(iterationLabelError[iterations],math.length(label-labelExact));
                                    double3 gradient=Formula(waves,label,footprint,out double unusedGradientVariance);
                                    iterationGradientError[iterations]=Math.Max(iterationGradientError[iterations],math.length(gradient-gradientTrue));
                                }
                            }
                            totalProbes++;
                        }
                    }
                    yield return null;
                }
            }
            report.Add("probes="+totalProbes+" fixedPointConverged: worst |L"+(ConvergedIterations+1)+"−L"+ConvergedIterations+"|="+worstConvergence.ToString("G3",CultureInfo.InvariantCulture)+"m unconverged="+unconverged);
            if(unconverged!=0){failures.Add("不動点反復が収束しない点が "+unconverged+" あります(反復の縮小率が 1 に近い)。");}
            // 集計
            double implementationLabel=0;double implementationGradient=0;double implementationVariance=0;
            double labelIteration=0;double gradientIteration=0;double gradientFirstOrder=0;double resolvedPhase=0;
            double totalMaximum=0;double totalSum=0;double totalSquare=0;double signalSquare=0;long totalCount=0;
            foreach(KeyValuePair<string,Statistics> pair in statistics){
                Statistics s=pair.Value;
                string rms=(Math.Sqrt(s.TotalSquare/s.Count)).ToString("G3",CultureInfo.InvariantCulture);
                string signal=(Math.Sqrt(s.SignalSquare/s.Count)).ToString("G3",CultureInfo.InvariantCulture);
                report.Add(pair.Key+": n="+s.Count+" impl label="+s.ImplementationLabel.ToString("G3",CultureInfo.InvariantCulture)+"m gradient="+s.ImplementationGradient.ToString("G3",CultureInfo.InvariantCulture)+
                    " variance(rel)="+s.ImplementationVariance.ToString("G3",CultureInfo.InvariantCulture)+" | iteration"+shaderIterations+" label="+s.LabelIteration.ToString("G3",CultureInfo.InvariantCulture)+"m gradient="+s.GradientIteration.ToString("G3",CultureInfo.InvariantCulture)+
                    " | firstOrder gradient="+s.GradientFirstOrder.ToString("G3",CultureInfo.InvariantCulture)+" | total max="+s.TotalMaximum.ToString("G3",CultureInfo.InvariantCulture)+" mean="+(s.TotalSum/s.Count).ToString("G3",CultureInfo.InvariantCulture)+" rms="+rms+
                    " (signal rms="+signal+") resolvedLabelPhase="+s.ResolvedLabelPhase.ToString("G3",CultureInfo.InvariantCulture)+"rad");
                implementationLabel=Math.Max(implementationLabel,s.ImplementationLabel);implementationGradient=Math.Max(implementationGradient,s.ImplementationGradient);
                implementationVariance=Math.Max(implementationVariance,s.ImplementationVariance);
                labelIteration=Math.Max(labelIteration,s.LabelIteration);gradientIteration=Math.Max(gradientIteration,s.GradientIteration);
                gradientFirstOrder=Math.Max(gradientFirstOrder,s.GradientFirstOrder);resolvedPhase=Math.Max(resolvedPhase,s.ResolvedLabelPhase);
                totalMaximum=Math.Max(totalMaximum,s.TotalMaximum);totalSum+=s.TotalSum;totalSquare+=s.TotalSquare;signalSquare+=s.SignalSquare;totalCount+=s.Count;
                if(s.TotalSum/s.Count>TotalGradientMeanTolerance){failures.Add(pair.Key+": 傾きの平均の誤差が "+(s.TotalSum/s.Count).ToString("G3",CultureInfo.InvariantCulture)+" で、許容 "+TotalGradientMeanTolerance+" を超えました。");}
            }
            // 傾きと位相の信号が無い組(帯域制限で落とす波が無い、または解像される波が無い。signal rms が丸めの大きさ)は、札の実装の一致だけが意味を持つ。数を報告し、信号のある点が少ないと不合格にする。
            int signalLessGroups=0;long signalLessPoints=0;
            foreach(KeyValuePair<string,Statistics> pair in statistics){
                if(Math.Sqrt(pair.Value.SignalSquare/pair.Value.Count)<1e-6){signalLessGroups++;signalLessPoints+=pair.Value.Count;}
            }
            report.Add("signal-less groups: "+signalLessGroups+" of "+statistics.Count+" ("+signalLessPoints+" of "+totalCount+" points). 傾きと位相の精度は残りの点の値");
            if(signalLessPoints*2>totalCount){failures.Add("傾きの信号が無い点が全体の半分を超えています("+signalLessPoints+" / "+totalCount+")。点の取り方が崩れて、傾きの精度の検査が空になっています。");}
            report.Add("overall: impl label="+implementationLabel.ToString("G3",CultureInfo.InvariantCulture)+"m gradient="+implementationGradient.ToString("G3",CultureInfo.InvariantCulture)+" variance(rel)="+implementationVariance.ToString("G3",CultureInfo.InvariantCulture)+
                " | iteration"+shaderIterations+" label="+labelIteration.ToString("G3",CultureInfo.InvariantCulture)+"m gradient="+gradientIteration.ToString("G3",CultureInfo.InvariantCulture)+" | firstOrder gradient="+gradientFirstOrder.ToString("G3",CultureInfo.InvariantCulture)+
                " | total max="+totalMaximum.ToString("G3",CultureInfo.InvariantCulture)+" mean="+(totalSum/totalCount).ToString("G3",CultureInfo.InvariantCulture)+" rms="+Math.Sqrt(totalSquare/totalCount).ToString("G3",CultureInfo.InvariantCulture)+
                " (signal rms="+Math.Sqrt(signalSquare/totalCount).ToString("G3",CultureInfo.InvariantCulture)+") resolvedLabelPhase="+resolvedPhase.ToString("G3",CultureInfo.InvariantCulture)+"rad");
            for(int iterations=1;iterations<=6;iterations++){
                report.Add("iterations="+iterations+": label error max="+iterationLabelError[iterations].ToString("G3",CultureInfo.InvariantCulture)+"m total gradient error max="+iterationGradientError[iterations].ToString("G3",CultureInfo.InvariantCulture));
            }
            if(implementationLabel>ImplementationLabelTolerance){failures.Add("GPU の札が、同じ式の倍精度の評価と最大 "+implementationLabel.ToString("G3",CultureInfo.InvariantCulture)+"m 違います(許容 "+ImplementationLabelTolerance+")。");}
            if(implementationGradient>ImplementationGradientTolerance){failures.Add("GPU の傾きが、同じ式の倍精度の評価と最大 "+implementationGradient.ToString("G3",CultureInfo.InvariantCulture)+" 違います(許容 "+ImplementationGradientTolerance+")。");}
            if(implementationVariance>1e-3){failures.Add("GPU の傾きの分散が、倍精度の評価と相対 "+implementationVariance.ToString("G3",CultureInfo.InvariantCulture)+" 違います(許容 1e-3)。");}
            if(totalMaximum>TotalGradientMaximumTolerance){failures.Add("GPU の傾きと正確な傾きの差が最大 "+totalMaximum.ToString("G3",CultureInfo.InvariantCulture)+" で、許容 "+TotalGradientMaximumTolerance+" を超えました。");}
            if(resolvedPhase>LabelPhaseTolerance){failures.Add("札の誤差による、解像される最短の波の位相の誤差が最大 "+resolvedPhase.ToString("G3",CultureInfo.InvariantCulture)+" rad で、許容 "+LabelPhaseTolerance+" を超えました。");}
        }

        // frame の Cell と時刻の波の値(倍精度)。
        private static WaveSet BuildWaveSet(in PlanetCellFrame frame,float quad){
            int count=ocean.WaveCount;
            var set=new WaveSet{
                Count=count,Direction=new double3[count],Wavelength=new double[count],Amplitude=new double[count],Sharpness=new double[count],Fraction=new double[count],TimePhase=new double[count],
                Radius=ocean.Radius,Quad=quad,
            };
            Vector3 up=frame.ApproximateOrigin.normalized;
            set.Origin=new double3(up.x,up.y,up.z);
            for(int i=0;i<count;i++){
                PlanetOceanSource.WaveInfo wave=ocean.GetWave(i);
                set.Direction[i]=new double3(wave.Direction.x,wave.Direction.y,wave.Direction.z);
                set.Wavelength[i]=wave.Wavelength;set.Amplitude[i]=wave.Amplitude;set.Sharpness[i]=wave.Sharpness;
                if(!ocean.TryWavePhaseFraction(in frame,i,out float fraction)){throw new InvalidOperationException("波の位相の端数を求められません。");}
                set.Fraction[i]=fraction;
                double product=(double)wave.AngularFrequency*ocean.WaveTime;
                set.TimePhase[i]=product-2.0*Math.PI*Math.Floor(product/(2.0*Math.PI));
            }
            return set;
        }

        private static double Theta(WaveSet waves,int k,double3 s){
            return 2.0*Math.PI*(waves.Fraction[k]+math.dot(waves.Direction[k],s)/waves.Wavelength[k])-waves.TimePhase[k];
        }

        // 変位。shortWaves が false なら帯域制限で残る波(波長が 2·quad 以上。頂点の変位)、true なら落とす波(波長が 2·quad 未満)に画素の大きさ footprint の重みを掛けたもの。
        private static double3 Offset(WaveSet waves,double3 s,double3 direction,bool shortWaves,double footprint){
            double3 offset=double3.zero;
            double limit=2.0*waves.Quad;
            for(int k=0;k<waves.Count;k++){
                double weight=1;
                bool isShort=waves.Wavelength[k]<limit;
                if(shortWaves){
                    if(!isShort){continue;}
                    weight=Math.Min(1.0,Math.Max(0.0,waves.Wavelength[k]/Math.Max(footprint,1e-6)-1.0));
                    if(weight==0){continue;}
                }else{
                    if(isShort){continue;}
                }
                double theta=Theta(waves,k,s);
                double sine=Math.Sin(theta);double cosine=Math.Cos(theta);
                double3 along=waves.Direction[k]-direction*math.dot(waves.Direction[k],direction);
                offset+=weight*(direction*(waves.Amplitude[k]*sine)+along*(waves.Sharpness[k]*waves.Amplitude[k]*cosine));
            }
            return offset;
        }

        // OceanLabel(PlanetOcean.hlsl)と同じ不動点反復を、倍精度で iterations 回。
        private static double3 LabelIterate(WaveSet waves,double3 position,int iterations){
            double3 s=position;
            for(int i=0;i<iterations;i++){
                double3 direction=math.normalize(waves.Origin*waves.Radius+s);
                s=position-Offset(waves,s,direction,false,0);
            }
            return s;
        }

        // 札を、半径 R の球面へ射影する(Cell の原点からの相対位置)。
        private static double3 ProjectOnSphere(WaveSet waves,double3 label){
            double3 direction=math.normalize(waves.Origin*waves.Radius+label);
            return direction*waves.Radius-waves.Origin*waves.Radius;
        }

        // OceanShortWaves(PlanetOcean.hlsl)と同じ式を倍精度で評価した、傾きと傾きの分散。
        private static double3 Formula(WaveSet waves,double3 label,double footprint,out double variance){
            double3 up=math.normalize(waves.Origin*waves.Radius+label);
            double3 gradient=double3.zero;
            variance=0;
            double limit=2.0*waves.Quad;
            for(int k=0;k<waves.Count;k++){
                double wavelength=waves.Wavelength[k];
                if(wavelength>=limit){continue;}
                double3 tangent=waves.Direction[k]-up*math.dot(waves.Direction[k],up);
                double slope=waves.Amplitude[k]*2.0*Math.PI/wavelength;
                double resolved=Math.Min(1.0,Math.Max(0.0,wavelength/Math.Max(footprint,1e-6)-1.0));
                gradient+=resolved*slope*Math.Cos(Theta(waves,k,label))*tangent;
                variance+=(1.0-resolved)*0.5*slope*slope*math.dot(tangent,tangent);
            }
            return gradient;
        }

        // 帯域制限で落とした波(重みつき)の変位した面の、正確な傾き(尖り=水平成分を含む)。球面上の点 s(α, β) を中心差分する。
        private static double3 ExactGradient(WaveSet waves,double3 sphereLabel,double footprint){
            double3 center=math.normalize(waves.Origin*waves.Radius+sphereLabel);
            double3 helper=new double3(0,1,0);
            if(Math.Abs(center.y)>0.9){helper=new double3(1,0,0);}
            double3 e1=math.normalize(math.cross(helper,center));
            double3 e2=math.cross(center,e1);
            double step=0.0005/waves.Radius;
            double3 pu=Surface(waves,center,e1,e2,step,0,footprint)-Surface(waves,center,e1,e2,-step,0,footprint);
            double3 pv=Surface(waves,center,e1,e2,0,step,footprint)-Surface(waves,center,e1,e2,0,-step,footprint);
            double3 normal=math.normalize(math.cross(pu,pv));
            double upward=math.dot(normal,center);
            if(upward<0){normal=-normal;upward=-upward;}
            double3 tangential=normal-center*upward;
            return -tangential/upward;
        }

        private static double3 Surface(WaveSet waves,double3 center,double3 e1,double3 e2,double alpha,double beta,double footprint){
            double3 direction=math.normalize(center+alpha*e1+beta*e2);
            double3 s=direction*waves.Radius-waves.Origin*waves.Radius;
            return s+Offset(waves,s,direction,true,footprint);
        }

        // 画素の大きさ footprint で解像される(重みが 0 でない)、帯域制限で落ちる波の最短の波長。無ければ 0。
        private static double ResolvedShortestWavelength(WaveSet waves,double footprint){
            double shortest=0;
            double limit=2.0*waves.Quad;
            for(int k=0;k<waves.Count;k++){
                if(waves.Wavelength[k]>=limit){continue;}
                double resolved=waves.Wavelength[k]/Math.Max(footprint,1e-6)-1.0;
                if(resolved<=0){continue;}
                if(shortest==0||waves.Wavelength[k]<shortest){shortest=waves.Wavelength[k];}
            }
            return shortest;
        }

        // PlanetOceanProbe.compute を 1 回 dispatch する。
        private static bool Evaluate(int kernel,in PlanetCellKey key,List<Vector4> positions,List<Vector4> origins,List<Vector4> parameters,out Vector4[] labels,out Vector4[] gradients){
            labels=new Vector4[positions.Count];gradients=new Vector4[positions.Count];
            if(!ocean.BindWavesForDiagnostics(probe,in key)){return false;}
            int count=positions.Count;
            var positionBuffer=new ComputeBuffer(count,16);var originBuffer=new ComputeBuffer(count,16);var parameterBuffer=new ComputeBuffer(count,16);
            var labelBuffer=new ComputeBuffer(count,16);var gradientBuffer=new ComputeBuffer(count,16);
            try{
                positionBuffer.SetData(positions);originBuffer.SetData(origins);parameterBuffer.SetData(parameters);
                probe.SetInt("_ProbeCount",count);
                probe.SetBuffer(kernel,"_ProbePosition",positionBuffer);probe.SetBuffer(kernel,"_ProbeOrigin",originBuffer);probe.SetBuffer(kernel,"_ProbeParameters",parameterBuffer);
                probe.SetBuffer(kernel,"_ProbeLabel",labelBuffer);probe.SetBuffer(kernel,"_ProbeGradient",gradientBuffer);
                probe.Dispatch(kernel,(count+63)/64,1,1);
                labelBuffer.GetData(labels);gradientBuffer.GetData(gradients);
            }finally{
                positionBuffer.Release();originBuffer.Release();parameterBuffer.Release();labelBuffer.Release();gradientBuffer.Release();
            }
            return true;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
