using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>Unity snapshot後のbuild/encode/hash/staging I/Oをworkerで処理し、main threadで短いcommitだけ行う。</summary>
    public sealed class VirtualGeometryConversionOperation:IDisposable{
        internal sealed class Prepared{
            public VirtualGeometryBuildOutput Output;public VirtualGeometryStreamMetadata Metadata;public string ContentHash;public bool CacheHit;
        }
        internal readonly Mesh Source;internal readonly string SourcePath,SourceGuid,SourceHash,OutputPath;internal readonly long SourceLocalId;
        internal readonly VirtualGeometryBuildSettings Settings;internal readonly VirtualGeometryMeshAsset Existing;
        private readonly CancellationTokenSource cancellation=new CancellationTokenSource();private readonly Task<Prepared> task;private float progress;private bool committed;private bool disposed;
        public string StagingPath{get;}public bool IsCompleted=>this.task.IsCompleted;public bool IsCancelled=>this.cancellation.IsCancellationRequested||this.task.IsCanceled;
        public float Progress=>Volatile.Read(ref this.progress);public bool WasCacheHit{get;private set;}
        internal VirtualGeometryConversionOperation(Mesh source,string path,VirtualGeometryBuildSettings settings,bool force){
            this.Source=source;this.OutputPath=path;this.Settings=settings.Clone();this.SourcePath=AssetDatabase.GetAssetPath(source);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source,out string guid,out long local);this.SourceGuid=guid;this.SourceLocalId=local;
            this.SourceHash=AssetDatabase.GetAssetDependencyHash(this.SourcePath).ToString();this.Existing=AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshAsset>(path);
            bool candidate=!force&&VirtualGeometryConversionService.Matches(this.Existing,guid,local,this.SourceHash,this.Settings);
            string existingPage=candidate?Path.Combine(Application.streamingAssetsPath,this.Existing.PageRelativePath):null;
            string existingHash=candidate?this.Existing.ContentHash:null;
            this.StagingPath=Path.GetFullPath(Path.Combine("Library","VirtualGeometryStaging",Guid.NewGuid().ToString("N")+".tmp"));
            VirtualGeometryMeshInput snapshot=VirtualGeometryMeshInput.FromMesh(source);
            this.task=Task.Run(()=>{
                CancellationToken token=this.cancellation.Token;
                try{
                    token.ThrowIfCancellationRequested();
                    if(candidate&&File.Exists(existingPage)&&HashFile(existingPage,token)==existingHash){Volatile.Write(ref this.progress,1);return new Prepared{CacheHit=true,ContentHash=existingHash};}
                    var output=VirtualGeometryBuilder.Build(snapshot,this.Settings,token,value=>Volatile.Write(ref this.progress,value*0.85f));
                    token.ThrowIfCancellationRequested();byte[] bytes=output.PageBytes;VirtualGeometryStreamMetadata metadata=null;
                    if(this.Settings.GenerateHierarchy&&this.Settings.GenerateStreamingPages){bytes=VirtualGeometryStreamCodec.Write(VirtualGeometryPageCodec.Read(output.PageBytes));metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(bytes,false));}
                    token.ThrowIfCancellationRequested();Volatile.Write(ref this.progress,0.9f);
                    string hash;using(var sha=SHA256.Create()){hash=Hex(sha.ComputeHash(bytes));}
                    Directory.CreateDirectory(Path.GetDirectoryName(this.StagingPath));
                    using(var stream=new FileStream(this.StagingPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536)){
                        for(int offset=0;offset<bytes.Length;offset+=1048576){token.ThrowIfCancellationRequested();stream.Write(bytes,offset,Math.Min(1048576,bytes.Length-offset));}
                        stream.Flush(true);
                    }
                    token.ThrowIfCancellationRequested();Volatile.Write(ref this.progress,1);return new Prepared{Output=output,Metadata=metadata,ContentHash=hash};
                }catch{DeleteStaging();throw;}
            });
        }
        internal void WaitUntilPrepared(){this.task.GetAwaiter().GetResult();}
        public void Cancel(){if(!this.disposed){this.cancellation.Cancel();}}
        public VirtualGeometryMeshAsset Commit(){
            this.cancellation.Token.ThrowIfCancellationRequested();if(this.disposed){throw new ObjectDisposedException(nameof(VirtualGeometryConversionOperation));}
            if(this.committed){throw new InvalidOperationException("Conversion has already been committed.");}
            if(!this.task.IsCompleted){throw new InvalidOperationException("Conversion is still running.");}
            Prepared prepared=this.task.GetAwaiter().GetResult();
            try{
                if(this.Source==null||AssetDatabase.GetAssetDependencyHash(this.SourcePath).ToString()!=this.SourceHash){throw new InvalidOperationException("Source changed during conversion. Convert again from a new snapshot.");}
                VirtualGeometryMeshAsset result=prepared.CacheHit?this.Existing:VirtualGeometryConversionService.CommitPrepared(this,prepared);
                this.WasCacheHit=prepared.CacheHit;this.committed=true;return result;
            }finally{this.DeleteStaging();}
        }
        public void Dispose(){
            if(this.disposed){return;}this.cancellation.Cancel();this.disposed=true;
            this.task.ContinueWith(task=>{if(task.IsFaulted){var observed=task.Exception;}this.DeleteStaging();this.cancellation.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        private void DeleteStaging(){if(File.Exists(this.StagingPath)){File.Delete(this.StagingPath);}}
        internal static string HashFile(string path,CancellationToken token){
            using(var sha=SHA256.Create())using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536)){
                byte[] buffer=new byte[65536];int count;while((count=stream.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,count,null,0);}sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return Hex(sha.Hash);
            }
        }
        private static string Hex(byte[] bytes)=>BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
    }
}