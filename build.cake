#addin "Cake.Git"
using Cake.Common.Diagnostics;
using System.Text.RegularExpressions;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

var buildDir = new DirectoryPath("./target").MakeAbsolute(Context.Environment);
var protocLink = "https://github.com/google/protobuf/releases/download/v2.6.1/protoc-2.6.1-win32.zip";
var protocArchive = buildDir.CombineWithFilePath("protoc-2.6.1-win32.zip");
var protocBinDir = buildDir.Combine("protoc");
var protocExe = protocBinDir.CombineWithFilePath("protoc.exe");

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
	{
		CleanDirectory(buildDir);
	});

Task("DownloadProtobuf")
	.WithCriteria(!FileExists(protocExe))
	.WithCriteria(!FileExists(protocArchive))
	.Does(() =>
	{
		DownloadFile(protocLink, protocArchive);
	});
	
Task("ExtractProtobuf")
	.WithCriteria(!FileExists(protocExe))
	.IsDependentOn("DownloadProtobuf")
	.Does(() =>
	{
		Unzip(protocArchive, protocBinDir);
	});
	
Task("GenerateProtoFiles")
	.IsDependentOn("ExtractProtobuf")
	.Does(() =>
	{
		var sourceProtoDir = new DirectoryPath("./proto/").MakeAbsolute(Context.Environment);
		var destinationProtoDir = new DirectoryPath("./src/main/java/").MakeAbsolute(Context.Environment);

		var protoFiles = GetFiles("./proto/**/*.proto");
		var modifiedProtoDir = buildDir.Combine("modified_proto");
		var patchedProtoFiles = PatchJavaProtoFiles(protoFiles, sourceProtoDir, modifiedProtoDir);

		CompileProtoFiles(patchedProtoFiles, modifiedProtoDir, destinationProtoDir);
	});
	
Task("Generate-Version-Info")
	.Does(() =>
	{
		var clearVersion = ClearVersionTag(GetVersionFromTag()) ?? "1.0.0";
		var semanticVersion = GetSemanticVersionV2(clearVersion);
		var appveyorVersion = GetAppVeyorBuildVersion(clearVersion);
		
		if (!string.IsNullOrEmpty(clearVersion))
		{
			Information("Version from tag: {0}", clearVersion);
			Information("Semantic version: {0}", semanticVersion);
			Information("AppVeyor version: {0}", appveyorVersion);
		}

		var xmlPokeSettings = new XmlPokeSettings 
		{
			Namespaces = new Dictionary<string, string> {
				{ "mvn", "http://maven.apache.org/POM/4.0.0" },
        	}
		};
		XmlPoke(new FilePath("./pom.xml"), "/mvn:project/mvn:version", semanticVersion, xmlPokeSettings);

		if (BuildSystem.IsRunningOnAppVeyor)
		{
			AppVeyor.UpdateBuildVersion(appveyorVersion);
		}
	});
	
Task("Check-Maven-Installed")
	.Does(() =>
	{
		StartProcess("mvn.cmd", "-v");
	});
	
Task("Package-With-Maven")
	.IsDependentOn("Generate-Version-Info")
	.IsDependentOn("Check-Maven-Installed")
	.IsDependentOn("GenerateProtoFiles")
	.Does(() =>
	{
		StartProcess("mvn.cmd", "package");
	});
	
Task("PublishArtifactsToAppVeyor")
	.IsDependentOn("Package-With-Maven")
	.WithCriteria(x => BuildSystem.IsRunningOnAppVeyor)
	.Does(() =>
	{
		var jarFiles = GetFiles(buildDir.FullPath + "/*.jar");
		foreach (var jar in jarFiles)
		{
			AppVeyor.UploadArtifact(jar);
		}
	});
	
//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
	.IsDependentOn("AppVeyor");
	
Task("AppVeyor")
	.IsDependentOn("PublishArtifactsToAppVeyor");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

public ProcessSettings GetBuildCMakeSettings()
{
	var cmakeBuildSettings = new ProcessSettings()
		.WithArguments(x => x
			.AppendSwitchQuoted("--build", buildDir.FullPath)
			.AppendSwitch("--config", configuration));
			
	return cmakeBuildSettings;
}

public IEnumerable<FilePath> PatchJavaProtoFiles(IEnumerable<FilePath> files, DirectoryPath sourceProtoDir, DirectoryPath destinationProtoDir)
{
	foreach (var file in files)
	{
		var relativeFile = sourceProtoDir.GetRelativePath(file);
		var destinationFile = destinationProtoDir.CombineWithFilePath(relativeFile);
		var destinationDirectory = destinationFile.GetDirectory();
		if (!DirectoryExists(destinationDirectory))
			CreateDirectory(destinationDirectory);

		if (!NeedUpdateFile(file, destinationFile))
		{
			Debug("Skip modification for file: {0}", file.FullPath);
			continue;
		}

		CopyFile(file, destinationFile);
		System.IO.File.AppendAllText(destinationFile.FullPath, string.Format("\n\noption java_outer_classname = \"{0}Protos\";", relativeFile.GetFilenameWithoutExtension()));
		yield return destinationFile;
	}
}

public void CompileProtoFiles(IEnumerable<FilePath> files, DirectoryPath sourceProtoDir, DirectoryPath destinationProtoDir)
{
	CreateDirectory(destinationProtoDir);

	var protocArguments = new ProcessSettings()
		.WithArguments(args => args
			.Append("-I=" + sourceProtoDir.FullPath)
			.Append("--java_out=" + destinationProtoDir.FullPath));

	var dirtyFiles = files.ToArray();
	if (dirtyFiles.Length == 0)
	{
		Information("All files are up to date");
		return;
	}
	
	foreach (var file in dirtyFiles)
	{
		protocArguments.WithArguments(args => args.Append(file.FullPath));
	}
	
	var exitCode = StartProcess(protocExe, protocArguments);
	if (exitCode != 0)
	{
		Error("Error processing proto files, protoc exit code: {0} ({1})", exitCode, protocArguments);
	}
}

public bool NeedUpdateFile(FilePath file, FilePath destinationFile)
{
	return !(FileExists(destinationFile)
		&& System.IO.File.GetLastWriteTime(file.FullPath) < System.IO.File.GetLastWriteTime(destinationFile.FullPath));
}

public string GetVersionFromTag()
{
	var lastestTag = "";
	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var tag = BuildSystem.AppVeyor.Environment.Repository.Tag;
		if (tag.IsTag)
		{
			return tag.Name;
		}
	}
	
	if (string.IsNullOrEmpty(lastestTag))
	{
		try
		{
			return GitDescribe(".", false, GitDescribeStrategy.Tags);
		}
		catch (Exception ex)
		{
			Warning(ex.Message, new object[] {});
		}
	}
	return null;
}

public static string ClearVersionTag(string lastestTag)
{
	if (string.IsNullOrEmpty(lastestTag))
		return null;
		
	if (lastestTag.StartsWith("versions/"))
	{
		lastestTag = lastestTag.Substring("versions/".Length);
	}
	
	var match = Regex.Match(lastestTag, @"^([0-9]+.[0-9]+.[0-9]*)");
	return match.Success
		? match.Value
		: lastestTag;
}

public string GetSemanticVersionV2(string clearVersion)
{
	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var tag = BuildSystem.AppVeyor.Environment.Repository.Tag;
		if (tag.IsTag)
		{
			return clearVersion;
		}

		return GetAppVeyorBuildVersion(clearVersion);		
	}

	var currentDate = DateTime.Now;
	var daysPart = (currentDate - new DateTime(2010, 01, 01)).Days;
	var secondsPart = Math.Floor((currentDate - currentDate.Date).TotalSeconds/2);
	return string.Format("{0}-dev.{1}.{2}", clearVersion, daysPart, secondsPart); 
}

public string GetAppVeyorBuildVersion(string clearVersion)
{
	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var buildNumber = BuildSystem.AppVeyor.Environment.Build.Number;
		clearVersion += string.Format("-CI.{0}", buildNumber);
		return (AppVeyor.Environment.PullRequest.IsPullRequest
			? clearVersion += string.Format("-PR.{0}", AppVeyor.Environment.PullRequest.Number)
			: clearVersion += "-" + AppVeyor.Environment.Repository.Branch);
	}
	return clearVersion;
}
