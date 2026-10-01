using System;
using System.IO;
using NUnit.Framework;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    public sealed class VirtualGeometryBuildTests{
        [Test]
        public void BuildArchiveScopeIncludesOnlyRequiredPathsAndRestoresAfterFailure(){
            string testRoot=Path.GetFullPath("Library/M5BuildScopeTest");string source=Path.Combine(testRoot,"Source");string staging=Path.Combine(testRoot,"Staging");
            Directory.CreateDirectory(source);File.WriteAllText(source+".meta","folder-guid");File.WriteAllText(Path.Combine(source,"required.vgpage"),"required");File.WriteAllText(Path.Combine(source,"unrelated.vgpage"),"unrelated");
            try{
                Assert.Throws<InvalidOperationException>(()=>{
                    using(var scope=new VirtualGeometryBuildArchiveScope(source,staging,new[]{"required.vgpage"})){
                        Assert.That(Directory.Exists(source),Is.False);Assert.That(File.Exists(source+".meta"),Is.False);Assert.That(scope.RequiredFiles.Length,Is.EqualTo(1));
                        Assert.That(File.ReadAllText(scope.RequiredFiles[0]),Is.EqualTo("required"));Directory.CreateDirectory(source);throw new InvalidOperationException("Build failure");
                    }
                });
                Assert.That(File.ReadAllText(Path.Combine(source,"unrelated.vgpage")),Is.EqualTo("unrelated"));
                Assert.That(Directory.Exists(staging),Is.False);Assert.That(File.ReadAllText(source+".meta"),Is.EqualTo("folder-guid"));Assert.That(File.Exists(staging+".meta"),Is.False);
                Assert.Throws<InvalidDataException>(()=>new VirtualGeometryBuildArchiveScope(source,staging,new[]{"../outside.vgpage"}));
                Assert.That(Directory.Exists(source),Is.True);
            }finally{if(Directory.Exists(testRoot)){Directory.Delete(testRoot,true);}}
        }
    }
}